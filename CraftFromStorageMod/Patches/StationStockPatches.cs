using System;
using HarmonyLib;
using SSSGame.AI;
using SSSGame.AI.FSM;

namespace CraftFromStorageMod.Patches;

// v0.9.0 (idea-17 Phase 2c) - see StationStocker.cs (project root) for the full design rationale.
// Deliberately a SEPARATE file/patch class from Patches/VillagerFetchPatches.cs: that file's header
// comment declares every patch in it READ-ONLY (observation only, no game-state writes), and this
// patch mutates the crafting station's inventory. A second, read-only postfix on this SAME target
// method already exists there (FetchCraftingSuppliesEnterPatch) - two Harmony patch classes on one
// target coexist fine (Harmony applies both postfixes independently); this one is purely additive
// alongside it.
//
// IMPORTANT: FSM state actions (FSM_FetchCraftingSupplies and its kin) are SHARED ScriptableObjects -
// one instance serves every villager running this FSM state, not one instance per villager. Per-
// villager identity must always come from the fsmBehaviour/QuestData parameters passed into
// OnStateEnter, never from a field cached on __instance (see VillagerFetchTrace.cs's own header for
// the same constraint on the read-only spike this patch sits alongside).
[HarmonyPatch(typeof(FSM_FetchCraftingSupplies), nameof(FSM_FetchCraftingSupplies.OnStateEnter))]
internal static class FetchCraftingSuppliesStockPatch
{
    static void Postfix(FSM_FetchCraftingSupplies __instance, IFSMBehaviourController fsmBehaviour)
    {
        try
        {
            StationStocker.HandleFetchStateEnter(__instance, fsmBehaviour);
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"[CFS] [CFS-SS] FetchCraftingSuppliesStockPatch: {ex}");
        }
    }
}
