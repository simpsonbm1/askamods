using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using SSSGame;
using SSSGame.AI;
using UnityEngine;

namespace SupplyChainMod;

// RankActuator (v0.3.0) — the shared list-move actuator, extracted from ActuationSpike (v0.2.3) so
// BOTH the manual spike (ActuationSpike) and the automated controller (SupplyController) drive the
// SAME write path and the SAME mutual-exclusion registry (BoostedStationKeys). No functional change
// from the v0.2.3 spike logic — ApplyRank/ProbeNetworkComponent/TryHostUpdateNetworkWorkstations/
// FindTaskIndex/FindStation/SafeTaskItemName/LogTaskList moved here verbatim and made internal. The
// one deliberate change: internal log lines here use the "[rank]" tag instead of "[spike]", since
// this code now runs under two different callers and "[spike]" would mislabel controller-triggered
// actuation.
//
// Priority is a RANK POSITION re-derived from list order (test run 1, v0.2.3): "changing priority"
// means reordering the LIVE ws._taskDatas backing list, not writing a value — see ApplyRank.
internal static class RankActuator
{
    // PosKeys with an active boost, shared across the spike and the controller. Both consult this
    // before choosing a new target so the two actuators never fight over the same station. Cleared
    // on world-leave by BOTH ActuationSpike.NoteWorldLeft and SupplyController.NoteWorldLeft
    // (redundant but harmless — it's the same static set either way).
    internal static readonly HashSet<string> BoostedStationKeys = new();

    // v0.2.3: LIST-MOVE actuation (see the class header for the test-run rationale). Priority is a
    // rank position re-derived from list order, so "changing priority" means reordering the LIVE
    // ws._taskDatas backing list, not writing a value. Returns a short path descriptor for the log.
    internal static string ApplyRank(Workstation ws, WorkstationTaskData task, int fromIndex, int toIndex)
    {
        // 1. Setter probe (instrumentation only) — tells us whether the setter does ANYTHING
        // before RefreshTaskDataPriorities gets a chance to squash it (test run 1: it doesn't).
        try
        {
            task.SetPriority(toIndex);
            int readback = -1;
            try { readback = task.priority; } catch { }
            Plugin.Logger.LogInfo($"[SupplyChain] [rank] setter probe: SetPriority({toIndex}) readback={readback}");
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"[SupplyChain] [rank] setter probe error: {ex}");
        }

        // 2. The move — operate on the LIVE backing list, not a fresh GetWorkstationTaskDatas()
        // snapshot copy.
        Il2CppSystem.Collections.Generic.List<WorkstationTaskData>? list = null;
        try { list = ws._taskDatas; }
        catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] [rank] _taskDatas read error: {ex}"); }
        if (list == null)
        {
            Plugin.Logger.LogWarning("[SupplyChain] [rank] _taskDatas is null — aborting this apply.");
            return "abort:_taskDatas-null";
        }

        try
        {
            bool sameList = Common.GetPointer(list) == Common.GetPointer(ws.GetWorkstationTaskDatas());
            Plugin.Logger.LogInfo(
                $"[SupplyChain] [rank] _taskDatas vs GetWorkstationTaskDatas same-native-list={sameList} " +
                $"(_taskDatas={Common.PointerHex(list)} fresh={Common.PointerHex(ws.GetWorkstationTaskDatas())})");
        }
        catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] [rank] pointer-identity probe error: {ex}"); }

        try
        {
            list.RemoveAt(fromIndex);
            int clampedTo = Math.Clamp(toIndex, 0, list.Count);
            list.Insert(clampedTo, task);
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"[SupplyChain] [rank] LOUD: list move (fromIndex={fromIndex} toIndex={toIndex}) FAILED: {ex}");
            try
            {
                int recoverIndex = Math.Min(fromIndex, list.Count);
                list.Insert(recoverIndex, task);
                Plugin.Logger.LogError($"[SupplyChain] [rank] LOUD: recovered — re-inserted task at index {recoverIndex} so it is not lost from the list; order may be off.");
            }
            catch (Exception ex2)
            {
                Plugin.Logger.LogError($"[SupplyChain] [rank] LOUD: recovery insert ALSO FAILED — task may be lost from the list: {ex2}");
            }
        }

        // 3. Let the game renumber priority=index from the new list order.
        try { ws.RefreshTaskDataPriorities(); }
        catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] [rank] RefreshTaskDataPriorities error: {ex}"); }

        // 4. Readback — locate the task's new position by pointer identity (managed reference
        // equality doesn't reliably work through interop), falling back to item-name match.
        try
        {
            var after = ws.GetWorkstationTaskDatas();
            if (after != null)
            {
                IntPtr taskPtr = Common.GetPointer(task);
                int foundIndex = -1;
                int foundPriority = -1;

                for (int i = 0; i < after.Count; i++)
                {
                    WorkstationTaskData? candidate = null;
                    try { candidate = after[i]; } catch { continue; }
                    if (candidate == null) continue;
                    if (Common.GetPointer(candidate) == taskPtr)
                    {
                        foundIndex = i;
                        try { foundPriority = candidate.priority; } catch { }
                        break;
                    }
                }

                if (foundIndex < 0)
                {
                    string wantName = SafeTaskItemName(task);
                    for (int i = 0; i < after.Count; i++)
                    {
                        WorkstationTaskData? candidate = null;
                        try { candidate = after[i]; } catch { continue; }
                        if (candidate == null) continue;
                        if (SafeTaskItemName(candidate) == wantName)
                        {
                            foundIndex = i;
                            try { foundPriority = candidate.priority; } catch { }
                            break;
                        }
                    }
                }

                Plugin.Logger.LogInfo($"[SupplyChain] [rank] post-move index={foundIndex} priority={foundPriority}");
            }
        }
        catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] [rank] post-move readback error: {ex}"); }

        // 5. Network sync attempt — best-effort HostUpdateTasks only. No Rpc_ReorderTasks in this
        // version (one actuator per experiment).
        TryHostUpdateNetworkWorkstations(ws);

        var probed = ProbeNetworkComponent(ws, out string via);
        string probedClass = probed != null ? Common.NativeClassName(probed) : "n/a";
        Plugin.Logger.LogInfo($"[SupplyChain] [rank] network probe: NetworkCraftingStation found={probed != null} via={via} class='{probedClass}'");
        if (probed != null)
        {
            try
            {
                probed.HostUpdateTasks();
                Plugin.Logger.LogInfo("[SupplyChain] [rank] network probe: HostUpdateTasks() on probed component succeeded.");
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError($"[SupplyChain] [rank] network probe: HostUpdateTasks() on probed component failed: {ex}");
            }
        }

        return $"reorder({fromIndex}->{toIndex})";
    }

    internal static void TryHostUpdateNetworkWorkstations(Workstation ws)
    {
        Il2CppReferenceArray<SSSGame.Network.INetworkWorkstation>? arr = null;
        try { arr = ws.NetworkWorkstations; }
        catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] [rank] NetworkWorkstations read error: {ex}"); return; }
        if (arr == null || arr.Length == 0)
        {
            Plugin.Logger.LogInfo("[SupplyChain] [rank] NetworkWorkstations is null/empty — skipping HostUpdateTasks over the array.");
            return;
        }

        for (int i = 0; i < arr.Length; i++)
        {
            SSSGame.Network.INetworkWorkstation? elem;
            try { elem = arr[i]; }
            catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] [rank] networkWorkstation[{i}] read error: {ex}"); continue; }
            if (elem == null) continue;

            string cls = Common.NativeClassName(elem);
            try
            {
                elem.HostUpdateTasks();
                Plugin.Logger.LogInfo($"[SupplyChain] [rank] HostUpdateTasks[{i}] class='{cls}' succeeded.");
            }
            catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] [rank] HostUpdateTasks[{i}] class='{cls}' error: {ex}"); }
        }
    }

    // Diagnostic probe (read-only apart from the caller's own HostUpdateTasks call): looks for a
    // NetworkCraftingStation on the station's own GameObject, then up to 4 parents, then a
    // downward child walk (StationWalker.FindComponent). Only NetworkCraftingStation for now (the
    // test station is a CraftingStation) — generalize to other station classes later if useful.
    // Singular generic GetComponent<T>() works through interop; plural/non-generic forms do not
    // (project-wide gotcha) — this probe sticks to the singular generic throughout.
    internal static SSSGame.Network.NetworkCraftingStation? ProbeNetworkComponent(Workstation ws, out string via)
    {
        via = "none";

        Transform t;
        try { t = ws.transform; } catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] [rank] network probe: transform read error: {ex}"); return null; }

        SSSGame.Network.NetworkCraftingStation? found = null;

        try { found = ws.GetComponent<SSSGame.Network.NetworkCraftingStation>(); } catch { found = null; }
        if (found != null) { via = "gameObject"; return found; }

        Transform? node = t;
        for (int depth = 1; depth <= 4; depth++)
        {
            try { node = node != null ? node.parent : null; } catch { node = null; }
            if (node == null) break;

            try { found = node.GetComponent<SSSGame.Network.NetworkCraftingStation>(); } catch { found = null; }
            if (found != null) { via = $"parent[{depth}]"; return found; }
        }

        try { found = StationWalker.FindComponent<SSSGame.Network.NetworkCraftingStation>(t); } catch { found = null; }
        if (found != null) { via = "childWalk"; return found; }

        return null;
    }

    // ── Shared lookups/logging ──────────────────────────────────────────────────────────────────
    internal static StationEntry? FindStation(List<StationEntry> stations, string posKey, string stationName)
    {
        foreach (var entry in stations)
        {
            if (entry.PosKey == posKey) return entry;
        }
        foreach (var entry in stations)
        {
            if (entry.StructureName == stationName) return entry;
        }
        return null;
    }

    // Locates a task by index-if-item-matches, else scans all tasks for the first item-name match.
    internal static int FindTaskIndex(Il2CppSystem.Collections.Generic.List<WorkstationTaskData>? datas, int preferredIndex, string itemName, out WorkstationTaskData? task)
    {
        task = null;
        if (datas == null) return -1;

        if (preferredIndex >= 0 && preferredIndex < datas.Count)
        {
            WorkstationTaskData? candidate = null;
            try { candidate = datas[preferredIndex]; } catch { }
            if (candidate != null && SafeTaskItemName(candidate) == itemName)
            {
                task = candidate;
                return preferredIndex;
            }
        }

        for (int i = 0; i < datas.Count; i++)
        {
            WorkstationTaskData? candidate = null;
            try { candidate = datas[i]; } catch { continue; }
            if (candidate == null) continue;
            if (SafeTaskItemName(candidate) == itemName)
            {
                task = candidate;
                return i;
            }
        }

        return -1;
    }

    internal static string SafeTaskItemName(WorkstationTaskData task)
    {
        try
        {
            var iq = task.itemInfoQuantity;
            if (iq != null) return Common.SafeItemName(iq.itemInfo);
        }
        catch { }
        return "?";
    }

    internal static void LogTaskList(string label, StationEntry target, Il2CppSystem.Collections.Generic.List<WorkstationTaskData> datas)
    {
        Plugin.Logger.LogInfo($"[SupplyChain] [rank] {label} station='{target.StructureName}' taskCount={datas.Count}");
        for (int i = 0; i < datas.Count; i++)
        {
            WorkstationTaskData? task = null;
            try { task = datas[i]; } catch { }
            if (task == null) continue;

            string itemName = SafeTaskItemName(task);
            int prio = -1; bool pinned = false;
            try { prio = task.priority; } catch { }
            try { pinned = task.pinnedTask; } catch { }

            Plugin.Logger.LogInfo($"[SupplyChain] [rank]   [{i}] item='{itemName}' priority={prio} pinned={pinned}");
        }
    }
}
