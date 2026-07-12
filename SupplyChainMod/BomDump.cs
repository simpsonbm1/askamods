using System;
using System.Collections.Generic;
using System.Text;
using SSSGame;
using SSSGame.AI;
using SandSailorStudio.Inventory;

namespace SupplyChainMod;

// BomDump (Phase 0, read-only, BEST-EFFORT feasibility probe) — for each CraftingStation, for each
// of its task datas' items, calls KnowsBlueprintForItem and tries to log the recipe manifest via
// ItemManifest.CreateFromBlueprint. Per the delegation spec this is a feasibility probe, not a
// load-bearing feature: any part that doesn't compile cleanly is reported as a blocker rather than
// pursued further.
//
// Identifies CraftingStations via a FRESH StationWalker.FindComponent<CraftingStation>() walk
// (Unity's own native GetComponent<T> resolution) rather than downcasting an existing
// Workstation-typed StationEntry.Ws — a plain `as CraftingStation` on an already-obtained
// Workstation-declared wrapper would LIE (project-wide gotcha: managed casts lie for interop
// objects materialized under a base declared type).
//
// v0.1.2 — run 2 confirmed CraftingStation.KnowsBlueprintForItem (the station-level probe below,
// unchanged since v0.1.1) returns false + null blueprint for EVERY valid item on all 3 crafting
// stations tested — its semantics are evidently NOT "can this station craft X". Added a SECOND,
// more promising path: the crafting TABLES (CraftInteraction._craftingTables on the station), each
// with its OWN _localBlueprints dictionary (bpId -> CraftBlueprintInfo) and its OWN
// KnowsBlueprintForItem, probed independently below (DumpCraftingTables/DumpCraftingTable). The
// crafting-table CraftBlueprintInfo values are ALREADY correctly typed (no box-and-rewrap needed —
// unlike the station-level bp.info path, which returns the generic base type ItemInfo).
internal static class BomDump
{
    internal static void Dump(string trigger)
    {
        Plugin.Logger.LogInfo($"[SupplyChain] === BomDump start (trigger={trigger}) ===");

        var structures = StationWalker.ResolveStructures();
        int stationCount = 0;

        foreach (var s in structures)
        {
            UnityEngine.Transform root;
            try { root = s.transform; } catch { continue; }

            CraftingStation? cs;
            try { cs = StationWalker.FindComponent<CraftingStation>(root); } catch { cs = null; }
            if (cs == null) continue;

            stationCount++;
            try { DumpStation(cs, Common.SafeName(s)); }
            catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] BomDump station '{Common.SafeName(s)}' error: {ex}"); }
        }

        Plugin.Logger.LogInfo($"[SupplyChain] === BomDump end (trigger={trigger}, craftingStations={stationCount}) ===");
    }

    private static void DumpStation(CraftingStation cs, string structureName)
    {
        string stationClass = Common.NativeClassName(cs);

        Il2CppSystem.Collections.Generic.List<WorkstationTaskData>? datas = null;
        try { datas = cs.GetWorkstationTaskDatas(); } catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] BomDump GetWorkstationTaskDatas error: {ex}"); }
        if (datas == null)
        {
            Plugin.Logger.LogInfo($"[SupplyChain] [bom] station='{structureName}' class='{stationClass}': GetWorkstationTaskDatas() returned null.");
            return;
        }

        int n = datas.Count;
        Plugin.Logger.LogInfo($"[SupplyChain] [bom] station='{structureName}' class='{stationClass}': {n} task data(s) to probe.");

        // d. station-level probe, unchanged since v0.1.1 (kept per the coordinator's spec — "keep
        // the existing station-level probe lines unchanged"). Also collects the resolved task items
        // into taskItems so the v0.1.2 table-level probe (c, below) can reuse the exact same items
        // without re-walking task datas.
        var taskItems = new List<(int index, string taskType, ItemInfo item)>();

        for (int i = 0; i < n; i++)
        {
            WorkstationTaskData? task = null;
            try { task = datas[i]; } catch { }
            if (task == null) continue;

            string taskType = "?";
            try { taskType = task.GetIl2CppType().FullName; } catch { }

            ItemInfo? item = null;
            try { item = task.itemInfoQuantity?.itemInfo; } catch { }
            if (item == null)
            {
                Plugin.Logger.LogInfo($"[SupplyChain] [bom]   task[{i}] type='{taskType}': itemInfoQuantity/item is null — not probed (no single item on this task).");
                continue;
            }

            taskItems.Add((i, taskType, item));

            try { DumpBlueprintForItem(cs, stationClass, item, structureName, i, taskType); }
            catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] BomDump item '{Common.SafeItemName(item)}' error: {ex}"); }
        }

        // a-c. v0.1.2 table-level probe.
        try { DumpCraftingTables(cs, structureName, taskItems); }
        catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] DumpCraftingTables('{structureName}') error: {ex}"); }
    }

    // a. station-level table/blueprint counts.
    private static void DumpCraftingTables(CraftingStation cs, string structureName, List<(int index, string taskType, ItemInfo item)> taskItems)
    {
        int knownBlueprintsCount = -1, craftingTablesCount = -1, craftingProjectsCount = -1;
        Il2CppSystem.Collections.Generic.List<CraftInteraction>? tables = null;

        try { knownBlueprintsCount = cs._knownBlueprints != null ? cs._knownBlueprints.Count : -1; } catch { }
        try { tables = cs._craftingTables; craftingTablesCount = tables != null ? tables.Count : -1; } catch { }
        try { craftingProjectsCount = cs.craftingProjects != null ? cs.craftingProjects.Count : -1; } catch { }

        Plugin.Logger.LogInfo(
            $"[SupplyChain] [bom] station='{structureName}': _knownBlueprints={knownBlueprintsCount} " +
            $"_craftingTables={craftingTablesCount} craftingProjects={craftingProjectsCount}");

        if (tables == null) return;

        int tableCount = tables.Count;
        for (int t = 0; t < tableCount; t++)
        {
            // CraftInteraction comes straight out of a List<CraftInteraction> indexer — already
            // correctly typed (no base-declared-type cast involved), so the "managed casts lie"
            // gotcha and its Il2CppObjectBase boxing workaround don't apply to obtaining `table`
            // itself. Common.NativeClassName's internal `is Il2CppObjectBase` check still applies
            // the boxing pattern where it's actually needed (native class-name lookups).
            CraftInteraction? table = null;
            try { table = tables[t]; } catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] _craftingTables[{t}] indexer error: {ex}"); continue; }
            if (table == null) continue;

            try { DumpCraftingTable(table, t, structureName, taskItems); }
            catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] DumpCraftingTable[{t}] error: {ex}"); }
        }
    }

    // b + c. one crafting table: its own blueprint dictionary (recipe dump, capped at 20 entries to
    // bound log volume) and its own KnowsBlueprintForItem for every station task item (compared
    // against the station-level result logged by DumpBlueprintForItem for the same task index).
    private static void DumpCraftingTable(CraftInteraction table, int tableIndex, string structureName, List<(int index, string taskType, ItemInfo item)> taskItems)
    {
        string tableClass = Common.NativeClassName(table);

        var localBlueprints = table._localBlueprints;
        int lbCount = localBlueprints != null ? localBlueprints.Count : -1;
        Plugin.Logger.LogInfo($"[SupplyChain] [bom]   table[{tableIndex}] class='{tableClass}' station='{structureName}': _localBlueprints={lbCount}");

        // v0.1.3 — collected while walking the (capped) dictionary below so the pointer-identity
        // scan (c) can reuse the exact same capped set instead of re-walking the dictionary.
        const int cap = 20;
        var cappedEntries = new List<(int bpId, CraftBlueprintInfo bpInfo)>();

        if (localBlueprints != null)
        {
            int logged = 0;
            foreach (var kv in localBlueprints)
            {
                if (logged >= cap)
                {
                    Plugin.Logger.LogInfo($"[SupplyChain] [bom]   table[{tableIndex}]: capping at {cap} blueprint(s) logged (of {lbCount}).");
                    break;
                }
                logged++;

                int bpId = kv.Key;
                CraftBlueprintInfo? bpInfo = kv.Value;
                if (bpInfo != null) cappedEntries.Add((bpId, bpInfo));

                // CraftBlueprintInfo values here are ALREADY correctly typed (the dictionary's own
                // declared value type) — no box-and-rewrap needed, unlike the station-level bp.info
                // path (DumpBlueprintForItem) which only has the generic base type ItemInfo.
                string itemName = "?";
                try { itemName = bpInfo != null ? Common.SafeItemName(bpInfo.GetItemInfo()) : "?"; } catch { }

                if (bpInfo == null)
                {
                    Plugin.Logger.LogInfo($"[SupplyChain] [bom]     table[{tableIndex}] blueprint id={bpId}: null CraftBlueprintInfo.");
                    continue;
                }

                ItemManifest? manifest = null;
                try { manifest = ItemManifest.CreateFromBlueprint(bpInfo); }
                catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] table[{tableIndex}] blueprint id={bpId} CreateFromBlueprint error: {ex}"); }

                if (manifest == null)
                {
                    Plugin.Logger.LogInfo($"[SupplyChain] [bom]     table[{tableIndex}] blueprint id={bpId} item='{itemName}': CreateFromBlueprint returned null.");
                    continue;
                }

                var items = manifest.GetItems();
                int mn = items != null ? items.Count : 0;
                var sb = new StringBuilder();
                for (int i = 0; i < mn; i++)
                {
                    try
                    {
                        var iq = items![i];
                        if (sb.Length > 0) sb.Append(", ");
                        sb.Append(Common.SafeItemName(iq.itemInfo)).Append(" x").Append(iq.quantity);
                    }
                    catch { }
                }

                Plugin.Logger.LogInfo($"[SupplyChain] [bom]     table[{tableIndex}] blueprint id={bpId} item='{itemName}': recipe ({mn}): {(sb.Length > 0 ? sb.ToString() : "<empty>")}");
            }
        }

        // c. table-level KnowsBlueprintForItem, for every station task item — logged next to the
        // station-level result (same task index) so the two semantics can be compared directly.
        // v0.1.3 — ALSO scans cappedEntries for a name match and, if found, compares native pointer
        // identity between the task item's ItemInfo and the matched blueprint's produced ItemInfo:
        // run 2 (v0.1.2) showed KnowsBlueprintForItem returning false+null even when the SAME
        // table's _localBlueprints demonstrably held the item's blueprint (AnvilInteraction / Iron
        // Bar), so this distinguishes "different native ItemInfo instance" from "method semantics
        // are just something else entirely".
        foreach (var (taskIndex, taskType, item) in taskItems)
        {
            bool tableKnown;
            CraftBlueprint tableBp;
            try { tableKnown = table.KnowsBlueprintForItem(item, out tableBp); }
            catch (Exception ex)
            {
                Plugin.Logger.LogError($"[SupplyChain] table[{tableIndex}] KnowsBlueprintForItem('{Common.SafeItemName(item)}') error: {ex}");
                continue;
            }

            bool tableBpIsNull = tableBp == null;
            Plugin.Logger.LogInfo(
                $"[SupplyChain] [bom]   table[{tableIndex}] task[{taskIndex}] type='{taskType}' item='{Common.SafeItemName(item)}': " +
                $"table-level knowsBlueprint={tableKnown} blueprintIsNull={tableBpIsNull}");

            try { LogPointerIdentityIfMatched(tableIndex, taskIndex, item, cappedEntries); }
            catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] table[{tableIndex}] task[{taskIndex}] pointer-identity scan error: {ex}"); }
        }
    }

    // v0.1.3 — pointer-identity diagnostic (change 2). Scans the already-capped blueprint entries
    // for the FIRST one whose produced item has the same NAME as the task item; if found, logs the
    // matched bpId plus both ItemInfo wrappers' raw native pointers and whether they're the SAME
    // native instance. Both ItemInfo and CraftBlueprintInfo.GetItemInfo()'s result run through the
    // unity-libs UnityEngine.Object stub at compile time (project-wide gotcha) — Common.GetPointer
    // does the box-to-Il2CppObjectBase read, same as Common.NativeClassName/PointerHex.
    private static void LogPointerIdentityIfMatched(int tableIndex, int taskIndex, ItemInfo item, List<(int bpId, CraftBlueprintInfo bpInfo)> cappedEntries)
    {
        string taskItemName = Common.SafeItemName(item);

        foreach (var (bpId, bpInfo) in cappedEntries)
        {
            ItemInfo? bpItemInfo = null;
            try { bpItemInfo = bpInfo.GetItemInfo(); } catch { }
            if (bpItemInfo == null) continue;
            if (Common.SafeItemName(bpItemInfo) != taskItemName) continue;

            IntPtr taskPtr = Common.GetPointer(item);
            IntPtr bpPtr = Common.GetPointer(bpItemInfo);
            bool samePointer = taskPtr != IntPtr.Zero && taskPtr == bpPtr;

            Plugin.Logger.LogInfo(
                $"[SupplyChain] [bom]   table[{tableIndex}] task[{taskIndex}] pointer-identity: matched bpId={bpId} " +
                $"taskItem.Pointer=0x{taskPtr.ToString("X")} blueprint.GetItemInfo().Pointer=0x{bpPtr.ToString("X")} samePointer={samePointer}");
            return; // first match only
        }
    }

    private static void DumpBlueprintForItem(CraftingStation cs, string stationClass, ItemInfo item, string structureName, int taskIndex, string taskType)
    {
        bool itemIsNull = item == null;
        bool known;
        CraftBlueprint bp;
        try { known = cs.KnowsBlueprintForItem(item, out bp); }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"[SupplyChain] KnowsBlueprintForItem('{Common.SafeItemName(item)}') error: {ex}");
            return;
        }

        bool bpIsNull = bp == null;
        // Unambiguous per-probe input/output line — logged EVERY time, not just on failure, so the
        // false-for-everything anomaly can be diagnosed from the next run: which stations, which
        // items, and whether the out-param came back null even when `known` is somehow true.
        Plugin.Logger.LogInfo(
            $"[SupplyChain] [bom]   task[{taskIndex}] type='{taskType}' probe: station='{structureName}' class='{stationClass}' " +
            $"item='{Common.SafeItemName(item)}' itemIsNull={itemIsNull} knowsBlueprint={known} blueprintIsNull={bpIsNull}");

        if (!known || bp == null)
        {
            return; // inputs/outputs already logged above unambiguously — nothing more to probe
        }

        // bp.info is declared ItemInfo (Blueprint : Item, Item.info : ItemInfo) but the real native
        // object is a CraftBlueprintInfo (: BlueprintInfo : ItemInfo) — managed casts lie here, so
        // rewrap via the native pointer to the compile-time type ItemManifest.CreateFromBlueprint
        // actually needs (BlueprintInfo). This is the probe's feasibility-critical step.
        ItemInfo? bpItemInfo = null;
        try { bpItemInfo = bp.info; } catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] bp.info error: {ex}"); }

        string nativeType = Common.NativeClassName(bpItemInfo, out var ptr);
        if (bpItemInfo == null || ptr == IntPtr.Zero)
        {
            Plugin.Logger.LogInfo($"[SupplyChain] [bom]   task[{taskIndex}] station='{structureName}' item='{Common.SafeItemName(item)}': blueprint native type='{nativeType}' — could not resolve bp.info pointer; BLOCKED (feasibility probe stops here).");
            return;
        }

        BlueprintInfo? bpInfo = null;
        try { bpInfo = new BlueprintInfo(ptr); }
        catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] BlueprintInfo rewrap error: {ex}"); }

        if (bpInfo == null)
        {
            Plugin.Logger.LogInfo($"[SupplyChain] [bom]   task[{taskIndex}] station='{structureName}' item='{Common.SafeItemName(item)}': blueprint native type='{nativeType}' — BlueprintInfo rewrap failed; BLOCKED (feasibility probe stops here).");
            return;
        }

        ItemManifest? manifest = null;
        try { manifest = ItemManifest.CreateFromBlueprint(bpInfo); }
        catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] ItemManifest.CreateFromBlueprint error: {ex}"); }

        if (manifest == null)
        {
            Plugin.Logger.LogInfo($"[SupplyChain] [bom]   task[{taskIndex}] station='{structureName}' item='{Common.SafeItemName(item)}': blueprint native type='{nativeType}' — CreateFromBlueprint returned null; BLOCKED.");
            return;
        }

        var items = manifest.GetItems();
        int mn = items != null ? items.Count : 0;
        var sb = new StringBuilder();
        for (int i = 0; i < mn; i++)
        {
            try
            {
                var iq = items![i];
                if (sb.Length > 0) sb.Append(", ");
                sb.Append(Common.SafeItemName(iq.itemInfo)).Append(" x").Append(iq.quantity);
            }
            catch { }
        }

        Plugin.Logger.LogInfo($"[SupplyChain] [bom]   task[{taskIndex}] station='{structureName}' item='{Common.SafeItemName(item)}': recipe ({mn}): {(sb.Length > 0 ? sb.ToString() : "<empty>")}");
    }
}
