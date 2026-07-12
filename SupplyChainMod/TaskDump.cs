using System;
using System.Collections.Generic;
using SSSGame.AI;

namespace SupplyChainMod;

// TaskDump (Phase 0, read-only) — on hotkey, and once automatically ~60s after a world loads
// (SupplyChainTracker drives both triggers). Walks every resolved station and logs its
// WorkstationTaskDatas: priority, pinnedTask, isRemovable, itemInfoQuantity, VillagersInCharge
// count, plus the FiltersTaskData/CraftingStationTaskData subclass details.
internal static class TaskDump
{
    internal static void Dump(string trigger, List<StationEntry> stations)
    {
        Plugin.Logger.LogInfo($"[SupplyChain] === TaskDump start (trigger={trigger}, stations={stations.Count}) ===");

        foreach (var entry in stations)
        {
            try { DumpStation(entry); }
            catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] TaskDump station '{entry.StructureName}' error: {ex}"); }
        }

        Plugin.Logger.LogInfo($"[SupplyChain] === TaskDump end (trigger={trigger}) ===");
    }

    private static void DumpStation(StationEntry entry)
    {
        var ws = entry.Ws;

        string wsClass = Common.NativeClassName(ws);
        int taskCount = -1;
        try { taskCount = ws.GetTaskDataCount(); } catch { }

        Plugin.Logger.LogInfo($"[SupplyChain] [taskdump] station='{entry.StructureName}' class='{wsClass}' posKey={entry.PosKey} taskCount={taskCount}");

        Il2CppSystem.Collections.Generic.List<WorkstationTaskData>? datas = null;
        try { datas = ws.GetWorkstationTaskDatas(); } catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] GetWorkstationTaskDatas error: {ex}"); }
        if (datas == null) return;

        int n = datas.Count;
        for (int i = 0; i < n; i++)
        {
            WorkstationTaskData? task = null;
            try { task = datas[i]; } catch { }
            if (task == null) continue;

            try { DumpTaskData(task, i); }
            catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] DumpTaskData[{i}] error: {ex}"); }
        }
    }

    private static void DumpTaskData(WorkstationTaskData task, int index)
    {
        // WorkstationTaskData is Il2CppSystem.Object-derived — GetIl2CppType() compiles/works
        // directly here (unlike Workstation/Structure, which are UnityEngine.Object-derived).
        string nativeType = "?";
        try { nativeType = task.GetIl2CppType().FullName; } catch { }

        int priority = -1; bool pinned = false, removable = false;
        try { priority = task.priority; } catch { }
        try { pinned = task.pinnedTask; } catch { }
        try { removable = task.isRemovable; } catch { }

        string itemQtyStr = "null";
        try
        {
            var iq = task.itemInfoQuantity;
            if (iq != null) itemQtyStr = $"{Common.SafeItemName(iq.itemInfo)} x{iq.quantity}";
        }
        catch { }

        string qtyRange = "?";
        try { qtyRange = task.GetQuantityRange().ToString(); } catch { }

        int villagerCount = -1;
        try { var v = task.VillagersInCharge; villagerCount = v != null ? v.Count : 0; } catch { }

        Plugin.Logger.LogInfo(
            $"[SupplyChain] [taskdump]   task[{index}] type='{nativeType}' priority={priority} pinned={pinned} " +
            $"removable={removable} itemInfoQuantity={itemQtyStr} quantityRange={qtyRange} villagersInCharge={villagerCount}");

        // TryCast<T>() works here — WorkstationTaskData and its subclasses are all
        // Il2CppSystem.Object-derived (confirmed via Cecil sweep).
        try
        {
            var filters = task.TryCast<FiltersTaskData>();
            if (filters != null)
            {
                int taskId = -1;
                try { taskId = filters.TaskID; } catch { }
                Plugin.Logger.LogInfo($"[SupplyChain] [taskdump]     FiltersTaskData TaskID={taskId}");

                var list = filters.FilterItemPriorityList;
                int fn = list != null ? list.Count : 0;
                for (int i = 0; i < fn; i++)
                {
                    try
                    {
                        var item = list![i];
                        int fp = -1;
                        try { fp = filters.GetFilterItemPriority(item); } catch { }
                        Plugin.Logger.LogInfo($"[SupplyChain] [taskdump]       filter[{i}] item='{Common.SafeItemName(item)}' priority={fp}");
                    }
                    catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] FiltersTaskData filter[{i}] error: {ex}"); }
                }
            }
        }
        catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] FiltersTaskData probe error: {ex}"); }

        try
        {
            var crafting = task.TryCast<CraftingStationTaskData>();
            if (crafting != null)
            {
                bool refurbish = false;
                try { refurbish = crafting.refurbishOption; } catch { }
                Plugin.Logger.LogInfo($"[SupplyChain] [taskdump]     CraftingStationTaskData (crafting task) refurbishOption={refurbish}");
            }
        }
        catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] CraftingStationTaskData probe error: {ex}"); }
    }
}
