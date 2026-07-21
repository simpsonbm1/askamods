using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using SandSailorStudio.Inventory;
using SSSGame;
using UnityEngine;

namespace CraftFromStorageMod;

// Phase 1 (idea-17) core: turns v0.2.0's read-only trace patches (Patches/GatePatches.cs) into the
// actual storage-pull feature. Entry points are called from the SAME three patch families that
// already exist there:
//   - CheckOwnedRequirementsPatch.Postfix       -> TryReportAvailable   (point A)
//   - BeginCraftingSequence*Patch.Prefix (x3)   -> HandleBeginCraftingSequence (point C)
//   - OnCraftingSuccess*Patch.Postfix (x2)      -> HandleCraftingSuccess (point D)
//
// PLAYER CRAFTING ONLY - villager crafting is Phase 2 (out of scope). Villagers ride the SAME
// CheckOwnedRequirements gate as the player (confirmed fact), so every entry point below gates on
// the agent's native class being exactly "PlayerInteractionAgent" - this is not optional.
//
// Settlement-container reads/writes are gated on IsHostOrSolo() - the same co-op write-authority
// pattern OuthouseComposterMod (ComposterDiag.IsHostOrSolo) and GroundItemVacuumMod
// (VacuumTracker.IsHost) already use and have confirmed working in-game for settlement-shared state.
// Agent-inventory-only operations don't need it (each client already has authority over its own
// local player), but this mod always touches at least one settlement container per pull/sweep, so
// the gate is applied at the top of every entry point for simplicity.
internal static class CraftTransfer
{
    private sealed class LedgerEntry
    {
        internal ItemContainer SourceContainer = null!;
        internal Vector3 SourceWorldPos;
        internal ItemInfo Info = null!;
        internal int QtyMoved;
        internal int AgentQtyBeforePull;
    }

    private static readonly List<LedgerEntry> _ledger = new();

    // Reentrancy flag (design point A/C). While true, TryReportAvailable must report VANILLA truth,
    // never fabricate __result=true - this is what makes the verify-call in
    // HandleBeginCraftingSequence an honest check of what was actually moved instead of an infinite
    // "yes" loop that never validates anything.
    private static bool _verifying;

    // World-leave (CraftWatcher.ClearWorldState -> here). Never hold ItemContainer/ItemInfo wrappers
    // of per-world objects across sessions (project-wide gotcha).
    internal static void ClearWorldState()
    {
        if (_ledger.Count > 0)
            Plugin.Logger.LogWarning($"[CFS] CraftTransfer: world-leave with {_ledger.Count} unswept ledger " +
                "entr(y/ies) - discarding (cannot safely sweep across a world boundary). Items already moved into " +
                "the agent's inventory are NOT lost (player inventory persists); they simply won't be swept back.");
        _ledger.Clear();
        _verifying = false;
    }

    // ---- Point A: called from Patches/GatePatches.cs CheckOwnedRequirementsPatch.Postfix. Only
    // ever WIDENS __result (false -> true); never narrows an already-true vanilla result. ----
    internal static void TryReportAvailable(CraftInteraction instance, Blueprint bp, IInteractionAgent agent, ref bool result)
    {
        try
        {
            if (!SafeGet(Plugin.EnablePlayerPull, true)) return;
            if (result) return;
            if (_verifying) return; // reentrancy: report vanilla truth during our own verify call
            if (Plugin.NativeClassName(agent) != "PlayerInteractionAgent") return;
            if (!IsHostOrSolo()) return;

            var wanted = BuildWantedManifest(bp);
            if (wanted == null) return;

            ItemCollection? agentInv = SafeGetInventory(agent);
            ItemCollection? stationInv = SafeGetStationInventory(instance);

            var shortfall = ComputeShortfall(wanted, agentInv, stationInv);
            if (shortfall.Count == 0) return; // nothing missing but __result was false - not ours to fix

            foreach (var (info, missing) in shortfall)
            {
                int avail = SettlementStock.GetAvailableQuantity(info);
                if (avail < missing) return; // settlement can't cover it either - leave __result alone
            }

            result = true;
            if (Plugin.TransferDiagnostics.Value)
                Plugin.Logger.LogInfo($"[CFS] TryReportAvailable: settlement storage can cover {shortfall.Count} " +
                    "missing item type(s) - reporting craftable.");
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"[CFS] CraftTransfer.TryReportAvailable error: {ex}");
        }
    }

    // ---- Point C: called from the 3 BeginCraftingSequence prefixes (Patches/GatePatches.cs).
    // Returns true = let vanilla run as normal; false = abort the craft (already swept back, or
    // nothing was ever moved). ----
    internal static bool HandleBeginCraftingSequence(CraftInteraction instance, InteractionSession session)
    {
        ItemCollection? agentInv = null;
        try
        {
            if (!SafeGet(Plugin.EnablePlayerPull, true)) return true;

            IInteractionAgent? agent = null;
            try { agent = session?.Agent; } catch { }
            if (agent == null) return true;
            if (Plugin.NativeClassName(session) != "PlayerCraftInteractionSession") return true;
            if (Plugin.NativeClassName(agent) != "PlayerInteractionAgent") return true;
            if (!IsHostOrSolo()) return true;

            Blueprint? bp = null;
            try { bp = instance.ActiveBlueprint; } catch { }
            if (bp == null) { try { bp = instance.blueprint; } catch { } }
            var wanted = BuildWantedManifest(bp);
            if (wanted == null) return true;

            agentInv = SafeGetInventory(agent);
            ItemCollection? stationInv = SafeGetStationInventory(instance);
            if (agentInv == null) return true;

            var shortfall = ComputeShortfall(wanted, agentInv, stationInv);
            if (shortfall.Count == 0) return true; // agent+station already cover it - nothing to do

            if (_ledger.Count > 0)
            {
                Plugin.Logger.LogWarning($"[CFS] HandleBeginCraftingSequence: {_ledger.Count} stale ledger " +
                    "entr(y/ies) from a prior craft (abandoned session?) - sweeping back before starting a new pull.");
                SweepBackFull(agentInv, "stale-ledger-before-new-pull");
            }

            PullShortfall(shortfall, agentInv);

            // An empty ledger here does NOT mean "nothing was needed" - the shortfall.Count == 0
            // check above already returned for that case. It means a real shortfall could not be
            // moved AT ALL. The observed cause is a full player pack while the settlement DID hold
            // the items (confirmed in-game 2026-07-20, v0.3.1): returning true here let the craft
            // proceed with materials missing, which produced a silent no-op craft - sound cue, no
            // item, no error, and no way for the player to tell what went wrong. Fall through to
            // the fail-closed verify in every case; with an empty ledger the sweep-back is simply
            // a no-op and only the abort + player-facing message fire.
            bool pulledNothing = _ledger.Count == 0;

            bool ok;
            _verifying = true;
            try { ok = instance.CheckOwnedRequirements(bp!, agent); }
            catch (Exception ex) { Plugin.Logger.LogError($"[CFS] HandleBeginCraftingSequence verify error: {ex}"); ok = false; }
            finally { _verifying = false; }

            SettlementStock.Invalidate();

            if (ok)
            {
                if (Plugin.TransferDiagnostics.Value)
                    Plugin.Logger.LogInfo($"[CFS] HandleBeginCraftingSequence: pulled {_ledger.Count} ledger " +
                        "entr(y/ies), verify PASSED - craft proceeding.");
                return true;
            }

            Plugin.Logger.LogWarning("[CFS] HandleBeginCraftingSequence: verify FAILED - cancelling craft. " +
                (pulledNothing
                    ? "Pulled NOTHING despite a real shortfall (settlement had the items; the player's " +
                      "pack could not receive any of them)."
                    : $"Pulled {_ledger.Count} entr(y/ies) then fell short (agent inventory likely full) - sweeping back."));
            SweepBackFull(agentInv, "verify-failed");
            CraftWatcher.ShowMessage("Craft cancelled - your inventory couldn't hold the required materials.");
            return false;
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"[CFS] CraftTransfer.HandleBeginCraftingSequence error: {ex} - sweeping back " +
                "as a precaution and cancelling craft.");
            try { SweepBackFull(agentInv, "exception"); }
            catch (Exception ex2) { Plugin.Logger.LogError($"[CFS] SweepBackFull (exception path) also failed: {ex2}"); }
            try { CraftWatcher.ShowMessage("Craft cancelled - a storage-pull error occurred, see log."); } catch { }
            return false;
        }
    }

    // ---- Point D: called from the 2 _OnCraftingSuccess postfixes (Patches/GatePatches.cs). ----
    internal static void HandleCraftingSuccess(IInteractionAgent agent)
    {
        try
        {
            // Agent gate FIRST - villagers ride this same _OnCraftingSuccess and craft constantly
            // (5+ villager successes inside 4 s observed in the v0.2.0 log). Without this, a
            // villager finishing a craft during the player's ~1.0 s pull->consume window would
            // consume the player's ledger: sweep-back would be computed against the VILLAGER's
            // inventory (clearing the ledger so the real sweep never runs, and - if that villager
            // happened to carry the same item - shipping the villager's own goods off to storage).
            if (Plugin.NativeClassName(agent) != "PlayerInteractionAgent") return;

            if (_ledger.Count == 0) return;

            if (!SafeGet(Plugin.SweepBackLeftovers, true))
            {
                Plugin.Logger.LogInfo($"[CFS] HandleCraftingSuccess: SweepBackLeftovers=false - leaving " +
                    $"{_ledger.Count} entr(y/ies) in agent inventory, clearing ledger.");
                _ledger.Clear();
                return;
            }

            ItemCollection? agentInv = SafeGetInventory(agent);
            if (agentInv == null)
            {
                Plugin.Logger.LogWarning("[CFS] HandleCraftingSuccess: could not resolve agent inventory - " +
                    "cannot compute sweep-back, leaving items with the player.");
                _ledger.Clear();
                return;
            }

            // Group by item: the clamp formula's upper bound is the TOTAL pulled for this item across
            // every source container, not any single ledger entry's own share (worked example in the
            // design spec: 4 pulled - possibly split across containers - still clamps against 4, not
            // per-entry) - grouping first avoids double-sweeping the same leftover once per entry.
            var byItem = new Dictionary<int, List<LedgerEntry>>();
            foreach (var e in _ledger)
            {
                int id;
                try { id = e.Info.id; } catch { continue; }
                if (!byItem.TryGetValue(id, out var list)) { list = new List<LedgerEntry>(); byItem[id] = list; }
                list.Add(e);
            }

            int sweptTotal = 0, keptTotal = 0;
            foreach (var group in byItem.Values)
            {
                try
                {
                    ItemInfo info = group[0].Info;
                    int agentQtyBeforePull = group[0].AgentQtyBeforePull;
                    int ledgerQty = 0;
                    foreach (var e in group) ledgerQty += e.QtyMoved;

                    // sweepBackQty = clamp(packQtyAfterCraft - packQtyBeforePull, 0, ledgerQty) -
                    // design point D. packQtyAfterCraft is read NOW (this postfix runs AFTER
                    // consumption - confirmed fact: consumption happens inside _OnCraftingSuccess,
                    // between its prefix and postfix).
                    int packQtyAfterCraft = agentInv.GetItemQuantity(info);
                    int sweepBackQty = packQtyAfterCraft - agentQtyBeforePull;
                    if (sweepBackQty < 0) sweepBackQty = 0;
                    if (sweepBackQty > ledgerQty) sweepBackQty = ledgerQty;
                    if (sweepBackQty <= 0) continue;

                    int stillToSweep = sweepBackQty;
                    foreach (var e in group)
                    {
                        if (stillToSweep <= 0) break;
                        int take = Math.Min(stillToSweep, e.QtyMoved);
                        if (take <= 0) continue;

                        int moved = MoveAgentToContainer(agentInv, e.SourceContainer, info, take);
                        sweptTotal += moved;
                        stillToSweep -= take;

                        if (moved < take)
                        {
                            int kept = take - moved;
                            keptTotal += kept;
                            Plugin.Logger.LogWarning($"[CFS] HandleCraftingSuccess: source container for " +
                                $"'{SafeName(info)}' at {e.SourceWorldPos} only accepted {moved}/{take} back " +
                                "(full/destroyed?) - remainder left in player inventory.");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Plugin.Logger.LogError($"[CFS] HandleCraftingSuccess sweep group error: {ex}");
                }
            }

            if (Plugin.TransferDiagnostics.Value)
                Plugin.Logger.LogInfo($"[CFS] HandleCraftingSuccess: swept {sweptTotal} leftover item(s) back to " +
                    "storage" + (keptTotal > 0 ? $", {keptTotal} retained by player (source full/gone)." : "."));

            _ledger.Clear();
            SettlementStock.Invalidate();
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"[CFS] CraftTransfer.HandleCraftingSuccess error: {ex}");
            _ledger.Clear();
        }
    }

    // ---- shared helpers ----

    // Cast-free path to the recipe's ingredient manifest: FillPartsManifest is declared directly on
    // Blueprint (virtual - dispatches correctly to CraftBlueprint/DyeingBlueprint's own override), so
    // this avoids the "managed casts lie for interop objects materialized under a base declared type"
    // gotcha that ItemManifest.CreateFromBlueprint(BlueprintInfo) would hit via Blueprint.info (typed
    // ItemInfo, no confirmed covariant override down to BlueprintInfo).
    private static ItemManifest? BuildWantedManifest(Blueprint? bp)
    {
        if (bp == null) return null;
        try
        {
            var m = new ItemManifest();
            bp.FillPartsManifest(m);
            return m;
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"[CFS] BuildWantedManifest error: {ex}");
            return null;
        }
    }

    private static ItemCollection? SafeGetInventory(IInteractionAgent agent)
    {
        try { return agent.GetInventory(); } catch { return null; }
    }

    // Confirmed fact: CraftInteraction.ItemInventory IS Workstation.GetInventory() (same collection,
    // delta-verified in Phase 0) - reading the interaction's own property avoids a separate
    // Workstation transform walk.
    private static ItemCollection? SafeGetStationInventory(CraftInteraction instance)
    {
        try { return instance.ItemInventory; } catch { return null; }
    }

    // wanted minus (agentInv union stationInv), per item. Shared by point A (read-only check against
    // the cached snapshot) and point C (the actual pull).
    private static List<(ItemInfo info, int missing)> ComputeShortfall(ItemManifest wanted, ItemCollection? agentInv, ItemCollection? stationInv)
    {
        var result = new List<(ItemInfo, int)>();
        try
        {
            var items = wanted.GetItems();
            if (items == null) return result;
            foreach (var iq in items)
            {
                if (iq == null) continue;
                ItemInfo? info = null; int wantedQty = 0;
                try { info = iq.itemInfo; wantedQty = iq.quantity; } catch { }
                if (info == null || wantedQty <= 0) continue;

                int have = 0;
                try { if (agentInv != null) have += agentInv.GetItemQuantity(info); } catch { }
                try { if (stationInv != null) have += stationInv.GetItemQuantity(info); } catch { }

                int missing = wantedQty - have;
                if (missing > 0) result.Add((info, missing));
            }
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"[CFS] ComputeShortfall error: {ex}");
        }
        return result;
    }

    // Pulls each shortfall entry from SettlementStock candidates into agentInv, recording a ledger
    // entry per (container, item) actually moved. Stops early per item once satisfied; a partially-
    // satisfied item (settlement ran out) is left short deliberately - HandleBeginCraftingSequence's
    // verify step is what catches that, not this method.
    private static void PullShortfall(List<(ItemInfo info, int missing)> shortfall, ItemCollection agentInv)
    {
        foreach (var (info, missingQty) in shortfall)
        {
            int remaining = missingQty;
            int agentQtyBeforeThisItem = 0;
            try { agentQtyBeforeThisItem = agentInv.GetItemQuantity(info); } catch { }

            List<SettlementStock.ContainerStock> candidates;
            try { candidates = SettlementStock.GetCandidates(info); }
            catch (Exception ex) { Plugin.Logger.LogError($"[CFS] PullShortfall GetCandidates error: {ex}"); continue; }

            foreach (var candidate in candidates)
            {
                if (remaining <= 0) break;
                int take = Math.Min(remaining, candidate.Qty);
                if (take <= 0) continue;

                int moved = MoveContainerToAgent(candidate.Container, agentInv, info, take);
                if (moved <= 0) continue;

                remaining -= moved;
                _ledger.Add(new LedgerEntry
                {
                    SourceContainer = candidate.Container,
                    SourceWorldPos = candidate.WorldPos,
                    Info = info,
                    QtyMoved = moved,
                    AgentQtyBeforePull = agentQtyBeforeThisItem
                });

                if (Plugin.TransferDiagnostics.Value)
                    Plugin.Logger.LogInfo($"[CFS] PullShortfall: -{moved} '{SafeName(info)}' from " +
                        $"{candidate.StructureName}@{candidate.WorldPos} -> agent (still need {Math.Max(0, remaining)}).");
            }

            if (remaining > 0 && Plugin.TransferDiagnostics.Value)
                Plugin.Logger.LogInfo($"[CFS] PullShortfall: '{SafeName(info)}' still short {remaining} after " +
                    "exhausting settlement candidates.");
        }
    }

    // Container -> agent collection. Remove-then-add (OuthouseComposterMod ComposterDiag.DoConvert
    // pattern, confirmed in-game): removes precisely `qty` from THIS container via RemoveFromContainer
    // below, then adds the ACTUAL removed amount to the destination via ItemCollection.AddItems
    // (2-arg, returns the count actually added - the capacity-failure detector). Any shortfall on the
    // add side is added back to the source container so nothing is lost.
    private static int MoveContainerToAgent(ItemContainer source, ItemCollection destAgentInv, ItemInfo info, int qty)
    {
        int removed = RemoveFromContainer(source, info, qty);
        if (removed <= 0) return 0;

        int added = 0;
        try { added = destAgentInv.AddItems(info, removed); }
        catch (Exception ex) { Plugin.Logger.LogError($"[CFS] MoveContainerToAgent AddItems error: {ex}"); }

        if (added < removed)
        {
            int giveBack = removed - added;
            try { source.AddItems(info, giveBack); }
            catch (Exception ex) { Plugin.Logger.LogError($"[CFS] MoveContainerToAgent give-back error: {ex}"); }
        }
        return added;
    }

    // Agent collection -> container (sweep-back direction). ItemCollection.RemoveItems(ItemInfo,int)
    // needs no ItemEventContext (unlike ItemContainer.RemoveItem) since it isn't targeting one
    // specific sub-container. Any shortfall on the container-add side is added back to the agent so
    // nothing is lost (design point D: never drop items, never delete them).
    private static int MoveAgentToContainer(ItemCollection sourceAgentInv, ItemContainer destContainer, ItemInfo info, int qty)
    {
        int removed = 0;
        try { removed = sourceAgentInv.RemoveItems(info, qty); }
        catch (Exception ex) { Plugin.Logger.LogError($"[CFS] MoveAgentToContainer RemoveItems error: {ex}"); }
        if (removed <= 0) return 0;

        int added = 0;
        try { added = destContainer.AddItems(info, removed); }
        catch (Exception ex) { Plugin.Logger.LogError($"[CFS] MoveAgentToContainer AddItems error: {ex}"); }

        if (added < removed)
        {
            int giveBack = removed - added;
            try { sourceAgentInv.AddItems(info, giveBack); }
            catch (Exception ex) { Plugin.Logger.LogError($"[CFS] MoveAgentToContainer give-back error: {ex}"); }
        }
        return added;
    }

    // Precise per-container removal (OuthouseComposterMod ComposterDiag.DoConvert pattern, confirmed
    // in-game working): ItemContainer has no bulk RemoveItems(ItemInfo,int) overload, only Item-
    // instance-based RemoveItem - so walk this container's own slots (bounded by capacity, same
    // pattern StorageCensus/SettlementStock already use), matching by ItemInfo.id (never managed
    // ==/is on interop objects - project-wide gotcha), removing from each matching stack until
    // satisfied.
    private static int RemoveFromContainer(ItemContainer container, ItemInfo info, int qty)
    {
        int remaining = qty;
        try
        {
            int targetId = info.id;
            int capacity = 0;
            try { capacity = container.capacity; } catch { }
            int bound = capacity > 0 ? capacity : 64;

            var items = container.GetItems();
            var matching = new List<Item>();
            for (int slot = 0; slot < bound; slot++)
            {
                Item? it = null;
                try { it = items != null ? items[slot] : null; } catch { break; }
                if (it == null) continue;
                try { if (it.info == null || it.info.id != targetId) continue; } catch { continue; }
                matching.Add(it);
            }

            foreach (var item in matching)
            {
                if (remaining <= 0) break;
                int itemCount = 0;
                try { itemCount = item.count; } catch { }
                if (itemCount <= 0) continue;
                int take = Math.Min(remaining, itemCount);

                bool ok = false;
                try { ok = container.RemoveItem(item, take, ItemEventContext.Default); }
                catch (Exception ex) { Plugin.Logger.LogError($"[CFS] RemoveFromContainer RemoveItem error: {ex}"); }
                if (ok) remaining -= take;
            }
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"[CFS] RemoveFromContainer error: {ex}");
        }
        return qty - remaining;
    }

    // Full-ledger sweep-back (point C failure paths only - verify-failed / stale-ledger / exception).
    // Unlike point D's leftover-only clamp math, a full sweep-back always returns 100% of QtyMoved per
    // entry: the craft never happened at all, so nothing was legitimately consumed.
    private static void SweepBackFull(ItemCollection? agentInv, string reason)
    {
        if (_ledger.Count == 0) return;
        if (agentInv == null)
        {
            Plugin.Logger.LogError($"[CFS] SweepBackFull ({reason}): no agent inventory reference available - " +
                $"{_ledger.Count} ledger entr(y/ies) LEFT WITH THE PLAYER, not swept. Check manually if this recurs.");
            _ledger.Clear();
            return;
        }

        int sweptTotal = 0, keptTotal = 0;
        foreach (var e in _ledger)
        {
            try
            {
                int moved = MoveAgentToContainer(agentInv, e.SourceContainer, e.Info, e.QtyMoved);
                sweptTotal += moved;
                if (moved < e.QtyMoved)
                {
                    int kept = e.QtyMoved - moved;
                    keptTotal += kept;
                    Plugin.Logger.LogWarning($"[CFS] SweepBackFull ({reason}): source container for " +
                        $"'{SafeName(e.Info)}' at {e.SourceWorldPos} only accepted {moved}/{e.QtyMoved} back - " +
                        $"{kept} left in player inventory.");
                }
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError($"[CFS] SweepBackFull ({reason}) entry error: {ex}");
            }
        }

        Plugin.Logger.LogInfo($"[CFS] SweepBackFull ({reason}): returned {sweptTotal} item(s) to storage" +
            (keptTotal > 0 ? $", {keptTotal} retained by player (source full/gone)." : "."));

        _ledger.Clear();
    }

    private static bool SafeGet(ConfigEntry<bool>? entry, bool fallback)
    {
        try { return entry?.Value ?? fallback; } catch { return fallback; }
    }

    private static string SafeName(ItemInfo? info)
    {
        if (info == null) return "?";
        try { return info.Name ?? "?"; } catch { return "?"; }
    }

    // Co-op write-authority gate for settlement-container state - OuthouseComposterMod
    // (ComposterDiag.IsHostOrSolo) / GroundItemVacuumMod (VacuumTracker.IsHost) pattern, confirmed
    // working in-game for exactly this class of write (settlement-shared containers, as opposed to
    // player-owned state which each client already has authority over).
    private static bool IsHostOrSolo()
    {
        try
        {
            var p = Plugin.LocalPlayer;
            if (p == null || p.NetworkObject == null || p.NetworkObject.Runner == null) return false;
            var runner = p.NetworkObject.Runner;
            return runner.IsServer || runner.IsSharedModeMasterClient;
        }
        catch { return false; }
    }
}
