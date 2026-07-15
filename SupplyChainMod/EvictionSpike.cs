using System;
using System.Collections.Generic;
using SandSailorStudio.Inventory;
using SSSGame;
using SSSGame.AI;
using SSSGame.Network;
using UnityEngine;

namespace SupplyChainMod;

// EvictionSpike (v0.7.0, Phase 2d fire-verify spike) — a hotkey-gated spike that fire-verifies the
// two runtime unknowns the Phase 2d design (soft quota budgeting + eviction + tier lever) depends
// on, mirroring QuotaSpike's structure (hotkey/state/ledger/OnWorldReady/NoteWorldLeft):
//
//   (a) ground-drop eviction — does ItemContainer.DropItem(Item, count, pos, rot, parent), reached
//       from a warehouse row via ResourceStorageTaskData.storageSupply.interaction.Container, spawn
//       a ground item and decrement the container? Plain hotkey (F6 by default): one-shot, no
//       ledger (nothing to restore — a dropped item just lives in the world), auto-selects the
//       best-stocked true-warehouse row (or EvictItem if set), drops EvictDropCount units, and
//       schedules a ONE-SHOT delayed re-check (next Tick) to see whether the decrement stuck or a
//       hauler already refilled.
//
//   (b) tier write — does Rpc_ChangeTaskPriority(taskIndex, newPriority), inherited by
//       NetworkCompositeResourceStorage/NetworkSimpleResourceStorage from the generic
//       NetworkWorkstation<T> base, actually move a warehouse row's tier (High=0/Med=1/Low=2/
//       None=3 VALUE, never a rank/list-move — confirmed 2026-07-14)? Shift+hotkey: stateful,
//       write-ahead ledgered (Kind="tier", shared SpikeLedger schema — OrigPriority/BoostPriority
//       carry orig/boosted TIER VALUES), boosts a Med row to High, watches for the write to land
//       (RPC first, direct SetPriority fallback), holds, then reverts — logging every phase
//       transition LOUDLY so the two write paths are unambiguously proven or disproven.
//
// Never cache interop wrappers (Workstation/ResourceStorageTaskData/ItemInfo/ItemContainer/Item)
// across ticks (project-wide gotcha) — ActiveTier and DelayedRecheck hold only strings/ints/
// floats/bools; every Tick touch re-resolves the warehouse/task/container fresh by
// PosKey/TaskIndex/ItemName/SupplyOwner. Wrappers obtained during a single hotkey call (RunDropTest,
// StartTier) are local to that call only. No Harmony patches are added by this module — DropItem and
// friends have inventory-family parameters, a forbidden patch target (project-wide gotcha); this
// spike only ever CALLS these methods directly from a hotkey/tick, never patches them.
internal static class EvictionSpike
{
    // Tier test state — re-resolved by PosKey/TaskIndex/ItemName/SupplyOwner at every tick, never a
    // cached wrapper. Snapshot is a plain-value copy of every row on the target warehouse at boost
    // time, used to detect the RPC landing on the WRONG row.
    private sealed class ActiveTier
    {
        public string WorldId = "?";
        public string PosKey = "?";
        public string StationName = "?";
        public string ItemName = "?";
        public string SupplyOwner = "-";
        public int TaskIndex;
        public int OrigRank;
        public int TargetRank; // always 0 (High) this version
        public string Phase = "rpcWait"; // "rpcWait" | "directWait" | "hold" | "revertWait" | "revertRetry"
        public float PhaseStart;
        public float StartTime;
        public string ProvenPath = ""; // "" | "rpc" | "direct"
        public bool RevertPathTried;
        public List<(int taskIndex, string itemName, int rank)> Snapshot = new();
    }

    // One-shot delayed re-check for the drop test — set after a drop, consumed on the NEXT Tick.
    private sealed class DelayedRecheck
    {
        public string PosKey = "?";
        public string StationName = "?";
        public string ItemName = "?";
        public string SupplyOwner = "-";
        public int TaskIndex;
        public int ExpectedContainerCount;
        public int DropCount;
        public float SetAtTime;
    }

    private static ActiveTier? _activeTier;
    private static DelayedRecheck? _delayedRecheck;

    // ── Public surface (all wrapped so no exception escapes the tracker) ────────────────────────
    internal static void OnHotkey(List<StationEntry> stations, string worldId, bool tier)
    {
        try
        {
            if (tier) { HandleTierHotkey(stations, worldId); return; }
            RunDropTest(stations);
        }
        catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] [evict] OnHotkey error: {ex}"); }
    }

    internal static void Tick(List<StationEntry> stations)
    {
        try { TickDelayedRecheck(stations); }
        catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] [evict] Tick (delayed re-check) error: {ex}"); }

        try { TickTier(stations); }
        catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] [tier] Tick error: {ex}"); }
    }

    internal static void OnWorldReady(string worldId, List<StationEntry> stations)
    {
        try
        {
            var entries = SpikeLedger.LoadFor(worldId);
            foreach (var entry in entries)
            {
                // Shared ledger with QuotaSpike/ClogController (Kind="quota") and ActuationSpike/
                // SupplyController (Kind="rank") — only restore this experiment's own kind here.
                if (entry.Kind != "tier") continue;
                try { RestoreTierLedgerEntry(entry, stations); }
                catch (Exception ex)
                {
                    Plugin.Logger.LogError($"[SupplyChain] [tier] Restore entry error: {ex}");
                    try { SpikeLedger.Remove(entry); } catch { }
                }
            }
        }
        catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] [tier] OnWorldReady error: {ex}"); }
    }

    internal static void NoteWorldLeft()
    {
        try
        {
            if (_activeTier != null)
            {
                Plugin.Logger.LogWarning(
                    $"[SupplyChain] [tier] World left with an active tier test — item='{_activeTier.ItemName}' " +
                    $"station='{_activeTier.StationName}' phase={_activeTier.Phase} — ledger entry (if any) remains " +
                    $"for restore at the next load of world {_activeTier.WorldId}.");
                RankActuator.BoostedStationKeys.Remove(_activeTier.PosKey);
            }
            _activeTier = null;
            _delayedRecheck = null;
        }
        catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] [evict] NoteWorldLeft error: {ex}"); }
    }

    // ── Drop test (plain hotkey) — one-shot, no ledger ──────────────────────────────────────────
    private static void RunDropTest(List<StationEntry> stations)
    {
        var allRows = WarehouseTasks.BuildRows(stations);
        var rows = new List<WarehouseTasks.WarehouseTaskRow>();
        foreach (var r in allRows) if (r.HasTaskContainer) rows.Add(r);

        string itemFilter = (Plugin.EvictItem.Value ?? "").Trim();
        if (itemFilter.Length > 0)
        {
            var filtered = new List<WarehouseTasks.WarehouseTaskRow>();
            foreach (var r in rows)
                if (string.Equals(r.ItemName.Trim(), itemFilter, StringComparison.OrdinalIgnoreCase))
                    filtered.Add(r);
            if (filtered.Count == 0)
            {
                Plugin.Logger.LogInfo($"[SupplyChain] [evict] no row matches EvictItem='{itemFilter}' among {rows.Count} true-warehouse row(s).");
                SupplyChainTracker.ShowMessage($"Evict: no droppable row found for '{itemFilter}'.", 6f);
                return;
            }
            rows = filtered;
        }

        WarehouseTasks.WarehouseTaskRow? bestRow = null;
        StorageInteraction? bestSi = null;
        ItemContainer? bestContainer = null;
        int bestCnt = 0;
        int nullInteraction = 0, nullContainer = 0, zeroCount = 0;

        foreach (var r in rows)
        {
            StorageInteraction? rowSi = null;
            try { rowSi = r.Rst.storageSupply.interaction; } catch { }
            if (rowSi == null) { nullInteraction++; continue; }

            ItemContainer? rowContainer = null;
            try { rowContainer = rowSi.Container; } catch { }
            if (rowContainer == null) { nullContainer++; continue; }

            int cnt = 0;
            try { cnt = rowContainer.GetItemCount(r.Rst.itemInfoQuantity.itemInfo); } catch { }
            if (cnt < 1) { zeroCount++; continue; }

            if (cnt > bestCnt)
            {
                bestCnt = cnt;
                bestRow = r;
                bestSi = rowSi;
                bestContainer = rowContainer;
            }
        }

        if (bestRow == null || bestSi == null || bestContainer == null)
        {
            Plugin.Logger.LogInfo(
                $"[SupplyChain] [evict] no candidate — rows={rows.Count} nullInteraction={nullInteraction} " +
                $"nullContainer={nullContainer} zeroCount={zeroCount}");
            SupplyChainTracker.ShowMessage("Evict: no droppable row found.", 6f);
            return;
        }

        var row = bestRow;
        var si = bestSi;
        var container = bestContainer;
        var warehouse = row.Warehouse;

        bool allowed = true;
        try
        {
            allowed = warehouse.Ws.HasStateAuthority;
            if (!allowed)
            {
                Plugin.Logger.LogWarning($"[SupplyChain] [evict] blocked: not host (HasStateAuthority=false) for station '{warehouse.StructureName}'.");
                SupplyChainTracker.ShowMessage("Evict blocked: not the session host.", 8f);
                return;
            }
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogInfo($"[SupplyChain] [evict] HasStateAuthority unreadable ({ex.Message}) — assuming offline/solo, allowing.");
        }

        ItemInfo? info = null;
        try { info = row.Rst.itemInfoQuantity.itemInfo; } catch { }
        if (info == null)
        {
            Plugin.Logger.LogError("[SupplyChain] [evict] LOUD: could not re-read itemInfoQuantity.itemInfo on the selected row — aborting.");
            return;
        }

        int capacity = 0;
        try { capacity = container.capacity; } catch { }
        int bound = capacity > 0 ? capacity : 64;

        Item? item = null;
        try
        {
            var items = container.GetItems();
            for (int i = 0; i < bound; i++)
            {
                Item? candidate = null;
                try { candidate = items != null ? items[i] : null; }
                catch { break; } // past the real slot count
                if (candidate == null) continue;
                string nm = "?";
                try { nm = Common.SafeItemName(candidate.info); } catch { }
                if (string.Equals(nm, row.ItemName, StringComparison.Ordinal)) { item = candidate; break; }
            }
        }
        catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] [evict] GetItems enumeration error: {ex}"); }

        if (item == null)
        {
            Plugin.Logger.LogError(
                $"[SupplyChain] [evict] LOUD: no item stack matching '{row.ItemName}' found in container despite cnt={bestCnt} " +
                "(shouldn't happen) — aborting.");
            return;
        }

        int itemCount = 0;
        try { itemCount = item.count; } catch { }
        int dropCount = Math.Min(Math.Min(Math.Max(1, Math.Min(Plugin.EvictDropCount.Value, 5)), itemCount), bestCnt);
        if (dropCount < 1)
        {
            Plugin.Logger.LogError($"[SupplyChain] [evict] LOUD: computed dropCount < 1 (itemCount={itemCount} cnt={bestCnt}) — aborting.");
            return;
        }

        int containerBefore = bestCnt;
        try { containerBefore = container.GetItemCount(info); } catch { }

        int storedBefore = -1;
        try
        {
            var inv = warehouse.Ws.GetInventory();
            if (inv != null) storedBefore = inv.GetItemQuantity(info);
        }
        catch { }

        Vector3 pos = default;
        try
        {
            var t = si.transform;
            pos = t.position + t.forward * 1.2f + Vector3.up * 0.6f;
        }
        catch
        {
            try { pos = warehouse.Ws.transform.position + Vector3.up; } catch { }
        }
        string posStr = pos.ToString();
        Plugin.Logger.LogInfo($"[SupplyChain] [evict] drop position: {posStr}");

        try
        {
            container.DropItem(item, dropCount, pos, Quaternion.identity, null);
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"[SupplyChain] [evict] LOUD: DropItem threw: {ex}");
            SupplyChainTracker.ShowMessage("Evict: DropItem threw an exception — see log.", 8f);
            return;
        }

        int containerAfter = containerBefore;
        try { containerAfter = container.GetItemCount(info); } catch { }

        int storedAfter = -1;
        try
        {
            var inv = warehouse.Ws.GetInventory();
            if (inv != null) storedAfter = inv.GetItemQuantity(info);
        }
        catch { }

        int decrement = containerBefore - containerAfter;
        if (decrement == dropCount)
        {
            Plugin.Logger.LogInfo(
                $"[SupplyChain] [evict] DROP fire-verified: '{row.ItemName}' container {containerBefore}->{containerAfter}, " +
                $"stored {storedBefore}->{storedAfter}, dropped {dropCount} at {posStr} — visually confirm ground item(s) " +
                "(GroundItemVacuum 'n' can recover them).");
            SupplyChainTracker.ShowMessage(
                $"Evict: dropped {dropCount}x '{row.ItemName}' from '{warehouse.StructureName}' — container {containerBefore}->{containerAfter}.", 10f);
        }
        else
        {
            Plugin.Logger.LogError(
                $"[SupplyChain] [evict] DROP MISMATCH: '{row.ItemName}' container {containerBefore}->{containerAfter} " +
                $"(expected -{dropCount}), stored {storedBefore}->{storedAfter}, dropCount={dropCount} at {posStr}.");
            SupplyChainTracker.ShowMessage(
                $"Evict MISMATCH: '{row.ItemName}' container {containerBefore}->{containerAfter} (expected drop {dropCount}) — see log.", 10f);
        }

        _delayedRecheck = new DelayedRecheck
        {
            PosKey = warehouse.PosKey,
            StationName = warehouse.StructureName,
            ItemName = row.ItemName,
            SupplyOwner = row.SupplyOwner,
            TaskIndex = row.TaskIndex,
            ExpectedContainerCount = containerAfter,
            DropCount = dropCount,
            SetAtTime = Time.time,
        };
    }

    private static void TickDelayedRecheck(List<StationEntry> stations)
    {
        var pending = _delayedRecheck;
        if (pending == null) return;
        _delayedRecheck = null; // one-shot; clear regardless of outcome

        StationEntry? station = RankActuator.FindStation(stations, pending.PosKey, pending.StationName);
        if (station == null)
        {
            Plugin.Logger.LogInfo($"[SupplyChain] [evict] delayed re-check: station '{pending.StationName}' not found — skipping.");
            return;
        }

        Il2CppSystem.Collections.Generic.List<WorkstationTaskData>? datas = null;
        try { datas = station.Ws.GetWorkstationTaskDatas(); }
        catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] [evict] delayed re-check GetWorkstationTaskDatas error: {ex}"); return; }

        WarehouseTasks.FindQuotaTask(datas, pending.TaskIndex, pending.ItemName, pending.SupplyOwner, out var rst);
        if (rst == null)
        {
            Plugin.Logger.LogInfo($"[SupplyChain] [evict] delayed re-check: task '{pending.ItemName}' not found on '{station.StructureName}' — skipping.");
            return;
        }

        StorageInteraction? si = null;
        try { si = rst.storageSupply.interaction; } catch { }
        ItemContainer? container = si != null ? SafeContainer(si) : null;
        if (container == null)
        {
            Plugin.Logger.LogInfo("[SupplyChain] [evict] delayed re-check: interaction/container not resolvable — skipping.");
            return;
        }

        ItemInfo? info = null;
        try { info = rst.itemInfoQuantity.itemInfo; } catch { }
        int nowCount = -1;
        if (info != null) { try { nowCount = container.GetItemCount(info); } catch { } }

        float elapsed = Time.time - pending.SetAtTime;
        string verdict = nowCount == pending.ExpectedContainerCount ? "decrement held" : "count changed since (hauler refill or other activity)";
        Plugin.Logger.LogInfo(
            $"[SupplyChain] [evict] delayed re-check: '{pending.ItemName}' container was {pending.ExpectedContainerCount}, now {nowCount} " +
            $"(dropCount={pending.DropCount}) after {elapsed:F0}s — {verdict}.");
    }

    private static ItemContainer? SafeContainer(StorageInteraction si)
    {
        try { return si.Container; } catch { return null; }
    }

    // ── Tier test (Shift+hotkey) — stateful, write-ahead ledgered ───────────────────────────────
    private static void HandleTierHotkey(List<StationEntry> stations, string worldId)
    {
        var active = _activeTier;
        if (active == null)
        {
            StartTier(stations, worldId);
            return;
        }

        if (active.Phase == "rpcWait" || active.Phase == "directWait" || active.Phase == "hold")
        {
            StationEntry? station = RankActuator.FindStation(stations, active.PosKey, active.StationName);
            if (station == null)
            {
                Plugin.Logger.LogWarning(
                    $"[SupplyChain] [tier] Shift press: station '{active.StationName}' not found — cannot revert now; " +
                    "ledger entry kept, restored at next world load.");
                SupplyChainTracker.ShowMessage("Tier test: station not found — cannot revert now.", 8f);
                return;
            }

            Il2CppSystem.Collections.Generic.List<WorkstationTaskData>? datas = null;
            try { datas = station.Ws.GetWorkstationTaskDatas(); }
            catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] [tier] Shift press GetWorkstationTaskDatas error: {ex}"); return; }

            WarehouseTasks.FindQuotaTask(datas, active.TaskIndex, active.ItemName, active.SupplyOwner, out var rst);
            if (rst == null)
            {
                Plugin.Logger.LogWarning("[SupplyChain] [tier] Shift press: task not found on station — cannot revert now.");
                SupplyChainTracker.ShowMessage("Tier test: task not found — cannot revert now.", 8f);
                return;
            }

            string path = string.IsNullOrEmpty(active.ProvenPath) ? "direct" : active.ProvenPath;
            ApplyRevertVia(station, rst, active, path);
            active.Phase = "revertWait";
            active.PhaseStart = Time.time;

            Plugin.Logger.LogInfo($"[SupplyChain] [tier] Shift press: manual revert requested (path={path}).");
            SupplyChainTracker.ShowMessage($"Tier test: manual revert requested (path={path}).", 8f);
        }
        else
        {
            Plugin.Logger.LogInfo($"[SupplyChain] [tier] Shift press ignored — already reverting (phase={active.Phase}).");
        }
    }

    private static void StartTier(List<StationEntry> stations, string worldId)
    {
        var allRows = WarehouseTasks.BuildRows(stations);
        var rows = new List<WarehouseTasks.WarehouseTaskRow>();
        foreach (var r in allRows) if (r.HasTaskContainer) rows.Add(r);

        string itemFilter = (Plugin.TierItem.Value ?? "").Trim();
        WarehouseTasks.WarehouseTaskRow? target = null;

        if (itemFilter.Length > 0)
        {
            bool anyMatch = false;
            WarehouseTasks.WarehouseTaskRow? medRow = null;
            WarehouseTasks.WarehouseTaskRow? firstNonHigh = null;
            foreach (var r in rows)
            {
                if (!string.Equals(r.ItemName.Trim(), itemFilter, StringComparison.OrdinalIgnoreCase)) continue;
                anyMatch = true;
                if (r.Rank == 1 && medRow == null) medRow = r;
                if (r.Rank != 0 && firstNonHigh == null) firstNonHigh = r;
            }
            if (!anyMatch)
            {
                Plugin.Logger.LogInfo($"[SupplyChain] [tier] no row matches TierItem='{itemFilter}'.");
                SupplyChainTracker.ShowMessage($"Tier test: no row found for '{itemFilter}'.", 6f);
                return;
            }
            target = medRow ?? firstNonHigh;
            if (target == null)
            {
                Plugin.Logger.LogInfo($"[SupplyChain] [tier] TierItem='{itemFilter}' — all matching rows already High.");
                SupplyChainTracker.ShowMessage($"Tier test: '{itemFilter}' already High.", 6f);
                return;
            }
        }
        else
        {
            foreach (var r in rows) { if (r.Rank == 1) { target = r; break; } }
            if (target == null)
            {
                Plugin.Logger.LogInfo("[SupplyChain] [tier] no Med-tier true-warehouse row found (auto-select).");
                SupplyChainTracker.ShowMessage("Tier test: no Med-tier row found.", 6f);
                return;
            }
        }

        var row = target;
        var warehouse = row.Warehouse;

        bool allowed = true;
        try
        {
            allowed = warehouse.Ws.HasStateAuthority;
            if (!allowed)
            {
                Plugin.Logger.LogWarning($"[SupplyChain] [tier] blocked: not host (HasStateAuthority=false) for station '{warehouse.StructureName}'.");
                SupplyChainTracker.ShowMessage("Tier test blocked: not the session host.", 8f);
                return;
            }
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogInfo($"[SupplyChain] [tier] HasStateAuthority unreadable ({ex.Message}) — assuming offline/solo, allowing.");
        }

        var snapshot = new List<(int taskIndex, string itemName, int rank)>();
        foreach (var r in allRows)
        {
            if (r.Warehouse.PosKey != warehouse.PosKey) continue;
            snapshot.Add((r.TaskIndex, r.ItemName, r.Rank));
        }

        int origRank = row.Rank;
        const int targetRank = 0; // High

        var ledgerEntry = new LedgerEntry
        {
            WorldId = worldId,
            PosKey = warehouse.PosKey,
            StationName = warehouse.StructureName,
            TaskIndex = row.TaskIndex,
            ItemName = row.ItemName,
            OrigPriority = origRank,
            BoostPriority = targetRank,
            UtcTicks = DateTime.UtcNow.Ticks,
            Kind = "tier",
            SupplyOwner = row.SupplyOwner,
        };
        SpikeLedger.Append(ledgerEntry);

        RankActuator.BoostedStationKeys.Add(warehouse.PosKey);

        var (composite, simple, via) = ProbeTierComponent(warehouse);
        string foundClass = composite != null ? Common.NativeClassName(composite) : (simple != null ? Common.NativeClassName(simple) : "none");
        Plugin.Logger.LogInfo($"[SupplyChain] [tier] network probe: found={foundClass} via={via}");

        if (composite == null && simple == null)
        {
            Plugin.Logger.LogError("[SupplyChain] [tier] LOUD: no network component found — cannot fire tier RPC.");
            SpikeLedger.Remove(ledgerEntry);
            RankActuator.BoostedStationKeys.Remove(warehouse.PosKey);
            SupplyChainTracker.ShowMessage("Tier test: no network component found — cannot fire RPC.", 8f);
            return;
        }

        try
        {
            if (composite != null) composite.Rpc_ChangeTaskPriority(row.TaskIndex, targetRank);
            else simple!.Rpc_ChangeTaskPriority(row.TaskIndex, targetRank);
            Plugin.Logger.LogInfo($"[SupplyChain] [tier] Rpc_ChangeTaskPriority({row.TaskIndex}, {targetRank}) fired via {foundClass} ({via}).");
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"[SupplyChain] [tier] LOUD: Rpc_ChangeTaskPriority threw: {ex}");
        }

        int readback = -1;
        try { readback = row.Rst.priority; } catch { }
        Plugin.Logger.LogInfo($"[SupplyChain] [tier] immediate readback after RPC: priority={readback}");

        _activeTier = new ActiveTier
        {
            WorldId = worldId,
            PosKey = warehouse.PosKey,
            StationName = warehouse.StructureName,
            ItemName = row.ItemName,
            SupplyOwner = row.SupplyOwner,
            TaskIndex = row.TaskIndex,
            OrigRank = origRank,
            TargetRank = targetRank,
            Phase = "rpcWait",
            PhaseStart = Time.time,
            StartTime = Time.time,
            ProvenPath = "",
            RevertPathTried = false,
            Snapshot = snapshot,
        };

        Plugin.Logger.LogInfo($"[SupplyChain] [tier] TIER TEST START: '{row.ItemName}' rank {origRank}->{targetRank} on '{warehouse.StructureName}' (task[{row.TaskIndex}]).");
        SupplyChainTracker.ShowMessage($"Tier test: '{row.ItemName}' Med->High RPC fired on '{warehouse.StructureName}'.", 8f);
    }

    private static void TickTier(List<StationEntry> stations)
    {
        var active = _activeTier;
        if (active == null) return;

        // Abort rail — checked every tick regardless of phase.
        if (Time.time - active.StartTime > Plugin.TierAbortSeconds.Value)
        {
            AbortTier(stations, active);
            return;
        }

        StationEntry? station = RankActuator.FindStation(stations, active.PosKey, active.StationName);
        if (station == null) return; // world may be mid-resolve; avoid noisy per-tick logging

        Il2CppSystem.Collections.Generic.List<WorkstationTaskData>? datas = null;
        try { datas = station.Ws.GetWorkstationTaskDatas(); }
        catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] [tier] TickTier GetWorkstationTaskDatas error: {ex}"); return; }
        if (datas == null) return;

        var wrongRow = DiffAndFindWrongRow(active, datas);

        WarehouseTasks.FindQuotaTask(datas, active.TaskIndex, active.ItemName, active.SupplyOwner, out var rst);
        if (rst == null) return; // our row gone this tick — a hotkey/ledger restore reports it properly

        int ourRank = -1;
        try { ourRank = rst.priority; } catch { }

        float elapsedInPhase = Time.time - active.PhaseStart;

        switch (active.Phase)
        {
            case "rpcWait":
                if (ourRank == active.TargetRank)
                {
                    Plugin.Logger.LogInfo($"[SupplyChain] [tier] TIER RPC FIRE-VERIFIED (applied after {elapsedInPhase:F1} s)");
                    active.ProvenPath = "rpc";
                    active.Phase = "hold";
                    active.PhaseStart = Time.time;
                    SupplyChainTracker.ShowMessage($"Tier test: RPC fire-verified for '{active.ItemName}'.", 8f);
                }
                else if (wrongRow != null)
                {
                    var (wIdx, wItem, wOrigRank) = wrongRow.Value;
                    Plugin.Logger.LogError(
                        $"[SupplyChain] [tier] WRONG-ROW: Rpc_ChangeTaskPriority({active.TaskIndex}) hit task[{wIdx}] '{wItem}' instead.");
                    RevertWrongRow(station, datas, wIdx, wOrigRank);
                    SpikeLedger.Remove(new LedgerEntry { WorldId = active.WorldId, PosKey = active.PosKey, TaskIndex = active.TaskIndex, ItemName = active.ItemName, Kind = "tier" });
                    RankActuator.BoostedStationKeys.Remove(active.PosKey);
                    _activeTier = null;
                    SupplyChainTracker.ShowMessage($"Tier test: RPC hit the WRONG row ('{wItem}') — reverted, see log.", 10f);
                }
                else if (elapsedInPhase > Plugin.TierRpcTimeoutSeconds.Value)
                {
                    Plugin.Logger.LogInfo($"[SupplyChain] [tier] RPC ineffective after {elapsedInPhase:F0} s — trying direct value write");
                    DirectWrite(station, rst, active.TargetRank);
                    active.Phase = "directWait";
                    active.PhaseStart = Time.time;
                }
                break;

            case "directWait":
                if (ourRank == active.TargetRank)
                {
                    Plugin.Logger.LogInfo("[SupplyChain] [tier] TIER DIRECT WRITE VERIFIED (RPC was inert)");
                    active.ProvenPath = "direct";
                    active.Phase = "hold";
                    active.PhaseStart = Time.time;
                    SupplyChainTracker.ShowMessage($"Tier test: direct write verified for '{active.ItemName}' (RPC was inert).", 8f);
                }
                else if (elapsedInPhase > Plugin.TierRpcTimeoutSeconds.Value)
                {
                    Plugin.Logger.LogError("[SupplyChain] [tier] LOUD: NO TIER WRITE PATH: both RPC and direct write left rank unchanged.");
                    SpikeLedger.Remove(new LedgerEntry { WorldId = active.WorldId, PosKey = active.PosKey, TaskIndex = active.TaskIndex, ItemName = active.ItemName, Kind = "tier" });
                    RankActuator.BoostedStationKeys.Remove(active.PosKey);
                    _activeTier = null;
                    SupplyChainTracker.ShowMessage("Tier test: NO write path worked — see log.", 10f);
                }
                break;

            case "hold":
                if (elapsedInPhase > Plugin.TierHoldSeconds.Value)
                {
                    ApplyRevertVia(station, rst, active, active.ProvenPath);
                    active.Phase = "revertWait";
                    active.PhaseStart = Time.time;
                }
                break;

            case "revertWait":
                if (ourRank == active.OrigRank)
                {
                    Plugin.Logger.LogInfo($"[SupplyChain] [tier] TIER CYCLE COMPLETE: boost + revert both confirmed (path={active.ProvenPath})");
                    SpikeLedger.Remove(new LedgerEntry { WorldId = active.WorldId, PosKey = active.PosKey, TaskIndex = active.TaskIndex, ItemName = active.ItemName, Kind = "tier" });
                    RankActuator.BoostedStationKeys.Remove(active.PosKey);
                    _activeTier = null;
                    SupplyChainTracker.ShowMessage($"Tier test complete: '{active.ItemName}' boost + revert confirmed (path={active.ProvenPath}).", 10f);
                }
                else if (elapsedInPhase > Plugin.TierRpcTimeoutSeconds.Value)
                {
                    string otherPath = active.ProvenPath == "rpc" ? "direct" : "rpc";
                    Plugin.Logger.LogInfo($"[SupplyChain] [tier] revert not yet confirmed via '{active.ProvenPath}' — trying '{otherPath}' once.");
                    active.RevertPathTried = true;
                    ApplyRevertVia(station, rst, active, otherPath);
                    active.Phase = "revertRetry";
                    active.PhaseStart = Time.time;
                }
                break;

            case "revertRetry":
                if (ourRank == active.OrigRank)
                {
                    Plugin.Logger.LogInfo($"[SupplyChain] [tier] TIER CYCLE COMPLETE (revert confirmed on retry, path={active.ProvenPath})");
                    SpikeLedger.Remove(new LedgerEntry { WorldId = active.WorldId, PosKey = active.PosKey, TaskIndex = active.TaskIndex, ItemName = active.ItemName, Kind = "tier" });
                    RankActuator.BoostedStationKeys.Remove(active.PosKey);
                    _activeTier = null;
                    SupplyChainTracker.ShowMessage($"Tier test complete: '{active.ItemName}' revert confirmed on retry.", 10f);
                }
                else if (elapsedInPhase > Plugin.TierRpcTimeoutSeconds.Value)
                {
                    Plugin.Logger.LogError(
                        $"[SupplyChain] [tier] LOUD: REVERT UNCONFIRMED — ledger entry KEPT; will restore at next world load " +
                        $"(revertPathTried={active.RevertPathTried}).");
                    RankActuator.BoostedStationKeys.Remove(active.PosKey);
                    _activeTier = null;
                    SupplyChainTracker.ShowMessage("Tier test: revert UNCONFIRMED — will restore at next world load.", 10f);
                }
                break;
        }
    }

    private static void AbortTier(List<StationEntry> stations, ActiveTier active)
    {
        Plugin.Logger.LogError($"[SupplyChain] [tier] LOUD: TierAbortSeconds ({Plugin.TierAbortSeconds.Value:F0}) elapsed — aborting tier test '{active.ItemName}'.");

        bool confirmedOrig = false;
        StationEntry? station = RankActuator.FindStation(stations, active.PosKey, active.StationName);
        if (station != null)
        {
            Il2CppSystem.Collections.Generic.List<WorkstationTaskData>? datas = null;
            try { datas = station.Ws.GetWorkstationTaskDatas(); } catch { }
            if (datas != null)
            {
                WarehouseTasks.FindQuotaTask(datas, active.TaskIndex, active.ItemName, active.SupplyOwner, out var rst);
                if (rst != null)
                {
                    DirectWrite(station, rst, active.OrigRank);
                    int readback = -1;
                    try { readback = rst.priority; } catch { }
                    confirmedOrig = readback == active.OrigRank;
                }
            }
        }

        if (confirmedOrig)
        {
            SpikeLedger.Remove(new LedgerEntry { WorldId = active.WorldId, PosKey = active.PosKey, TaskIndex = active.TaskIndex, ItemName = active.ItemName, Kind = "tier" });
            Plugin.Logger.LogInfo("[SupplyChain] [tier] abort: direct-write revert confirmed — ledger entry removed.");
        }
        else
        {
            Plugin.Logger.LogWarning("[SupplyChain] [tier] abort: revert NOT confirmed — ledger entry KEPT for restore at next world load.");
        }

        RankActuator.BoostedStationKeys.Remove(active.PosKey);
        _activeTier = null;
        SupplyChainTracker.ShowMessage(
            $"Tier test ABORTED — '{active.ItemName}': {(confirmedOrig ? "reverted" : "revert unconfirmed, will restore next load")}.", 10f);
    }

    // ── Shared tier write helpers ────────────────────────────────────────────────────────────────
    private static void DirectWrite(StationEntry station, ResourceStorageTaskData rst, int newRank)
    {
        try { rst.SetPriority(newRank); }
        catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] [tier] SetPriority({newRank}) error: {ex}"); }

        HostUpdateViaProbe(station);

        int readback = -1;
        try { readback = rst.priority; } catch { }
        Plugin.Logger.LogInfo($"[SupplyChain] [tier] direct write: SetPriority({newRank}) readback={readback}");
    }

    private static void ApplyRevertVia(StationEntry station, ResourceStorageTaskData rst, ActiveTier active, string path)
    {
        if (path == "rpc")
        {
            var (composite, simple, via) = ProbeTierComponent(station);
            try
            {
                if (composite != null) composite.Rpc_ChangeTaskPriority(active.TaskIndex, active.OrigRank);
                else if (simple != null) simple.Rpc_ChangeTaskPriority(active.TaskIndex, active.OrigRank);
                else Plugin.Logger.LogError("[SupplyChain] [tier] LOUD: revert-via-rpc but no network component found.");
            }
            catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] [tier] revert-via-rpc error: {ex}"); }

            int readback = -1;
            try { readback = rst.priority; } catch { }
            Plugin.Logger.LogInfo($"[SupplyChain] [tier] revert via RPC({active.TaskIndex}, {active.OrigRank}) via {via} — immediate readback={readback}");
        }
        else
        {
            DirectWrite(station, rst, active.OrigRank);
        }
    }

    private static void RevertWrongRow(StationEntry station, Il2CppSystem.Collections.Generic.List<WorkstationTaskData> datas, int wrongIndex, int snapshotRank)
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

    // Probe order mirrors QuotaActuator.ProbeAndPushNetwork: own GameObject composite storage, then
    // own GameObject simple storage, then a child walk for each.
    private static (NetworkCompositeResourceStorage? composite, NetworkSimpleResourceStorage? simple, string via) ProbeTierComponent(StationEntry entry)
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

    // Diffs the boost-time snapshot against the current task list (by taskIndex), logging every
    // changed row, and reports the first row that flipped to TargetRank that ISN'T our own boosted
    // row (the RPC-hit-the-wrong-row failure mode).
    private static (int taskIndex, string itemName, int origRank)? DiffAndFindWrongRow(
        ActiveTier active, Il2CppSystem.Collections.Generic.List<WorkstationTaskData> datas)
    {
        (int taskIndex, string itemName, int origRank)? wrongRow = null;

        foreach (var snap in active.Snapshot)
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

            if (wrongRow == null && snap.taskIndex != active.TaskIndex && snap.rank != active.TargetRank && curRank == active.TargetRank)
            {
                wrongRow = (snap.taskIndex, curItem, snap.rank);
            }
        }

        return wrongRow;
    }

    // ── Ledger restore (world-load pass) ────────────────────────────────────────────────────────
    private static void RestoreTierLedgerEntry(LedgerEntry entry, List<StationEntry> stations)
    {
        StationEntry? station = RankActuator.FindStation(stations, entry.PosKey, entry.StationName);
        if (station == null)
        {
            Plugin.Logger.LogWarning(
                $"[SupplyChain] [tier] Stranded tier boost station '{entry.StationName}' (posKey={entry.PosKey}) " +
                "not found — keeping ledger entry, next load retries.");
            return;
        }

        Il2CppSystem.Collections.Generic.List<WorkstationTaskData>? datas = null;
        try { datas = station.Ws.GetWorkstationTaskDatas(); }
        catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] [tier] GetWorkstationTaskDatas error: {ex}"); }

        WarehouseTasks.FindQuotaTask(datas, entry.TaskIndex, entry.ItemName, entry.SupplyOwner, out var rst);
        if (rst == null)
        {
            Plugin.Logger.LogWarning(
                $"[SupplyChain] [tier] Stranded tier boost item '{entry.ItemName}' not found on station " +
                $"'{station.StructureName}' — dropping ledger entry.");
            SpikeLedger.Remove(entry);
            return;
        }

        int observed = -1;
        try { observed = rst.priority; } catch { }

        if (observed == entry.BoostPriority)
        {
            DirectWrite(station, rst, entry.OrigPriority);
            int readback = -1;
            try { readback = rst.priority; } catch { }
            if (readback == entry.OrigPriority)
            {
                Plugin.Logger.LogWarning(
                    $"[SupplyChain] [tier] Restored stranded tier boost: station='{station.StructureName}' item='{entry.ItemName}' " +
                    $"rank -> {entry.OrigPriority}");
                SupplyChainTracker.ShowMessage(
                    $"SupplyChain: restored stranded tier boost on '{station.StructureName}' ('{entry.ItemName}' back to {entry.OrigPriority}).", 12f);
            }
            else
            {
                Plugin.Logger.LogError(
                    $"[SupplyChain] [tier] LOUD: restore write did not stick (wrote {entry.OrigPriority}, read back {readback}) — dropping ledger entry.");
            }
            SpikeLedger.Remove(entry);
        }
        else if (observed == entry.OrigPriority)
        {
            Plugin.Logger.LogInfo($"[SupplyChain] [tier] Stranded tier boost '{entry.ItemName}' already at original rank ({observed}) — dropping ledger entry.");
            SpikeLedger.Remove(entry);
        }
        else
        {
            Plugin.Logger.LogWarning(
                $"[SupplyChain] [tier] Stranded tier boost '{entry.ItemName}' observed rank={observed} " +
                $"(neither orig={entry.OrigPriority} nor boost={entry.BoostPriority}) — dropping stale ledger entry.");
            SpikeLedger.Remove(entry);
        }
    }
}
