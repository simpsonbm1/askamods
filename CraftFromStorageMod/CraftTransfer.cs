using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using BepInEx.Configuration;
using Il2CppInterop.Runtime.InteropTypes;
using SandSailorStudio.Inventory;
using SSSGame;
using UnityEngine;

namespace CraftFromStorageMod;

// Phase 1 (idea-17, v0.3.0) + Phase 2 (v0.7.0): turns v0.2.0's read-only trace patches
// (Patches/GatePatches.cs) into the actual storage-pull feature, for BOTH the player and villager
// crafting agents. Entry points are called from the SAME three patch families that already exist
// there:
//   - CheckOwnedRequirementsPatch.Postfix       -> TryReportAvailable   (point A)
//   - BeginCraftingSequence*Patch.Prefix (x3)   -> HandleBeginCraftingSequence (point C)
//   - OnCraftingSuccess*Patch.Postfix (x2)      -> HandleCraftingSuccess (point D)
//
// PLAYER path: session native class "PlayerCraftInteractionSession", agent "PlayerInteractionAgent",
// gated on Plugin.EnablePlayerPull, pulls into the AGENT's own inventory.
// VILLAGER path (v0.7.0 Phase 2): session native class "VillagerCraftSession", agent "Villager",
// gated on Plugin.EnableForVillagers, pulls into the STATION's inventory instead - ground truth
// confirmed in-game 2026-07-21: villager craft consumption was observed on the STATION collections
// (STATION_inventory/STATION_ItemInventory/WORKSTATION all showed the ingredient draw), not the
// villager's own carried inventory, while the crafted OUTPUT landed on the agent (CRAFTINGAGENT).
// The two config toggles are fully independent - either can be on alone.
//
// It is NOT yet known whether flipping the villager availability gate actually suppresses the
// villager's vanilla supply-fetch trip (FSM_FetchCraftingSupplies), or whether that trip is
// scheduled off the station's own supply manifests independently - that is exactly what the v0.6.0
// read-only spike (Patches/VillagerFetchPatches.cs / VillagerFetchTrace.cs, untouched by this
// phase) measures via its "[CFS-P2] CYCLE SUMMARY verdict=DIRECT|TOURED" line. Villager-specific
// lines in THIS file carry an extra "[CFS-V]" tag (on top of the mod-wide "[CFS]" tag) so the two
// can be correlated in the log.
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

        // v0.7.0 Phase 2: generalized from "AgentQtyBeforePull" - the pull destination is the
        // agent's own inventory for a player craft, but the STATION's inventory for a villager
        // craft (ground truth 2026-07-21: villager craft consumption was observed on the station's
        // collections, not the villager's carried inventory). DestCollection is the actual
        // collection instance pulled into, recorded once at pull time and read back unchanged at
        // sweep time - never re-resolved, so sweep-back always targets exactly where the pull went.
        internal int DestQtyBeforePull;
        internal ItemCollection? DestCollection;
        internal string DestLabel = "agent"; // "agent" or "station" - log/message text only
    }

    // v0.7.0 Phase 2: keyed per-agent (native pointer) rather than one flat list - concurrent
    // villager crafts (7 BeginCraftingSequence calls observed in a single short session, ground
    // truth 2026-07-21) would otherwise cross-attribute: one agent's success sweeping back items
    // another agent just pulled, or clearing the ledger before the real sweep runs. See AgentKey()
    // below for the keying mechanism. A player-only session still ends up using exactly one
    // dictionary key, so existing player behavior is unchanged.
    private static readonly Dictionary<IntPtr, List<LedgerEntry>> _ledger = new();

    // Reentrancy flag (design point A/C). While true, TryReportAvailable must report VANILLA truth,
    // never fabricate __result=true - this is what makes the verify-call in
    // HandleBeginCraftingSequence an honest check of what was actually moved instead of an infinite
    // "yes" loop that never validates anything. Deliberately a single global flag, not per-agent:
    // Unity's crafting call chain is single-threaded, so only one HandleBeginCraftingSequence
    // invocation is ever "inside" its own verify call at any instant, regardless of how many
    // distinct agents are craft-active this frame.
    private static bool _verifying;

    // Project-wide boxing pattern (CLAUDE.md gotcha: managed as/is casts lie for interop objects
    // materialized under a base declared type, but "x is Il2CppObjectBase b" works at runtime
    // regardless, since the compile-time base-chain gap is unity-libs-stub-only - same pattern
    // Plugin.NativeClassName and VillagerFetchTrace.SafePointer already use elsewhere in this mod).
    // Gives a stable per-agent ledger key without gambling on a derived-type cast. IntPtr.Zero on
    // failure collapses every unresolvable agent into one shared bucket - acceptable because both
    // PlayerInteractionAgent and Villager are always real IL2CPP objects in practice, so Zero should
    // never actually occur for the two agent kinds this mod tracks.
    private static IntPtr AgentKey(IInteractionAgent? agent)
    {
        try { if (agent is Il2CppObjectBase b) return b.Pointer; }
        catch { }
        return IntPtr.Zero;
    }

    // World-leave (CraftWatcher.ClearWorldState -> here). Never hold ItemContainer/ItemInfo wrappers
    // of per-world objects across sessions (project-wide gotcha).
    internal static void ClearWorldState()
    {
        int totalEntries = 0;
        foreach (var list in _ledger.Values) totalEntries += list.Count;
        if (totalEntries > 0)
            Plugin.Logger.LogWarning($"[CFS] CraftTransfer: world-leave with {totalEntries} unswept ledger " +
                $"entr(y/ies) across {_ledger.Count} agent(s) - discarding (cannot safely sweep across a " +
                "world boundary). Items already moved into a destination inventory are NOT lost (agent/" +
                "station inventory persists within the same save); they simply won't be swept back.");
        _ledger.Clear();
        _verifying = false;
    }

    // ---- Point A: called from Patches/GatePatches.cs CheckOwnedRequirementsPatch.Postfix. Only
    // ever WIDENS __result (false -> true); never narrows an already-true vanilla result. Handles
    // both the player (EnablePlayerPull) and villager (EnableForVillagers, v0.7.0) agents - the
    // widening logic itself doesn't care which one it is once past the per-branch config gate. ----
    internal static void TryReportAvailable(CraftInteraction instance, Blueprint bp, IInteractionAgent agent, ref bool result)
    {
        try
        {
            // v0.7.0 Phase 2: combined cheap bail (both master switches off) replaces the old single
            // EnablePlayerPull check - villagers hit this gate 3-64x/s (ground truth 2026-07-21), so
            // this must stay the cheapest possible first check, ahead of resolving the agent's
            // native class (a Marshal.PtrToStringAnsi call) below.
            if (!SafeGet(Plugin.EnablePlayerPull, true) && !SafeGet(Plugin.EnableForVillagers, true)) return;
            if (result) return;
            if (_verifying) return; // reentrancy: report vanilla truth during our own verify call

            string agentClass = Plugin.NativeClassName(agent);
            bool isPlayer = agentClass == "PlayerInteractionAgent";
            bool isVillager = agentClass == "Villager";
            if (!isPlayer && !isVillager) return;
            if (isPlayer && !SafeGet(Plugin.EnablePlayerPull, true)) return;
            if (isVillager && !SafeGet(Plugin.EnableForVillagers, true)) return;
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
            {
                // v0.8.0 (Change 3): the villager branch is rate-limited (in-game evidence 2026-07-21:
                // 5619 identical lines in one session) - the WIDENING above is completely unchanged,
                // only how often this fact gets its own log line. Player branch is untouched/unthrottled.
                if (isVillager)
                {
                    string villagerName = SafeVillagerName(agent);
                    if (VillagerAvailabilityRollup.Record(villagerName))
                    {
                        Plugin.Logger.LogInfo($"[CFS] [CFS-V] TryReportAvailable: settlement storage can cover " +
                            $"{shortfall.Count} missing item type(s) for agent={agentClass} villager={villagerName} - reporting craftable.");
                    }
                }
                else
                {
                    Plugin.Logger.LogInfo($"[CFS] TryReportAvailable: settlement storage can cover {shortfall.Count} " +
                        $"missing item type(s) for agent={agentClass} - reporting craftable.");
                }
            }
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"[CFS] CraftTransfer.TryReportAvailable error: {ex}");
        }
    }

    // ---- Point C: called from the 3 BeginCraftingSequence prefixes (Patches/GatePatches.cs).
    // Returns true = let vanilla run as normal; false = abort the craft (already swept back, or
    // nothing was ever moved). Handles BOTH the player path (pulls into the agent's own inventory)
    // and the v0.7.0 villager path (pulls into the STATION's inventory instead - see the file
    // header comment). ----
    internal static bool HandleBeginCraftingSequence(CraftInteraction instance, InteractionSession session)
    {
        IntPtr agentKeyOuter = IntPtr.Zero;
        bool isVillagerOuter = false;
        string villagerNameOuter = "?"; // v0.7.1: hoisted so the exception-path [CFS-V] line below can use it
        try
        {
            if (!SafeGet(Plugin.EnablePlayerPull, true) && !SafeGet(Plugin.EnableForVillagers, true)) return true;

            IInteractionAgent? agent = null;
            try { agent = session?.Agent; } catch { }
            if (agent == null) return true;

            string sessionClass = Plugin.NativeClassName(session);
            string agentClass = Plugin.NativeClassName(agent);
            bool isPlayer = sessionClass == "PlayerCraftInteractionSession" && agentClass == "PlayerInteractionAgent";
            bool isVillager = sessionClass == "VillagerCraftSession" && agentClass == "Villager";
            if (!isPlayer && !isVillager) return true;
            if (isPlayer && !SafeGet(Plugin.EnablePlayerPull, true)) return true;
            if (isVillager && !SafeGet(Plugin.EnableForVillagers, true)) return true;
            if (!IsHostOrSolo()) return true;

            isVillagerOuter = isVillager;
            IntPtr agentKey = AgentKey(agent);
            agentKeyOuter = agentKey;
            string vtag = isVillager ? "[CFS] [CFS-V]" : "[CFS]";
            string destNoun = isVillager ? "station" : "agent";
            // v0.7.1: resolved once and reused by every log line below (and NoteModPull further
            // down) - see SafeVillagerName's own doc comment for the exact correlation contract.
            string villagerName = isVillager ? SafeVillagerName(agent) : "";
            string villagerPart = isVillager ? $" villager={villagerName}" : "";
            if (isVillager) villagerNameOuter = villagerName;

            Blueprint? bp = null;
            try { bp = instance.ActiveBlueprint; } catch { }
            if (bp == null) { try { bp = instance.blueprint; } catch { } }
            var wanted = BuildWantedManifest(bp);
            if (wanted == null) return true;

            ItemCollection? agentInv = SafeGetInventory(agent);
            ItemCollection? stationInv = SafeGetStationInventory(instance);
            ItemCollection? destInv = isVillager ? stationInv : agentInv;
            if (destInv == null)
            {
                if (Plugin.TransferDiagnostics.Value)
                    Plugin.Logger.LogInfo($"{vtag} HandleBeginCraftingSequence: could not resolve {destNoun} " +
                        $"inventory for agent={agentClass}{villagerPart} - cannot pull, letting vanilla run unmodified.");
                return true;
            }

            var shortfall = ComputeShortfall(wanted, agentInv, stationInv);
            if (shortfall.Count == 0) return true; // agent+station already cover it - nothing to do

            if (_ledger.TryGetValue(agentKey, out var staleList) && staleList.Count > 0)
            {
                Plugin.Logger.LogWarning($"{vtag} HandleBeginCraftingSequence: {staleList.Count} stale ledger " +
                    $"entr(y/ies) from a prior craft (abandoned session?) for agent={agentClass}{villagerPart} - sweeping back before starting a new pull.");
                SweepBackFull(agentKey, "stale-ledger-before-new-pull");
            }

            PullShortfall(shortfall, destInv, isVillager, agentKey);

            // An empty ledger here does NOT mean "nothing was needed" - the shortfall.Count == 0
            // check above already returned for that case. It means a real shortfall could not be
            // moved AT ALL. The observed cause (player path) is a full player pack while the
            // settlement DID hold the items (confirmed in-game 2026-07-20, v0.3.1): returning true
            // here let the craft proceed with materials missing, which produced a silent no-op
            // craft - sound cue, no item, no error, and no way for the player to tell what went
            // wrong. Fall through to the fail-closed verify in every case; with an empty ledger the
            // sweep-back is simply a no-op and only the abort + player-facing message fire.
            _ledger.TryGetValue(agentKey, out var pulledLedger);
            int pulledCount = pulledLedger?.Count ?? 0;
            bool pulledNothing = pulledCount == 0;

            // v0.7.1: villager-only, only when this pull actually moved something - feeds
            // VillagerFetchTrace's modPulls/modItemsPulled fields on the CYCLE SUMMARY line.
            // itemsPulled is the TOTAL QUANTITY moved this pull (sum of QtyMoved across every ledger
            // entry just added), not the entry count - a 4-unit pull split across 2 containers must
            // still read modItemsPulled=4, not 2.
            if (isVillager && !pulledNothing)
            {
                try
                {
                    int pulledQtyTotal = 0;
                    foreach (var e in pulledLedger!) pulledQtyTotal += e.QtyMoved;
                    VillagerFetchTrace.NoteModPull(villagerName, pulledQtyTotal);
                }
                catch (Exception ex) { Plugin.Logger.LogError($"[CFS] [CFS-V] NoteModPull call error: {ex}"); }
            }

            bool ok;
            _verifying = true;
            try { ok = instance.CheckOwnedRequirements(bp!, agent); }
            catch (Exception ex) { Plugin.Logger.LogError($"{vtag} HandleBeginCraftingSequence verify error{villagerPart}: {ex}"); ok = false; }
            finally { _verifying = false; }

            SettlementStock.Invalidate();

            if (ok)
            {
                if (Plugin.TransferDiagnostics.Value)
                    Plugin.Logger.LogInfo($"{vtag} HandleBeginCraftingSequence: pulled {pulledCount} ledger " +
                        $"entr(y/ies) into {destNoun} inventory for agent={agentClass}{villagerPart}, verify PASSED - craft proceeding.");
                return true;
            }

            Plugin.Logger.LogWarning($"{vtag} HandleBeginCraftingSequence: verify FAILED - cancelling craft for agent={agentClass}{villagerPart}. " +
                (pulledNothing
                    ? $"Pulled NOTHING despite a real shortfall (settlement had the items; the {destNoun} " +
                      "inventory could not receive any of them)."
                    : $"Pulled {pulledCount} entr(y/ies) then fell short ({destNoun} inventory likely full) - sweeping back."));
            SweepBackFull(agentKey, "verify-failed");

            if (isPlayer)
            {
                CraftWatcher.ShowMessage("Craft cancelled - your inventory couldn't hold the required materials.");
            }
            else
            {
                // Villager crafts have no player to show an on-screen message to - a villager
                // didn't click anything, so popping banner text would be meaningless. Log instead.
                Plugin.Logger.LogWarning($"{vtag} HandleBeginCraftingSequence: villager craft aborted for agent={agentClass}{villagerPart} (no on-screen message).");
            }
            return false;
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"[CFS] CraftTransfer.HandleBeginCraftingSequence error: {ex} - sweeping back " +
                "as a precaution and cancelling craft.");
            try { SweepBackFull(agentKeyOuter, "exception"); }
            catch (Exception ex2) { Plugin.Logger.LogError($"[CFS] SweepBackFull (exception path) also failed: {ex2}"); }
            if (!isVillagerOuter)
            {
                try { CraftWatcher.ShowMessage("Craft cancelled - a storage-pull error occurred, see log."); } catch { }
            }
            else
            {
                Plugin.Logger.LogWarning($"[CFS] [CFS-V] HandleBeginCraftingSequence exception path: villager craft aborted for villager={villagerNameOuter} (no on-screen message).");
            }
            return false;
        }
    }

    // ---- Point D: called from the 2 _OnCraftingSuccess postfixes (Patches/GatePatches.cs). Sweeps
    // back against whichever collection each ledger entry recorded as its pull destination (agent
    // inventory for the player, station inventory for a villager - v0.7.0), so this one method
    // handles both paths identically once the ledger lookup succeeds. ----
    internal static void HandleCraftingSuccess(IInteractionAgent agent)
    {
        IntPtr agentKey = IntPtr.Zero;
        try
        {
            // v0.7.0 Phase 2: the ledger is keyed per-agent now, so a dictionary miss here (by far
            // the common case - this postfix fires for EVERY agent's EVERY successful craft,
            // including constant villager traffic, but most crafts pulled nothing) is both the
            // correctness fix for concurrent villager crafts (see _ledger's own comment above) and
            // cheaper than the old single "PlayerInteractionAgent" gate's class-name string read.
            agentKey = AgentKey(agent);
            if (!_ledger.TryGetValue(agentKey, out var myLedger) || myLedger.Count == 0) return;

            string agentClass = Plugin.NativeClassName(agent);
            bool isVillager = agentClass == "Villager";
            string vtag = isVillager ? "[CFS] [CFS-V]" : "[CFS]";
            string destNoun = isVillager ? "station" : "agent";
            // v0.7.1: resolved once and reused by every log line below - see SafeVillagerName's own
            // doc comment for the exact correlation contract with VillagerFetchTrace's [CFS-P2] lines.
            string villagerPart = isVillager ? $" villager={SafeVillagerName(agent)}" : "";

            if (!SafeGet(Plugin.SweepBackLeftovers, true))
            {
                Plugin.Logger.LogInfo($"{vtag} HandleCraftingSuccess: SweepBackLeftovers=false - leaving " +
                    $"{myLedger.Count} entr(y/ies) in {destNoun} inventory for agent={agentClass}{villagerPart}, clearing ledger.");
                _ledger.Remove(agentKey);
                return;
            }

            // Group by item: the clamp formula's upper bound is the TOTAL pulled for this item
            // across every source container, not any single ledger entry's own share (worked
            // example in the design spec: 4 pulled - possibly split across containers - still
            // clamps against 4, not per-entry) - grouping first avoids double-sweeping the same
            // leftover once per entry.
            var byItem = new Dictionary<int, List<LedgerEntry>>();
            foreach (var e in myLedger)
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
                    int destQtyBeforePull = group[0].DestQtyBeforePull;
                    ItemCollection? destColl = group[0].DestCollection;
                    int ledgerQty = 0;
                    foreach (var e in group) ledgerQty += e.QtyMoved;

                    if (destColl == null)
                    {
                        Plugin.Logger.LogError($"{vtag} HandleCraftingSuccess: no destination collection " +
                            $"recorded for '{SafeName(info)}' (agent={agentClass}{villagerPart}) - {ledgerQty} left unswept, skipping this item.");
                        continue;
                    }

                    // sweepBackQty = clamp(destQtyAfterCraft - destQtyBeforePull, 0, ledgerQty) -
                    // design point D, generalized (v0.7.0) to whichever collection was the actual
                    // pull destination: the agent's own inventory for a player craft, or the
                    // STATION's inventory for a villager craft (ground truth 2026-07-21: villager
                    // craft consumption was observed on the station's collections, not the
                    // villager's carried inventory). destQtyAfterCraft is read NOW (this postfix
                    // runs AFTER consumption - confirmed fact: consumption happens inside
                    // _OnCraftingSuccess, between its prefix and postfix).
                    int destQtyAfterCraft = destColl.GetItemQuantity(info);
                    int sweepBackQty = destQtyAfterCraft - destQtyBeforePull;
                    if (sweepBackQty < 0) sweepBackQty = 0;
                    if (sweepBackQty > ledgerQty) sweepBackQty = ledgerQty;
                    if (sweepBackQty <= 0) continue;

                    int stillToSweep = sweepBackQty;
                    foreach (var e in group)
                    {
                        if (stillToSweep <= 0) break;
                        int take = Math.Min(stillToSweep, e.QtyMoved);
                        if (take <= 0) continue;

                        int moved = MoveAgentToContainer(destColl, e.SourceContainer, info, take);
                        sweptTotal += moved;
                        stillToSweep -= take;

                        if (moved < take)
                        {
                            int kept = take - moved;
                            keptTotal += kept;
                            Plugin.Logger.LogWarning($"{vtag} HandleCraftingSuccess: source container for " +
                                $"'{SafeName(info)}' at {e.SourceWorldPos} only accepted {moved}/{take} back " +
                                $"(full/destroyed?) - remainder left in {destNoun} inventory{villagerPart}.");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Plugin.Logger.LogError($"[CFS] HandleCraftingSuccess sweep group error: {ex}");
                }
            }

            if (Plugin.TransferDiagnostics.Value)
                Plugin.Logger.LogInfo($"{vtag} HandleCraftingSuccess: swept {sweptTotal} leftover item(s) back " +
                    $"to storage for agent={agentClass}{villagerPart}" +
                    (keptTotal > 0 ? $", {keptTotal} retained in {destNoun} inventory (source full/gone)." : "."));

            _ledger.Remove(agentKey);
            SettlementStock.Invalidate();
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"[CFS] CraftTransfer.HandleCraftingSuccess error: {ex}");
            if (agentKey != IntPtr.Zero) _ledger.Remove(agentKey); else _ledger.Clear();
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
    // Workstation transform walk. This is also the v0.7.0 villager pull DESTINATION (see file header).
    private static ItemCollection? SafeGetStationInventory(CraftInteraction instance)
    {
        try { return instance.ItemInventory; } catch { return null; }
    }

    // wanted minus (agentInv union stationInv), per item. Shared by point A (read-only check against
    // the cached snapshot) and point C (the actual pull) - agent- and villager-agnostic, the "have"
    // union is computed the same way for both agent kinds.
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

    // Pulls each shortfall entry from SettlementStock candidates into destColl - the agent's own
    // inventory for a player craft, or the STATION's inventory for a villager craft (v0.7.0 ground
    // truth 2026-07-21: villager craft consumption was observed on the STATION collections, not the
    // villager's own carried inventory) - recording a ledger entry (keyed under agentKey) per
    // (container, item) actually moved. A ledger list is only created lazily the moment something is
    // actually moved, so a craft that pulls nothing leaves no dictionary entry behind. Stops early
    // per item once satisfied; a partially-satisfied item (settlement ran out) is left short
    // deliberately - HandleBeginCraftingSequence's verify step is what catches that, not this method.
    private static void PullShortfall(List<(ItemInfo info, int missing)> shortfall, ItemCollection destColl, bool isVillager, IntPtr agentKey)
    {
        string tag = isVillager ? "[CFS] [CFS-V]" : "[CFS]";
        string destLabel = isVillager ? "station" : "agent";

        foreach (var (info, missingQty) in shortfall)
        {
            int remaining = missingQty;
            int destQtyBeforeThisItem = 0;
            try { destQtyBeforeThisItem = destColl.GetItemQuantity(info); } catch { }

            List<SettlementStock.ContainerStock> candidates;
            try { candidates = SettlementStock.GetCandidates(info); }
            catch (Exception ex) { Plugin.Logger.LogError($"{tag} PullShortfall GetCandidates error: {ex}"); continue; }

            foreach (var candidate in candidates)
            {
                if (remaining <= 0) break;
                int take = Math.Min(remaining, candidate.Qty);
                if (take <= 0) continue;

                int moved = MoveContainerToAgent(candidate.Container, destColl, info, take);
                if (moved <= 0) continue;

                remaining -= moved;

                if (!_ledger.TryGetValue(agentKey, out var myLedger))
                {
                    myLedger = new List<LedgerEntry>();
                    _ledger[agentKey] = myLedger;
                }
                myLedger.Add(new LedgerEntry
                {
                    SourceContainer = candidate.Container,
                    SourceWorldPos = candidate.WorldPos,
                    Info = info,
                    QtyMoved = moved,
                    DestQtyBeforePull = destQtyBeforeThisItem,
                    DestCollection = destColl,
                    DestLabel = destLabel
                });

                if (Plugin.TransferDiagnostics.Value)
                    Plugin.Logger.LogInfo($"{tag} PullShortfall: -{moved} '{SafeName(info)}' from " +
                        $"{candidate.StructureName}@{candidate.WorldPos} -> {destLabel} (still need {Math.Max(0, remaining)}).");
            }

            if (remaining > 0 && Plugin.TransferDiagnostics.Value)
                Plugin.Logger.LogInfo($"{tag} PullShortfall: '{SafeName(info)}' still short {remaining} after " +
                    "exhausting settlement candidates.");
        }
    }

    // Container -> destination collection (agent inventory for a player pull, station inventory for
    // a villager pull). Remove-then-add (OuthouseComposterMod ComposterDiag.DoConvert pattern,
    // confirmed in-game): removes precisely `qty` from THIS container via RemoveFromContainer below,
    // then adds the ACTUAL removed amount to the destination via ItemCollection.AddItems (2-arg,
    // returns the count actually added - the capacity-failure detector). Any shortfall on the add
    // side is added back to the source container so nothing is lost.
    private static int MoveContainerToAgent(ItemContainer source, ItemCollection destColl, ItemInfo info, int qty)
    {
        int removed = RemoveFromContainer(source, info, qty);
        if (removed <= 0) return 0;

        int added = 0;
        try { added = destColl.AddItems(info, removed); }
        catch (Exception ex) { Plugin.Logger.LogError($"[CFS] MoveContainerToAgent AddItems error: {ex}"); }

        if (added < removed)
        {
            int giveBack = removed - added;
            try { source.AddItems(info, giveBack); }
            catch (Exception ex) { Plugin.Logger.LogError($"[CFS] MoveContainerToAgent give-back error: {ex}"); }
        }
        return added;
    }

    // Destination collection -> container (sweep-back direction; source collection is the agent's
    // own inventory for a player pull, or the station's inventory for a villager pull - see
    // LedgerEntry.DestCollection). ItemCollection.RemoveItems(ItemInfo,int) needs no
    // ItemEventContext (unlike ItemContainer.RemoveItem) since it isn't targeting one specific
    // sub-container. Any shortfall on the container-add side is added back to the source collection
    // so nothing is lost (design point D: never drop items, never delete them).
    private static int MoveAgentToContainer(ItemCollection sourceColl, ItemContainer destContainer, ItemInfo info, int qty)
    {
        int removed = 0;
        try { removed = sourceColl.RemoveItems(info, qty); }
        catch (Exception ex) { Plugin.Logger.LogError($"[CFS] MoveAgentToContainer RemoveItems error: {ex}"); }
        if (removed <= 0) return 0;

        int added = 0;
        try { added = destContainer.AddItems(info, removed); }
        catch (Exception ex) { Plugin.Logger.LogError($"[CFS] MoveAgentToContainer AddItems error: {ex}"); }

        if (added < removed)
        {
            int giveBack = removed - added;
            try { sourceColl.AddItems(info, giveBack); }
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

    // Full-ledger sweep-back for ONE agent (point C failure paths only - verify-failed / stale-
    // ledger / exception). Unlike point D's leftover-only clamp math, a full sweep-back always
    // returns 100% of QtyMoved per entry: the craft never happened at all, so nothing was
    // legitimately consumed. Reads each entry's OWN recorded DestCollection (v0.7.0) instead of a
    // passed-in collection, so the same code works unchanged for the player's agent-inventory pulls
    // and a villager's station-inventory pulls. A miss/empty lookup is a silent no-op - every call
    // site already only calls this when it has reason to believe there's something to sweep.
    private static void SweepBackFull(IntPtr agentKey, string reason)
    {
        if (!_ledger.TryGetValue(agentKey, out var list) || list.Count == 0) return;

        string tag = (list.Count > 0 && list[0].DestLabel == "station") ? "[CFS] [CFS-V]" : "[CFS]";

        int sweptTotal = 0, keptTotal = 0;
        foreach (var e in list)
        {
            try
            {
                if (e.DestCollection == null)
                {
                    Plugin.Logger.LogError($"{tag} SweepBackFull ({reason}): no destination collection recorded " +
                        $"for '{SafeName(e.Info)}' - {e.QtyMoved} LEFT UNSWEPT. Check manually if this recurs.");
                    continue;
                }
                int moved = MoveAgentToContainer(e.DestCollection, e.SourceContainer, e.Info, e.QtyMoved);
                sweptTotal += moved;
                if (moved < e.QtyMoved)
                {
                    int kept = e.QtyMoved - moved;
                    keptTotal += kept;
                    Plugin.Logger.LogWarning($"{tag} SweepBackFull ({reason}): source container for " +
                        $"'{SafeName(e.Info)}' at {e.SourceWorldPos} only accepted {moved}/{e.QtyMoved} back - " +
                        $"{kept} left in {e.DestLabel} inventory.");
                }
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError($"{tag} SweepBackFull ({reason}) entry error: {ex}");
            }
        }

        Plugin.Logger.LogInfo($"{tag} SweepBackFull ({reason}): returned {sweptTotal} item(s) to storage" +
            (keptTotal > 0 ? $", {keptTotal} retained (source full/gone)." : "."));

        _ledger.Remove(agentKey);
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

    // v0.7.1: resolves the SAME GameObject-name string VillagerFetchTrace.SafeVillagerName reads
    // (via fsmBehaviour.gameObject.name) - normalization matched exactly (no trim, "?" fallback) so
    // this file's [CFS-V] lines and VillagerFetchTrace's [CFS-P2] CYCLE SUMMARY lines key on the
    // IDENTICAL string for the same villager and a tester can grep one name across both. Reaches the
    // agent's own Transform the same way CraftWatcher.BuildSlots already does for the CRAFTINGAGENT
    // slot (session.Agent.GetTransform()), then reads .gameObject.name straight off it - no
    // intermediate rewrap.
    //
    // Correlation caveat (confirmed via Cecil, not just asserted): IFSMBehaviourController is
    // implemented by a SEPARATE concrete class (SSSGame.AI.FSM.FSMBehaviourController, base
    // Invector.vMonoBehaviour) - NOT by Villager itself. So fsmBehaviour and this agent are two
    // different components, and this method's output only matches VillagerFetchTrace.SafeVillagerName
    // if those two components sit on the SAME GameObject. That is exactly the assumption
    // VillagerFetchTrace.cs's own header comment already documents ("the SAME GameObject in the
    // expected case") for its OWN cross-patch correlation (fsmBehaviour.gameObject vs.
    // villager.gameObject in the whitelist patch) - this method relies on that same pre-existing
    // assumption one hop further, not a new one. Static analysis can't prove scene/prefab
    // composition; the in-game grep correlation this change enables is the actual test.
    private static string SafeVillagerName(IInteractionAgent? agent)
    {
        try
        {
            var t = agent?.GetTransform();
            var go = t?.gameObject;
            if (go != null) return go.name;
        }
        catch { }
        return "?";
    }

    // Co-op write-authority gate for settlement-container state - OuthouseComposterMod
    // (ComposterDiag.IsHostOrSolo) / GroundItemVacuumMod (VacuumTracker.IsHost) pattern, confirmed
    // working in-game for exactly this class of write (settlement-shared containers, as opposed to
    // player-owned state which each client already has authority over).
    // v0.8.0: widened private -> internal so FetchQuestSuppression.cs (Phase 2b fetch-quest priority
    // suppression) can reuse the SAME host gate rather than duplicating it - the delegation spec for
    // that feature explicitly calls for reusing "CraftTransfer's existing host gate". No behavior
    // change, visibility only.
    internal static bool IsHostOrSolo()
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

    // v0.8.0 (Phase 2b): shared manifest-enumeration helper - yields (ItemInfo, quantity) pairs from
    // any ItemManifest, skipping null/zero-or-negative entries. Reads an ItemManifest the exact same
    // way ComputeShortfall already does (same GetItems()/.itemInfo/.quantity shape), so
    // FetchQuestSuppression.cs (fetch-quest priority suppression) doesn't have to re-guess the shape
    // of an ItemManifest from scratch. This is a plain read with no subtraction - for a manifest that
    // ALREADY represents exactly what's still needed (e.g. GetNeededSuppliesManifest()), unlike
    // ComputeShortfall which computes wanted-minus-have. Does not replace or alter ComputeShortfall
    // itself (untouched, still Point A/C's own wanted-minus-have calculation).
    internal static List<(ItemInfo info, int qty)> EnumerateManifest(ItemManifest? manifest)
    {
        var result = new List<(ItemInfo, int)>();
        if (manifest == null) return result;
        try
        {
            var items = manifest.GetItems();
            if (items == null) return result;
            foreach (var iq in items)
            {
                if (iq == null) continue;
                ItemInfo? info = null; int qty = 0;
                try { info = iq.itemInfo; qty = iq.quantity; } catch { }
                if (info == null || qty <= 0) continue;
                result.Add((info, qty));
            }
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"[CFS] CraftTransfer.EnumerateManifest error: {ex}");
        }
        return result;
    }
}

// v0.8.0 (Change 3): rate limiter for TryReportAvailable's villager-side [CFS-V] log line - in-game
// evidence (2026-07-21) showed it firing 5619 times in one session (the gate is widened on every
// CheckOwnedRequirements call while a villager is short, which happens several times per second).
// Behavior is UNCHANGED (still widens __result exactly as before, every time) - only how often that
// fact gets its own log line changes. Follows the GateRollup idiom (Patches/GatePatches.cs): a small
// per-villager quota of raw lines, then a periodic idle-flush rollup with a per-villager count
// breakdown. Ticked from CraftWatcher.Update() alongside GateRollup.Tick().
internal static class VillagerAvailabilityRollup
{
    private const long FlushIdleMs = 1500;
    private const int MaxRawLogsPerVillager = 3;

    private static int _totalCalls;
    private static readonly Dictionary<string, int> _rawLoggedPerVillager = new();
    private static readonly Dictionary<string, int> _countPerVillager = new();
    private static long _lastCallMs = -1;
    private static readonly Stopwatch _sw = new();

    // Returns true if THIS call should also get its own raw log line (first MaxRawLogsPerVillager
    // per villager per idle-flush burst).
    internal static bool Record(string villagerName)
    {
        try
        {
            if (!_sw.IsRunning) _sw.Restart();
            _lastCallMs = _sw.ElapsedMilliseconds;
            _totalCalls++;
            _countPerVillager.TryGetValue(villagerName, out int c);
            _countPerVillager[villagerName] = c + 1;

            _rawLoggedPerVillager.TryGetValue(villagerName, out int logged);
            if (logged < MaxRawLogsPerVillager)
            {
                _rawLoggedPerVillager[villagerName] = logged + 1;
                return true;
            }
            return false;
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"[CFS] [CFS-V] VillagerAvailabilityRollup.Record error: {ex}");
            return false;
        }
    }

    // Called every frame from CraftWatcher.Update(), same wiring as GatePatches.cs GateRollup.Tick().
    internal static void Tick()
    {
        if (_totalCalls == 0) return;
        if (!_sw.IsRunning) return;
        if (_sw.ElapsedMilliseconds - _lastCallMs < FlushIdleMs) return;
        Flush();
    }

    private static void Flush()
    {
        try
        {
            string breakdown = string.Join(", ", _countPerVillager.Select(kv => $"{kv.Key}={kv.Value}"));
            Plugin.Logger.LogInfo($"[CFS] [CFS-V] TryReportAvailable rollup: {_totalCalls} widening(s) " +
                $"across {_countPerVillager.Count} villager(s): [{breakdown}]");
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"[CFS] [CFS-V] VillagerAvailabilityRollup.Flush error: {ex}");
        }
        finally
        {
            _totalCalls = 0;
            _rawLoggedPerVillager.Clear();
            _countPerVillager.Clear();
            _sw.Reset();
            _lastCallMs = -1;
        }
    }
}
