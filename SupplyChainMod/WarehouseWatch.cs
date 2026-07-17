using System;
using System.Collections.Generic;
using System.Diagnostics;
using SandSailorStudio.Inventory;
using SSSGame;
using SSSGame.AI;
using SSSGame.Network;

namespace SupplyChainMod;

// WarehouseWatch (Phase 2a, v0.4.0, READ-ONLY) — warehouse (ResourceStorage) allotment
// diagnostics. Fed by the storage-full widget events (WidgetPatches), polled from
// SupplyChainTracker's master tick and the F9 full-dump path. No game-state writes — observes and
// logs only, same as every other Phase 0/2a component.
//
// v0.14.0 — the clog VERDICT A/B and STALL toast/log vocabulary is retired (BudgetPlane's HOG/
// BLOCKAGE/SHADOWED lines now carry the storage-full signal directly via a `distress=storage-full`
// tag). DetectClogs keeps only: the storage-full registry (Add/RemoveStorageFullComplaint feed,
// IsStorageFullActive — BudgetPlane's distress-tag source) and the dominant-item composition scan
// that feeds MetabolicPlane samples. The minimal VERDICT-A detection (dominant item's warehouse
// allotment fully met) survives solely to call ClogController.NoteVerdictA, and only when
// EnableClogController is explicitly re-enabled (default false as of v0.14.0 — the quota-raise
// lever it fed was retired 2026-07-14); no toast/log fires from this path any more.
internal static class WarehouseWatch
{
    private sealed class StorageFullEntry
    {
        public string StructureName = "?";
        public DateTime LastSeenUtc;
    }

    internal sealed class AllotmentRow
    {
        public string Warehouse = "?";
        public string Item = "?";
        public int Qty;
        public int Stored;
        public bool Met;
        public int Rank;
        public int MaxQuota;
        public string SupplyOwner = "?";
        public int Gathered = -1;
        public int Room = -1;
        public string PosKey = "?";
        // v0.8.0 (Phase 2d) — true-warehouse discriminator (mirrors WarehouseTasks.HasTaskContainer)
        // and the item's physical max warehouse-wide capacity, read only when EnableBudgetPlane is on.
        public bool HasTaskContainer;
        public int MaxCapacity = -1;
        // v0.11.0 (Phase2d classifier v3) — the PHYSICAL container this row resolves to (pointer
        // hex via storageSupply.interaction.Container, the ContainerProbe-proven accessor; "" when
        // unresolved), and the row's own ItemInfo. Both read only when EnableBudgetPlane is on. Info
        // is an interop wrapper but AllotmentRow instances live only within one poll's call stack
        // (a fresh List<AllotmentRow> built and discarded per Poll() call, never held statically) —
        // legal under the project's "wrappers only within a single call stack" rule.
        public string ContainerKey = "";
        public ItemInfo? Info;
    }

    // v0.11.0 — per-poll, per-container snapshot (Part B). Built fresh inside Poll() (never a
    // static field) and passed down the SAME call stack into BudgetPlane.Evaluate — legal to hold
    // the live ItemContainer reference here for exactly that reason (never survives past the poll).
    internal sealed class ContainerSnapshot
    {
        public float FillRatio = -1f;
        public int Capacity = -1;
        public ItemContainer Container = null!;
    }

    // ── Storage-full registry (fed by WidgetPatches) ────────────────────────────────────────────
    private static readonly Dictionary<string, StorageFullEntry> _storageFull = new();

    // ── Per-world caches ─────────────────────────────────────────────────────────────────────────
    private static readonly HashSet<string> _probed = new();
    private static readonly Dictionary<string, bool> _metCache = new();
    private static bool _firstPollDone;

    private const double StaleStorageFullMinutes = 10.0;

    internal static void NoteStorageFull(string posKey, string name)
    {
        try
        {
            _storageFull[posKey] = new StorageFullEntry { StructureName = name, LastSeenUtc = DateTime.UtcNow };
        }
        catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] WarehouseWatch.NoteStorageFull error: {ex}"); }
    }

    internal static void NoteStorageFullCleared(string posKey)
    {
        try { _storageFull.Remove(posKey); }
        catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] WarehouseWatch.NoteStorageFullCleared error: {ex}"); }
    }

    // v0.6.0 (Phase 2c) — accessor for ClogController: is the given station currently registered as
    // storage-full (widget-fed) within the last withinSeconds? Used both as a Pending eligibility gate
    // and a Boosting exit condition ("clog-cleared").
    internal static bool IsStorageFullRecent(string posKey, double withinSeconds)
    {
        try
        {
            if (_storageFull.TryGetValue(posKey, out var full))
                return (DateTime.UtcNow - full.LastSeenUtc).TotalSeconds <= withinSeconds;
        }
        catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] WarehouseWatch.IsStorageFullRecent error: {ex}"); }
        return false;
    }

    // Active = present in the registry and within the same 10-min staleness window the detector
    // uses. LastSeenUtc only refreshes on complaint ADD, so tight freshness windows starve
    // persistent non-cycling complaints — gate on this, never on IsStorageFullRecent with a
    // short window.
    internal static bool IsStorageFullActive(string posKey)
        => IsStorageFullRecent(posKey, StaleStorageFullMinutes * 60.0);

    internal static void NoteWorldLeft()
    {
        _storageFull.Clear();
        _probed.Clear();
        _metCache.Clear();
        _firstPollDone = false;
    }

    // ── Main poll (master tick + F9 full-dump path) ─────────────────────────────────────────────
    internal static void Poll(List<StationEntry> stations, bool fullTable)
    {
        if (!Plugin.EnableWarehouseWatch.Value) return;

        var sw = Stopwatch.StartNew();
        bool effectiveFullTable = fullTable || !_firstPollDone;
        int warehouseCount = 0;

        try
        {
            var allowedClasses = ParseClassList(Plugin.WarehouseClassList.Value);
            var warehouses = new List<StationEntry>();
            foreach (var entry in stations)
            {
                string cls = "?";
                try { cls = Common.NativeClassName(entry.Ws); } catch { }
                if (allowedClasses.Contains(cls)) warehouses.Add(entry);
            }
            warehouseCount = warehouses.Count;

            var allRows = new List<AllotmentRow>();
            // v0.11.0 — transient per-poll container map (Part B). Only populated when
            // EnableBudgetPlane is on; never stored statically (see ContainerSnapshot's own note).
            var containerMap = new Dictionary<string, ContainerSnapshot>();
            foreach (var wh in warehouses)
            {
                try
                {
                    ProbeNetwork(wh);
                    BuildWarehouseRows(wh, effectiveFullTable, allRows, containerMap);
                }
                catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] WarehouseWatch warehouse '{wh.StructureName}' error: {ex}"); }
            }

            if (Plugin.EnableMetabolicPlane.Value)
            {
                try { FeedMetabolicSamples(allRows); }
                catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] WarehouseWatch FeedMetabolicSamples error: {ex}"); }
            }

            try { DetectClogs(stations, allRows); }
            catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] WarehouseWatch.DetectClogs error: {ex}"); }

            if (Plugin.EnableBudgetPlane.Value)
            {
                try { BudgetPlane.Evaluate(allRows, containerMap, stations, effectiveFullTable); }
                catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] WarehouseWatch BudgetPlane.Evaluate error: {ex}"); }
            }

            if (effectiveFullTable && Plugin.EnableContainerProbe.Value)
            {
                try { ContainerProbe.Dump(warehouses); }
                catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] WarehouseWatch ContainerProbe error: {ex}"); }
            }
        }
        catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] WarehouseWatch.Poll error: {ex}"); }
        finally
        {
            // Only latch once a warehouse was actually seen — a poll that ran before the station
            // list resolved (early after world load) must not consume the one-time full table.
            if (warehouseCount > 0) _firstPollDone = true;
            sw.Stop();
            double ms = sw.Elapsed.TotalMilliseconds;
            if (ms > 2.0 || effectiveFullTable)
                Plugin.Logger.LogInfo($"[SupplyChain] [Perf] WarehouseWatch poll: {warehouseCount} warehouse(s) in {ms:F1} ms");
        }
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

    // ── Metabolic plane feed (Phase 2c) — one sample per (warehouse, item) pair per poll ─────────
    // v0.9.0: also accumulates a settlement-wide per-item total (across every deduped group,
    // warehouse AND hut alike) and feeds it under an "ALL|"-prefixed key — BudgetPlane's demand
    // gate needs a settlement-wide net/churn signal, not just a single warehouse's. The "ALL|"
    // prefix can't collide with a real PosKey (those are formatted like "(98.90, 50.29, 432.86)").
    private static void FeedMetabolicSamples(List<AllotmentRow> allRows)
    {
        var seen = new HashSet<string>();
        var itemTotals = new Dictionary<string, int>();
        foreach (var row in allRows)
        {
            string key = row.PosKey + "|" + row.Item;
            if (!seen.Add(key)) continue;
            MetabolicPlane.NoteSample(key, row.Stored);

            itemTotals.TryGetValue(row.Item, out int existing);
            itemTotals[row.Item] = existing + row.Stored;
        }

        foreach (var kv in itemTotals)
        {
            MetabolicPlane.NoteSample("ALL|" + kv.Key, kv.Value);
        }
    }

    // ── Network component probe (Phase 2b fire-verification; one-time per world per warehouse) ──
    private static void ProbeNetwork(StationEntry entry)
    {
        if (_probed.Contains(entry.PosKey)) return;
        _probed.Add(entry.PosKey);

        try
        {
            object? found = null;

            try { var c = entry.Ws.gameObject.GetComponent<NetworkCompositeResourceStorage>(); if (c != null) found = c; } catch { }
            if (found == null)
            {
                try { var s = entry.Ws.gameObject.GetComponent<NetworkSimpleResourceStorage>(); if (s != null) found = s; } catch { }
            }
            if (found == null)
            {
                try { var c = StationWalker.FindComponent<NetworkCompositeResourceStorage>(entry.Ws.transform); if (c != null) found = c; } catch { }
            }
            if (found == null)
            {
                try { var s = StationWalker.FindComponent<NetworkSimpleResourceStorage>(entry.Ws.transform); if (s != null) found = s; } catch { }
            }

            string foundClass = found != null ? Common.NativeClassName(found) : "none";
            Plugin.Logger.LogInfo($"[SupplyChain] [warehouse] network probe '{entry.StructureName}': {foundClass}");
        }
        catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] WarehouseWatch network probe '{entry.StructureName}' error: {ex}"); }
    }

    // ── Allotment table build (per warehouse) ───────────────────────────────────────────────────
    private static void BuildWarehouseRows(StationEntry entry, bool fullTable, List<AllotmentRow> allRowsAccumulator,
        Dictionary<string, ContainerSnapshot> containerMap)
    {
        Il2CppSystem.Collections.Generic.List<WorkstationTaskData>? datas = null;
        try { datas = entry.Ws.GetWorkstationTaskDatas(); }
        catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] WarehouseWatch GetWorkstationTaskDatas '{entry.StructureName}' error: {ex}"); }
        if (datas == null) return;

        ItemCollection? inv = null;
        try { inv = entry.Ws.GetInventory(); } catch { }

        // Resolved once per warehouse per poll (not per-task) — feeds the per-supply
        // GetGatheredItemQuantity call below. May be null; every downstream use is null-guarded.
        ResourceStorage? rs = null;
        try { rs = entry.Ws.gameObject.GetComponent<ResourceStorage>(); } catch { }

        // v0.8.0 (Phase 2d) — per-warehouse-call cache so a multi-row item group (composite
        // warehouse, several sub-storage tasks for the same item) costs only ONE
        // GetMaximumStorageCapacity call; only populated when BudgetPlane is enabled.
        var maxCapCache = new Dictionary<string, int>();

        int nonStorageTasks = 0;
        int n = datas.Count;
        for (int i = 0; i < n; i++)
        {
            try
            {
                WorkstationTaskData? task = null;
                try { task = datas[i]; } catch { }
                if (task == null) continue;

                ResourceStorageTaskData? rst = null;
                try { rst = task.TryCast<ResourceStorageTaskData>(); } catch { }
                if (rst == null) { nonStorageTasks++; continue; }

                var iq = rst.itemInfoQuantity;
                if (iq == null) continue;
                var info = iq.itemInfo;
                if (info == null) continue;

                string itemName = Common.SafeItemName(info);
                int qty = 0; try { qty = iq.quantity; } catch { }
                int rank = -1; try { rank = rst.priority; } catch { }
                int stored = 0;
                try { stored = inv != null ? inv.GetItemQuantity(info) : 0; } catch { }
                bool met = stored >= qty;

                int maxQuota = -1;
                string supplyOwner = "?";
                StorageSupply? supply = null;
                try { supply = rst.storageSupply; } catch { }
                if (supply != null)
                {
                    try { maxQuota = supply.taskMaxQuota; } catch { }
                    try { supplyOwner = Common.SafeName(supply.OwnerStructure); } catch { }
                }

                // Per-supply "gathered" count (game's own bookkeeping, exact semantics TBD —
                // logged raw to learn them) and the warehouse's remaining item-agnostic capacity.
                int gathered = -1;
                if (rs != null && supply != null && info != null)
                {
                    try { gathered = rs.GetGatheredItemQuantity(supply, info); } catch { }
                }
                int room = -1;
                try { room = inv != null ? inv.GetTotalRemainingCapacity(info) : -1; } catch { }

                // v0.8.0 (Phase 2d) — true-warehouse discriminator (mirrors WarehouseTasks.
                // HasTaskContainer) and the item's physical max capacity, only read when
                // BudgetPlane is enabled (the capacity call is otherwise skipped entirely).
                bool hasTaskContainer = false;
                try { hasTaskContainer = supply != null && supply.taskContainer != null; } catch { }

                int maxCapacity = -1;
                if (Plugin.EnableBudgetPlane.Value)
                {
                    if (!maxCapCache.TryGetValue(itemName, out maxCapacity))
                    {
                        maxCapacity = -1;
                        try { maxCapacity = inv != null ? inv.GetMaximumStorageCapacity(info, false) : -1; } catch { }
                        maxCapCache[itemName] = maxCapacity;
                    }
                }

                // v0.11.0 (Phase2d classifier v3, Part B) — resolve the PHYSICAL container via
                // storageSupply.interaction.Container (the ContainerProbe/EvictionSpike-proven
                // accessor — mirrors ContainerProbe's exact member usage and Il2CppObjectBase
                // boxing pattern via Common.PointerHex). Only when BudgetPlane is enabled; one
                // capacity/fill read per DISTINCT container per poll (containerMap dedupes by key).
                string containerKey = "";
                if (Plugin.EnableBudgetPlane.Value)
                {
                    try
                    {
                        StorageInteraction? interaction = supply != null ? supply.interaction : null;
                        ItemContainer? container = interaction != null ? interaction.Container : null;
                        if (container != null)
                        {
                            string key = Common.PointerHex(container);
                            if (key != "n/a")
                            {
                                containerKey = key;
                                if (!containerMap.ContainsKey(key))
                                {
                                    var snap = new ContainerSnapshot { Container = container };
                                    try { snap.Capacity = container.capacity; } catch { }
                                    try { snap.FillRatio = container.GetFillRatio(); } catch { }
                                    containerMap[key] = snap;
                                }
                            }
                        }
                    }
                    catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] WarehouseWatch container resolve '{entry.StructureName}' error: {ex}"); }
                }

                allRowsAccumulator.Add(new AllotmentRow
                {
                    Warehouse = entry.StructureName,
                    Item = itemName,
                    Qty = qty,
                    Stored = stored,
                    Met = met,
                    Rank = rank,
                    MaxQuota = maxQuota,
                    SupplyOwner = supplyOwner,
                    Gathered = gathered,
                    Room = room,
                    PosKey = entry.PosKey,
                    HasTaskContainer = hasTaskContainer,
                    MaxCapacity = maxCapacity,
                    ContainerKey = containerKey,
                    Info = Plugin.EnableBudgetPlane.Value ? info : null,
                });

                string metKey = $"{entry.PosKey}|{itemName}|{i}";

                if (fullTable)
                {
                    Plugin.Logger.LogInfo(
                        $"[SupplyChain] [warehouse] '{entry.StructureName}' task[{i}] item='{itemName}' qty={qty} stored={stored} gathered={gathered} room={room} " +
                        $"met={met} rank={rank} maxQuota={maxQuota} supplyOwner='{supplyOwner}'");
                }
                else
                {
                    if (_metCache.TryGetValue(metKey, out bool prevMet) && prevMet != met)
                    {
                        Plugin.Logger.LogInfo(
                            $"[SupplyChain] [warehouse] '{entry.StructureName}' allotment '{itemName}' met {prevMet}->{met} (qty={qty} stored={stored} gathered={gathered} room={room})");
                    }
                }

                _metCache[metKey] = met;
            }
            catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] WarehouseWatch task[{i}] '{entry.StructureName}' error: {ex}"); }
        }

        // v0.9.1 — gated to fullTable only: the widened WarehouseClassList (HuntingStation/
        // FishingStation/CaveResourceStorage) adds ~20 structures/poll that may legitimately carry
        // non-ResourceStorageTaskData rows, which turned this into per-poll log spam.
        if (fullTable && nonStorageTasks > 0)
            Plugin.Logger.LogInfo($"[SupplyChain] [warehouse] '{entry.StructureName}' nonStorageTasks={nonStorageTasks}");
    }

    // ── Dominant-item composition scan (v0.14.0: clog VERDICT/STALL toast+log vocabulary retired) ──
    // Still runs for every registered storage-full station every poll to feed two things: (1) the
    // MetabolicPlane sample for the dominant item (kept — other diagnostics read this rate), and
    // (2) — only when EnableClogController is explicitly re-enabled — ClogController.NoteVerdictA,
    // the sole remaining consumer of a "dominant item's warehouse allotment is met" verdict. No
    // toast, no VERDICT/STALL log line fires from this path any more; BudgetPlane's HOG/BLOCKAGE/
    // SHADOWED lines now carry the storage-full signal instead, via a `distress=storage-full` tag
    // fed by IsStorageFullActive (kept, below).
    private static void DetectClogs(List<StationEntry> stations, List<AllotmentRow> allRows)
    {
        if (_storageFull.Count == 0) return;

        var now = DateTime.UtcNow;
        var keys = new List<string>(_storageFull.Keys);

        foreach (var posKey in keys)
        {
            try
            {
                if (!_storageFull.TryGetValue(posKey, out var full)) continue;
                if ((now - full.LastSeenUtc).TotalMinutes > StaleStorageFullMinutes) continue;

                StationEntry? station = null;
                foreach (var s in stations)
                {
                    if (s.PosKey == posKey) { station = s; break; }
                }
                if (station == null) continue;

                ItemCollection? inv = null;
                try { inv = station.Ws.GetInventory(); } catch { }
                if (inv == null) continue;

                var infos = inv.GetItemInfos();
                int n = infos != null ? infos.Count : 0;
                if (n == 0) continue;

                long total = 0;
                string dominantName = "?";
                int dominantQty = -1;
                for (int i = 0; i < n; i++)
                {
                    try
                    {
                        var info = infos![i];
                        if (info == null) continue;
                        int qty = 0;
                        try { qty = inv.GetItemQuantity(info); } catch { }
                        total += qty;
                        if (qty > dominantQty) { dominantQty = qty; dominantName = Common.SafeItemName(info); }
                    }
                    catch { }
                }
                if (total <= 0 || dominantQty <= 0) continue;

                int sharePercent = (int)(dominantQty * 100L / total);
                if (sharePercent < Plugin.ClogDominantSharePercent.Value) continue;

                if (Plugin.EnableMetabolicPlane.Value)
                {
                    try { MetabolicPlane.NoteSample(posKey + "|" + dominantName, dominantQty); }
                    catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] WarehouseWatch metabolic sample error: {ex}"); }
                }

                if (!Plugin.EnableClogController.Value) continue; // retired lever, off unless deliberately re-enabled

                var matches = new List<AllotmentRow>();
                foreach (var r in allRows) { if (r.Item == dominantName) matches.Add(r); }
                if (matches.Count == 0) continue;

                bool allMet = matches.TrueForAll(r => r.Met);
                if (!allMet) continue;

                string stationName = full.StructureName;
                try { ClogController.NoteVerdictA(posKey, stationName, dominantName, dominantQty); }
                catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] WarehouseWatch ClogController.NoteVerdictA error: {ex}"); }
            }
            catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] WarehouseWatch clog-check '{posKey}' error: {ex}"); }
        }
    }
}
