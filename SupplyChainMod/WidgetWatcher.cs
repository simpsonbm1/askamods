using System;
using System.Collections.Generic;
using SSSGame;

namespace SupplyChainMod;

// WidgetWatcher (Phase 0, read-only) — polls SSSGame.SettlementIssueTrackerWidget.Instance every
// Plugin.WidgetPollSeconds and logs DELTAS (entries appearing/disappearing) in its complaint maps.
// State is keyed by STRINGS (structure posKey / item name), never by the widget's own Workstation/
// ItemInfo wrapper keys (project-wide gotcha: never hold interop wrapper references in long-lived
// dictionaries) — this class's own snapshot dictionaries only ever store strings/ints.
internal static class WidgetWatcher
{
    private static Dictionary<string, int> _prevStorageFull = new();
    private static Dictionary<string, int> _prevMarkers = new();       // key: "posKey|itemName"
    private static Dictionary<string, string> _prevNoProduction = new();
    private static HashSet<string> _prevNoCraftingStation = new();
    private static Dictionary<string, int> _prevMineExhausted = new();
    private static HashSet<string> _prevFarm = new();
    private static bool _loggedWidgetUnavailable;

    // v0.1.1 — first in-game run: Instance stayed null all session. Fall back to
    // FindAnyObjectByType<SettlementIssueTrackerWidget>() (SINGULAR — the plural finders throw,
    // project-wide gotcha; singular is confirmed safe elsewhere in this repo). Never cached across
    // world sessions — re-resolved every poll like everything else here; only the "which path
    // connected" log line is a one-shot, reset in ClearState so it re-announces after a world-leave.
    private static bool _loggedConnected;

    // v0.1.3 — run 3 (v0.1.2): the widget connects but has never produced a delta. Need to tell
    // "maps genuinely empty in this save" apart from "our enumeration silently sees nothing" —
    // logs each map's raw Count every poll where it changed (or once on first connect), so an
    // always-zero size across a whole session is unambiguous evidence vs. a silent read failure.
    private static bool _loggedSizesOnce;
    private static int _prevSizeStorageFull = -1;
    private static int _prevSizeMarkers = -1;
    private static int _prevSizeNoProduction = -1;
    private static int _prevSizeNoCraftingStation = -1;
    private static int _prevSizeMineExhausted = -1;
    private static int _prevSizeFarm = -1;

    // v0.9.1 — BudgetPlane demand-pull signal: the game's own "not being produced, needed by X"
    // issue list. Strings only; snapshot is replaced wholesale each widget poll so entries are live.
    internal static bool TryGetNoProductionNeededBy(string itemName, out string neededBy)
    {
        neededBy = "";
        try
        {
            if (_prevNoProduction.TryGetValue(itemName, out var val))
            {
                neededBy = val ?? "";
                return true;
            }
            return false;
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"[SupplyChain] [widget] TryGetNoProductionNeededBy error: {ex}");
            neededBy = "";
            return false;
        }
    }

    // Called by SupplyChainTracker on world-leave — the widget itself is a per-world singleton, and
    // holding stale prior-snapshot keys across worlds would produce bogus "disappeared" deltas on
    // the next world's first poll.
    internal static void ClearState()
    {
        _prevStorageFull = new();
        _prevMarkers = new();
        _prevNoProduction = new();
        _prevNoCraftingStation = new();
        _prevMineExhausted = new();
        _prevFarm = new();
        _loggedWidgetUnavailable = false;
        _loggedConnected = false;
        _loggedSizesOnce = false;
        _prevSizeStorageFull = -1;
        _prevSizeMarkers = -1;
        _prevSizeNoProduction = -1;
        _prevSizeNoCraftingStation = -1;
        _prevSizeMineExhausted = -1;
        _prevSizeFarm = -1;
    }

    internal static void Poll()
    {
        SettlementIssueTrackerWidget? widget = null;
        string via = "Instance";
        try { widget = SettlementIssueTrackerWidget.Instance; } catch { widget = null; }

        if (widget == null)
        {
            via = "FindAnyObjectByType";
            try { widget = UnityEngine.Object.FindAnyObjectByType<SettlementIssueTrackerWidget>(); } catch { widget = null; }
        }

        if (widget == null)
        {
            if (Plugin.EnableDiagnostics.Value && !_loggedWidgetUnavailable)
            {
                _loggedWidgetUnavailable = true;
                Plugin.Logger.LogInfo("[SupplyChain] [widget] SettlementIssueTrackerWidget not yet available (tried Instance and FindAnyObjectByType).");
            }
            return;
        }
        _loggedWidgetUnavailable = false;

        if (!_loggedConnected)
        {
            _loggedConnected = true;
            Plugin.Logger.LogInfo($"[SupplyChain] [widget] connected via {via}");
        }

        try { PollSizes(widget); } catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] PollSizes error: {ex}"); }

        try { PollStorageFull(widget); } catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] PollStorageFull error: {ex}"); }
        try { PollMarkers(widget); } catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] PollMarkers error: {ex}"); }
        try { PollNoProduction(widget); } catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] PollNoProduction error: {ex}"); }
        try { PollNoCraftingStation(widget); } catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] PollNoCraftingStation error: {ex}"); }
        try { PollMineExhausted(widget); } catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] PollMineExhausted error: {ex}"); }
        try { PollFarm(widget); } catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] PollFarm error: {ex}"); }
    }

    // v0.1.3 — raw map-size line: (a) once immediately on first connect, (b) thereafter only when
    // any size differs from the previous poll. Each .Count read is its own try/catch naming the map
    // on failure — if Count itself throws on one of these Il2Cpp collections we need to know WHICH.
    private static void PollSizes(SettlementIssueTrackerWidget widget)
    {
        int storageFull = -1, markers = -1, noProduction = -1, noCraftingStation = -1, mineExhausted = -1, farm = -1;

        try { storageFull = widget._storageFullComplaintsMap != null ? widget._storageFullComplaintsMap.Count : 0; }
        catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] [widget] _storageFullComplaintsMap.Count error: {ex}"); }

        try { markers = widget._markersComplaintsMap != null ? widget._markersComplaintsMap.Count : 0; }
        catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] [widget] _markersComplaintsMap.Count error: {ex}"); }

        try { noProduction = widget._noProductionComplaints != null ? widget._noProductionComplaints.Count : 0; }
        catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] [widget] _noProductionComplaints.Count error: {ex}"); }

        try { noCraftingStation = widget._noCraftingStationComplaints != null ? widget._noCraftingStationComplaints.Count : 0; }
        catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] [widget] _noCraftingStationComplaints.Count error: {ex}"); }

        try { mineExhausted = widget._mineExhaustedComplaintsMap != null ? widget._mineExhaustedComplaintsMap.Count : 0; }
        catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] [widget] _mineExhaustedComplaintsMap.Count error: {ex}"); }

        try { farm = widget._farmComplaints != null ? widget._farmComplaints.Count : 0; }
        catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] [widget] _farmComplaints.Count error: {ex}"); }

        bool changed = storageFull != _prevSizeStorageFull || markers != _prevSizeMarkers || noProduction != _prevSizeNoProduction
            || noCraftingStation != _prevSizeNoCraftingStation || mineExhausted != _prevSizeMineExhausted || farm != _prevSizeFarm;

        if (!_loggedSizesOnce || changed)
        {
            _loggedSizesOnce = true;
            Plugin.Logger.LogInfo(
                $"[SupplyChain] [widget] sizes: storageFull={storageFull} markers={markers} noProd={noProduction} " +
                $"noCraft={noCraftingStation} mineExhausted={mineExhausted} farm={farm}");

            _prevSizeStorageFull = storageFull;
            _prevSizeMarkers = markers;
            _prevSizeNoProduction = noProduction;
            _prevSizeNoCraftingStation = noCraftingStation;
            _prevSizeMineExhausted = mineExhausted;
            _prevSizeFarm = farm;
        }
    }

    private static string StationKey(Workstation? ws)
    {
        if (ws == null) return "?";
        Structure? s = null;
        try { s = ws.GetStructure(); } catch { }
        return s != null ? $"{Common.SafeName(s)}@{Common.PosKey(s)}" : "?";
    }

    private static void LogIntDelta(string label, Dictionary<string, int> current, Dictionary<string, int> previous)
    {
        if (!Plugin.EnableDiagnostics.Value) return;
        foreach (var kv in current)
        {
            if (!previous.ContainsKey(kv.Key))
                Plugin.Logger.LogInfo($"[SupplyChain] [widget-delta] {label} APPEARED: '{kv.Key}' count={kv.Value}");
        }
        foreach (var kv in previous)
        {
            if (!current.ContainsKey(kv.Key))
                Plugin.Logger.LogInfo($"[SupplyChain] [widget-delta] {label} GONE: '{kv.Key}'");
        }
    }

    private static void PollStorageFull(SettlementIssueTrackerWidget widget)
    {
        var current = new Dictionary<string, int>();
        var map = widget._storageFullComplaintsMap;
        if (map != null)
        {
            foreach (var kv in map)
            {
                string key = StationKey(kv.Key);
                current[key] = kv.Value;
            }
        }
        LogIntDelta("storageFull", current, _prevStorageFull);
        _prevStorageFull = current;
    }

    private static void PollMarkers(SettlementIssueTrackerWidget widget)
    {
        var current = new Dictionary<string, int>();
        var map = widget._markersComplaintsMap;
        if (map != null)
        {
            foreach (var kv in map)
            {
                string stationKey = StationKey(kv.Key);
                var inner = kv.Value;
                if (inner == null) continue;
                foreach (var ikv in inner)
                {
                    string itemName = Common.SafeItemName(ikv.Key);
                    current[$"{stationKey}|{itemName}"] = ikv.Value;
                }
            }
        }
        LogIntDelta("markers", current, _prevMarkers);
        _prevMarkers = current;
    }

    // The no-production payload is a Phase 0 unknown per the delegation spec — log it fully
    // (both the item name AND the string value) whenever it appears/changes/disappears.
    private static void PollNoProduction(SettlementIssueTrackerWidget widget)
    {
        var current = new Dictionary<string, string>();
        var map = widget._noProductionComplaints;
        if (map != null)
        {
            foreach (var kv in map)
            {
                current[Common.SafeItemName(kv.Key)] = kv.Value ?? "";
            }
        }

        if (Plugin.EnableDiagnostics.Value)
        {
            foreach (var kv in current)
            {
                if (!_prevNoProduction.TryGetValue(kv.Key, out var prevVal))
                    Plugin.Logger.LogInfo($"[SupplyChain] [widget-delta] noProduction APPEARED: '{kv.Key}' value='{kv.Value}'");
                else if (prevVal != kv.Value)
                    Plugin.Logger.LogInfo($"[SupplyChain] [widget-delta] noProduction CHANGED: '{kv.Key}' '{prevVal}' -> '{kv.Value}'");
            }
            foreach (var kv in _prevNoProduction)
            {
                if (!current.ContainsKey(kv.Key))
                    Plugin.Logger.LogInfo($"[SupplyChain] [widget-delta] noProduction GONE: '{kv.Key}' (was '{kv.Value}')");
            }
        }
        _prevNoProduction = current;
    }

    private static void PollNoCraftingStation(SettlementIssueTrackerWidget widget)
    {
        var current = new HashSet<string>();
        var set = widget._noCraftingStationComplaints;
        if (set != null)
        {
            foreach (var info in set) current.Add(Common.SafeItemName(info));
        }
        LogSetDelta("noCraftingStation", current, _prevNoCraftingStation);
        _prevNoCraftingStation = current;
    }

    private static void PollMineExhausted(SettlementIssueTrackerWidget widget)
    {
        var current = new Dictionary<string, int>();
        var map = widget._mineExhaustedComplaintsMap;
        if (map != null)
        {
            foreach (var kv in map) current[StationKey(kv.Key)] = kv.Value;
        }
        LogIntDelta("mineExhausted", current, _prevMineExhausted);
        _prevMineExhausted = current;
    }

    private static void PollFarm(SettlementIssueTrackerWidget widget)
    {
        var current = new HashSet<string>();
        var list = widget._farmComplaints;
        if (list != null)
        {
            int n = list.Count;
            for (int i = 0; i < n; i++)
            {
                try
                {
                    var fs = list[i];
                    if (fs == null) continue;
                    Structure? s = null;
                    try { s = fs.GetStructure(); } catch { }
                    current.Add(s != null ? $"{Common.SafeName(s)}@{Common.PosKey(s)}" : "?");
                }
                catch { }
            }
        }
        LogSetDelta("farm", current, _prevFarm);
        _prevFarm = current;
    }

    private static void LogSetDelta(string label, HashSet<string> current, HashSet<string> previous)
    {
        if (!Plugin.EnableDiagnostics.Value) return;
        foreach (var key in current)
            if (!previous.Contains(key)) Plugin.Logger.LogInfo($"[SupplyChain] [widget-delta] {label} APPEARED: '{key}'");
        foreach (var key in previous)
            if (!current.Contains(key)) Plugin.Logger.LogInfo($"[SupplyChain] [widget-delta] {label} GONE: '{key}'");
    }
}
