using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using BepInEx.Configuration;
using Il2CppInterop.Runtime;
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

    // v0.9.2: rate limiters for StockStation's zero-move / candidate-dump diagnostic lines. String-
    // keyed only (itemName|stationName) - never an interop wrapper (project-wide gotcha) - so these
    // are safe to hold across frames and just need clearing on world-leave like every other tracker.
    private static readonly Dictionary<string, int> _zeroMoveLogged = new();
    private static readonly Dictionary<string, int> _candidateDumpLogged = new();

    // v0.10.0 (TRANSFORM_STATION_PLAN.md Change 2): rate limiter for MoveContainerToAgent's new
    // capacity-refused-move diagnostic, keyed by itemName so a single item type churning through
    // many candidates cannot spam the log.
    private static readonly Dictionary<string, int> _capacitySkipLogged = new();

    // v0.10.0 (TRANSFORM_STATION_PLAN.md Change 1): cached parse of Plugin.SkipStationClasses, same
    // shape as StationStocker's own _skipClassCache/_skipClassRaw pair for SkipBlueprintClasses -
    // rebuilt only when the raw config string actually changes, because IsSkippedStation's first
    // entry point runs from TryReportAvailable, which villagers hit dozens of times a second.
    private static HashSet<string>? _skipStationClassCache;
    private static string _skipStationClassRaw = "";

    // v0.10.1 (defect fix, TRANSFORM_STATION_PLAN.md Change 2): rate limiter for the new stationProbe
    // diagnostic in IsSkippedStation(CraftingStation) below - keyed by station GameObject name (never
    // an interop wrapper - project-wide gotcha), capped per station name so a single stuck station
    // cannot spam the log.
    private static readonly Dictionary<string, int> _stationProbeLogged = new();

    // v0.10.1 (defect fix, TRANSFORM_STATION_PLAN.md Change 3): rate limiter for the fire-verification
    // lines at TryReportAvailable's and HandleBeginCraftingSequence's silent IsSkippedStation early-
    // returns - keyed by "site|stationClass" so each call site's own quota is independent.
    // TryReportAvailable runs dozens of times a second per villager, so this must stay a small quota
    // (in the style of VillagerAvailabilityRollup's own MaxRawLogsPerVillager), never one line per call.
    private static readonly Dictionary<string, int> _standAsideLogged = new();

    // v0.11.0 (TRANSFORM_STATION_PLAN.md Change 3): rate limiter for StockStation's new destinationProbe
    // diagnostic - keyed by station name, capped at 1 ("emitted once per station") so a stuck station's
    // hundred-cycle loop can't repeat it.
    private static readonly Dictionary<string, int> _destinationProbeLogged = new();

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
        // v0.9.2: string-keyed rate-limiter dictionaries, cleared alongside the ledger.
        _zeroMoveLogged.Clear();
        _candidateDumpLogged.Clear();
        // v0.10.0: same reasoning for the capacity-skip rate limiter.
        _capacitySkipLogged.Clear();
        // v0.10.1: same reasoning for the two new defect-fix diagnostic rate limiters.
        _stationProbeLogged.Clear();
        _standAsideLogged.Clear();
        // v0.11.0: same reasoning for the new destination-probe rate limiter.
        _destinationProbeLogged.Clear();
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
            // v0.10.0 (TRANSFORM_STATION_PLAN.md Change 1): a physical-transform station (forge/
            // sawhorses/dye bench) structurally cannot have its requirement satisfied from a bin -
            // this is the widening that causes the idle-carpenter stall, so it matters most that it
            // sits ahead of the manifest build below.
            // v0.10.1 (Change 3): this early return used to be silent, making a working gate and an
            // absent workload look identical in the log - now fire-verified via LogStandAside.
            // v0.11.0 (Change 2): Plugin.StockTransformStationMaterials deliberately does NOT apply
            // here. That toggle only lets the STOCKER top up a transform station's rack; reporting a
            // transform recipe already satisfied is exactly the widening that stalled villagers
            // entirely (TRANSFORM_STATION_PLAN.md), so this stand-aside stays unconditional regardless
            // of the toggle's value.
            if (IsSkippedStation(instance))
            {
                LogStandAside("TryReportAvailable", isVillager, instance);
                return;
            }

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
                    // v0.9.4: resolved only inside this already-widening, already-diagnostics-gated
                    // branch (never above the early-outs) - this is WHICH recipe got widened and by
                    // what shortfall, feeding VillagerAvailabilityRollup's new per-recipe breakdown so
                    // a future run can tell whether the widening correlates with odd villager choices.
                    string bpName = Patches.GateLog.SafeBlueprintName(bp);
                    string shortfallSig;
                    try
                    {
                        shortfallSig = string.Join("+", shortfall.Select(s => $"{SafeName(s.info)}x{s.missing}"));
                    }
                    catch { shortfallSig = "?"; }

                    if (VillagerAvailabilityRollup.Record(villagerName, bpName, shortfallSig))
                    {
                        Plugin.Logger.LogInfo($"[CFS] [CFS-V] TryReportAvailable: settlement storage can cover " +
                            $"{shortfall.Count} missing item type(s) for agent={agentClass} villager={villagerName} " +
                            $"bp='{bpName}' shortfall=[{shortfallSig}] - reporting craftable.");
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
            // v0.10.0 (TRANSFORM_STATION_PLAN.md Change 1): same physical-transform stand-aside as
            // TryReportAvailable above - let vanilla run unmodified at these stations.
            // v0.10.1 (Change 3): fire-verified via LogStandAside - was silent before.
            // v0.11.0 (Change 2): Plugin.StockTransformStationMaterials deliberately does NOT apply
            // here either, for the same reason as TryReportAvailable above - the craft-start pull must
            // keep standing aside at these stations unconditionally, so a future reader does not "fix"
            // this into consulting the stocker-only toggle.
            if (IsSkippedStation(instance))
            {
                LogStandAside("HandleBeginCraftingSequence", isVillager, instance);
                return true;
            }

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

    // v0.10.0 (TRANSFORM_STATION_PLAN.md Change 1): parses Plugin.SkipStationClasses the same way
    // StationStocker.GetSkipClasses parses SkipBlueprintClasses - cached, re-parsed only when the
    // raw config string changes.
    private static HashSet<string> GetSkipStationClasses()
    {
        string raw = "";
        try { raw = Plugin.SkipStationClasses?.Value ?? ""; } catch { }
        if (_skipStationClassCache != null && raw == _skipStationClassRaw) return _skipStationClassCache;

        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in raw.Split(','))
        {
            var t = part.Trim();
            if (t.Length > 0) set.Add(t);
        }
        _skipStationClassRaw = raw;
        _skipStationClassCache = set;
        return set;
    }

    // v0.10.0 (TRANSFORM_STATION_PLAN.md Change 1): true when `interaction` is, or derives from, one
    // of the configured SkipStationClasses - a physical-transform station (AnvilInteraction/
    // CarpenterInteraction sawhorses, DyeingInteraction dye bench by default) where an item is placed
    // on the station and struck/dyed rather than drawn from a bin. Walks the native class PARENT
    // chain (not just the leaf class), matching the DemandGraph.cs/TaskUnlockTracker.cs ancestor-walk
    // idiom already used elsewhere in this repo, so CarpenterInteraction (-> AnvilInteraction ->
    // CraftInteraction) matches on either name and any future subclass is caught automatically.
    // Managed as/is casts lie for interop objects materialized under a base declared type (project-
    // wide gotcha) - this reads the native class name instead of casting. Loop-guarded at 20 and
    // wrapped in try/catch returning FALSE on any exception, so a probe failure fails OPEN (the mod
    // behaves exactly as it did before this change - it still acts on the station).
    internal static bool IsSkippedStation(CraftInteraction? interaction)
    {
        try
        {
            if (interaction == null) return false;
            var names = GetSkipStationClasses();
            if (names.Count == 0) return false;
            if ((object)interaction is not Il2CppObjectBase baseObj) return false;

            IntPtr walk;
            try { walk = IL2CPP.il2cpp_object_get_class(baseObj.Pointer); } catch { return false; }

            int depth = 0;
            while (walk != IntPtr.Zero && depth < 20)
            {
                string? name = null;
                try { name = Marshal.PtrToStringAnsi(IL2CPP.il2cpp_class_get_name(walk)); } catch { }
                if (name != null && names.Contains(name)) return true;
                try { walk = IL2CPP.il2cpp_class_get_parent(walk); } catch { break; }
                depth++;
            }
            return false;
        }
        catch
        {
            return false;
        }
    }

    // v0.10.1 (defect fix, TRANSFORM_STATION_PLAN.md Change 1): station overload for StationStocker's
    // fetch-start path - SSSGame.CraftingStation carries no interaction property. v0.10.0's original
    // implementation (a single go.GetComponent<CraftInteraction>() call on the station's own
    // GameObject) never resolved anything: confirmed in-game 2026-07-29, a run against Workshop House
    // carpenter stations (AnvilInteraction is the easiest possible match) logged this gate's SKIP line
    // ZERO times while the older blueprint-class gate below it in StationStocker.cs logged 20x "SKIP
    // blueprintClass=ForgingBlueprintInfo" - proof the single-GetComponent lookup came up empty every
    // time, so the older gate always claimed the case first. Fixed by searching self, then every
    // ancestor (transform.parent, depth-guarded at 10), then every descendant (breadth-first via
    // transform.GetChild(i), node-guarded at 200 total) via ResolveStationInteractions below - the
    // same "singular GetComponent<T>() per node, walked by hand" pattern MineRefreshMod/TreeRespawn's
    // well-refill code already use, because the plural GetComponentsInChildren<T>(bool) and the
    // non-generic GameObject.GetComponents(System.Type) are both missing through the interop
    // trampoline in this game (project-wide gotcha). A base-typed singular GetComponent<T>() still
    // returns derived instances, so this yields the CarpenterInteraction where one exists.
    //
    // v0.11.0 (defect fix, TRANSFORM_STATION_PLAN.md 2026-07-29 hypothesis): v0.10.1's resolver
    // returned only the FIRST CraftInteraction it found and stopped searching. In-game evidence the
    // SAME day showed Workshop House 2 (all carpenters, confirmed in-game to hold one hardwood log and
    // one hardwood long stick at a time on its rack) logging stationProbe class=CraftInteraction
    // foundWhere=descendant outcome=not-matched five times running, while the two OTHER sites
    // (TryReportAvailable/HandleBeginCraftingSequence, which receive the interaction directly as a
    // parameter) correctly saw CarpenterInteraction for the same building. The working hypothesis -
    // that building holds MORE than one CraftInteraction component (ordinary sawhorses AND a plain
    // crafting table), and the first-found search happened to return the wrong one - is what this
    // fixes: ResolveStationInteractions now collects EVERY CraftInteraction in the searched hierarchy
    // instead of stopping at the first. Selection rule: prefer an interaction whose class matches the
    // configured SkipStationClasses set (a building containing a transform surface must be treated as
    // a transform station); otherwise fall back to the first one found, exactly as before. Delegates
    // to the interaction overload above UNCHANGED once selected - only the resolution changed here.
    // Every outcome (matched / resolved-not-matched / unresolved / null) gets a rate-limited
    // "[CFS] [CFS-SS] stationProbe" line via LogStationProbe, now reporting every interaction found
    // (not just the selected one) so this hypothesis is provable or disprovable straight from the log.
    internal static bool IsSkippedStation(SSSGame.CraftingStation? station)
    {
        string stationName = "?";
        try
        {
            if (station == null)
            {
                LogStationProbe("?", "null", "?", "-", EmptyProbeDetail);
                return false;
            }

            GameObject? go = null;
            try { go = station.gameObject; } catch { }
            if (go == null)
            {
                LogStationProbe(Plugin.NativeClassName(station), "null", "?", "-", EmptyProbeDetail);
                return false;
            }
            stationName = go.name;

            var candidates = ResolveStationInteractions(go);
            if (candidates.Count == 0)
            {
                LogStationProbe(stationName, "unresolved", "?", "-", EmptyProbeDetail);
                return false;
            }

            // Selection rule (TRANSFORM_STATION_PLAN.md 2026-07-29): prefer the first candidate whose
            // class matches the skip set; a building containing a transform surface must be treated as
            // a transform station even if a plain CraftInteraction elsewhere in the same hierarchy
            // would otherwise have been picked first. Fall back to the first candidate found (the old
            // behavior) when none match.
            (CraftInteraction Interaction, string FoundWhere)? selected = null;
            foreach (var c in candidates)
            {
                if (IsSkippedStation(c.Interaction)) { selected = c; break; }
            }
            selected ??= candidates[0];

            string resolvedClass = Plugin.NativeClassName(selected.Value.Interaction);
            bool skipped = IsSkippedStation(selected.Value.Interaction);

            var detail = new List<(string Class, string FoundWhere)>(candidates.Count);
            foreach (var c in candidates) detail.Add((Plugin.NativeClassName(c.Interaction), c.FoundWhere));

            LogStationProbe(stationName, skipped ? "matched" : "not-matched", resolvedClass, selected.Value.FoundWhere, detail);
            return skipped;
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"[CFS] [CFS-SS] IsSkippedStation(CraftingStation) error for station={stationName}: {ex}");
            return false;
        }
    }

    private static readonly List<(string Class, string FoundWhere)> EmptyProbeDetail = new();

    // v0.11.0 (TRANSFORM_STATION_PLAN.md 2026-07-29 hypothesis, renamed+reworked from v0.10.1's
    // singular ResolveStationInteraction): self -> ancestors -> descendants search for EVERY
    // CraftInteraction in the hierarchy, per Change 1's specified order and guards - continues
    // searching after a hit instead of returning on the first one, so a building with more than one
    // CraftInteraction component (the working hypothesis for Workshop House 2's carpentry rack) has
    // all of them reported, not just whichever happened to be found first. Ancestor walk is depth-
    // guarded at 10 (transform.parent chains are shallow in practice); descendant walk is a breadth-
    // first transform.GetChild(i) walk, node-guarded at 200 total nodes visited so a deep prefab cannot
    // stall the search. Every per-node GetComponent/parent/childCount/GetChild call is individually
    // wrapped so one bad node can't abort the rest of the walk. Returns an empty list if nothing turns
    // up anywhere searched.
    private static List<(CraftInteraction Interaction, string FoundWhere)> ResolveStationInteractions(GameObject go)
    {
        var found = new List<(CraftInteraction, string)>();

        Transform? root = null;
        try { root = go.transform; } catch { }
        if (root == null) return found;

        CraftInteraction? self = null;
        try { self = root.GetComponent<CraftInteraction>(); } catch { self = null; }
        if (self != null) found.Add((self, "self"));

        Transform? cur = root;
        int depth = 0;
        while (depth < 10)
        {
            Transform? parent = null;
            try { parent = cur?.parent; } catch { parent = null; }
            if (parent == null) break;

            CraftInteraction? ancestor = null;
            try { ancestor = parent.GetComponent<CraftInteraction>(); } catch { ancestor = null; }
            if (ancestor != null) found.Add((ancestor, "ancestor"));

            cur = parent;
            depth++;
        }

        var queue = new Queue<Transform>();
        try
        {
            int childCount = root.childCount;
            for (int i = 0; i < childCount; i++)
            {
                var child = root.GetChild(i);
                if (child != null) queue.Enqueue(child);
            }
        }
        catch { }

        int visited = 0;
        while (queue.Count > 0 && visited < 200)
        {
            var node = queue.Dequeue();
            visited++;

            CraftInteraction? descendant = null;
            try { descendant = node.GetComponent<CraftInteraction>(); } catch { descendant = null; }
            if (descendant != null) found.Add((descendant, "descendant"));

            try
            {
                int childCount = node.childCount;
                for (int i = 0; i < childCount; i++)
                {
                    var child = node.GetChild(i);
                    if (child != null) queue.Enqueue(child);
                }
            }
            catch { }
        }

        return found;
    }

    // v0.10.1 (defect fix, TRANSFORM_STATION_PLAN.md Change 2): one rate-limited line per station
    // (capped at 5 per station name, matching this file's other diagnostic caps) reporting the
    // outcome of IsSkippedStation(CraftingStation)'s resolution:
    //   outcome=matched          - resolved, and the SELECTED interaction's class matched the skip set.
    //   outcome=not-matched      - resolved, but the SELECTED interaction's class did not match; class/
    //                               foundWhere are the selected interaction's native class name and
    //                               where it was found (self/ancestor/descendant).
    //   outcome=unresolved       - no CraftInteraction found anywhere in the searched hierarchy.
    //   outcome=null             - the station or its GameObject was null.
    // A silent pass is exactly what made v0.10.0's defect unreadable from the log, so every outcome
    // gets a line, not just the skip case.
    // v0.11.0 (TRANSFORM_STATION_PLAN.md 2026-07-29 hypothesis): gained `all`, the full list of every
    // interaction ResolveStationInteractions found (native class + foundWhere for each), appended as
    // count=N plus an "all=[...]" segment - this is what proves or disproves the multiple-interaction
    // hypothesis directly from the log, since class/foundWhere above now describe only the SELECTED one.
    private static void LogStationProbe(string stationName, string outcome, string resolvedClass, string foundWhere,
        List<(string Class, string FoundWhere)> all)
    {
        if (!SafeGet(Plugin.TransferDiagnostics, true)) return;
        try
        {
            _stationProbeLogged.TryGetValue(stationName, out int count);
            if (count >= 5) return;
            _stationProbeLogged[stationName] = count + 1;

            string allStr = all.Count > 0
                ? string.Join("; ", all.Select(a => $"{a.Class}@{a.FoundWhere}"))
                : "-";
            Plugin.Logger.LogInfo($"[CFS] [CFS-SS] stationProbe station={stationName} outcome={outcome} " +
                $"class={resolvedClass} foundWhere={foundWhere} count={all.Count} all=[{allStr}]");
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"[CFS] [CFS-SS] LogStationProbe error: {ex}");
        }
    }

    // v0.10.1 (defect fix, TRANSFORM_STATION_PLAN.md Change 3): fire-verification for TryReportAvailable's
    // and HandleBeginCraftingSequence's IsSkippedStation early-returns, which were previously silent -
    // that made a working gate and an absent workload look identical in the log, which is why the
    // v0.10.0 defect could not be attributed from the log alone. Rate-limited by "site|stationClass" -
    // TryReportAvailable runs dozens of times a second per villager (ground truth 2026-07-21), so the
    // quota here is small (3), in the style of VillagerAvailabilityRollup.MaxRawLogsPerVillager rather
    // than a raw per-call line.
    private static void LogStandAside(string site, bool isVillager, CraftInteraction? instance)
    {
        if (!SafeGet(Plugin.TransferDiagnostics, true)) return;
        try
        {
            string stationClass = Plugin.NativeClassName(instance);
            string key = site + "|" + stationClass;
            _standAsideLogged.TryGetValue(key, out int count);
            if (count >= 3) return;
            _standAsideLogged[key] = count + 1;
            string vtag = isVillager ? "[CFS] [CFS-V]" : "[CFS]";
            Plugin.Logger.LogInfo($"{vtag} {site}: standing aside for physical-transform station " +
                $"stationClass={stationClass} - letting vanilla run unmodified.");
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"[CFS] LogStandAside error: {ex}");
        }
    }

    // v0.11.0 (TRANSFORM_STATION_PLAN.md Change 3): one rate-limited line per station name (capped at
    // 1 - "emitted once per station") reporting the destination StockStation is about to write into:
    // the native class name of the collection returned by the station's GetInventory() (destColl IS
    // that collection - see StationStocker.HandleFetchStateEnter's own GetInventory() call and
    // CraftTransfer.SafeGetStationInventory's comment, both confirming CraftInteraction.ItemInventory
    // and CraftingStation.GetInventory() are the same collection), its canAddItems value, and the
    // remaining capacity it reports for the specific item about to be moved - read BEFORE the move, so
    // this is ground truth about the destination, not a result of the move that just happened.
    private static void LogDestinationProbe(ItemCollection destColl, ItemInfo info, string villagerName, string stationName)
    {
        if (!SafeGet(Plugin.TransferDiagnostics, true)) return;
        try
        {
            _destinationProbeLogged.TryGetValue(stationName, out int count);
            if (count >= 1) return;
            _destinationProbeLogged[stationName] = count + 1;

            string destClass = Plugin.NativeClassName(destColl);
            bool canAdd = false;
            try { canAdd = destColl.canAddItems; } catch { }
            int remainingCapacity = -1;
            try { remainingCapacity = destColl.GetTotalRemainingCapacity(info); } catch { }

            Plugin.Logger.LogInfo($"[CFS] [CFS-SS] destinationProbe station={stationName} destClass={destClass} " +
                $"canAddItems={canAdd} remainingCapacityFor='{SafeName(info)}'={remainingCapacity} " +
                $"(villager={villagerName}).");
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"[CFS] [CFS-SS] LogDestinationProbe error: {ex}");
        }
    }

    // ---- v1.4.0: entry points for the personal (bench-free) crafting path in HandcraftPull.cs. ----
    //
    // That path has no interaction agent and no craft-success hook, so it cannot use
    // HandleBeginCraftingSequence or HandleCraftingSuccess. It supplies its own ledger key (the open
    // build menu's pointer) and calls these two. Both are thin: the pull delegates to the SAME
    // PullShortfall the station path uses, so there is exactly one implementation of the candidate
    // loop, the blacklist and the capacity pre-check.

    // Pull into a destination collection under a caller-supplied ledger key. isVillager is false:
    // the destination is the player's own pack, as it is for every player-side pull.
    internal static void PullShortfallFor(List<(ItemInfo info, int missing)> shortfall,
        ItemCollection destColl, IntPtr ledgerKey, string tag)
    {
        if (shortfall == null || shortfall.Count == 0 || destColl == null) return;
        try { PullShortfall(shortfall, destColl, false, ledgerKey); }
        catch (Exception ex) { Plugin.Logger.LogError($"{tag} PullShortfallFor error: {ex}"); }
    }

    // Sweep every entry recorded under one ledger key back to its source container, using the same
    // clamp as HandleCraftingSuccess: sweepBackQty = clamp(destQtyNow - destQtyBeforePull, 0,
    // ledgerQty). The clamp is what makes this safe to run at an arbitrary moment rather than at a
    // craft boundary - anything the player consumed is already missing from the destination, so it
    // is never clawed back, and stock the player owned before the pull is never taken.
    internal static void SweepLedgerFor(IntPtr ledgerKey, string tag)
    {
        try
        {
            if (!_ledger.TryGetValue(ledgerKey, out var myLedger) || myLedger.Count == 0) return;

            if (!SafeGet(Plugin.SweepBackLeftovers, true))
            {
                Plugin.Logger.LogInfo($"{tag} SweepLedgerFor: SweepBackLeftovers=false - leaving " +
                    $"{myLedger.Count} entr(y/ies) with the player, clearing ledger.");
                _ledger.Remove(ledgerKey);
                return;
            }

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
                        Plugin.Logger.LogError($"{tag} SweepLedgerFor: no destination collection recorded " +
                            $"for '{SafeName(info)}' - {ledgerQty} left unswept, skipping this item.");
                        continue;
                    }

                    int destQtyNow = destColl.GetItemQuantity(info);
                    int sweepBackQty = destQtyNow - destQtyBeforePull;
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
                            Plugin.Logger.LogWarning($"{tag} SweepLedgerFor: source container for " +
                                $"'{SafeName(info)}' at {e.SourceWorldPos} only accepted {moved}/{take} back " +
                                "(full/destroyed?) - remainder left with the player.");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Plugin.Logger.LogError($"{tag} SweepLedgerFor group error: {ex}");
                }
            }

            if (sweptTotal > 0 || keptTotal > 0)
                Plugin.Logger.LogInfo($"{tag} SweepLedgerFor: swept {sweptTotal} unused item(s) back to storage" +
                    (keptTotal > 0 ? $", {keptTotal} retained by the player (source full/gone)." : "."));

            _ledger.Remove(ledgerKey);
            SettlementStock.Invalidate();
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"{tag} CraftTransfer.SweepLedgerFor error: {ex}");
            _ledger.Remove(ledgerKey);
        }
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

                int moved = MoveContainerToAgent(candidate.Container, destColl, info, take, out _);
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
                        $"{candidate.StructureName}@{candidate.WorldPos} node={candidate.NodeName} " +
                        $"type={candidate.TypeName} -> {destLabel} (still need {Math.Max(0, remaining)}).");
            }

            if (remaining > 0 && Plugin.TransferDiagnostics.Value)
                Plugin.Logger.LogInfo($"{tag} PullShortfall: '{SafeName(info)}' still short {remaining} after " +
                    "exhausting settlement candidates.");
        }
    }

    // v0.9.0 Phase 2c: called from StationStocker.HandleFetchStateEnter when a villager's crafting
    // fetch quest STARTS - the same per-item candidate loop as PullShortfall above (SettlementStock.
    // GetCandidates -> MoveContainerToAgent, stopping early once an item is satisfied), but with NO
    // ledger write at all.
    //
    // The absent ledger is deliberate: hauling these items to the station is precisely what the
    // villager's own fetch walk would have done, so they belong in the station whether or not the
    // craft completes. Sweeping them back would recreate the empty-station stall v0.8.0 produced -
    // v0.8.0 suppressed the fetch quest outright, leaving a villager standing at an empty station with
    // nothing to trigger a Point C pull (BeginCraftingSequence, which the AI scheduler never reaches
    // from there). This method exists precisely so the station is never left empty after a fetch quest
    // starts, so there is nothing to sweep back and no ledger entry that would ever need one.
    internal static (int itemsMoved, int qtyMoved, int stillShort) StockStation(
        List<(ItemInfo info, int missing)> shortfall, ItemCollection destColl,
        string villagerName, string stationName)
    {
        const string tag = "[CFS] [CFS-SS]";
        int itemsMoved = 0, qtyMoved = 0, stillShort = 0;

        // v0.11.0 (TRANSFORM_STATION_PLAN.md Change 3): identifies the ACTUAL destination the stocker
        // is about to write to, before any move for this station happens. The user visually confirmed
        // a carpentry rack holds only one hardwood log and one hardwood long stick at a time, so this
        // establishes whether the destination the mod sees really is that one-slot rack. Read against
        // the first shortfall item (the one about to be attempted first).
        if (shortfall.Count > 0) LogDestinationProbe(destColl, shortfall[0].info, villagerName, stationName);

        foreach (var (info, missingQty) in shortfall)
        {
            int remaining = missingQty;
            int movedForThisItem = 0;
            // v0.9.3: set when a candidate's ADD was refused by the destination (removed>0, moved==0)
            // - once that happens no other candidate can succeed either (same destination, same
            // refusal), so the candidate loop below breaks early instead of retrying. Distinct from
            // removed==0 (the SOURCE had nothing - stale/duplicate snapshot entry), which still
            // continues to the next candidate exactly as before.
            bool destinationRefused = false;

            List<SettlementStock.ContainerStock> candidates;
            try { candidates = SettlementStock.GetCandidates(info); }
            catch (Exception ex) { Plugin.Logger.LogError($"{tag} StockStation GetCandidates error: {ex}"); continue; }

            foreach (var candidate in candidates)
            {
                if (remaining <= 0) break;
                int take = Math.Min(remaining, candidate.Qty);
                if (take <= 0) continue;

                // v0.9.2: before/after snapshots of the DESTINATION's quantity of this item, so a
                // zero-move candidate can be told apart from a station that silently already had the
                // item (stationHad == stationNow either way, but removed/added below distinguish a
                // failed REMOVE from a refused ADD). Defaults to -1 on read failure, matching the
                // GetItemQuantity usage already at StationStocker.cs:117.
                int stationHadBefore = -1;
                try { stationHadBefore = destColl.GetItemQuantity(info); } catch { }

                int moved = 0;
                int removed = 0;
                try { moved = MoveContainerToAgent(candidate.Container, destColl, info, take, out removed); }
                catch (Exception ex) { Plugin.Logger.LogError($"{tag} StockStation MoveContainerToAgent error: {ex}"); }

                if (moved <= 0)
                {
                    if (Plugin.TransferDiagnostics.Value)
                    {
                        int stationHadAfter = -1;
                        try { stationHadAfter = destColl.GetItemQuantity(info); } catch { }

                        string zeroMoveKey = SafeName(info) + "|" + stationName;
                        _zeroMoveLogged.TryGetValue(zeroMoveKey, out int zeroMoveCount);
                        if (zeroMoveCount < 5)
                        {
                            _zeroMoveLogged[zeroMoveKey] = zeroMoveCount + 1;
                            // Vector3 must be .ToString()'d explicitly - interpolating a Unity struct
                            // directly into a BepInEx Logger.Log* call throws VerificationException at
                            // the log site (project-wide gotcha).
                            Plugin.Logger.LogInfo($"{tag} StockStation ZERO-MOVE: '{SafeName(info)}' from " +
                                $"{candidate.StructureName}@{candidate.WorldPos.ToString()} node={candidate.NodeName} " +
                                $"type={candidate.TypeName} snapshotQty={candidate.Qty} " +
                                $"take={take} removed={removed} added={moved} stationHad={stationHadBefore} " +
                                $"stationNow={stationHadAfter} (villager={villagerName} station={stationName}).");
                        }
                    }

                    // v0.9.3: removed>0 means the DESTINATION refused the add (not a stale source
                    // snapshot) - in-game evidence 2026-07-28 showed this retried every remaining
                    // candidate for a full/refusing station (6x 'Hot Iron Bloom' into an empty
                    // Storage_HotItemsSmall, 5x 'Bark' into a full 40/40 bin), which just shuffled
                    // items between stations instead of stocking anything. No other candidate can
                    // succeed once the destination itself has refused, so stop trying for this item.
                    if (removed > 0)
                    {
                        destinationRefused = true;
                        break;
                    }
                    continue;
                }

                remaining -= moved;
                movedForThisItem += moved;

                if (Plugin.TransferDiagnostics.Value)
                    Plugin.Logger.LogInfo($"{tag} StockStation: -{moved} '{SafeName(info)}' from " +
                        $"{candidate.StructureName}@{candidate.WorldPos} node={candidate.NodeName} " +
                        $"type={candidate.TypeName} -> station (villager={villagerName} " +
                        $"station={stationName}, still need {Math.Max(0, remaining)}).");
            }

            // v0.9.1: shortfall log line - before v0.9.1 a shortfall here logged NOTHING at all (the
            // per-item line inside the loop above only fires when moved > 0), so a station stuck at
            // e.g. wanted=1/short=1/itemsMoved=0 (confirmed in-game 2026-07-27, Workshop House 4, 280
            // consecutive cycles) gave no clue which item was missing or why. settlementCandidates is
            // the number that answers that: 0 means settlement storage holds none of this item at
            // all; above 0 means candidates existed but the move still failed to shift anything.
            // v0.9.3: appends " destinationRefused=true" when the loop above broke early because the
            // destination itself refused an add, so the log says WHY the item was abandoned early
            // (as opposed to simply exhausting the candidate list).
            if (remaining > 0 && Plugin.TransferDiagnostics.Value)
            {
                Plugin.Logger.LogInfo($"{tag} StockStation SHORT: '{SafeName(info)}' need {missingQty}, moved " +
                    $"{movedForThisItem}, still short {remaining} (villager={villagerName} station={stationName}, " +
                    $"settlementCandidates={candidates.Count})." +
                    (destinationRefused ? " destinationRefused=true" : ""));

                // v0.9.2: candidate dump - immediately follows SHORT so a stuck workshop's log answers
                // "what did the candidate list actually look like" without a second run. Capped at the
                // first 20 candidates and rate-limited separately from the SHORT line itself (3 dumps
                // per item|station - a stuck workshop loops hundreds of times per run, and the
                // candidate list rarely changes call to call).
                string candidatesKey = SafeName(info) + "|" + stationName;
                _candidateDumpLogged.TryGetValue(candidatesKey, out int candidatesCount);
                if (candidatesCount < 3)
                {
                    _candidateDumpLogged[candidatesKey] = candidatesCount + 1;
                    var sb = new StringBuilder();
                    sb.Append($"{tag} StockStation CANDIDATES '{SafeName(info)}' n={candidates.Count}: ");
                    int shown = Math.Min(candidates.Count, 20);
                    for (int i = 0; i < shown; i++)
                    {
                        if (i > 0) sb.Append("; ");
                        var c = candidates[i];
                        sb.Append($"[{i}] {c.StructureName}@{c.WorldPos.ToString()} node={c.NodeName} " +
                            $"type={c.TypeName} qty={c.Qty}");
                    }
                    if (candidates.Count > shown) sb.Append($" ...(+{candidates.Count - shown} more)");
                    Plugin.Logger.LogInfo(sb.ToString());
                }
            }

            if (movedForThisItem > 0) itemsMoved++;
            qtyMoved += movedForThisItem;
            if (remaining > 0) stillShort += remaining;
        }

        if (qtyMoved > 0)
        {
            try { SettlementStock.Invalidate(); }
            catch (Exception ex) { Plugin.Logger.LogError($"{tag} StockStation Invalidate error: {ex}"); }
        }

        return (itemsMoved, qtyMoved, stillShort);
    }

    // Container -> destination collection (agent inventory for a player pull, station inventory for
    // a villager pull). Remove-then-add (OuthouseComposterMod ComposterDiag.DoConvert pattern,
    // confirmed in-game): removes precisely `qty` from THIS container via RemoveFromContainer below,
    // then adds the ACTUAL removed amount to the destination via ItemCollection.AddItems (2-arg,
    // returns the count actually added - the capacity-failure detector). Any shortfall on the add
    // side is added back to the source container so nothing is lost.
    //
    // v0.9.2: gained the `removed` out-parameter so a caller can tell a failed REMOVE (settlement
    // container didn't actually have the item despite SettlementStock's snapshot saying so - a stale
    // or racing snapshot) from a refused ADD (removed fine, but the destination collection wouldn't
    // accept it - e.g. station inventory full/capacity-gated). Both previously collapsed into the
    // same "moved <= 0" outcome at every call site, which is exactly what made a failed candidate
    // leave no trace in the log.
    private static int MoveContainerToAgent(ItemContainer source, ItemCollection destColl, ItemInfo info, int qty, out int removed)
    {
        removed = 0;

        // v0.10.0 (TRANSFORM_STATION_PLAN.md Change 2): ask the destination whether it can even take
        // the item BEFORE touching the source - the prior remove-then-add-then-give-back sequence
        // churned the source on every refusal and left no trace in the log, because the per-move log
        // line below only ever fires on a nonzero move. Both reads share one try/catch: on exception,
        // fall through to the existing behavior unclamped, so a probe failure never blocks a move that
        // works today.
        int clampedQty = qty;
        try
        {
            if (!destColl.canAddItems)
            {
                LogCapacitySkip(info, "destination canAddItems=false");
                return 0;
            }
            int remainingCapacity = destColl.GetTotalRemainingCapacity(info);
            if (remainingCapacity <= 0)
            {
                LogCapacitySkip(info, "destination GetTotalRemainingCapacity<=0");
                return 0;
            }
            if (remainingCapacity < clampedQty) clampedQty = remainingCapacity;
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"[CFS] MoveContainerToAgent capacity pre-check error: {ex} - falling through unclamped.");
        }

        removed = RemoveFromContainer(source, info, clampedQty);
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

    // v0.10.0 (TRANSFORM_STATION_PLAN.md Change 2): rate-limited diagnostic for MoveContainerToAgent's
    // capacity pre-check - same idiom as StockStation's existing zero-move/candidate-dump diagnostics
    // in this file, capped per item name (never an interop wrapper - project-wide gotcha) so one item
    // type refusing repeatedly cannot spam the log.
    private static void LogCapacitySkip(ItemInfo info, string reason)
    {
        if (!Plugin.TransferDiagnostics.Value) return;
        try
        {
            string key = SafeName(info);
            _capacitySkipLogged.TryGetValue(key, out int count);
            if (count >= 5) return;
            _capacitySkipLogged[key] = count + 1;
            Plugin.Logger.LogInfo($"[CFS] MoveContainerToAgent SKIP: '{SafeName(info)}' - {reason} - " +
                "move abandoned without touching the source.");
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"[CFS] LogCapacitySkip error: {ex}");
        }
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
    // v0.9.4: bounds the per-recipe dictionary below - a villager churning through many distinct
    // blueprints in one idle-flush burst shouldn't grow this rollup's memory unbounded.
    private const int MaxRecipesPerVillager = 40;
    // v0.9.4: how many of a villager's recorded recipes get their own segment on the new
    // per-villager Flush line - keeps that line readable when a villager widened a lot of distinct
    // recipes in one burst.
    private const int MaxRecipesShownPerLine = 8;

    private static int _totalCalls;
    private static readonly Dictionary<string, int> _rawLoggedPerVillager = new();
    private static readonly Dictionary<string, int> _countPerVillager = new();
    // v0.9.4: per villager, per recipe name -> (call count, first-seen shortfall signature). Answers
    // WHICH recipe the gate widened and WHAT was missing, which the plain per-villager total above
    // can't - needed to tell whether widening correlates with odd villager crafting choices.
    private static readonly Dictionary<string, Dictionary<string, (int count, string shortfallSig)>> _recipesPerVillager = new();
    // v0.9.5: distinct recipe NAMES dropped past MaxRecipesPerVillager, per villager - so Flush can
    // report the omission instead of silently hiding recipes past the cap. A plain call counter would
    // tally every repeat widening of an already-dropped recipe, so with widening counts in the
    // thousands it would report hundreds where the honest answer is "5 recipes didn't fit" - the set
    // is what makes the reported number match the "distinct recipe(s)" wording on the Flush line.
    private static readonly Dictionary<string, HashSet<string>> _droppedRecipesPerVillager = new();
    private static long _lastCallMs = -1;
    private static readonly Stopwatch _sw = new();

    // Returns true if THIS call should also get its own raw log line (first MaxRawLogsPerVillager
    // per villager per idle-flush burst).
    internal static bool Record(string villagerName, string bpName, string shortfallSig)
    {
        try
        {
            if (!_sw.IsRunning) _sw.Restart();
            _lastCallMs = _sw.ElapsedMilliseconds;
            _totalCalls++;
            _countPerVillager.TryGetValue(villagerName, out int c);
            _countPerVillager[villagerName] = c + 1;

            // v0.9.4: per-recipe detail for this villager - first shortfall signature seen for a
            // given recipe wins, later differing signatures aren't interesting enough to keep.
            if (!_recipesPerVillager.TryGetValue(villagerName, out var recipes))
            {
                recipes = new Dictionary<string, (int, string)>();
                _recipesPerVillager[villagerName] = recipes;
            }
            if (recipes.TryGetValue(bpName, out var entry))
            {
                recipes[bpName] = (entry.count + 1, entry.shortfallSig);
            }
            else if (recipes.Count < MaxRecipesPerVillager)
            {
                recipes[bpName] = (1, shortfallSig);
            }
            else
            {
                if (!_droppedRecipesPerVillager.TryGetValue(villagerName, out var dropped))
                {
                    dropped = new HashSet<string>();
                    _droppedRecipesPerVillager[villagerName] = dropped;
                }
                dropped.Add(bpName);
            }

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

        // v0.9.4: one extra line per villager with recorded recipes - which recipe(s) got widened
        // and what was missing, so the rollup line's per-villager COUNT can be paired with the
        // recipe(s) driving it. Isolated in its own try/catch so a formatting bug here can never
        // take down the existing rollup line above.
        try
        {
            foreach (var kv in _recipesPerVillager)
            {
                string villagerName = kv.Key;
                var recipes = kv.Value;
                if (recipes.Count == 0) continue;

                var ordered = recipes.OrderByDescending(r => r.Value.count).ToList();
                var shown = ordered.Take(MaxRecipesShownPerLine)
                    .Select(r => $"'{r.Key}'x{r.Value.count} shortfall=[{r.Value.shortfallSig}]");
                string segments = string.Join(", ", shown);

                int omitted = ordered.Count - MaxRecipesShownPerLine;
                string omittedPart = omitted > 0 ? $" (+{omitted} more recipe(s) omitted)" : "";

                _droppedRecipesPerVillager.TryGetValue(villagerName, out var neverRecordedSet);
                int neverRecorded = neverRecordedSet?.Count ?? 0;
                string neverRecordedPart = neverRecorded > 0
                    ? $" (+{neverRecorded} distinct recipe(s) never recorded - per-villager cap {MaxRecipesPerVillager})"
                    : "";

                Plugin.Logger.LogInfo($"[CFS] [CFS-V] widened recipes for {villagerName}: {segments}{omittedPart}{neverRecordedPart}");
            }
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"[CFS] [CFS-V] VillagerAvailabilityRollup.Flush per-recipe error: {ex}");
        }
        finally
        {
            _totalCalls = 0;
            _rawLoggedPerVillager.Clear();
            _countPerVillager.Clear();
            _recipesPerVillager.Clear();
            _droppedRecipesPerVillager.Clear();
            _sw.Reset();
            _lastCallMs = -1;
        }
    }
}
