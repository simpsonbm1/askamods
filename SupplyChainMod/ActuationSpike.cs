using System;
using System.Collections.Generic;
using SSSGame;
using SSSGame.AI;
using UnityEngine;

namespace SupplyChainMod;

// ActuationSpike (v0.2.0, Phase 1a) — the FIRST write-capable machinery in this mod. Hotkey-gated
// manual "boost" of one workstation task's priority, with guaranteed revert (hotkey toggle or an
// auto-revert timer) and a crash-safe write-ahead file ledger (SpikeLedger) so a boost stranded by
// a crash/quit gets un-done at the next load of the SAME world. Purpose: learn (a) what the
// priority Int32 actually means (ordering vs. raw value) and (b) whether Rpc_ChangeTaskPriority is
// reachable at runtime. Everything else in this mod (Phase 0) stays untouched and read-only.
//
// v0.2.3 — test run 1 (Workshop House 2, CraftingStation, 28 tasks) answered (a): priority IS a
// RANK POSITION, continuously renumbered from list order (priority == list index, 0 = top) — NOT
// an arbitrary value. It also showed ws.NetworkWorkstations is null/empty at runtime (the rpc
// value-write path never fires) and that a plain WorkstationTaskData.SetPriority() value write
// gets squashed the instant Workstation.RefreshTaskDataPriorities() re-derives priority from list
// order. Actuation switched from a value write to a LIST-MOVE (ApplyRank: RemoveAt+Insert on the
// live ws._taskDatas backing list, then RefreshTaskDataPriorities to renumber), plus a setter probe
// and a NetworkCraftingStation component probe for network-sync diagnostics.
//
// v0.3.0 — Phase 1a CONFIRMED in-game (boost/revert/ledger-crash-restore all verified; ranks are
// 0-based; network component reachable via GetComponent<NetworkCraftingStation> on the station's
// own GameObject, NetworkWorkstations stays null). The list-move actuator (ApplyRank) and its
// helpers moved out to RankActuator.cs so the new automated controller (SupplyController, Phase 1b)
// can share the SAME write path. ActuationSpike now also registers/unregisters its boosted
// station's posKey in RankActuator.BoostedStationKeys, so the spike and the controller never target
// the same station at once.
//
// Never store the Workstation wrapper across ticks (project-wide gotcha: don't cache per-world
// interop wrappers) — the active boost is re-resolved by PosKey from the tracker's freshly-resolved
// station list every time it's touched.
internal static class ActuationSpike
{
    private sealed class ActiveBoost
    {
        public string WorldId = "?";
        public string PosKey = "?";
        public string StationName = "?";
        public int TaskIndex;
        public string ItemName = "?";
        public int OrigPriority;
        public int NewPriority;
        public float RevertAtTime;
    }

    private static ActiveBoost? _active;

    // ── Public surface (all wrapped so no exception escapes to the tracker) ────────────────────
    internal static void OnHotkey(List<StationEntry> stations, string worldId)
    {
        try
        {
            if (_active != null) Revert(stations, "hotkey");
            else Boost(stations, worldId);
        }
        catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] [spike] OnHotkey error: {ex}"); }
    }

    internal static void TickAutoRevert(List<StationEntry> stations)
    {
        try
        {
            if (_active != null && Time.time >= _active.RevertAtTime)
                Revert(stations, "auto");
        }
        catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] [spike] TickAutoRevert error: {ex}"); }
    }

    internal static void OnWorldReady(string worldId, List<StationEntry> stations)
    {
        try
        {
            var entries = SpikeLedger.LoadFor(worldId);
            foreach (var entry in entries)
            {
                try { RestoreLedgerEntry(entry, stations); }
                catch (Exception ex)
                {
                    Plugin.Logger.LogError($"[SupplyChain] [ledger] Restore entry error: {ex}");
                    try { SpikeLedger.Remove(entry); } catch { }
                }
            }
        }
        catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] [spike] OnWorldReady error: {ex}"); }
    }

    internal static void NoteWorldLeft()
    {
        try
        {
            if (_active != null)
            {
                Plugin.Logger.LogWarning(
                    $"[SupplyChain] [spike] World left with an active boost — station='{_active.StationName}' " +
                    $"item='{_active.ItemName}' remains on the ledger and will be restored at the next load of world {_active.WorldId}.");
            }
            _active = null;
            RankActuator.BoostedStationKeys.Clear();
        }
        catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] [spike] NoteWorldLeft error: {ex}"); }
    }

    // ── Boost ────────────────────────────────────────────────────────────────────────────────
    // v0.2.1: a single hotkey press with untouched config now picks a target and applies the
    // boost in the same press — no runtime config reload (established project position: the
    // reload-cadence cost isn't worth it unless required for mod function), so the fully-automatic
    // default path exists instead of the old "press once to list, edit config, press again" flow.
    private static void Boost(List<StationEntry> stations, string worldId)
    {
        var filter = Plugin.SpikeStationFilter.Value;
        StationEntry? target;
        Il2CppSystem.Collections.Generic.List<WorkstationTaskData>? datas;

        if (string.IsNullOrWhiteSpace(filter))
        {
            var candidates = LogCandidateList(stations);
            target = AutoSelectStation(candidates, out datas, out string autoClass);
            if (target == null)
            {
                Plugin.Logger.LogWarning("[SupplyChain] [spike] no station with >= 2 tasks — set SpikeStationFilter to force a 1-task station.");
                SupplyChainTracker.ShowMessage("Spike: no station with >= 2 tasks found.", 6f);
                return;
            }
            Plugin.Logger.LogInfo(
                $"[SupplyChain] [spike] AUTO-SELECTED station='{target.StructureName}' class='{autoClass}' " +
                $"taskCount={(datas != null ? datas.Count : 0)} (filter empty — set [Phase1Spike] SpikeStationFilter to override)");
        }
        else
        {
            target = null;
            foreach (var entry in stations)
            {
                if (entry.StructureName != null && entry.StructureName.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    target = entry;
                    break;
                }
            }
            if (target == null)
            {
                Plugin.Logger.LogWarning($"[SupplyChain] [spike] No station matched filter '{filter}'.");
                SupplyChainTracker.ShowMessage($"Spike: no station matched filter '{filter}'.", 6f);
                return;
            }

            datas = null;
            try { datas = target.Ws.GetWorkstationTaskDatas(); }
            catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] [spike] GetWorkstationTaskDatas error: {ex}"); return; }
        }

        if (datas == null || datas.Count == 0)
        {
            Plugin.Logger.LogWarning($"[SupplyChain] [spike] Station '{target.StructureName}' has no tasks.");
            return;
        }

        // v0.3.0 — never target a station the controller currently has boosted (mutual exclusion).
        if (RankActuator.BoostedStationKeys.Contains(target.PosKey))
        {
            Plugin.Logger.LogWarning($"[SupplyChain] [spike] station '{target.StructureName}' already has an active boost (spike or controller) — skipping.");
            SupplyChainTracker.ShowMessage($"Spike: '{target.StructureName}' already boosted — skipping.", 6f);
            return;
        }

        var ws = target.Ws;

        int idx = Plugin.SpikeTaskIndex.Value;
        WorkstationTaskData? task;

        if (idx == -1)
        {
            // Auto (default): pick the LAST non-pinned task, walking from the end. Boosting the
            // last-ranked task is the informative experiment for priority semantics — boosting
            // task[0], already top, would show nothing. All tasks pinned → nothing eligible.
            task = null;
            for (int i = datas.Count - 1; i >= 0; i--)
            {
                WorkstationTaskData? candidate = null;
                try { candidate = datas[i]; } catch { continue; }
                if (candidate == null) continue;

                bool pinnedCandidate = false;
                try { pinnedCandidate = candidate.pinnedTask; } catch { pinnedCandidate = false; }
                if (pinnedCandidate) continue;

                task = candidate;
                idx = i;
                break;
            }
            if (task == null)
            {
                Plugin.Logger.LogWarning($"[SupplyChain] [spike] all tasks on station '{target.StructureName}' are pinned — nothing eligible to boost.");
                SupplyChainTracker.ShowMessage($"Spike: all tasks on '{target.StructureName}' are pinned.", 6f);
                return;
            }
        }
        else if (idx >= 0)
        {
            if (idx >= datas.Count)
            {
                Plugin.Logger.LogWarning($"[SupplyChain] [spike] SpikeTaskIndex {idx} out of range — valid range is 0..{datas.Count - 1} for station '{target.StructureName}'.");
                SupplyChainTracker.ShowMessage($"Spike: task index {idx} out of range (0..{datas.Count - 1}).", 6f);
                return;
            }

            task = null;
            try { task = datas[idx]; }
            catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] [spike] task[{idx}] read error: {ex}"); return; }
            if (task == null)
            {
                Plugin.Logger.LogWarning($"[SupplyChain] [spike] task[{idx}] is null.");
                return;
            }

            bool pinned = false;
            try { pinned = task.pinnedTask; } catch { pinned = false; }
            if (pinned)
            {
                Plugin.Logger.LogWarning($"[SupplyChain] [spike] task[{idx}] is pinned — pinned tasks are sacrosanct (design contract), refusing to touch it.");
                return;
            }
        }
        else
        {
            Plugin.Logger.LogWarning($"[SupplyChain] [spike] SpikeTaskIndex {idx} is invalid — use -1 for auto (last non-pinned task) or a value >= 0.");
            return;
        }

        RankActuator.LogTaskList("BEFORE", target, datas);

        bool allowed = true;
        try
        {
            allowed = ws.HasStateAuthority;
            if (!allowed)
            {
                Plugin.Logger.LogWarning($"[SupplyChain] [spike] blocked: not host (HasStateAuthority=false) for station '{target.StructureName}'.");
                SupplyChainTracker.ShowMessage("Spike blocked: not the session host.", 8f);
                return;
            }
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogInfo($"[SupplyChain] [spike] HasStateAuthority unreadable ({ex.Message}) — assuming offline/solo, allowing.");
        }

        int orig;
        try { orig = task.priority; }
        catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] [spike] read priority error: {ex}"); return; }

        int newPriority = Plugin.SpikeNewPriority.Value;
        if (orig == newPriority)
        {
            Plugin.Logger.LogInfo($"[SupplyChain] [spike] task[{idx}] priority is already {orig} — nothing to do.");
            return;
        }

        string itemName = RankActuator.SafeTaskItemName(task);

        // Write-ahead: append BEFORE mutating. A crash between this and ApplyRank below is
        // harmless — the restore pass would just rewrite the rank index that's already there.
        // OrigPriority/BoostPriority are rank indices (== priority, see LedgerEntry doc comment).
        var ledgerEntry = new LedgerEntry
        {
            WorldId = worldId,
            PosKey = target.PosKey,
            StationName = target.StructureName,
            TaskIndex = idx,
            ItemName = itemName,
            OrigPriority = orig,
            BoostPriority = newPriority,
            UtcTicks = DateTime.UtcNow.Ticks,
        };
        SpikeLedger.Append(ledgerEntry);

        string path = RankActuator.ApplyRank(ws, task, idx, newPriority);

        int after = orig;
        try { after = task.priority; } catch { }

        RankActuator.LogTaskList("AFTER (compare ordering vs. value — that's exactly what this spike measures)", target, datas);

        RankActuator.BoostedStationKeys.Add(target.PosKey);

        _active = new ActiveBoost
        {
            WorldId = worldId,
            PosKey = target.PosKey,
            StationName = target.StructureName,
            TaskIndex = idx,
            ItemName = itemName,
            OrigPriority = orig,
            NewPriority = newPriority,
            RevertAtTime = Time.time + Plugin.SpikeAutoRevertSeconds.Value,
        };

        Plugin.Logger.LogInfo(
            $"[SupplyChain] [spike] BOOST applied station='{target.StructureName}' task[{idx}]='{itemName}' " +
            $"rank {orig}->{newPriority} (readback={after}) path={path} autoRevertIn={Plugin.SpikeAutoRevertSeconds.Value}s");

        SupplyChainTracker.ShowMessage(
            $"Spike BOOST: '{itemName}' moved to rank {newPriority} on '{target.StructureName}' — check the task list order. F10 again to revert.", 12f);
    }

    // ── Revert ───────────────────────────────────────────────────────────────────────────────
    private static void Revert(List<StationEntry> stations, string reason)
    {
        var active = _active;
        if (active == null) return;

        StationEntry? target = RankActuator.FindStation(stations, active.PosKey, active.StationName);
        if (target == null)
        {
            Plugin.Logger.LogWarning(
                $"[SupplyChain] [spike] Revert({reason}): station '{active.StationName}' (posKey={active.PosKey}) " +
                "not found — ledger entry kept, will be restored at next world load.");
            RankActuator.BoostedStationKeys.Remove(active.PosKey);
            _active = null;
            return;
        }

        var ws = target.Ws;
        Il2CppSystem.Collections.Generic.List<WorkstationTaskData>? datas = null;
        try { datas = ws.GetWorkstationTaskDatas(); }
        catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] [spike] Revert GetWorkstationTaskDatas error: {ex}"); }

        int taskIndex = RankActuator.FindTaskIndex(datas, active.TaskIndex, active.ItemName, out var task);
        if (task == null)
        {
            Plugin.Logger.LogWarning(
                $"[SupplyChain] [spike] Revert({reason}): task with item '{active.ItemName}' not found on station " +
                $"'{target.StructureName}' — dropping ledger entry.");
            SupplyChainTracker.ShowMessage($"Spike: revert failed — task '{active.ItemName}' not found on '{target.StructureName}'.", 6f);
            SpikeLedger.Remove(MakeLedgerKey(active.WorldId, active.PosKey, active.TaskIndex, active.ItemName));
            RankActuator.BoostedStationKeys.Remove(active.PosKey);
            _active = null;
            return;
        }

        if (datas != null) RankActuator.LogTaskList("BEFORE revert", target, datas);

        string path = RankActuator.ApplyRank(ws, task, taskIndex, active.OrigPriority);

        if (datas != null) RankActuator.LogTaskList("AFTER revert", target, datas);

        SpikeLedger.Remove(MakeLedgerKey(active.WorldId, active.PosKey, active.TaskIndex, active.ItemName));
        RankActuator.BoostedStationKeys.Remove(active.PosKey);

        Plugin.Logger.LogInfo(
            $"[SupplyChain] [spike] REVERT ({reason}) station='{target.StructureName}' task[{taskIndex}]='{active.ItemName}' " +
            $"rank back to {active.OrigPriority} path={path}");

        SupplyChainTracker.ShowMessage(
            $"Spike REVERT ({reason}): '{active.ItemName}' back to rank {active.OrigPriority} on '{target.StructureName}'.", 8f);

        _active = null;
    }

    // ── Ledger restore (world-load pass) ────────────────────────────────────────────────────────
    private static void RestoreLedgerEntry(LedgerEntry entry, List<StationEntry> stations)
    {
        StationEntry? target = RankActuator.FindStation(stations, entry.PosKey, entry.StationName);
        if (target == null)
        {
            Plugin.Logger.LogWarning(
                $"[SupplyChain] [ledger] Stranded boost station '{entry.StationName}' (posKey={entry.PosKey}) " +
                "not found — dropping ledger entry.");
            SpikeLedger.Remove(entry);
            return;
        }

        var ws = target.Ws;
        Il2CppSystem.Collections.Generic.List<WorkstationTaskData>? datas = null;
        try { datas = ws.GetWorkstationTaskDatas(); }
        catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] [ledger] GetWorkstationTaskDatas error: {ex}"); }

        int taskIndex = RankActuator.FindTaskIndex(datas, entry.TaskIndex, entry.ItemName, out var task);
        if (task == null)
        {
            Plugin.Logger.LogWarning(
                $"[SupplyChain] [ledger] Stranded boost item '{entry.ItemName}' not found on station " +
                $"'{target.StructureName}' — dropping ledger entry.");
            SpikeLedger.Remove(entry);
            return;
        }

        string path = RankActuator.ApplyRank(ws, task, taskIndex, entry.OrigPriority);

        Plugin.Logger.LogWarning(
            $"[SupplyChain] [ledger] Restored stranded boost: station='{target.StructureName}' item='{entry.ItemName}' " +
            $"rank -> {entry.OrigPriority} path={path}");
        SupplyChainTracker.ShowMessage(
            $"SupplyChain: restored stranded boost on '{target.StructureName}' ('{entry.ItemName}' back to {entry.OrigPriority}).", 12f);
        SpikeLedger.Remove(entry);
    }

    // ── Local helpers (spike-specific — not shared with the controller) ────────────────────────
    private static LedgerEntry MakeLedgerKey(string worldId, string posKey, int taskIndex, string itemName) => new()
    {
        WorldId = worldId,
        PosKey = posKey,
        TaskIndex = taskIndex,
        ItemName = itemName,
    };

    // Logs the full candidate list (always useful reference data) AND returns per-station
    // (class, datas) so Boost()'s auto-select path can reuse the same GetWorkstationTaskDatas()
    // read instead of re-fetching.
    private static List<(StationEntry Entry, string Class, Il2CppSystem.Collections.Generic.List<WorkstationTaskData>? Datas)> LogCandidateList(List<StationEntry> stations)
    {
        var result = new List<(StationEntry, string, Il2CppSystem.Collections.Generic.List<WorkstationTaskData>?)>();

        Plugin.Logger.LogInfo($"[SupplyChain] [spike] === Candidate stations ({stations.Count}) ===");
        for (int i = 0; i < stations.Count; i++)
        {
            var entry = stations[i];
            var ws = entry.Ws;
            string cls = Common.NativeClassName(ws);

            Il2CppSystem.Collections.Generic.List<WorkstationTaskData>? datas = null;
            try { datas = ws.GetWorkstationTaskDatas(); } catch { }
            int taskCount = datas != null ? datas.Count : 0;

            Plugin.Logger.LogInfo($"[SupplyChain] [spike]   [{i}] station='{entry.StructureName}' class='{cls}' taskCount={taskCount}");

            result.Add((entry, cls, datas));

            if (datas == null) continue;
            int cap = Math.Min(taskCount, 12);
            for (int t = 0; t < cap; t++)
            {
                WorkstationTaskData? task = null;
                try { task = datas[t]; } catch { }
                if (task == null) continue;

                string itemName = RankActuator.SafeTaskItemName(task);
                int prio = -1; bool pinned = false;
                try { prio = task.priority; } catch { }
                try { pinned = task.pinnedTask; } catch { }

                Plugin.Logger.LogInfo($"[SupplyChain] [spike]     [{t}] item='{itemName}' priority={prio} pinned={pinned}");
            }
        }

        return result;
    }

    // Selection order (first match wins): CraftingStation with >= 2 tasks, then CookingStation
    // with >= 2 tasks, then ANY class with >= 2 tasks. A single task can't demonstrate ordering
    // vs. value, so single-task stations are never auto-picked (force one via SpikeStationFilter).
    // Also skips any station already in RankActuator.BoostedStationKeys (v0.3.0 mutual exclusion).
    private static StationEntry? AutoSelectStation(
        List<(StationEntry Entry, string Class, Il2CppSystem.Collections.Generic.List<WorkstationTaskData>? Datas)> candidates,
        out Il2CppSystem.Collections.Generic.List<WorkstationTaskData>? datas,
        out string autoClass)
    {
        foreach (var prefix in new[] { "CraftingStation", "CookingStation" })
        {
            foreach (var c in candidates)
            {
                if (c.Datas != null && c.Datas.Count >= 2 && c.Class.StartsWith(prefix, StringComparison.Ordinal)
                    && !RankActuator.BoostedStationKeys.Contains(c.Entry.PosKey))
                {
                    datas = c.Datas;
                    autoClass = c.Class;
                    return c.Entry;
                }
            }
        }

        foreach (var c in candidates)
        {
            if (c.Datas != null && c.Datas.Count >= 2 && !RankActuator.BoostedStationKeys.Contains(c.Entry.PosKey))
            {
                datas = c.Datas;
                autoClass = c.Class;
                return c.Entry;
            }
        }

        datas = null;
        autoClass = "?";
        return null;
    }
}
