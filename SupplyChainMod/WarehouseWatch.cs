using System;
using System.Collections.Generic;
using System.Diagnostics;
using SandSailorStudio.Inventory;
using SSSGame;
using SSSGame.AI;
using SSSGame.Network;

namespace SupplyChainMod;

// WarehouseWatch (Phase 2a, v0.4.0, READ-ONLY) — warehouse (ResourceStorage) allotment
// diagnostics + a dry-run clog detector. Fed by the storage-full widget events (WidgetPatches),
// polled from SupplyChainTracker's master tick and the F9 full-dump path. No game-state writes —
// observes and logs only, same as every other Phase 0/2a component.
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

    private sealed class StallTrack
    {
        public int LastStored;
        public DateTime FirstSeenUtc;
        public DateTime LastChangeUtc;
    }

    // ── Storage-full registry (fed by WidgetPatches) ────────────────────────────────────────────
    private static readonly Dictionary<string, StorageFullEntry> _storageFull = new();

    // ── Per-world caches ─────────────────────────────────────────────────────────────────────────
    private static readonly HashSet<string> _probed = new();
    private static readonly Dictionary<string, bool> _metCache = new();
    private static readonly Dictionary<string, DateTime> _lastVerdictToast = new();
    // Keyed "stationPosKey|itemName" — tracks how long a "watching" (unmet allotment, haulers
    // should be draining) case has shown an unchanged warehouse stored-count, to discriminate
    // capacity-blocked (no room) from priority-shadowed (room exists, haulers just aren't going).
    private static readonly Dictionary<string, StallTrack> _stallTracks = new();
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
        _lastVerdictToast.Clear();
        _stallTracks.Clear();
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
                try { BudgetPlane.Evaluate(allRows, containerMap, effectiveFullTable); }
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

    // ── Dry-run clog detection ───────────────────────────────────────────────────────────────────
    private static void DetectClogs(List<StationEntry> stations, List<AllotmentRow> allRows)
    {
        if (_storageFull.Count == 0) return;

        var now = DateTime.UtcNow;
        var keys = new List<string>(_storageFull.Keys);

        foreach (var posKey in keys)
        {
            try
            {
                if (!_storageFull.TryGetValue(posKey, out var full)) { ClearStallTrack(posKey, null); continue; }
                if ((now - full.LastSeenUtc).TotalMinutes > StaleStorageFullMinutes) { ClearStallTrack(posKey, null); continue; }

                StationEntry? station = null;
                foreach (var s in stations)
                {
                    if (s.PosKey == posKey) { station = s; break; }
                }
                if (station == null) { ClearStallTrack(posKey, null); continue; }

                ItemCollection? inv = null;
                try { inv = station.Ws.GetInventory(); } catch { }
                if (inv == null) { ClearStallTrack(posKey, null); continue; }

                var infos = inv.GetItemInfos();
                int n = infos != null ? infos.Count : 0;
                if (n == 0) { ClearStallTrack(posKey, null); continue; }

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
                if (total <= 0 || dominantQty <= 0) { ClearStallTrack(posKey, null); continue; }

                int sharePercent = (int)(dominantQty * 100L / total);
                if (sharePercent < Plugin.ClogDominantSharePercent.Value) { ClearStallTrack(posKey, null); continue; }

                if (Plugin.EnableMetabolicPlane.Value)
                {
                    try { MetabolicPlane.NoteSample(posKey + "|" + dominantName, dominantQty); }
                    catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] WarehouseWatch metabolic sample error: {ex}"); }
                }

                var matches = new List<AllotmentRow>();
                foreach (var r in allRows) { if (r.Item == dominantName) matches.Add(r); }

                string stationName = full.StructureName;
                if (matches.Count == 0)
                {
                    ClearStallTrack(posKey, null);
                    Plugin.Logger.LogInfo(
                        $"[SupplyChain] [clog] VERDICT: '{stationName}' clogged by '{dominantName}' x{dominantQty} ({sharePercent}%) — NO warehouse allotment task for it");
                    MaybeToast(posKey, dominantName, $"Clog: '{stationName}' full of '{dominantName}' — no warehouse allotment");
                }
                else
                {
                    bool allMet = matches.TrueForAll(r => r.Met);
                    if (allMet)
                    {
                        ClearStallTrack(posKey, null);
                        string rowsStr = FormatGroupedRows(matches);
                        Plugin.Logger.LogInfo(
                            $"[SupplyChain] [clog] VERDICT: '{stationName}' clogged by '{dominantName}' x{dominantQty} ({sharePercent}%) — " +
                            $"warehouse allotment(s) met ({rowsStr}) — drain-boost candidate, delta={dominantQty}");
                        MaybeToast(posKey, dominantName, $"Clog: '{stationName}' full of '{dominantName}' — warehouse allotment met");

                        if (Plugin.EnableClogController.Value)
                        {
                            try { ClogController.NoteVerdictA(posKey, stationName, dominantName, dominantQty); }
                            catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] WarehouseWatch ClogController.NoteVerdictA error: {ex}"); }
                        }
                    }
                    else
                    {
                        AllotmentRow? unmet = null;
                        foreach (var r in matches) { if (!r.Met) { unmet = r; break; } }
                        Plugin.Logger.LogInfo(
                            $"[SupplyChain] [clog] '{stationName}' dominated by '{dominantName}' ({sharePercent}%) but an unmet allotment " +
                            $"exists ('{unmet!.Warehouse}' qty={unmet.Qty} stored={unmet.Stored}) — haulers should drain; watching.");

                        // Stall escalation: distinguishes "warehouse physically out of room" from
                        // "collect task priority-shadowed" for a persistently-unmet watching case.
                        string stallKey = $"{posKey}|{dominantName}";
                        ClearStallTrack(posKey, stallKey);

                        int watchStored = unmet!.Stored;
                        int watchRoom = unmet.Room;
                        if (!_stallTracks.TryGetValue(stallKey, out var track))
                        {
                            track = new StallTrack { LastStored = watchStored, FirstSeenUtc = now, LastChangeUtc = now };
                            _stallTracks[stallKey] = track;
                        }
                        else if (track.LastStored != watchStored)
                        {
                            track.LastStored = watchStored;
                            track.LastChangeUtc = now;
                        }

                        if ((now - track.LastChangeUtc).TotalMinutes >= Plugin.ClogStallMinutes.Value)
                        {
                            if (watchRoom == 0)
                            {
                                Plugin.Logger.LogInfo(
                                    $"[SupplyChain] [clog] STALL: '{stationName}' clogged by '{dominantName}' — unmet allotment NOT filling and " +
                                    $"warehouse has NO room for it ('{unmet.Warehouse}' qty={unmet.Qty} stored={unmet.Stored} room=0) — capacity-blocked");
                                MaybeToast(posKey, dominantName, $"Clog stall: '{stationName}' '{dominantName}' — warehouse out of room");
                            }
                            else
                            {
                                Plugin.Logger.LogInfo(
                                    $"[SupplyChain] [clog] STALL: '{stationName}' clogged by '{dominantName}' — unmet allotment NOT filling despite " +
                                    $"room ('{unmet.Warehouse}' qty={unmet.Qty} stored={unmet.Stored} room={watchRoom} rank={unmet.Rank}) — likely priority-shadowed");
                                MaybeToast(posKey, dominantName, $"Clog stall: '{stationName}' '{dominantName}' — haulers not draining (priority?)");
                            }
                            track.LastChangeUtc = now;
                        }
                    }
                }
            }
            catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] WarehouseWatch clog-check '{posKey}' error: {ex}"); }
        }
    }

    // Groups identical (Warehouse, Qty, Stored) rows into "N× '<W>' qty=Q stored=S gathered=G"
    // (comma-separated Gathered values when a group's rows disagree) — a composite warehouse can
    // hold multiple tasks for the same item, one per child sub-storage supply (confirmed in-game
    // 2026-07-13, v0.4.1).
    private static string FormatGroupedRows(List<AllotmentRow> rows)
    {
        var groups = new List<(string Warehouse, int Qty, int Stored, List<int> Gathereds)>();
        foreach (var r in rows)
        {
            bool found = false;
            for (int i = 0; i < groups.Count; i++)
            {
                if (groups[i].Warehouse == r.Warehouse && groups[i].Qty == r.Qty && groups[i].Stored == r.Stored)
                {
                    groups[i].Gathereds.Add(r.Gathered);
                    found = true;
                    break;
                }
            }
            if (!found) groups.Add((r.Warehouse, r.Qty, r.Stored, new List<int> { r.Gathered }));
        }

        var parts = new List<string>();
        foreach (var g in groups)
        {
            bool differ = false;
            for (int i = 1; i < g.Gathereds.Count; i++) { if (g.Gathereds[i] != g.Gathereds[0]) { differ = true; break; } }
            string gStr = differ ? string.Join(",", g.Gathereds) : g.Gathereds[0].ToString();
            string prefix = g.Gathereds.Count > 1 ? $"{g.Gathereds.Count}× " : "";
            parts.Add($"{prefix}'{g.Warehouse}' qty={g.Qty} stored={g.Stored} gathered={gStr}");
        }
        return string.Join("; ", parts);
    }

    // Removes all _stallTracks entries for a station (posKey) whose key doesn't match exceptKey —
    // called whenever a poll's clog check for that station doesn't land in the "watching" case
    // (no clog, stale, verdict A/B), or to drop a now-stale different-item watch at the same station.
    private static void ClearStallTrack(string posKey, string? exceptKey)
    {
        string prefix = posKey + "|";
        List<string>? toRemove = null;
        foreach (var k in _stallTracks.Keys)
        {
            if (k.StartsWith(prefix) && k != exceptKey) (toRemove ??= new List<string>()).Add(k);
        }
        if (toRemove != null) foreach (var k in toRemove) _stallTracks.Remove(k);
    }

    private static void MaybeToast(string posKey, string item, string message)
    {
        try
        {
            string key = $"{posKey}|{item}";
            var now = DateTime.UtcNow;
            if (_lastVerdictToast.TryGetValue(key, out var last) && (now - last).TotalMinutes < Plugin.ClogRealertMinutes.Value)
                return;
            _lastVerdictToast[key] = now;
            SupplyChainTracker.ShowMessage(message, 5f);
        }
        catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] WarehouseWatch.MaybeToast error: {ex}"); }
    }
}
