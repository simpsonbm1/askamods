using System;
using System.Collections.Generic;
using UnityEngine;

namespace SupplyChainMod;

// SupplyChainTracker — the mod's single polling MonoBehaviour (registered via ClassInjector, same
// pattern as every other mod's tracker). Update() only accumulates Time.deltaTime timers and does
// real work on multi-second intervals — no per-frame work beyond the hotkey/typing-guard check and
// the world-session id poll (both cheap, and the world-session poll itself is throttled below).
//
// Phase 0 (dump/composition/complaint/BOM diagnostics) is READ-ONLY: it only observes and logs.
// v0.2.0 adds Phase 1a: a hotkey-gated ActuationSpike boost/revert with a crash-safe ledger — the
// mod's first write-capable path, gated behind Plugin.EnableSpike and only ever triggered by the
// spike hotkey or a ledger restore at world load (see ActuationSpike.cs). v0.3.0 adds Phase 1b:
// SupplyController, the complaint-driven autopilot (gated behind Plugin.EnableController, dry-run
// by default, F11-armed), driven from the same MasterTick and sharing RankActuator's write path.
public class SupplyChainTracker : MonoBehaviour
{
    private const float WorldPollInterval = 5f;
    private const float StationResolveInterval = 30f;
    private const float AutoTaskDumpDelaySeconds = 60f;

    private float _worldPollTimer;
    private float _tickTimer;
    private float _widgetTimer;
    private float _warehouseTimer;

    private KeyCode _dumpKey = KeyCode.F9;
    private KeyCode _spikeKey = KeyCode.F10;
    private KeyCode _controllerArmKey = KeyCode.F11;
    private KeyCode _quotaSpikeKey = KeyCode.F7;
    private KeyCode _evictSpikeKey = KeyCode.F6;
    private bool _ledgerRestoreDone;

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

    // On-screen toast (v0.2.2) — ported from MineRefreshMod/MineRefreshTracker.cs's proven
    // ShowMessage/OnGUI pattern. Static so ActuationSpike (a static class with no MonoBehaviour
    // reference) can post a toast directly; OnGUI (an instance lifecycle method) just renders
    // whatever the static fields currently hold.
    private static string? _guiMessage;
    private static float _guiMessageExpiry;

    internal static void ShowMessage(string message, float seconds)
    {
        _guiMessage = message;
        _guiMessageExpiry = Time.time + seconds;
    }

    private void Start()
    {
        _dumpKey = ParseKey(Plugin.DumpHotkey.Value, KeyCode.F9, "DumpHotkey");
        _spikeKey = ParseKey(Plugin.SpikeHotkey.Value, KeyCode.F10, "SpikeHotkey");
        _controllerArmKey = ParseKey(Plugin.ControllerArmHotkey.Value, KeyCode.F11, "ControllerArmHotkey");
        _quotaSpikeKey = ParseKey(Plugin.QuotaSpikeHotkey.Value, KeyCode.F7, "QuotaSpikeHotkey");
        _evictSpikeKey = ParseKey(Plugin.EvictSpikeHotkey.Value, KeyCode.F6, "EvictSpikeHotkey");
    }

    private static KeyCode ParseKey(string raw, KeyCode fallback, string keyName)
    {
        if (Enum.TryParse<KeyCode>(raw, true, out var parsed)) return parsed;
        Plugin.Logger.LogWarning($"[SupplyChain] Could not parse {keyName} '{raw}'. Using default '{fallback}'.");
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

        try
        {
            if (Plugin.EnableSpike.Value && !Common.IsTextInputFocused() && Input.GetKeyDown(_spikeKey))
            {
                if (_currentWorldId == null)
                {
                    Plugin.Logger.LogInfo("[SupplyChain] [spike] Hotkey pressed but skipped — no active world session.");
                    ShowMessage("Spike: no active world.", 4f);
                }
                else
                {
                    try
                    {
                        ResolveStationsIfDue(force: true);
                        ActuationSpike.OnHotkey(_stations, _currentWorldId);
                    }
                    catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] ActuationSpike.OnHotkey error: {ex}"); }
                }
            }
        }
        catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] Spike hotkey check error: {ex}"); }

        try
        {
            if (Plugin.EnableQuotaSpike.Value && !Common.IsTextInputFocused() && Input.GetKeyDown(_quotaSpikeKey))
            {
                if (_currentWorldId == null)
                {
                    Plugin.Logger.LogInfo("[SupplyChain] [quota] Hotkey pressed but skipped — no active world session.");
                    ShowMessage("Quota spike: no active world.", 4f);
                }
                else
                {
                    try
                    {
                        // v0.5.2 — Shift+<key> selects forge mode; plain key is always micro-test
                        // (an active boost/forge/drain reverts on either combination — see OnHotkey).
                        bool forge = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
                        ResolveStationsIfDue(force: true);
                        QuotaSpike.OnHotkey(_stations, _currentWorldId, forge);
                    }
                    catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] QuotaSpike.OnHotkey error: {ex}"); }
                }
            }
        }
        catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] Quota spike hotkey check error: {ex}"); }

        try
        {
            if (Plugin.EnableEvictSpike.Value && !Common.IsTextInputFocused() && Input.GetKeyDown(_evictSpikeKey))
            {
                if (_currentWorldId == null)
                {
                    Plugin.Logger.LogInfo("[SupplyChain] [evict] Hotkey pressed but skipped — no active world session.");
                    ShowMessage("Evict spike: no active world.", 4f);
                }
                else
                {
                    try
                    {
                        bool tier = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
                        ResolveStationsIfDue(force: true);
                        EvictionSpike.OnHotkey(_stations, _currentWorldId, tier);
                    }
                    catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] EvictionSpike.OnHotkey error: {ex}"); }
                }
            }
        }
        catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] Evict spike hotkey check error: {ex}"); }

        try
        {
            // v0.17.0 — ungated: the armed flag (ArmState) is now shared by SupplyController AND
            // TierCaseController, so the hotkey must keep working even when EnableController=false
            // (the v0.17.0 test path retires SupplyController from the test save via that cfg key).
            if (!Common.IsTextInputFocused() && Input.GetKeyDown(_controllerArmKey))
            {
                ArmState.ToggleArmed();
            }
        }
        catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] Controller arm-hotkey check error: {ex}"); }

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

        // v0.4.0 Phase 2a — WarehouseWatch's own poll cadence. The tracker always passes
        // fullTable=false here; WarehouseWatch tracks its own first-poll-after-world-load
        // internally (_firstPollDone) and upgrades that one poll to a full table itself.
        _warehouseTimer += Time.deltaTime;
        if (_warehouseTimer >= Plugin.WarehousePollSeconds.Value)
        {
            _warehouseTimer = 0f;
            if (_currentWorldId != null)
            {
                try { WarehouseWatch.Poll(_stations, fullTable: false); }
                catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] WarehouseWatch.Poll error: {ex}"); }
            }
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
        _ledgerRestoreDone = false;
        WidgetWatcher.ClearState();
        Patches.ComplaintLog.ClearState();
        try { ActuationSpike.NoteWorldLeft(); }
        catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] ActuationSpike.NoteWorldLeft error: {ex}"); }
        try { QuotaSpike.NoteWorldLeft(); }
        catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] QuotaSpike.NoteWorldLeft error: {ex}"); }
        try { EvictionSpike.NoteWorldLeft(); }
        catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] EvictionSpike.NoteWorldLeft error: {ex}"); }
        try { SupplyController.NoteWorldLeft(); }
        catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] SupplyController.NoteWorldLeft error: {ex}"); }
        try { WarehouseWatch.NoteWorldLeft(); }
        catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] WarehouseWatch.NoteWorldLeft error: {ex}"); }
        try { ClogController.NoteWorldLeft(); }
        catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] ClogController.NoteWorldLeft error: {ex}"); }
        try { MetabolicPlane.NoteWorldLeft(); }
        catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] MetabolicPlane.NoteWorldLeft error: {ex}"); }
        try { BudgetPlane.NoteWorldLeft(); }
        catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] BudgetPlane.NoteWorldLeft error: {ex}"); }
        try { DemandGraph.NoteWorldLeft(); }
        catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] DemandGraph.NoteWorldLeft error: {ex}"); }
        try { CaseTracker.NoteWorldLeft(); }
        catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] CaseTracker.NoteWorldLeft error: {ex}"); }
        try { TierCaseController.NoteWorldLeft(); }
        catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] TierCaseController.NoteWorldLeft error: {ex}"); }
    }

    // ── Master tick: rolling composition sweep + ~60s post-load auto TaskDump ──────────────────
    private void MasterTick()
    {
        if (_currentWorldId == null) return;

        ResolveStationsIfDue();
        if (_stations.Count == 0) return;

        CompositionSweep.TickRolling(_stations, Plugin.SweepStationsPerTick.Value, ref _sweepCursor);

        // v0.5.0 — the ledger-restore latch is hoisted out of the EnableSpike gate so QuotaSpike's
        // own restore pass still fires when EnableSpike=false but EnableQuotaSpike=true.
        // v0.6.0 — widened further: ClogController writes Kind="quota" ledger entries too, restored
        // by this same QuotaSpike.OnWorldReady pass, so it must also fire when EnableClogController
        // is the only one of the two enabled.
        if (!_ledgerRestoreDone)
        {
            _ledgerRestoreDone = true;
            if (Plugin.EnableSpike.Value)
            {
                try { ActuationSpike.OnWorldReady(_currentWorldId!, _stations); }
                catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] ActuationSpike.OnWorldReady error: {ex}"); }
            }
            if (Plugin.EnableQuotaSpike.Value || Plugin.EnableClogController.Value)
            {
                try { QuotaSpike.OnWorldReady(_currentWorldId!, _stations); }
                catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] QuotaSpike.OnWorldReady error: {ex}"); }
            }
            // v0.17.0 — widened to EnableTierCases too: TierCaseController writes the SAME
            // Kind="tier" ledger schema this restore pass reads (it keys on Kind, not on who wrote
            // the entry), so this pass must also fire when EnableEvictSpike is the only one of the
            // two OFF (same widening pattern as QuotaSpike's restore pass for EnableClogController).
            if (Plugin.EnableEvictSpike.Value || Plugin.EnableTierCases.Value)
            {
                try { EvictionSpike.OnWorldReady(_currentWorldId!, _stations); }
                catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] EvictionSpike.OnWorldReady error: {ex}"); }
            }
        }

        if (Plugin.EnableSpike.Value)
        {
            try { ActuationSpike.TickAutoRevert(_stations); }
            catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] ActuationSpike.TickAutoRevert error: {ex}"); }
        }

        if (Plugin.EnableQuotaSpike.Value)
        {
            try { QuotaSpike.TickWatch(_stations); }
            catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] QuotaSpike.TickWatch error: {ex}"); }
        }

        if (Plugin.EnableEvictSpike.Value)
        {
            try { EvictionSpike.Tick(_stations); }
            catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] EvictionSpike.Tick error: {ex}"); }
        }

        if (Plugin.EnableController.Value)
        {
            try { SupplyController.Tick(_stations, _currentWorldId!); }
            catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] SupplyController.Tick error: {ex}"); }
        }

        if (Plugin.EnableClogController.Value)
        {
            try { ClogController.Tick(_stations, _currentWorldId!); }
            catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] ClogController.Tick error: {ex}"); }
        }

        if (Plugin.EnableMetabolicPlane.Value)
        {
            try { MetabolicPlane.MaybeLogMovers(); }
            catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] MetabolicPlane.MaybeLogMovers error: {ex}"); }
        }

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

        if (Plugin.EnableDemandGraph.Value)
        {
            try { DemandGraph.Dump(trigger); }
            catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] DemandGraph.Dump error: {ex}"); }
        }

        try { WarehouseWatch.Poll(_stations, fullTable: true); }
        catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] WarehouseWatch.Poll({trigger}) error: {ex}"); }
    }

    // ── On-screen toast rendering (ported verbatim from MineRefreshMod/MineRefreshTracker.cs) ──
    private void OnGUI()
    {
        try
        {
            if (Time.time < _guiMessageExpiry && !string.IsNullOrEmpty(_guiMessage))
            {
                // Drop shadow style
                var shadowStyle = new GUIStyle(GUI.skin.label);
                shadowStyle.fontSize = 24;
                shadowStyle.fontStyle = FontStyle.Bold;
                shadowStyle.alignment = TextAnchor.MiddleCenter;
                shadowStyle.normal.textColor = Color.black;

                // Main text style
                var textStyle = new GUIStyle(shadowStyle);
                textStyle.normal.textColor = Color.yellow;

                // Render drop shadow
                GUI.Label(new Rect(0, 81, Screen.width, 100), _guiMessage, shadowStyle);

                // Render main text
                GUI.Label(new Rect(0, 80, Screen.width, 100), _guiMessage, textStyle);
            }
        }
        catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] OnGUI error: {ex}"); }
    }
}
