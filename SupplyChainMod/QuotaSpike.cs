using System;
using System.Collections.Generic;
using SandSailorStudio.Inventory;
using SSSGame;
using SSSGame.AI;
using UnityEngine;

namespace SupplyChainMod;

// QuotaSpike (v0.5.0, Phase 2b) — hotkey-gated (F7 micro-test, Shift+F7 forge) manual "quota-raise
// drain actuator" spike, mirroring ActuationSpike's (Phase 1a) structure: hotkey toggle, active-state,
// a crash-safe ledger (SpikeLedger, shared with ActuationSpike/SupplyController via the Kind="quota"
// discriminator), world-ready restore, and NoteWorldLeft. Where ActuationSpike moves a task's RANK,
// this actuates a warehouse task's QUOTA (ResourceStorageTaskData.itemInfoQuantity.quantity) through
// QuotaActuator.ApplyQuota — a plain value write, not a list-move. NOTE (run-1, v0.5.2): warehouse
// task priority was found to be a VALUE (tier: observed 0/1/2/3, not list-position-derived) — this
// mod never writes priority on warehouse tasks and must not gain any rank-reorder logic here.
//
// Three modes:
//   - micro-test (F7, default): auto-selects a (warehouse, item) pair whose intake is currently MET
//     (stored >= summed quota across all of that item's rows in that warehouse) with room to receive
//     more, finds a donor station holding a healthy stock of the same item, and raises the
//     warehouse's quota by min(sourceStock, room, QuotaBoostMaxDelta) — a bounded drain-invitation
//     experiment. TickWatch (from the tracker's MasterTick) watches for hauler response and
//     auto-reverts on target-met or a timeout.
//   - forge (Shift+F7, requires ClogForgeItem set): deliberately clamps ALL of a named item's rows in
//     ONE warehouse down so their sum equals the warehouse's current stored count exactly, shutting
//     intake on purpose so a clog can be reproduced for other diagnostics (WarehouseWatch's clog
//     detector). No auto-revert — only a hotkey press (or a ledger restore) reverts it.
//   - drain (internal only, entered from a forge revert): once a forge clamp is reverted, quotas are
//     restored but the artificial stockpile the clamp built up is still sitting there — drain is a
//     READ-ONLY watch (no further writes, ledger already clean) for whether haulers drain it, with
//     the same response/target-met/timeout semantics as micro-test.
//
// v0.5.2 (run-1 findings) — five changes: (1) true-warehouse filter: gathering-hut self-storage rows
// (rst.storageSupply.taskContainer == null) are excluded from micro-test/forge candidate selection —
// run 1's "no candidate" breakdown (groups=452 met=32 room0=22 noSourceStock=10) was diluted by hut
// rows that can never be real drain targets; (2) near-miss diagnostics: a "no candidate" outcome now
// logs the top 3 rejected-but-met groups with full gate-failure detail, for data-driven threshold
// tuning; (3) forge now clamps an entire multi-row group (a composite warehouse can hold several rows
// for the same item) to sum exactly to stored, not a single row — the old single-row check aborted
// as a false "already shut" on any multi-row item; (4) a forge revert transitions into the drain
// watch described above instead of just clearing; (5) forge moved off the plain hotkey onto
// Shift+<key> so F7 alone is unambiguously always micro-test.
//
// Never cache interop wrappers (Workstation/ResourceStorageTaskData/ItemInfo) across ticks
// (project-wide gotcha) — _active stores only strings/ints/floats and every touch (TickWatch,
// Revert, OnWorldReady) re-resolves the warehouse and task(s) fresh from the current station list by
// PosKey/TaskIndex/ItemName/SupplyOwner. Wrappers built during a single Boost() call (the warehouse
// row scan) are local to that call only.
internal static class QuotaSpike
{
    // One row's boost bookkeeping — re-resolved by TaskIndex/ItemName/SupplyOwner at watch/revert
    // time, never a cached wrapper. Micro-test uses a single-element Rows list; forge uses one entry
    // per row in the clamped group (all sharing the same warehouse PosKey/ItemName).
    private sealed class RowState
    {
        public int TaskIndex;
        public string ItemName = "?";
        public string SupplyOwner = "-";
        public int OrigQty;
        public int NewQty;
    }

    private sealed class ActiveQuotaBoost
    {
        public string WorldId = "?";
        public string PosKey = "?";
        public string StationName = "?";
        public string ItemName = "?"; // shared across all Rows — forge groups are all the same item
        public List<RowState> Rows = new();
        public float StartTime;
        public string Mode = "micro"; // "micro" | "forge" | "drain"
        public string SourcePosKey = "-";
        public string SourceName = "-";
        public int SourceStockAtBoost = -1;
        public int StoredAtBoost;
        public int Target; // micro: boosted quota target; drain: restored group qty sum; unused (0) for forge
        public bool Responded;
        public float RespondedAtTime;
    }

    // Near-miss diagnostic record for a met-but-rejected group (v0.5.2) — used only to log the top 3
    // when a micro-test Boost finds no eligible candidate.
    private sealed class MetGroupDiag
    {
        public string ItemName = "?";
        public string WarehouseName = "?";
        public int SumQty;
        public int Stored;
        public int Room;
        public int BestSourceStock = -1;
        public string BestSourceName = "-";
        public string FailedGate = "?"; // "room=0" | "sourceStock<min"
    }

    private static ActiveQuotaBoost? _active;
    private static float _lastForgeStatusLog;

    // ── Public surface (all wrapped so no exception escapes the tracker) ────────────────────────
    internal static void OnHotkey(List<StationEntry> stations, string worldId, bool forge)
    {
        try
        {
            if (_active != null) { Revert(stations, "hotkey"); return; }

            if (forge)
            {
                if (string.IsNullOrEmpty(Plugin.ClogForgeItem.Value))
                {
                    Plugin.Logger.LogInfo("[SupplyChain] [quota] Shift+hotkey pressed but ClogForgeItem is empty — nothing to forge.");
                    SupplyChainTracker.ShowMessage("Forge: set ClogForgeItem in the config first.", 6f);
                    return;
                }
                var rows = WarehouseTasks.BuildRows(stations);
                BoostForge(stations, worldId, rows);
            }
            else
            {
                var rows = WarehouseTasks.BuildRows(stations);
                BoostMicro(stations, worldId, rows);
            }
        }
        catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] [quota] OnHotkey error: {ex}"); }
    }

    internal static void TickWatch(List<StationEntry> stations)
    {
        try
        {
            var active = _active;
            if (active == null || active.Rows.Count == 0) return;

            StationEntry? target = RankActuator.FindStation(stations, active.PosKey, active.StationName);
            if (target == null) return; // world may be mid-resolve; avoid noisy per-tick logging

            Il2CppSystem.Collections.Generic.List<WorkstationTaskData>? datas = null;
            try { datas = target.Ws.GetWorkstationTaskDatas(); }
            catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] [quota] TickWatch GetWorkstationTaskDatas error: {ex}"); return; }

            // A representative row is enough — we only need warehouse-wide stored (item-level, not
            // row-level) for the watch/status logic below, for every mode.
            var rep = active.Rows[0];
            WarehouseTasks.FindQuotaTask(datas, rep.TaskIndex, rep.ItemName, rep.SupplyOwner, out var rst);
            if (rst == null) return; // task gone this tick; a hotkey revert will report it properly if pressed

            ItemInfo? info = null;
            try { info = rst.itemInfoQuantity.itemInfo; } catch { }
            if (info == null) return;

            int stored = 0;
            try
            {
                var inv = target.Ws.GetInventory();
                if (inv != null) stored = inv.GetItemQuantity(info);
            }
            catch { }

            if (active.Mode == "forge")
            {
                if (Time.time - _lastForgeStatusLog >= 60f)
                {
                    _lastForgeStatusLog = Time.time;
                    Plugin.Logger.LogInfo(
                        $"[SupplyChain] [quota] forge active: '{active.ItemName}' stored={stored} ({active.Rows.Count} row(s) clamped)");
                }
                return;
            }

            // "micro" and "drain" share the same response/target-met/timeout watch semantics.
            int sourceStock = -1;
            if (active.SourcePosKey != "-")
            {
                StationEntry? source = null;
                foreach (var s in stations) { if (s.PosKey == active.SourcePosKey) { source = s; break; } }
                if (source != null)
                {
                    try
                    {
                        var sInv = source.Ws.GetInventory();
                        if (sInv != null) sourceStock = sInv.GetItemQuantity(info);
                    }
                    catch { }
                }
            }

            if (stored > active.StoredAtBoost && !active.Responded)
            {
                active.Responded = true;
                active.RespondedAtTime = Time.time;
                float latency = active.RespondedAtTime - active.StartTime;
                string srcPart = active.SourcePosKey != "-" ? $" (src {active.SourceStockAtBoost}->{sourceStock})" : "";
                Plugin.Logger.LogInfo(
                    $"[SupplyChain] [quota] HAULER RESPONSE after {latency:F0}s: stored {active.StoredAtBoost}->{stored}{srcPart}");
                SupplyChainTracker.ShowMessage(
                    $"Quota spike: haulers responding — '{active.ItemName}' stored {active.StoredAtBoost}->{stored}.", 6f);
            }

            if (stored >= active.Target)
            {
                Revert(stations, "target-met");
            }
            else if (Time.time - active.StartTime > Plugin.QuotaSpikeMaxHoldSeconds.Value)
            {
                Revert(stations, "timeout");
            }
        }
        catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] [quota] TickWatch error: {ex}"); }
    }

    internal static void OnWorldReady(string worldId, List<StationEntry> stations)
    {
        try
        {
            var entries = SpikeLedger.LoadFor(worldId);
            foreach (var entry in entries)
            {
                // Shared ledger with ActuationSpike/SupplyController (Kind="rank") — only restore
                // this experiment's own kind here. Forge writes one ledger entry PER ROW, so a
                // multi-row forge clamp naturally restores row-by-row through this same per-entry pass.
                if (entry.Kind != "quota") continue;
                try { RestoreLedgerEntry(entry, stations); }
                catch (Exception ex)
                {
                    Plugin.Logger.LogError($"[SupplyChain] [quota] Restore entry error: {ex}");
                    try { SpikeLedger.Remove(entry); } catch { }
                }
            }
        }
        catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] [quota] OnWorldReady error: {ex}"); }
    }

    internal static void NoteWorldLeft()
    {
        try
        {
            if (_active != null)
            {
                if (_active.Mode == "drain")
                {
                    Plugin.Logger.LogInfo(
                        $"[SupplyChain] [quota] World left during a drain watch — station='{_active.StationName}' item='{_active.ItemName}' " +
                        "watch dropped (quotas already restored, ledger already clean — nothing to restore).");
                }
                else
                {
                    Plugin.Logger.LogWarning(
                        $"[SupplyChain] [quota] World left with {_active.Rows.Count} active quota row(s) boosted — station='{_active.StationName}' " +
                        $"item='{_active.ItemName}' remain(s) on the ledger and will be restored at the next load of world {_active.WorldId}.");
                }
            }
            _active = null;
            // Already cleared by ActuationSpike.NoteWorldLeft (shared static set) — redundant but
            // harmless self-containment, same rationale as SupplyController.NoteWorldLeft.
            RankActuator.BoostedStationKeys.Clear();
        }
        catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] [quota] NoteWorldLeft error: {ex}"); }
    }

    // ── Boost: micro-test ────────────────────────────────────────────────────────────────────────
    private static void BoostMicro(List<StationEntry> stations, string worldId, List<WarehouseTasks.WarehouseTaskRow> rows)
    {
        // v0.5.2 — exclude gathering-hut self-storage rows (no taskContainer) before grouping; they
        // are never real drain targets and were diluting run-1's candidate pool (452 groups, most hut
        // self-storage).
        int hutRowsExcluded = 0;
        var trueRows = new List<WarehouseTasks.WarehouseTaskRow>();
        foreach (var r in rows)
        {
            if (!r.HasTaskContainer) { hutRowsExcluded++; continue; }
            trueRows.Add(r);
        }

        // Group rows by (warehouse PosKey, item name) — a composite warehouse can hold multiple
        // task rows for the same item (one per child sub-storage supply); the effective intake
        // allotment is the SUM of that group's quotas.
        var groups = new Dictionary<string, List<WarehouseTasks.WarehouseTaskRow>>();
        foreach (var r in trueRows)
        {
            string key = r.Warehouse.PosKey + "|" + r.ItemName;
            if (!groups.TryGetValue(key, out var list)) { list = new List<WarehouseTasks.WarehouseTaskRow>(); groups[key] = list; }
            list.Add(r);
        }

        var metStatus = new Dictionary<string, bool>();
        int metCount = 0, roomZeroCount = 0, noSourceCount = 0;
        var nearMisses = new List<MetGroupDiag>();

        WarehouseTasks.WarehouseTaskRow? bestRow = null;
        string? bestSourcePosKey = null;
        string? bestSourceName = null;
        int bestSourceStock = -1;
        int bestStored = 0;
        int bestRoom = 0;

        foreach (var kv in groups)
        {
            var groupRows = kv.Value;
            var first = groupRows[0];
            int sumQty = 0;
            foreach (var r in groupRows) sumQty += r.Qty;

            ItemCollection? inv = null;
            try { inv = first.Warehouse.Ws.GetInventory(); } catch { }

            ItemInfo? info = null;
            try { info = first.Rst.itemInfoQuantity.itemInfo; } catch { }

            int stored = 0;
            int room = 0;
            if (inv != null && info != null)
            {
                try { stored = inv.GetItemQuantity(info); } catch { }
                try { room = inv.GetTotalRemainingCapacity(info); } catch { }
            }

            bool met = stored >= sumQty;
            metStatus[kv.Key] = met;
            if (!met) continue;
            metCount++;
            if (info == null) continue; // can't source-scan without ItemInfo — vanishingly rare

            // Source scan is always performed for a met group (even one that will fail the room
            // gate) so the near-miss diagnostics below stay complete regardless of which gate fails.
            string? sourcePosKey = null, sourceName = null;
            int sourceStock = -1;
            foreach (var st in stations)
            {
                if (st.PosKey == first.Warehouse.PosKey) continue;
                ItemCollection? sInv = null;
                try { sInv = st.Ws.GetInventory(); } catch { }
                if (sInv == null) continue;
                int q = 0;
                try { q = sInv.GetItemQuantity(info); } catch { }
                if (q > sourceStock) { sourceStock = q; sourcePosKey = st.PosKey; sourceName = st.StructureName; }
            }

            bool roomOk = room > 0;
            bool sourceOk = sourceStock >= Plugin.QuotaMinStationStock.Value;

            if (!roomOk)
            {
                roomZeroCount++;
                nearMisses.Add(new MetGroupDiag
                {
                    ItemName = first.ItemName,
                    WarehouseName = first.Warehouse.StructureName,
                    SumQty = sumQty,
                    Stored = stored,
                    Room = room,
                    BestSourceStock = sourceStock,
                    BestSourceName = sourceName ?? "-",
                    FailedGate = "room=0",
                });
                continue;
            }

            if (!sourceOk)
            {
                noSourceCount++;
                nearMisses.Add(new MetGroupDiag
                {
                    ItemName = first.ItemName,
                    WarehouseName = first.Warehouse.StructureName,
                    SumQty = sumQty,
                    Stored = stored,
                    Room = room,
                    BestSourceStock = sourceStock,
                    BestSourceName = sourceName ?? "-",
                    FailedGate = "sourceStock<min",
                });
                continue;
            }

            if (sourceStock > bestSourceStock)
            {
                bestSourceStock = sourceStock;
                bestSourcePosKey = sourcePosKey;
                bestSourceName = sourceName;
                // Multiple rows can exist for the same item in the same warehouse (composite
                // sub-storage supplies) — raising any single one raises the group sum, so the first
                // row encountered is as good as any; there is no further disambiguation signal.
                bestRow = first;
                bestStored = stored;
                bestRoom = room;
            }
        }

        if (bestRow == null)
        {
            Plugin.Logger.LogInfo(
                $"[SupplyChain] [quota] micro-test: no candidate — groups={groups.Count} (hutRowsExcluded={hutRowsExcluded}) " +
                $"met={metCount} room0={roomZeroCount} noSourceStock={noSourceCount}");

            // v0.5.2 near-miss diagnostics: top 3 met-but-rejected groups, "closest to viable" first
            // (a sourceStock-only miss with room ahead of a hard room=0 block; larger source stock
            // first) — the most useful data for tuning QuotaMinStationStock/room thresholds.
            nearMisses.Sort((a, b) =>
            {
                bool aRoomOk = a.FailedGate != "room=0";
                bool bRoomOk = b.FailedGate != "room=0";
                if (aRoomOk != bRoomOk) return aRoomOk ? -1 : 1;
                return b.BestSourceStock.CompareTo(a.BestSourceStock);
            });
            int shown = Math.Min(3, nearMisses.Count);
            for (int i = 0; i < shown; i++)
            {
                var d = nearMisses[i];
                Plugin.Logger.LogInfo(
                    $"[SupplyChain] [quota] near-miss[{i}]: '{d.ItemName}' on '{d.WarehouseName}' sumQty={d.SumQty} stored={d.Stored} " +
                    $"room={d.Room} bestSource='{d.BestSourceName}'(stock={d.BestSourceStock}) failedGate={d.FailedGate}");
            }

            SupplyChainTracker.ShowMessage("Quota spike: no eligible (met + room>0 + station stock) pair found.", 6f);
            return;
        }

        var warehouse = bestRow.Warehouse;

        bool allowed = true;
        try
        {
            allowed = warehouse.Ws.HasStateAuthority;
            if (!allowed)
            {
                Plugin.Logger.LogWarning($"[SupplyChain] [quota] blocked: not host (HasStateAuthority=false) for station '{warehouse.StructureName}'.");
                SupplyChainTracker.ShowMessage("Quota spike blocked: not the session host.", 8f);
                return;
            }
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogInfo($"[SupplyChain] [quota] HasStateAuthority unreadable ({ex.Message}) — assuming offline/solo, allowing.");
        }

        int delta = Math.Min(Math.Min(bestSourceStock, bestRoom), Plugin.QuotaBoostMaxDelta.Value);
        if (delta < 1)
        {
            Plugin.Logger.LogInfo(
                $"[SupplyChain] [quota] micro-test: computed delta < 1 (sourceStock={bestSourceStock} room={bestRoom} " +
                $"maxDelta={Plugin.QuotaBoostMaxDelta.Value}) on '{warehouse.StructureName}' — nothing to do.");
            return;
        }

        int newQty = bestRow.Qty + delta;

        var ledgerEntry = new LedgerEntry
        {
            WorldId = worldId,
            PosKey = warehouse.PosKey,
            StationName = warehouse.StructureName,
            TaskIndex = bestRow.TaskIndex,
            ItemName = bestRow.ItemName,
            OrigPriority = bestRow.Qty,
            BoostPriority = newQty,
            UtcTicks = DateTime.UtcNow.Ticks,
            Kind = "quota",
            SupplyOwner = bestRow.SupplyOwner,
        };
        SpikeLedger.Append(ledgerEntry);

        string path = QuotaActuator.ApplyQuota(warehouse, bestRow.Rst, newQty);

        RankActuator.BoostedStationKeys.Add(warehouse.PosKey);

        var rowState = new RowState
        {
            TaskIndex = bestRow.TaskIndex,
            ItemName = bestRow.ItemName,
            SupplyOwner = bestRow.SupplyOwner,
            OrigQty = bestRow.Qty,
            NewQty = newQty,
        };

        _active = new ActiveQuotaBoost
        {
            WorldId = worldId,
            PosKey = warehouse.PosKey,
            StationName = warehouse.StructureName,
            ItemName = bestRow.ItemName,
            Rows = new List<RowState> { rowState },
            StartTime = Time.time,
            Mode = "micro",
            SourcePosKey = bestSourcePosKey ?? "-",
            SourceName = bestSourceName ?? "-",
            SourceStockAtBoost = bestSourceStock,
            StoredAtBoost = bestStored,
            Target = newQty,
            Responded = false,
            RespondedAtTime = 0f,
        };

        // Priority-shadow dataset: the same warehouse's other UNMET true-warehouse rows (item, rank)
        // — informative context for whether haulers not responding is a room/priority problem
        // elsewhere too.
        string boostedKey = warehouse.PosKey + "|" + bestRow.ItemName;
        var otherUnmet = new List<string>();
        foreach (var kv in groups)
        {
            if (kv.Key == boostedKey) continue;
            if (!kv.Key.StartsWith(warehouse.PosKey + "|", StringComparison.Ordinal)) continue;
            if (metStatus.TryGetValue(kv.Key, out bool met) && met) continue;
            var r0 = kv.Value[0];
            otherUnmet.Add($"{r0.ItemName}(rank={r0.Rank})");
        }

        Plugin.Logger.LogInfo(
            $"[SupplyChain] [quota] BOOST '{bestRow.ItemName}' {bestRow.Qty}->{newQty} on '{warehouse.StructureName}' rank={bestRow.Rank} " +
            $"stored={bestStored} room={bestRoom} source='{bestSourceName}'(stock={bestSourceStock}) delta={delta} path={path} " +
            $"otherUnmet=[{string.Join(", ", otherUnmet)}]");

        SupplyChainTracker.ShowMessage(
            $"Quota BOOST: '{bestRow.ItemName}' {bestRow.Qty}->{newQty} on '{warehouse.StructureName}' — watching for haulers " +
            $"(src '{bestSourceName}' has {bestSourceStock}). F7/Shift+F7 reverts.", 12f);
    }

    // ── Boost: forge ─────────────────────────────────────────────────────────────────────────────
    private static void BoostForge(List<StationEntry> stations, string worldId, List<WarehouseTasks.WarehouseTaskRow> rows)
    {
        string targetItem = Plugin.ClogForgeItem.Value;

        // v0.5.2 — matching rows, true-warehouse only, grouped by warehouse (a composite warehouse
        // can hold several rows for the same item — the whole group in ONE warehouse is the forge
        // unit, not a single row).
        var byWarehouse = new Dictionary<string, List<WarehouseTasks.WarehouseTaskRow>>();
        foreach (var r in rows)
        {
            if (!r.HasTaskContainer) continue;
            if (!string.Equals(r.ItemName, targetItem, StringComparison.OrdinalIgnoreCase)) continue;
            if (!byWarehouse.TryGetValue(r.Warehouse.PosKey, out var list)) { list = new List<WarehouseTasks.WarehouseTaskRow>(); byWarehouse[r.Warehouse.PosKey] = list; }
            list.Add(r);
        }

        if (byWarehouse.Count == 0)
        {
            Plugin.Logger.LogInfo($"[SupplyChain] [quota] forge: no true-warehouse task row matches ClogForgeItem='{targetItem}'.");
            SupplyChainTracker.ShowMessage($"Clog forge: no warehouse task found for '{targetItem}'.", 6f);
            return;
        }

        // Choose the warehouse with the largest warehouse-wide stored count among the matches.
        List<WarehouseTasks.WarehouseTaskRow>? bestGroup = null;
        StationEntry? bestWarehouse = null;
        int bestStored = -1;

        foreach (var kv in byWarehouse)
        {
            var groupRows = kv.Value;
            var first = groupRows[0];

            ItemCollection? inv = null;
            try { inv = first.Warehouse.Ws.GetInventory(); } catch { }
            ItemInfo? info = null;
            try { info = first.Rst.itemInfoQuantity.itemInfo; } catch { }
            if (inv == null || info == null) continue;

            int stored = 0;
            try { stored = inv.GetItemQuantity(info); } catch { }

            if (stored > bestStored)
            {
                bestStored = stored;
                bestGroup = groupRows;
                bestWarehouse = first.Warehouse;
            }
        }

        if (bestGroup == null || bestWarehouse == null)
        {
            Plugin.Logger.LogInfo($"[SupplyChain] [quota] forge: could not resolve inventory/ItemInfo for any matching warehouse for '{targetItem}'.");
            SupplyChainTracker.ShowMessage($"Clog forge: could not resolve warehouse inventory for '{targetItem}'.", 6f);
            return;
        }

        int n = bestGroup.Count;
        int sumQty = 0;
        foreach (var r in bestGroup) sumQty += r.Qty;

        if (sumQty <= bestStored)
        {
            Plugin.Logger.LogInfo(
                $"[SupplyChain] [quota] forge: '{targetItem}' on '{bestWarehouse.StructureName}' group qty sum={sumQty} " +
                $"already <= stored={bestStored} — forge unnecessary.");
            SupplyChainTracker.ShowMessage($"Clog forge: '{targetItem}' quota already <= stored — nothing to do.", 6f);
            return;
        }

        bool allowed = true;
        try
        {
            allowed = bestWarehouse.Ws.HasStateAuthority;
            if (!allowed)
            {
                Plugin.Logger.LogWarning($"[SupplyChain] [quota] blocked: not host (HasStateAuthority=false) for station '{bestWarehouse.StructureName}'.");
                SupplyChainTracker.ShowMessage("Quota spike blocked: not the session host.", 8f);
                return;
            }
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogInfo($"[SupplyChain] [quota] HasStateAuthority unreadable ({ex.Message}) — assuming offline/solo, allowing.");
        }

        // Clamp all n rows so the group sum == stored exactly: base + 1 for the first r rows
        // (r = stored % n), base for the rest.
        int baseQty = bestStored / n;
        int remainder = bestStored % n;

        var rowStates = new List<RowState>(n);
        for (int i = 0; i < n; i++)
        {
            var r = bestGroup[i];
            int newQty = baseQty + (i < remainder ? 1 : 0);
            rowStates.Add(new RowState
            {
                TaskIndex = r.TaskIndex,
                ItemName = r.ItemName,
                SupplyOwner = r.SupplyOwner,
                OrigQty = r.Qty,
                NewQty = newQty,
            });
        }

        // Write-ahead: one ledger entry PER ROW, ALL appended before any write.
        foreach (var rs in rowStates)
        {
            SpikeLedger.Append(new LedgerEntry
            {
                WorldId = worldId,
                PosKey = bestWarehouse.PosKey,
                StationName = bestWarehouse.StructureName,
                TaskIndex = rs.TaskIndex,
                ItemName = rs.ItemName,
                OrigPriority = rs.OrigQty,
                BoostPriority = rs.NewQty,
                UtcTicks = DateTime.UtcNow.Ticks,
                Kind = "quota",
                SupplyOwner = rs.SupplyOwner,
            });
        }

        var paths = new List<string>(n);
        for (int i = 0; i < n; i++)
            paths.Add(QuotaActuator.ApplyQuota(bestWarehouse, bestGroup[i].Rst, rowStates[i].NewQty));

        RankActuator.BoostedStationKeys.Add(bestWarehouse.PosKey);

        _active = new ActiveQuotaBoost
        {
            WorldId = worldId,
            PosKey = bestWarehouse.PosKey,
            StationName = bestWarehouse.StructureName,
            ItemName = targetItem,
            Rows = rowStates,
            StartTime = Time.time,
            Mode = "forge",
            SourcePosKey = "-",
            SourceName = "-",
            SourceStockAtBoost = -1,
            StoredAtBoost = bestStored,
            Target = 0,
            Responded = false,
            RespondedAtTime = 0f,
        };
        _lastForgeStatusLog = Time.time; // first ~60s status line lands ~60s from now, not immediately

        var rowSummary = new List<string>(n);
        foreach (var rs in rowStates) rowSummary.Add($"{rs.TaskIndex}:{rs.OrigQty}->{rs.NewQty}");

        Plugin.Logger.LogInfo(
            $"[SupplyChain] [quota] FORGE clamp '{targetItem}' on '{bestWarehouse.StructureName}' n={n} sum {sumQty}->{bestStored} " +
            $"rows=[{string.Join(", ", rowSummary)}] paths=[{string.Join(", ", paths)}] — NO auto-revert.");
        SupplyChainTracker.ShowMessage(
            $"Clog forge: '{targetItem}' quota clamped {sumQty}->{bestStored} across {n} row(s) on '{bestWarehouse.StructureName}' " +
            "— NO auto-revert, F7/Shift+F7 reverts.", 12f);
    }

    // ── Revert ───────────────────────────────────────────────────────────────────────────────────
    private static void Revert(List<StationEntry> stations, string reason)
    {
        var active = _active;
        if (active == null) return;

        if (active.Mode == "drain")
        {
            EndDrainWatch(stations, active, reason);
            return;
        }

        StationEntry? target = RankActuator.FindStation(stations, active.PosKey, active.StationName);
        if (target == null)
        {
            Plugin.Logger.LogWarning(
                $"[SupplyChain] [quota] Revert({reason}): station '{active.StationName}' (posKey={active.PosKey}) " +
                "not found — ledger entry(ies) kept, restored at next world load.");
            RankActuator.BoostedStationKeys.Remove(active.PosKey);
            _active = null;
            return;
        }

        Il2CppSystem.Collections.Generic.List<WorkstationTaskData>? datas = null;
        try { datas = target.Ws.GetWorkstationTaskDatas(); }
        catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] [quota] Revert GetWorkstationTaskDatas error: {ex}"); }

        int restoredCount = 0;
        int missingCount = 0;
        var restoredRows = new List<RowState>();
        string? lastPath = null;
        ItemInfo? repInfo = null; // representative ItemInfo, used for the post-revert stored readout

        foreach (var rowState in active.Rows)
        {
            WarehouseTasks.FindQuotaTask(datas, rowState.TaskIndex, rowState.ItemName, rowState.SupplyOwner, out var rst);
            if (rst == null)
            {
                missingCount++;
                SpikeLedger.Remove(new LedgerEntry
                {
                    WorldId = active.WorldId,
                    PosKey = active.PosKey,
                    TaskIndex = rowState.TaskIndex,
                    ItemName = rowState.ItemName,
                    Kind = "quota",
                });
                continue;
            }

            lastPath = QuotaActuator.ApplyQuota(target, rst, rowState.OrigQty);
            try { repInfo = rst.itemInfoQuantity.itemInfo; } catch { }

            SpikeLedger.Remove(new LedgerEntry
            {
                WorldId = active.WorldId,
                PosKey = active.PosKey,
                TaskIndex = rowState.TaskIndex,
                ItemName = rowState.ItemName,
                Kind = "quota",
            });

            restoredRows.Add(rowState);
            restoredCount++;
        }

        if (missingCount > 0)
        {
            Plugin.Logger.LogWarning(
                $"[SupplyChain] [quota] Revert({reason}): {missingCount}/{active.Rows.Count} row(s) of '{active.ItemName}' not found " +
                $"on station '{target.StructureName}' — their ledger entries dropped.");
        }

        if (restoredCount == 0)
        {
            Plugin.Logger.LogWarning(
                $"[SupplyChain] [quota] Revert({reason}): no rows of '{active.ItemName}' found on station '{target.StructureName}' — nothing restored.");
            SupplyChainTracker.ShowMessage($"Quota spike: revert failed — '{active.ItemName}' not found on '{target.StructureName}'.", 6f);
            RankActuator.BoostedStationKeys.Remove(active.PosKey);
            _active = null;
            return;
        }

        int finalStored = 0;
        try
        {
            var inv = target.Ws.GetInventory();
            if (repInfo != null && inv != null) finalStored = inv.GetItemQuantity(repInfo);
        }
        catch { }

        if (active.Mode == "forge")
        {
            // v0.5.2 — transition into a read-only drain watch instead of clearing: quotas are back
            // to their pre-forge values (or as many as could be found) and the ledger is clean; watch
            // for haulers draining the artificial stockpile the clamp created. BoostedStationKeys
            // stays registered until the drain watch itself ends.
            int restoredSum = 0;
            foreach (var rs in restoredRows) restoredSum += rs.OrigQty;

            Plugin.Logger.LogInfo(
                $"[SupplyChain] [quota] REVERT ({reason}) forge clamp on '{target.StructureName}' item='{active.ItemName}' " +
                $"{restoredCount}/{active.Rows.Count} row(s) restored, sum -> {restoredSum} stored={finalStored} path={lastPath} " +
                "— transitioning to drain watch.");
            SupplyChainTracker.ShowMessage(
                $"Clog forge REVERT ({reason}): '{active.ItemName}' quota restored on '{target.StructureName}' — watching for drain.", 8f);

            active.Mode = "drain";
            active.StoredAtBoost = finalStored;
            active.Target = restoredSum;
            active.StartTime = Time.time; // clock restarts at the transition
            active.Responded = false;
            active.RespondedAtTime = 0f;
            // active.Rows kept as-is (now-restored OrigQty values) — TickWatch only reads Rows[0] to
            // re-resolve a representative task for stored lookups; no further writes happen in drain.
            return; // _active stays set (now in drain mode) — deliberately NOT cleared
        }

        // Micro mode — straightforward revert-and-clear (single row).
        RankActuator.BoostedStationKeys.Remove(active.PosKey);

        int hauled = finalStored - active.StoredAtBoost;
        string respondedStr = active.Responded ? "yes" : "no";
        string latencyStr = active.Responded ? $"{(active.RespondedAtTime - active.StartTime):F0}s" : "n/a";
        var rowState0 = active.Rows[0];

        Plugin.Logger.LogInfo(
            $"[SupplyChain] [quota] SUMMARY REVERT ({reason}) mode={active.Mode} item='{active.ItemName}' station='{target.StructureName}' " +
            $"responded={respondedStr} latency={latencyStr} hauled={hauled} quota {rowState0.OrigQty}->{rowState0.NewQty}->{rowState0.OrigQty} path={lastPath}");

        SupplyChainTracker.ShowMessage(
            $"Quota REVERT ({reason}): '{active.ItemName}' back to {rowState0.OrigQty} on '{target.StructureName}' (hauled {hauled}).", 8f);

        _active = null;
    }

    // Closes out a read-only drain watch (no writes, no ledger — both already settled when the
    // forge revert transitioned into this mode). Reached either from TickWatch's target-met/timeout
    // or a manual hotkey press while a drain watch is active.
    private static void EndDrainWatch(List<StationEntry> stations, ActiveQuotaBoost active, string reason)
    {
        StationEntry? target = RankActuator.FindStation(stations, active.PosKey, active.StationName);
        string stationName = target != null ? target.StructureName : active.StationName;

        string respondedStr = active.Responded ? "yes" : "no";
        string latencyStr = active.Responded ? $"{(active.RespondedAtTime - active.StartTime):F0}s" : "n/a";
        float elapsed = Time.time - active.StartTime;

        Plugin.Logger.LogInfo(
            $"[SupplyChain] [quota] DRAIN WATCH END ({reason}) item='{active.ItemName}' station='{stationName}' " +
            $"responded={respondedStr} latency={latencyStr} elapsed={elapsed:F0}s target={active.Target}.");
        SupplyChainTracker.ShowMessage(
            $"Quota drain watch ({reason}): '{active.ItemName}' on '{stationName}' — watch ended.", 8f);

        RankActuator.BoostedStationKeys.Remove(active.PosKey);
        _active = null;
    }

    // ── Ledger restore (world-load pass) ────────────────────────────────────────────────────────
    private static void RestoreLedgerEntry(LedgerEntry entry, List<StationEntry> stations)
    {
        StationEntry? target = RankActuator.FindStation(stations, entry.PosKey, entry.StationName);
        if (target == null)
        {
            Plugin.Logger.LogWarning(
                $"[SupplyChain] [quota] Stranded quota boost station '{entry.StationName}' (posKey={entry.PosKey}) " +
                "not found — dropping ledger entry.");
            SpikeLedger.Remove(entry);
            return;
        }

        Il2CppSystem.Collections.Generic.List<WorkstationTaskData>? datas = null;
        try { datas = target.Ws.GetWorkstationTaskDatas(); }
        catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] [quota] GetWorkstationTaskDatas error: {ex}"); }

        WarehouseTasks.FindQuotaTask(datas, entry.TaskIndex, entry.ItemName, entry.SupplyOwner, out var rst);
        if (rst == null)
        {
            Plugin.Logger.LogWarning(
                $"[SupplyChain] [quota] Stranded quota boost item '{entry.ItemName}' not found on station " +
                $"'{target.StructureName}' — dropping ledger entry.");
            SpikeLedger.Remove(entry);
            return;
        }

        string path = QuotaActuator.ApplyQuota(target, rst, entry.OrigPriority);

        Plugin.Logger.LogWarning(
            $"[SupplyChain] [quota] Restored stranded quota boost: station='{target.StructureName}' item='{entry.ItemName}' " +
            $"quota -> {entry.OrigPriority} path={path}");
        SupplyChainTracker.ShowMessage(
            $"SupplyChain: restored stranded quota boost on '{target.StructureName}' ('{entry.ItemName}' back to {entry.OrigPriority}).", 12f);
        SpikeLedger.Remove(entry);
    }

}
