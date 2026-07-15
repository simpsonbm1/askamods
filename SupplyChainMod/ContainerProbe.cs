using System;
using System.Collections.Generic;
using System.Diagnostics;
using SandSailorStudio.Inventory;
using SSSGame;
using SSSGame.AI;
using UnityEngine;

namespace SupplyChainMod;

// ContainerProbe (v0.9.2, Phase2dProbe, READ-ONLY) — per-structure inventory of PHYSICAL storage
// containers: which item task rows resolve to which physical container, each container's
// capacity/fill/accepted item types, and a hierarchy-walk cross-check. Built to replace SupplyOwner
// display-name inference (proven wrong in-game 2026-07-15 — hut rows' SupplyOwner is the hut
// itself, which fabricated shared pools out of separate physical bins) with ground truth the future
// hog classifier can trust to distinguish SHARED from DEDICATED containers. Fires once per full
// dump (F9 / first poll) via WarehouseWatch. No writes of any kind; no wrapper caching across
// polls — every object reference here (task datas, StorageSupply, StorageInteraction, ItemContainer,
// ItemInfo) lives only within a single Dump() call, mirroring WarehouseTasks.WarehouseTaskRow's Rst.
//
// DEVIATION FROM PLAN (found during implementation, Cecil-verified against the live interop
// assembly — see cecil_idea12_evict_spike_out.txt line 211 and cecil_idea12_evict_spike2_out.txt):
// the plan's "primary" resolution path, ResourceStorageTaskData.storageSupply.taskContainer, is NOT
// an ItemContainer — it is typed SSSGame.Network.NetworkTaskContainer (a network task-list sync
// object) and exposes none of the ItemContainer capacity/accepted-types surface. It is retained here
// ONLY as the existing true-warehouse/hut classifier (the same boolean
// WarehouseTasks.HasTaskContainer already reads via != null). The SOLE path to the physical
// ItemContainer, for BOTH true-warehouse and hut rows, is storageSupply.interaction
// (StorageInteraction) -> .Container — the exact accessor EvictionSpike v0.7.0 fire-verified for
// warehouse-row eviction. Below, "src=task" means "row classified true-warehouse (taskContainer !=
// null), container resolved via interaction.Container" and "src=inter" means "row classified hut
// (taskContainer null), same resolution accessor" — both go through the identical
// interaction->Container call, not two physically different container reads.
//
// Also: SandSailorStudio.Inventory.ItemContainer is a PLAIN class (base Il2CppSystem.Object, not a
// Component/MonoBehaviour, Cecil-confirmed — see OuthouseComposterMod's AcceptancePatches.cs header
// note) — GetComponent<ItemContainer>() does not compile. The hierarchy walk instead collects
// SandSailorStudio.Inventory.ItemContainerComponent (base UnityEngine.MonoBehaviour), reading its
// `.container : ItemContainer` property per node — the same component OuthouseComposterMod's
// ComposterDiag walker already uses.
internal static class ContainerProbe
{
    private sealed class ContainerAgg
    {
        public IntPtr Pointer;
        public ItemContainer Container = null!;
        public List<(string itemName, int rank)> Items = new();
        public string Src = "?"; // "task" | "inter" | "walk"
        public ItemInfo? SampleInfo;
    }

    private readonly struct StructureStats
    {
        public readonly int Containers;
        public readonly int MultiRow;
        public readonly int SingleRow;
        public readonly int UnresolvedRows;

        public StructureStats(int containers, int multiRow, int singleRow, int unresolvedRows)
        {
            Containers = containers;
            MultiRow = multiRow;
            SingleRow = singleRow;
            UnresolvedRows = unresolvedRows;
        }
    }

    internal static void Dump(List<StationEntry> warehouses)
    {
        try
        {
            var sw = Stopwatch.StartNew();
            int structures = 0, containers = 0, multiRow = 0, singleRow = 0, unresolvedRows = 0;

            foreach (var entry in warehouses)
            {
                try
                {
                    var stats = DumpStructure(entry);
                    structures++;
                    containers += stats.Containers;
                    multiRow += stats.MultiRow;
                    singleRow += stats.SingleRow;
                    unresolvedRows += stats.UnresolvedRows;
                }
                catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] [probe] structure '{entry.StructureName}' error: {ex}"); }
            }

            Plugin.Logger.LogInfo(
                $"[SupplyChain] [probe] TOTAL: structures={structures} containers={containers} " +
                $"multiRowContainers={multiRow} singleRowContainers={singleRow} unresolvedRows={unresolvedRows}");

            sw.Stop();
            Plugin.Logger.LogInfo($"[SupplyChain] [Perf] ContainerProbe dump: {sw.Elapsed.TotalMilliseconds:F1} ms");
        }
        catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] [probe] Dump error: {ex}"); }
    }

    private static StructureStats DumpStructure(StationEntry entry)
    {
        try
        {
            Il2CppSystem.Collections.Generic.List<WorkstationTaskData>? datas = null;
            try { datas = entry.Ws.GetWorkstationTaskDatas(); }
            catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] [probe] GetWorkstationTaskDatas '{entry.StructureName}' error: {ex}"); }
            if (datas == null) return new StructureStats(0, 0, 0, 0);

            string nativeClass = "?";
            try { nativeClass = Common.NativeClassName(entry.Ws); } catch { }

            var byPointer = new Dictionary<IntPtr, ContainerAgg>();
            var order = new List<IntPtr>();
            int rowCount = 0, viaTask = 0, viaInter = 0, unresolvedRows = 0;

            int n = datas.Count;
            for (int i = 0; i < n; i++)
            {
                try
                {
                    WorkstationTaskData? task = null;
                    try { task = datas[i]; } catch { continue; }
                    if (task == null) continue;

                    ResourceStorageTaskData? rst = null;
                    try { rst = task.TryCast<ResourceStorageTaskData>(); } catch { }
                    if (rst == null) continue; // skip null / non-storage tasks

                    rowCount++;

                    string itemName = "?";
                    ItemInfo? info = null;
                    try { info = rst.itemInfoQuantity.itemInfo; itemName = Common.SafeItemName(info); } catch { }

                    int rank = -1;
                    try { rank = rst.priority; } catch { }

                    StorageSupply? supply = null;
                    try { supply = rst.storageSupply; } catch { }

                    bool hasTaskContainer = false;
                    if (supply != null) { try { hasTaskContainer = supply.taskContainer != null; } catch { } }

                    StorageInteraction? interaction = null;
                    if (supply != null) { try { interaction = supply.interaction; } catch { } }

                    ItemContainer? container = null;
                    if (interaction != null) { try { container = interaction.Container; } catch { } }

                    if (container == null)
                    {
                        unresolvedRows++;
                        continue;
                    }

                    IntPtr ptr = Common.GetPointer(container);
                    if (ptr == IntPtr.Zero)
                    {
                        unresolvedRows++;
                        continue;
                    }

                    if (hasTaskContainer) viaTask++; else viaInter++;

                    if (!byPointer.TryGetValue(ptr, out var agg))
                    {
                        agg = new ContainerAgg
                        {
                            Pointer = ptr,
                            Container = container,
                            Src = hasTaskContainer ? "task" : "inter",
                            SampleInfo = info,
                        };
                        byPointer[ptr] = agg;
                        order.Add(ptr);
                    }
                    agg.Items.Add((itemName, rank));
                }
                catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] [probe] '{entry.StructureName}' task[{i}] error: {ex}"); }
            }

            // Hierarchy-walk cross-check (SINGULAR GetComponent<ItemContainerComponent>() per node —
            // ItemContainer itself is not a Component; see file-header deviation note).
            var walkFound = new List<ItemContainer>();
            try
            {
                Transform root = entry.Ws.transform;
                WalkForContainers(root, walkFound, 0);
            }
            catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] [probe] '{entry.StructureName}' hierarchy walk error: {ex}"); }

            int walkFoundCount = walkFound.Count;
            int walkExtra = 0;
            foreach (var wc in walkFound)
            {
                IntPtr wptr = Common.GetPointer(wc);
                if (wptr == IntPtr.Zero) continue;
                if (!byPointer.ContainsKey(wptr))
                {
                    walkExtra++;
                    var agg = new ContainerAgg { Pointer = wptr, Container = wc, Src = "walk" };
                    byPointer[wptr] = agg;
                    order.Add(wptr);
                }
            }

            Plugin.Logger.LogInfo(
                $"[SupplyChain] [probe] '{entry.StructureName}' ({nativeClass}): rows={rowCount} " +
                $"resolved={viaTask}/{viaInter}/{unresolvedRows} containers={byPointer.Count} " +
                $"walkFound={walkFoundCount} walkExtra={walkExtra}");

            int multiRow = 0, singleRow = 0;
            foreach (var ptr in order)
            {
                var agg = byPointer[ptr];
                if (agg.Items.Count >= 2) multiRow++;
                else if (agg.Items.Count == 1) singleRow++;
                LogContainerLine(agg);
            }

            return new StructureStats(byPointer.Count, multiRow, singleRow, unresolvedRows);
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"[SupplyChain] [probe] DumpStructure '{entry.StructureName}' error: {ex}");
            return new StructureStats(0, 0, 0, 0);
        }
    }

    // First runtime use of capacity/GetFillRatio/GetAcceptedItemTypes/CanStoreItemType (Cecil-
    // confirmed 2026-07-14, never yet called) — each in its OWN try/catch; a thrown exception is
    // logged as a finding (type+message), never hidden, and never aborts the rest of the line.
    private static void LogContainerLine(ContainerAgg agg)
    {
        try
        {
            string hex6 = LastSix(agg.Pointer);
            var container = agg.Container;

            int capacity = -1;
            try { capacity = container.capacity; }
            catch (Exception ex) { Plugin.Logger.LogInfo($"[SupplyChain] [probe]   container {hex6}: capacity read error {ex.GetType().Name}: {ex.Message}"); }

            float fill = -1f;
            try { fill = container.GetFillRatio(); }
            catch (Exception ex) { Plugin.Logger.LogInfo($"[SupplyChain] [probe]   container {hex6}: GetFillRatio error {ex.GetType().Name}: {ex.Message}"); }

            string acceptedStr = "acceptedTypes=UNAVAILABLE(not attempted)";
            try
            {
                var accepted = container.GetAcceptedItemTypes();
                if (accepted == null)
                {
                    acceptedStr = "acceptedTypes=UNAVAILABLE(null)";
                }
                else
                {
                    int cnt = accepted.Count;
                    var names = new List<string>();
                    int lim = Math.Min(cnt, 8);
                    for (int j = 0; j < lim; j++)
                    {
                        object? el = null;
                        try { el = accepted[j]; } catch { }
                        names.Add(DescribeAcceptedElement(el));
                    }
                    acceptedStr = $"acceptedTypes={cnt} [{string.Join(", ", names)}]";
                }
            }
            catch (Exception ex)
            {
                acceptedStr = $"acceptedTypes=UNAVAILABLE({ex.GetType().Name}: {ex.Message})";
            }

            string canStoreStr = "";
            ItemInfo? sample = agg.SampleInfo;
            if (sample != null)
            {
                try
                {
                    bool canStore = container.CanStoreItemType(sample);
                    canStoreStr = $" canStore({Common.SafeItemName(sample)})={canStore}";
                }
                catch (Exception ex) { canStoreStr = $" canStore=ERROR({ex.GetType().Name})"; }
            }

            if (agg.Src == "walk")
            {
                Plugin.Logger.LogInfo(
                    $"[SupplyChain] [probe]   container {hex6} src=walk norows: capacity={capacity} fill={fill:F2} {acceptedStr}");
            }
            else
            {
                string itemsStr = FormatItems(agg.Items);
                Plugin.Logger.LogInfo(
                    $"[SupplyChain] [probe]   container {hex6} src={agg.Src}: items=[{itemsStr}] capacity={capacity} fill={fill:F2} {acceptedStr}{canStoreStr}");
            }
        }
        catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] [probe] LogContainerLine error: {ex}"); }
    }

    private static string DescribeAcceptedElement(object? el)
    {
        if (el == null) return "null";
        try
        {
            if (el is ItemInfo info) return Common.SafeItemName(info);
        }
        catch { }
        try { return Common.NativeClassName(el); } catch { }
        try { return el.ToString() ?? "?"; } catch { return "?"; }
    }

    private static string FormatItems(List<(string itemName, int rank)> items)
    {
        const int cap = 12;
        var parts = new List<string>();
        int lim = Math.Min(items.Count, cap);
        for (int i = 0; i < lim; i++) parts.Add($"{items[i].itemName}(r{items[i].rank})");
        string s = string.Join(", ", parts);
        if (items.Count > cap) s += $", +{items.Count - cap} more";
        return s;
    }

    private static string LastSix(IntPtr ptr)
    {
        try
        {
            string hex = ptr.ToString("X");
            return hex.Length <= 6 ? hex : hex.Substring(hex.Length - 6);
        }
        catch { return "?"; }
    }

    // Manual hierarchy walk collecting EVERY ItemContainerComponent under root (mirrors
    // StationWalker.FindComponent's per-node singular GetComponent<T>() + child recursion, but
    // collects ALL matches instead of stopping at the first). ItemContainer itself is a plain class
    // (base Il2CppSystem.Object) — not a Component — so GetComponent<ItemContainer>() would not
    // compile; ItemContainerComponent (base UnityEngine.MonoBehaviour) is the correct target,
    // exposing the ItemContainer via its `.container` property.
    private static void WalkForContainers(Transform node, List<ItemContainer> results, int depth)
    {
        if (node == null || depth > 12) return;

        try
        {
            var icc = node.GetComponent<ItemContainerComponent>();
            if (icc != null)
            {
                ItemContainer? c = null;
                try { c = icc.container; } catch { }
                if (c != null) results.Add(c);
            }
        }
        catch { }

        int childCount;
        try { childCount = node.childCount; } catch { return; }
        for (int i = 0; i < childCount; i++)
        {
            Transform? child = null;
            try { child = node.GetChild(i); } catch { }
            if (child == null) continue;
            WalkForContainers(child, results, depth + 1);
        }
    }
}
