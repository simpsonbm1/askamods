using System;
using System.Collections.Generic;
using UnityEngine;

namespace SupplyChainMod;

// SupplyChainTracker — the mod's single polling MonoBehaviour (registered via ClassInjector, same
// pattern as every other mod's tracker). Update() only accumulates Time.deltaTime timers and does
// real work on multi-second intervals — no per-frame work beyond the hotkey/typing-guard check and
// the world-session id poll (both cheap, and the world-session poll itself is throttled below).
//
// Phase 0 is READ-ONLY: this class and everything it calls only observes and logs. No priority
// changes, no task creation, no inventory writes.
public class SupplyChainTracker : MonoBehaviour
{
    private const float WorldPollInterval = 5f;
    private const float StationResolveInterval = 30f;
    private const float AutoTaskDumpDelaySeconds = 60f;

    private float _worldPollTimer;
    private float _tickTimer;
    private float _widgetTimer;

    private KeyCode _dumpKey = KeyCode.F9;

    // Never cache per-world native wrappers across world sessions (project-wide gotcha) — only
    // StorageManager itself (a persistent, cross-world singleton) is cached; everything else is
    // re-resolved and cleared in NoteWorldLeft.
    private SandSailorStudio.Storage.StorageManager? _storage;
    private string? _currentWorldId;
    private float? _worldLoadTime;
    private bool _autoTaskDumped;

    private List<StationEntry> _stations = new();
    private DateTime _lastStationResolveAt = DateTime.MinValue;
    private int _sweepCursor;

    private void Start()
    {
        _dumpKey = ParseKey(Plugin.DumpHotkey.Value, KeyCode.F9);
    }

    private static KeyCode ParseKey(string raw, KeyCode fallback)
    {
        if (Enum.TryParse<KeyCode>(raw, true, out var parsed)) return parsed;
        Plugin.Logger.LogWarning($"[SupplyChain] Could not parse DumpHotkey '{raw}'. Using default '{fallback}'.");
        return fallback;
    }

    private void Update()
    {
        try
        {
            if (!Common.IsTextInputFocused() && Input.GetKeyDown(_dumpKey))
            {
                try { FullDump("hotkey"); }
                catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] FullDump(hotkey) error: {ex}"); }
            }
        }
        catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] Hotkey check error: {ex}"); }

        // World-session tracking always runs (not gated by EnableDiagnostics) — every other
        // component depends on _currentWorldId / _stations being current.
        _worldPollTimer += Time.deltaTime;
        if (_worldPollTimer >= WorldPollInterval)
        {
            _worldPollTimer = 0f;
            try { PollWorldSession(); }
            catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] PollWorldSession error: {ex}"); }
        }

        _tickTimer += Time.deltaTime;
        if (_tickTimer >= Plugin.TickSeconds.Value)
        {
            _tickTimer = 0f;
            try { MasterTick(); }
            catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] MasterTick error: {ex}"); }
        }

        _widgetTimer += Time.deltaTime;
        if (_widgetTimer >= Plugin.WidgetPollSeconds.Value)
        {
            _widgetTimer = 0f;
            try { WidgetWatcher.Poll(); }
            catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] WidgetWatcher.Poll error: {ex}"); }

            // v0.1.1 — ComplaintLog's REMOVE-dedupe flush + suppressed-count summary ride the same
            // cadence as the widget poll (the coordinator's spec: "one summary line per widget-poll
            // interval").
            try { Patches.ComplaintLog.PeriodicFlush(); }
            catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] ComplaintLog.PeriodicFlush error: {ex}"); }
        }
    }

    // ── World session tracking (StorageManager/ActiveSessionID — TreeRespawnMod NoteWorldLeft
    // pattern) ───────────────────────────────────────────────────────────────────────────────────
    private void PollWorldSession()
    {
        if (_storage == null)
        {
            try { _storage = UnityEngine.Object.FindAnyObjectByType<SandSailorStudio.Storage.StorageManager>(); }
            catch { _storage = null; }
        }
        if (_storage == null) return;

        string id;
        try { id = _storage.ActiveSessionID; }
        catch { _storage = null; return; }

        if (string.IsNullOrEmpty(id))
        {
            if (_currentWorldId != null)
                Plugin.Logger.LogInfo("[SupplyChain] World session ended — dropping per-world state.");
            NoteWorldLeft();
            return;
        }

        if (id != _currentWorldId)
        {
            _currentWorldId = id;
            _worldLoadTime = Time.time;
            _autoTaskDumped = false;
            Plugin.Logger.LogInfo($"[SupplyChain] World session active: {id}");
        }
    }

    private void NoteWorldLeft()
    {
        _storage = null;
        _currentWorldId = null;
        _worldLoadTime = null;
        _autoTaskDumped = false;
        _stations = new List<StationEntry>();
        _lastStationResolveAt = DateTime.MinValue;
        _sweepCursor = 0;
        WidgetWatcher.ClearState();
        Patches.ComplaintLog.ClearState();
    }

    // ── Master tick: rolling composition sweep + ~60s post-load auto TaskDump ──────────────────
    private void MasterTick()
    {
        if (_currentWorldId == null) return;

        ResolveStationsIfDue();
        if (_stations.Count == 0) return;

        CompositionSweep.TickRolling(_stations, Plugin.SweepStationsPerTick.Value, ref _sweepCursor);

        if (!_autoTaskDumped && _worldLoadTime.HasValue && (Time.time - _worldLoadTime.Value) >= AutoTaskDumpDelaySeconds)
        {
            _autoTaskDumped = true;
            try { TaskDump.Dump("auto", _stations); }
            catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] auto TaskDump error: {ex}"); }
        }
    }

    private void ResolveStationsIfDue(bool force = false)
    {
        var now = DateTime.UtcNow;
        if (!force && _stations.Count > 0 && (now - _lastStationResolveAt).TotalSeconds < StationResolveInterval)
            return;

        _lastStationResolveAt = now;
        try
        {
            _stations = StationWalker.ResolveStations();
            if (Plugin.EnableDiagnostics.Value)
                Plugin.Logger.LogInfo($"[SupplyChain] Resolved {_stations.Count} station(s) with a Workstation component.");
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"[SupplyChain] ResolveStationsIfDue error: {ex}");
        }
    }

    // ── Full dump (hotkey): TaskDump + BOM + an immediate full composition sweep ────────────────
    private void FullDump(string trigger)
    {
        if (_currentWorldId == null)
        {
            Plugin.Logger.LogInfo("[SupplyChain] FullDump skipped — no active world session.");
            return;
        }

        ResolveStationsIfDue(force: true);

        try { TaskDump.Dump(trigger, _stations); }
        catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] TaskDump({trigger}) error: {ex}"); }

        try { CompositionSweep.FullPass(_stations); }
        catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] CompositionSweep.FullPass({trigger}) error: {ex}"); }

        try { BomDump.Dump(trigger); }
        catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] BomDump({trigger}) error: {ex}"); }
    }
}
