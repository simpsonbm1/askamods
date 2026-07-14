using System;
using System.Collections.Generic;
using System.Text;
using SandSailorStudio.Inventory;
using SSSGame.AI;
using UnityEngine;

namespace SupplyChainMod;

// ClogController (v0.6.0, Phase 2c) — automates what the F7/Shift+F7 QuotaSpike proved manually in
// Phase 2b: when a station is clogged by an item whose warehouse allotments are all met
// (WarehouseWatch VERDICT A — the "drain-boost candidate" case), raise ONE warehouse quota row for
// that item (inviting haulers to drain the clogged station), watch for a hauler response, then
// revert — full duty-cycle discipline mirroring SupplyController (Controller.cs). Entries are keyed
// by ITEM NAME (not station — the same item can clog different stations over time; the controller
// tracks the demand, not the location) and actuate through the SAME QuotaActuator/SpikeLedger write
// path QuotaSpike uses, sharing RankActuator.BoostedStationKeys so the spike/controller/ClogController
// never target the same warehouse. Starts in DRY-RUN — actuation only occurs when SupplyController.
// Armed (the shared F11 toggle) — and shares SupplyController's militia-alarm lockout window.
//
// State machine per clogged item name: Pending -> Boosting -> Cooldown -> (Pending again, or
// CapacityLockout after ClogMaxCyclesPerItem ineffective cycles) -> Pending. A clog that goes stale
// (no VERDICT A re-sighting for ClogRetainSeconds) is removed outright, reverting first if Boosting.
//
// Never cache interop wrappers (Workstation/ResourceStorageTaskData/ItemInfo) across ticks
// (project-wide gotcha) — ClogEntry stores only strings/ints/floats/DateTimes; every touch
// (TickBoosting, Revert, TickPending's target selection) re-resolves the warehouse/station/task fresh
// from the current station list by PosKey/TaskIndex/ItemName/SupplyOwner.
internal static class ClogController
{
    private enum State { Pending, Boosting, Cooldown, CapacityLockout }

    private sealed class ClogEntry
    {
        public string ItemName = "?";
        public string StationPosKey = "?";
        public string StationName = "?"; // most recent complaining station — refreshed on every NoteVerdictA
        public DateTime FirstSeen;
        public DateTime LastSeen;
        public int Sightings;
        public State State = State.Pending;
        public float StateSinceTime;
        public int CycleCount;
        public int NoTargetStrikes;
        public DateTime NextEligibleAt = DateTime.MinValue;
        public bool WouldBoostLogged;

        // Boost bookkeeping — re-resolved by PosKey/TaskIndex/ItemName/SupplyOwner at watch/revert
        // time, never a cached wrapper.
        public string? BoostWarehousePosKey;
        public string? BoostWarehouseName;
        public int BoostTaskIndex;
        public string? BoostSupplyOwner;
        public int BoostOrigQty;
        public int BoostNewQty;
        public int BoostDelta;
        public int WarehouseStoredAtBoost;
        public int StationCountAtBoost;
        public bool Responded;
        public float RespondedAtTime;
    }

    private static readonly Dictionary<string, ClogEntry> _entries = new();
    private static DateTime _lastStatusAt = DateTime.MinValue;

    // ── Public surface (all wrapped so no exception escapes the caller) ────────────────────────

    // Called from WarehouseWatch's VERDICT A branch (warehouse-poll cadence, ~30s — not hot).
    internal static void NoteVerdictA(string stationPosKey, string stationName, string itemName, int dominantQty)
    {
        try
        {
            var now = DateTime.UtcNow;
            if (_entries.TryGetValue(itemName, out var entry))
            {
                entry.LastSeen = now;
                entry.Sightings++;
                entry.StationPosKey = stationPosKey;
                entry.StationName = stationName;
            }
            else
            {
                _entries[itemName] = new ClogEntry
                {
                    ItemName = itemName,
                    StationPosKey = stationPosKey,
                    StationName = stationName,
                    FirstSeen = now,
                    LastSeen = now,
                    Sightings = 1,
                };
                Plugin.Logger.LogInfo($"[SupplyChain] [clog-ctl] clog demand created '{itemName}' at '{stationName}' (x{dominantQty})");
            }
        }
        catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] [clog-ctl] NoteVerdictA error: {ex}"); }
    }

    // Called from SupplyChainTracker.MasterTick (TickSeconds cadence, default 5s).
    internal static void Tick(List<StationEntry> stations, string worldId)
    {
        try
        {
            var now = DateTime.UtcNow;

            StaleSweep(stations, worldId, now);
            TickBoosting(stations, worldId, now);
            TickCooldown(now);
            TickCapacityLockout(now);

            // Shares SupplyController's militia-alarm lockout window — no new clog boosts start
            // while a defense complaint is fresh.
            if (!SupplyController.AlarmLockoutActive)
                TickPending(stations, worldId, now);

            MaybeLogStatus(now);
        }
        catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] [clog-ctl] Tick error: {ex}"); }
    }

    internal static void NoteWorldLeft()
    {
        try
        {
            int boostingCount = 0;
            foreach (var kv in _entries)
                if (kv.Value.State == State.Boosting) boostingCount++;

            if (boostingCount > 0)
            {
                Plugin.Logger.LogWarning(
                    $"[SupplyChain] [clog-ctl] World left with {boostingCount} active boost(s) — their ledger entries remain for restore at the next load of that world.");
            }

            _entries.Clear();
            _lastStatusAt = DateTime.MinValue;
        }
        catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] [clog-ctl] NoteWorldLeft error: {ex}"); }
    }

    // ── Step 1: stale sweep ──────────────────────────────────────────────────────────────────────
    private static void StaleSweep(List<StationEntry> stations, string worldId, DateTime now)
    {
        List<string>? toRemove = null;
        foreach (var kv in _entries)
        {
            var entry = kv.Value;
            double sinceLastSeen = (now - entry.LastSeen).TotalSeconds;
            if (sinceLastSeen <= Plugin.ClogRetainSeconds.Value) continue;

            if (entry.State == State.Boosting)
                Revert(entry, stations, worldId, "clog-cleared");

            double lifetime = (now - entry.FirstSeen).TotalSeconds;
            Plugin.Logger.LogInfo(
                $"[SupplyChain] [clog-ctl] clog demand '{entry.ItemName}' cleared (stale) after {lifetime:F0}s (sightings={entry.Sightings})");

            (toRemove ??= new List<string>()).Add(kv.Key);
        }

        if (toRemove != null)
            foreach (var key in toRemove) _entries.Remove(key);
    }

    // ── Step 2: boosting entries — response watch + exit conditions ────────────────────────────
    private static void TickBoosting(List<StationEntry> stations, string worldId, DateTime now)
    {
        foreach (var kv in _entries)
        {
            var entry = kv.Value;
            if (entry.State != State.Boosting) continue;

            StationEntry? warehouse = RankActuator.FindStation(stations, entry.BoostWarehousePosKey ?? "?", entry.BoostWarehouseName ?? "?");
            if (warehouse == null) continue; // world may be mid-resolve; avoid noisy per-tick logging

            Il2CppSystem.Collections.Generic.List<WorkstationTaskData>? datas = null;
            try { datas = warehouse.Ws.GetWorkstationTaskDatas(); }
            catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] [clog-ctl] TickBoosting GetWorkstationTaskDatas error: {ex}"); continue; }

            WarehouseTasks.FindQuotaTask(datas, entry.BoostTaskIndex, entry.ItemName, entry.BoostSupplyOwner ?? "-", out var rst);
            if (rst == null) continue; // task gone this tick; a stale sweep/hotkey will report it properly

            ItemInfo? info = null;
            try { info = rst.itemInfoQuantity.itemInfo; } catch { }
            if (info == null) continue;

            int warehouseStored = 0;
            try
            {
                var inv = warehouse.Ws.GetInventory();
                if (inv != null) warehouseStored = inv.GetItemQuantity(info);
            }
            catch { }

            int stationCount = -1;
            StationEntry? cloggedStation = RankActuator.FindStation(stations, entry.StationPosKey, entry.StationName);
            if (cloggedStation != null)
            {
                try
                {
                    var sInv = cloggedStation.Ws.GetInventory();
                    if (sInv != null) stationCount = sInv.GetItemQuantity(info);
                }
                catch { }
            }

            if (!entry.Responded && (warehouseStored > entry.WarehouseStoredAtBoost || (stationCount >= 0 && stationCount < entry.StationCountAtBoost)))
            {
                entry.Responded = true;
                entry.RespondedAtTime = Time.time;
                float latency = entry.RespondedAtTime - entry.StateSinceTime;
                Plugin.Logger.LogInfo(
                    $"[SupplyChain] [clog-ctl] HAULER RESPONSE after {latency:F0}s: warehouse {entry.WarehouseStoredAtBoost}->{warehouseStored}, station {entry.StationCountAtBoost}->{stationCount}");
                SupplyChainTracker.ShowMessage(
                    $"Clog controller: haulers responding — '{entry.ItemName}' warehouse {entry.WarehouseStoredAtBoost}->{warehouseStored}.", 6f);
            }

            if (warehouseStored >= entry.WarehouseStoredAtBoost + entry.BoostDelta)
            {
                Revert(entry, stations, worldId, "target-met");
                continue;
            }
            if (!WarehouseWatch.IsStorageFullRecent(entry.StationPosKey, 60.0))
            {
                Revert(entry, stations, worldId, "clog-cleared");
                continue;
            }
            if (!entry.Responded && (Time.time - entry.StateSinceTime) > Plugin.ClogResponseWindowSeconds.Value)
            {
                Revert(entry, stations, worldId, "no-response");
                continue;
            }
            if ((Time.time - entry.StateSinceTime) > Plugin.ClogHoldMaxSeconds.Value)
            {
                Revert(entry, stations, worldId, "hold-elapsed");
                continue;
            }
        }
    }

    // ── Step 3: cooldown entries — re-boost, or capacity verdict ────────────────────────────────
    private static void TickCooldown(DateTime now)
    {
        foreach (var kv in _entries)
        {
            var entry = kv.Value;
            if (entry.State != State.Cooldown) continue;
            if (Time.time - entry.StateSinceTime < Plugin.ClogCooldownSeconds.Value) continue;

            if (entry.CycleCount < Plugin.ClogMaxCyclesPerItem.Value)
            {
                entry.State = State.Pending; // re-evaluated by TickPending later in this same tick
            }
            else
            {
                Plugin.Logger.LogWarning(
                    $"[SupplyChain] [clog-ctl] VERDICT: clog '{entry.ItemName}' persists after {entry.CycleCount} quota cycles — " +
                    $"warehouse capacity/hauler shortage, not quota; locking out {Plugin.ClogCapacityLockoutMinutes.Value} min");
                SupplyChainTracker.ShowMessage(
                    $"Clog ALERT: '{entry.ItemName}' persists after {entry.CycleCount} quota cycles — capacity/hauler shortage, not quota", 12f);
                entry.State = State.CapacityLockout;
                entry.StateSinceTime = Time.time;
            }
        }
    }

    // ── Step 4: capacity-lockout entries — expiry ───────────────────────────────────────────────
    private static void TickCapacityLockout(DateTime now)
    {
        foreach (var kv in _entries)
        {
            var entry = kv.Value;
            if (entry.State != State.CapacityLockout) continue;
            if (Time.time - entry.StateSinceTime < Plugin.ClogCapacityLockoutMinutes.Value * 60f) continue;

            entry.CycleCount = 0;
            entry.NoTargetStrikes = 0;
            entry.State = State.Pending;
            entry.NextEligibleAt = DateTime.MinValue;
            entry.WouldBoostLogged = false;
        }
    }

    // ── Step 5: pending entries — target resolution + boost/dry-run ────────────────────────────
    private static void TickPending(List<StationEntry> stations, string worldId, DateTime now)
    {
        int boostingCount = 0;
        foreach (var kv in _entries)
            if (kv.Value.State == State.Boosting) boostingCount++;

        foreach (var kv in _entries)
        {
            var entry = kv.Value;
            if (entry.State != State.Pending) continue;

            double sinceLastSeen = (now - entry.LastSeen).TotalSeconds;
            if (sinceLastSeen > Plugin.ClogRetainSeconds.Value) continue; // defensive — StaleSweep runs first

            if ((now - entry.FirstSeen).TotalSeconds < Plugin.ClogHysteresisSeconds.Value) continue;
            if (now < entry.NextEligibleAt) continue;
            if (!WarehouseWatch.IsStorageFullRecent(entry.StationPosKey, 90.0)) continue; // still actually complaining

            if (boostingCount >= Plugin.MaxConcurrentClogBoosts.Value) break;

            // Target selection — fresh every time, nothing cached.
            var rows = WarehouseTasks.BuildRows(stations); // already skips BoostedStationKeys-locked warehouses

            var matchingRows = new List<WarehouseTasks.WarehouseTaskRow>();
            foreach (var r in rows)
            {
                if (!r.HasTaskContainer) continue;
                if (!string.Equals(r.ItemName, entry.ItemName, StringComparison.Ordinal)) continue;
                matchingRows.Add(r);
            }

            var groups = new Dictionary<string, List<WarehouseTasks.WarehouseTaskRow>>();
            foreach (var r in matchingRows)
            {
                string gKey = r.Warehouse.PosKey;
                if (!groups.TryGetValue(gKey, out var list)) { list = new List<WarehouseTasks.WarehouseTaskRow>(); groups[gKey] = list; }
                list.Add(r);
            }

            ItemInfo? repInfo = null;
            if (matchingRows.Count > 0)
            {
                try { repInfo = matchingRows[0].Rst.itemInfoQuantity.itemInfo; } catch { }
            }

            int stationCount = -1;
            StationEntry? cloggedStation = RankActuator.FindStation(stations, entry.StationPosKey, entry.StationName);
            if (cloggedStation != null && repInfo != null)
            {
                try
                {
                    var sInv = cloggedStation.Ws.GetInventory();
                    if (sInv != null) stationCount = sInv.GetItemQuantity(repInfo);
                }
                catch { }
            }

            WarehouseTasks.WarehouseTaskRow? bestRow = null;
            StationEntry? bestWarehouse = null;
            int bestRoom = -1;
            int bestWarehouseStored = 0;
            int roomBlockedCount = 0;

            foreach (var g in groups)
            {
                var groupRows = g.Value;
                var first = groupRows[0];

                ItemCollection? inv = null;
                try { inv = first.Warehouse.Ws.GetInventory(); } catch { }
                ItemInfo? info = null;
                try { info = first.Rst.itemInfoQuantity.itemInfo; } catch { }
                if (inv == null || info == null) { roomBlockedCount++; continue; }

                int stored = 0;
                int room = 0;
                try { stored = inv.GetItemQuantity(info); } catch { }
                try { room = inv.GetTotalRemainingCapacity(info); } catch { }

                if (room < 1) { roomBlockedCount++; continue; }

                if (room > bestRoom)
                {
                    bestRoom = room;
                    bestRow = first;
                    bestWarehouse = first.Warehouse;
                    bestWarehouseStored = stored;
                }
            }

            int delta = 0;
            if (bestRow != null)
            {
                int stationCap = stationCount > 0 ? stationCount : int.MaxValue;
                delta = Math.Min(Math.Min(stationCap, bestRoom), Plugin.ClogBoostMaxDelta.Value);
            }

            if (bestRow == null || bestWarehouse == null || delta < 1)
            {
                entry.NoTargetStrikes++;
                Plugin.Logger.LogInfo(
                    $"[SupplyChain] [clog-ctl] '{entry.ItemName}' no candidate: groups={groups.Count} roomBlocked={roomBlockedCount} " +
                    $"stationCount={stationCount} strike {entry.NoTargetStrikes}/3");
                entry.NextEligibleAt = now.AddSeconds(60);

                if (entry.NoTargetStrikes >= 3)
                {
                    Plugin.Logger.LogWarning(
                        $"[SupplyChain] [clog-ctl] VERDICT: no warehouse can absorb '{entry.ItemName}' — locked or out of room; add storage");
                    SupplyChainTracker.ShowMessage(
                        $"Clog ALERT: no warehouse can absorb '{entry.ItemName}' — locked or out of room; add storage", 12f);
                    entry.State = State.CapacityLockout;
                    entry.StateSinceTime = Time.time;
                    entry.NoTargetStrikes = 0;
                }
                continue;
            }

            var warehouse = bestWarehouse;

            bool allowed = true;
            try
            {
                allowed = warehouse.Ws.HasStateAuthority;
                if (!allowed)
                {
                    Plugin.Logger.LogWarning($"[SupplyChain] [clog-ctl] blocked: not host (HasStateAuthority=false) for warehouse '{warehouse.StructureName}'.");
                    entry.NextEligibleAt = now.AddSeconds(120);
                    continue;
                }
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogInfo($"[SupplyChain] [clog-ctl] HasStateAuthority unreadable ({ex.Message}) — assuming offline/solo, allowing.");
            }

            int newQty = bestRow.Qty + delta;

            if (SupplyController.Armed)
            {
                var ledgerEntry = new LedgerEntry
                {
                    WorldId = worldId,
                    PosKey = warehouse.PosKey,
                    StationName = warehouse.StructureName,
                    TaskIndex = bestRow.TaskIndex,
                    ItemName = entry.ItemName,
                    OrigPriority = bestRow.Qty,
                    BoostPriority = newQty,
                    UtcTicks = DateTime.UtcNow.Ticks,
                    Kind = "quota",
                    SupplyOwner = bestRow.SupplyOwner,
                };
                SpikeLedger.Append(ledgerEntry);

                QuotaActuator.ApplyQuota(warehouse, bestRow.Rst, newQty);

                RankActuator.BoostedStationKeys.Add(warehouse.PosKey);

                entry.State = State.Boosting;
                entry.CycleCount++;
                entry.StateSinceTime = Time.time;
                entry.BoostWarehousePosKey = warehouse.PosKey;
                entry.BoostWarehouseName = warehouse.StructureName;
                entry.BoostTaskIndex = bestRow.TaskIndex;
                entry.BoostSupplyOwner = bestRow.SupplyOwner;
                entry.BoostOrigQty = bestRow.Qty;
                entry.BoostNewQty = newQty;
                entry.BoostDelta = delta;
                entry.WarehouseStoredAtBoost = bestWarehouseStored;
                entry.StationCountAtBoost = stationCount;
                entry.Responded = false;
                entry.RespondedAtTime = 0f;
                boostingCount++;

                Plugin.Logger.LogInfo(
                    $"[SupplyChain] [clog-ctl] BOOST quota '{entry.ItemName}' {bestRow.Qty}+{delta}->{newQty} on '{warehouse.StructureName}' " +
                    $"to drain '{entry.StationName}'(x{stationCount}) room={bestRoom} cycle {entry.CycleCount}/{Plugin.ClogMaxCyclesPerItem.Value} " +
                    $"{MetabolicPlane.DescribeRate(entry.StationPosKey + "|" + entry.ItemName)}");
                SupplyChainTracker.ShowMessage(
                    $"Clog controller BOOST: '{entry.ItemName}' quota {bestRow.Qty}->{newQty} on '{warehouse.StructureName}' to drain '{entry.StationName}'.", 8f);
            }
            else
            {
                Plugin.Logger.LogInfo(
                    $"[SupplyChain] [clog-ctl] DRY-RUN would BOOST quota '{entry.ItemName}' +{delta} on '{warehouse.StructureName}' to drain '{entry.StationName}' — press F11 to arm");
                SupplyChainTracker.ShowMessage(
                    $"Clog controller [dry-run] would boost '{entry.ItemName}' on '{warehouse.StructureName}' — press F11 to arm", 8f);
                entry.WouldBoostLogged = true;
                entry.NextEligibleAt = now.AddSeconds(60);
                // Entry stays Pending — dry-run never advances the state machine past Pending.
            }
        }
    }

    // ── Shared revert (stale sweep + hold-elapsed/target-met/no-response/clog-cleared) ─────────
    // Mirrors SupplyController.RevertEntry's asymmetric ledger policy exactly: warehouse-not-found
    // KEEPS the ledger entry (restored at next world load); task-not-found REMOVES it (the warehouse
    // is there but the task itself is gone, nothing to restore).
    private static void Revert(ClogEntry entry, List<StationEntry> stations, string worldId, string reason)
    {
        string posKey = entry.BoostWarehousePosKey ?? "?";
        string stationName = entry.BoostWarehouseName ?? "?";
        int taskIndex = entry.BoostTaskIndex;
        string itemName = entry.ItemName;
        string supplyOwner = entry.BoostSupplyOwner ?? "-";

        StationEntry? warehouse = RankActuator.FindStation(stations, posKey, stationName);
        if (warehouse == null)
        {
            Plugin.Logger.LogWarning(
                $"[SupplyChain] [clog-ctl] Revert({reason}): warehouse '{stationName}' (posKey={posKey}) not found for '{itemName}' " +
                "— ledger entry kept for next world load; dropping to Cooldown.");
            RankActuator.BoostedStationKeys.Remove(posKey);
            MoveToCooldown(entry);
            return;
        }

        Il2CppSystem.Collections.Generic.List<WorkstationTaskData>? datas = null;
        try { datas = warehouse.Ws.GetWorkstationTaskDatas(); }
        catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] [clog-ctl] Revert GetWorkstationTaskDatas error: {ex}"); }

        WarehouseTasks.FindQuotaTask(datas, taskIndex, itemName, supplyOwner, out var rst);
        if (rst == null)
        {
            Plugin.Logger.LogWarning(
                $"[SupplyChain] [clog-ctl] Revert({reason}): task '{itemName}' not found on warehouse '{warehouse.StructureName}' " +
                "— dropping ledger entry; dropping to Cooldown.");
            SpikeLedger.Remove(new LedgerEntry { WorldId = worldId, PosKey = posKey, TaskIndex = taskIndex, ItemName = itemName, Kind = "quota" });
            RankActuator.BoostedStationKeys.Remove(posKey);
            MoveToCooldown(entry);
            return;
        }

        QuotaActuator.ApplyQuota(warehouse, rst, entry.BoostOrigQty);

        SpikeLedger.Remove(new LedgerEntry { WorldId = worldId, PosKey = posKey, TaskIndex = taskIndex, ItemName = itemName, Kind = "quota" });
        RankActuator.BoostedStationKeys.Remove(posKey);

        string respondedStr = entry.Responded ? "yes" : "no";
        string latencyStr = entry.Responded ? $"{(entry.RespondedAtTime - entry.StateSinceTime):F0}s" : "n/a";

        int hauledIn = 0;
        int stationDrop = 0;
        try
        {
            var info = rst.itemInfoQuantity.itemInfo;
            if (info != null)
            {
                var inv = warehouse.Ws.GetInventory();
                if (inv != null) hauledIn = inv.GetItemQuantity(info) - entry.WarehouseStoredAtBoost;

                StationEntry? cloggedStation = RankActuator.FindStation(stations, entry.StationPosKey, entry.StationName);
                if (cloggedStation != null)
                {
                    var sInv = cloggedStation.Ws.GetInventory();
                    if (sInv != null) stationDrop = entry.StationCountAtBoost - sInv.GetItemQuantity(info);
                }
            }
        }
        catch { }

        Plugin.Logger.LogInfo(
            $"[SupplyChain] [clog-ctl] REVERT ({reason}) '{itemName}': responded={respondedStr} latency={latencyStr} " +
            $"hauledIn={hauledIn} stationDrop={stationDrop} cycle {entry.CycleCount}/{Plugin.ClogMaxCyclesPerItem.Value}");
        SupplyChainTracker.ShowMessage(
            $"Clog controller REVERT ({reason}): '{itemName}' quota back to {entry.BoostOrigQty} on '{warehouse.StructureName}'.", 8f);

        MoveToCooldown(entry);
    }

    private static void MoveToCooldown(ClogEntry entry)
    {
        entry.State = State.Cooldown;
        entry.StateSinceTime = Time.time;
        entry.BoostWarehousePosKey = null;
        entry.BoostWarehouseName = null;
        entry.BoostSupplyOwner = null;
    }

    // ── Status line ──────────────────────────────────────────────────────────────────────────────
    private static void MaybeLogStatus(DateTime now)
    {
        if (_entries.Count == 0) return;
        if ((now - _lastStatusAt).TotalSeconds < 60) return;
        _lastStatusAt = now;

        int pending = 0, boosting = 0, cooldown = 0, lockout = 0;
        foreach (var kv in _entries)
        {
            switch (kv.Value.State)
            {
                case State.Pending: pending++; break;
                case State.Boosting: boosting++; break;
                case State.Cooldown: cooldown++; break;
                case State.CapacityLockout: lockout++; break;
            }
        }

        string detail = "";
        if (_entries.Count <= 8)
        {
            var sb = new StringBuilder();
            foreach (var kv in _entries)
            {
                var e = kv.Value;
                double age = (now - e.FirstSeen).TotalSeconds;
                sb.Append(" | ").Append(e.ItemName)
                  .Append("(state=").Append(e.State)
                  .Append(" age=").Append(age.ToString("F0")).Append('s')
                  .Append(" sightings=").Append(e.Sightings)
                  .Append(" station='").Append(e.StationName).Append('\'')
                  .Append(')');
            }
            detail = sb.ToString();
        }

        Plugin.Logger.LogInfo(
            $"[SupplyChain] [clog-ctl] status: clogs={_entries.Count} pending={pending} boosting={boosting} " +
            $"cooldown={cooldown} lockout={lockout} armed={SupplyController.Armed}{detail}");
    }
}
