using System;
using System.Collections.Generic;
using SandSailorStudio.Inventory;
using SSSGame;
using SSSGame.AI;

namespace SupplyChainMod;

// WarehouseTasks (v0.6.0, Phase 2c) — the warehouse-row scan hoisted out of QuotaSpike.cs so
// ClogController can share it without duplicating the WarehouseClassList filter / BoostedStationKeys
// lock-skip / non-pinned ResourceStorageTaskData row logic. No behavior change from QuotaSpike's own
// copy — BuildWarehouseTaskRows is renamed BuildRows; everything else moved verbatim.
internal static class WarehouseTasks
{
    // A single ResourceStorageTaskData row found during a warehouse scan. Rst is held only for the
    // duration of the single call that consumes this list — never stored in _active or any static
    // field (project-wide gotcha: no per-world interop wrappers cached across ticks).
    internal sealed class WarehouseTaskRow
    {
        public StationEntry Warehouse = null!;
        public int TaskIndex;
        public ResourceStorageTaskData Rst = null!;
        public string ItemName = "?";
        public int Qty;
        public int Rank;
        public string SupplyOwner = "-";
        // v0.5.2 — true-warehouse discriminator (Common.SafeName-style guarded probe):
        // storageSupply.taskContainer != null on a real composite/simple warehouse row; null on a
        // gathering-hut self-storage row, which is never a drain target.
        public bool HasTaskContainer;
    }

    private static HashSet<string> ParseClassList(string raw)
    {
        var set = new HashSet<string>();
        if (string.IsNullOrEmpty(raw)) return set;
        foreach (var part in raw.Split(','))
        {
            var t = part.Trim();
            if (t.Length > 0) set.Add(t);
        }
        return set;
    }

    // Scans every station matching WarehouseClassList (by default skipping stations already boosted
    // by another actuator) for non-pinned ResourceStorageTaskData rows. Item name/qty/rank/
    // supply-owner are read out as plain values; ItemInfo itself is never stored on the row (read
    // fresh from Rst.itemInfoQuantity.itemInfo wherever needed) — only Rst (the task wrapper) is
    // held, and only for the lifetime of the single call that consumes this list. Includes BOTH
    // true-warehouse and gathering-hut self-storage rows (HasTaskContainer distinguishes them);
    // callers filter to true-warehouse rows themselves.
    //
    // v0.17.2 — `includeBoosted` (default false, preserving every existing caller's behavior): a
    // caller resolving a row it ITSELF already holds boosted (a revert, a settlement-wide High-row
    // tally that should count its own active bumps) must pass true, or the row becomes permanently
    // unresolvable — the exact bug behind run 1's "every revert failed once" and the Rope/Linen
    // Thread blackout at Improved Warehouse 3 (a stuck Onion claim hid the WHOLE station from every
    // scan, DEMAND_MODEL_PLAN.md "Armed run 1 finding 2"). Target/candidate SELECTION (a NEW hold's
    // target search, the receiving-warehouse scan) must keep the default false — never pick a
    // station another hold already claims.
    internal static List<WarehouseTaskRow> BuildRows(List<StationEntry> stations, bool includeBoosted = false)
    {
        var result = new List<WarehouseTaskRow>();
        try
        {
            var allowedClasses = ParseClassList(Plugin.WarehouseClassList.Value);
            foreach (var entry in stations)
            {
                string cls = "?";
                try { cls = Common.NativeClassName(entry.Ws); } catch { }
                if (!allowedClasses.Contains(cls)) continue;
                if (!includeBoosted && RankActuator.BoostedStationKeys.Contains(entry.PosKey)) continue;

                Il2CppSystem.Collections.Generic.List<WorkstationTaskData>? datas = null;
                try { datas = entry.Ws.GetWorkstationTaskDatas(); }
                catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] [warehouse-tasks] GetWorkstationTaskDatas '{entry.StructureName}' error: {ex}"); continue; }
                if (datas == null) continue;

                for (int i = 0; i < datas.Count; i++)
                {
                    WorkstationTaskData? task = null;
                    try { task = datas[i]; } catch { continue; }
                    if (task == null) continue;

                    ResourceStorageTaskData? rst = null;
                    try { rst = task.TryCast<ResourceStorageTaskData>(); } catch { }
                    if (rst == null) continue;

                    bool pinned = false;
                    try { pinned = rst.pinnedTask; } catch { }
                    if (pinned) continue;

                    var iq = rst.itemInfoQuantity;
                    if (iq == null) continue;
                    var info = iq.itemInfo;
                    if (info == null) continue;

                    string itemName = Common.SafeItemName(info);
                    int qty = 0; try { qty = iq.quantity; } catch { }
                    int rank = -1; try { rank = rst.priority; } catch { }
                    string supplyOwner = GetSupplyOwner(rst);
                    bool hasTaskContainer = HasTaskContainer(rst);

                    result.Add(new WarehouseTaskRow
                    {
                        Warehouse = entry,
                        TaskIndex = i,
                        Rst = rst,
                        ItemName = itemName,
                        Qty = qty,
                        Rank = rank,
                        SupplyOwner = supplyOwner,
                        HasTaskContainer = hasTaskContainer,
                    });
                }
            }
        }
        catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] [warehouse-tasks] BuildRows error: {ex}"); }
        return result;
    }

    // internal — both QuotaSpike (via FindQuotaTask below) and ClogController resolve supply-owner
    // strings through this same probe.
    internal static string GetSupplyOwner(ResourceStorageTaskData rst)
    {
        try
        {
            var supply = rst.storageSupply;
            if (supply != null) return Common.SafeName(supply.OwnerStructure);
        }
        catch { }
        return "-";
    }

    // v0.5.2 — true-warehouse discriminator (confirmed in-game 2026-07-13, Phase 2a/TaskDump):
    // storageSupply.taskContainer != null on a real composite/simple warehouse row; null on a
    // gathering-hut self-storage row. Same guarded-probe idiom as TaskDump.cs's hasTaskContainer read.
    private static bool HasTaskContainer(ResourceStorageTaskData rst)
    {
        try
        {
            var supply = rst.storageSupply;
            if (supply != null) return supply.taskContainer != null;
        }
        catch { }
        return false;
    }

    // Loose owner comparison: "-" on either side means unresolvable/unknown, so it can't
    // discriminate — treated as a match, falling through to item-name-only matching upstream.
    private static bool OwnerMatches(string a, string b)
    {
        if (a == "-" || b == "-") return true;
        return a == b;
    }

    private static ResourceStorageTaskData? TryGetRst(Il2CppSystem.Collections.Generic.List<WorkstationTaskData> datas, int i)
    {
        WorkstationTaskData? task = null;
        try { task = datas[i]; } catch { return null; }
        if (task == null) return null;
        try { return task.TryCast<ResourceStorageTaskData>(); } catch { return null; }
    }

    // Locates the boosted task on a freshly-resolved warehouse. Tier A: preferredIndex, if it still
    // TryCasts and matches item (+owner, when both are resolvable). Tier B: first row matching
    // item+owner anywhere on the station. Tier C: first row matching item name alone. Returns the
    // found index (-1 if none) with the matching ResourceStorageTaskData via out rst.
    internal static int FindQuotaTask(Il2CppSystem.Collections.Generic.List<WorkstationTaskData>? datas, int preferredIndex, string itemName, string supplyOwner, out ResourceStorageTaskData? rst)
    {
        rst = null;
        if (datas == null) return -1;

        if (preferredIndex >= 0 && preferredIndex < datas.Count)
        {
            var candRst = TryGetRst(datas, preferredIndex);
            if (candRst != null && RankActuator.SafeTaskItemName(candRst) == itemName && OwnerMatches(supplyOwner, GetSupplyOwner(candRst)))
            {
                rst = candRst;
                return preferredIndex;
            }
        }

        for (int i = 0; i < datas.Count; i++)
        {
            var candRst = TryGetRst(datas, i);
            if (candRst == null) continue;
            if (RankActuator.SafeTaskItemName(candRst) != itemName) continue;
            if (!OwnerMatches(supplyOwner, GetSupplyOwner(candRst))) continue;
            rst = candRst;
            return i;
        }

        for (int i = 0; i < datas.Count; i++)
        {
            var candRst = TryGetRst(datas, i);
            if (candRst == null) continue;
            if (RankActuator.SafeTaskItemName(candRst) != itemName) continue;
            rst = candRst;
            return i;
        }

        return -1;
    }
}
