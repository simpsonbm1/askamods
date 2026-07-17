using System;
using System.Collections.Generic;
using SSSGame;
using SSSGame.AI;
using SSSGame.Network;

namespace SupplyChainMod;

// TierWriter (v0.17.0) — the shared warehouse-tier write/readback/snapshot core, extracted from
// EvictionSpike's v0.7.0 Shift+F6 tier-test spike (the same extraction pattern RankActuator was
// pulled from ActuationSpike, v0.3.0 — see RankActuator.cs's header) so BOTH EvictionSpike (the
// manual spike) and TierCaseController (the automated v0.17.0 arming layer) drive the SAME proven
// write path: SetPriority(newRank) direct value write + HostUpdateTasks() network sync + readback,
// plus the whole-warehouse rank snapshot/diff used to detect a write landing on the WRONG row.
// EvictionSpike's own RPC-first experiment (Rpc_ChangeTaskPriority, proven inert — see the lever
// map in DEMAND_MODEL_PLAN.md) stays inside EvictionSpike itself; only the direct-write core here
// is shared. No functional change to EvictionSpike's observable behavior from this extraction.
//
// Never cache interop wrappers (Workstation/ResourceStorageTaskData/NetworkXxxResourceStorage)
// across ticks (project-wide gotcha) — every method here re-resolves fresh from its StationEntry/
// task-list argument and returns only plain values (the one WorkstationTaskData/ResourceStorageTaskData
// used mid-call is local to that single call, never stored statically).
internal static class TierWriter
{
    // Probe order mirrors QuotaActuator.ProbeAndPushNetwork / EvictionSpike's own v0.7.0 probe: own
    // GameObject composite storage, then own GameObject simple storage, then a child walk for each.
    internal static (NetworkCompositeResourceStorage? composite, NetworkSimpleResourceStorage? simple, string via) ProbeTierComponent(StationEntry entry)
    {
        NetworkCompositeResourceStorage? composite = null;
        NetworkSimpleResourceStorage? simple = null;

        try { composite = entry.Ws.gameObject.GetComponent<NetworkCompositeResourceStorage>(); } catch { }
        if (composite != null) return (composite, null, "gameObject/composite");

        try { simple = entry.Ws.gameObject.GetComponent<NetworkSimpleResourceStorage>(); } catch { }
        if (simple != null) return (null, simple, "gameObject/simple");

        try { composite = StationWalker.FindComponent<NetworkCompositeResourceStorage>(entry.Ws.transform); } catch { }
        if (composite != null) return (composite, null, "childWalk/composite");

        try { simple = StationWalker.FindComponent<NetworkSimpleResourceStorage>(entry.Ws.transform); } catch { }
        if (simple != null) return (null, simple, "childWalk/simple");

        return (null, null, "none");
    }

    private static void HostUpdateViaProbe(StationEntry station)
    {
        var (composite, simple, via) = ProbeTierComponent(station);
        try
        {
            if (composite != null) composite.HostUpdateTasks();
            else simple?.HostUpdateTasks();
            Plugin.Logger.LogInfo($"[SupplyChain] [tier] HostUpdateTasks() via {via} succeeded.");
        }
        catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] [tier] HostUpdateTasks() via {via} failed: {ex}"); }
    }

    // Direct value write (the proven path — RPC is inert, see the lever map in DEMAND_MODEL_PLAN.md):
    // SetPriority + a network HostUpdateTasks sync + readback. Returns the readback value (-1 if
    // unreadable) so callers can log/verify it themselves without a second native read.
    internal static int DirectWrite(StationEntry station, ResourceStorageTaskData rst, int newRank)
    {
        try { rst.SetPriority(newRank); }
        catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] [tier] SetPriority({newRank}) error: {ex}"); }

        HostUpdateViaProbe(station);

        int readback = -1;
        try { readback = rst.priority; } catch { }
        Plugin.Logger.LogInfo($"[SupplyChain] [tier] direct write: SetPriority({newRank}) readback={readback}");
        return readback;
    }

    // Plain-value snapshot of every row (any item, any rank) on the given warehouse posKey — used
    // to detect a write landing on the WRONG row (DiffAndFindWrongRow below). Built fresh from
    // WarehouseTasks.BuildRows every call — never held statically by the caller.
    internal static List<(int taskIndex, string itemName, int rank)> SnapshotWarehouse(List<StationEntry> stations, string posKey)
    {
        var snapshot = new List<(int taskIndex, string itemName, int rank)>();
        try
        {
            var allRows = WarehouseTasks.BuildRows(stations);
            foreach (var r in allRows)
            {
                if (r.Warehouse.PosKey != posKey) continue;
                snapshot.Add((r.TaskIndex, r.ItemName, r.Rank));
            }
        }
        catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] [tier] SnapshotWarehouse error: {ex}"); }
        return snapshot;
    }

    // Diffs a boost-time snapshot against the CURRENT task list (by taskIndex), logging every
    // changed row, and reports the first row that flipped to targetRank that ISN'T ourTaskIndex
    // (the write-hit-the-wrong-row failure mode).
    internal static (int taskIndex, string itemName, int origRank)? DiffAndFindWrongRow(
        List<(int taskIndex, string itemName, int rank)> snapshot, int ourTaskIndex, int targetRank,
        Il2CppSystem.Collections.Generic.List<WorkstationTaskData> datas)
    {
        (int taskIndex, string itemName, int origRank)? wrongRow = null;

        foreach (var snap in snapshot)
        {
            if (snap.taskIndex < 0 || snap.taskIndex >= datas.Count) continue;

            WorkstationTaskData? task = null;
            try { task = datas[snap.taskIndex]; } catch { continue; }
            if (task == null) continue;

            ResourceStorageTaskData? rst = null;
            try { rst = task.TryCast<ResourceStorageTaskData>(); } catch { }
            if (rst == null) continue;

            int curRank = -1;
            try { curRank = rst.priority; } catch { }
            if (curRank == snap.rank) continue;

            string curItem = RankActuator.SafeTaskItemName(rst);
            Plugin.Logger.LogInfo($"[SupplyChain] [tier] rank change: task[{snap.taskIndex}] '{curItem}' {snap.rank}->{curRank}");

            if (wrongRow == null && snap.taskIndex != ourTaskIndex && snap.rank != targetRank && curRank == targetRank)
            {
                wrongRow = (snap.taskIndex, curItem, snap.rank);
            }
        }

        return wrongRow;
    }

    // Reverts a wrong-row hit back to its snapshotted rank, by index into the CURRENT task list.
    internal static void RevertWrongRow(StationEntry station, Il2CppSystem.Collections.Generic.List<WorkstationTaskData> datas, int wrongIndex, int snapshotRank)
    {
        if (wrongIndex < 0 || wrongIndex >= datas.Count)
        {
            Plugin.Logger.LogError("[SupplyChain] [tier] LOUD: wrong-row revert — index out of range.");
            return;
        }

        WorkstationTaskData? task = null;
        try { task = datas[wrongIndex]; } catch { }
        ResourceStorageTaskData? thatRst = null;
        if (task != null) { try { thatRst = task.TryCast<ResourceStorageTaskData>(); } catch { } }
        if (thatRst == null)
        {
            Plugin.Logger.LogError("[SupplyChain] [tier] LOUD: wrong-row revert — could not re-resolve task.");
            return;
        }

        DirectWrite(station, thatRst, snapshotRank);
    }
}
