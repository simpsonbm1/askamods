using System;
using System.Collections.Generic;
using HarmonyLib;
using SSSGame;

namespace TreeRespawnMod.Patches;

// Keep village woodcutters from grubbing firewood out of leftover tree stumps (which destroys the
// stump and trips DayTracker's "stump cleared → cancel respawn", slowly deforesting the area).
//
// Identity confirmed in-game (2026-06-26, TreeRespawnMod v1.1.3 diagnostic): the in-game "Tree Stump"
// is NOT a separate object — it's the same Harvest_Wood_* BiomeItemInstance as the tree, sitting at
// its LAST harvest piece (a Fir has pieces=2: trunk, then stump). Standing trees are the same object
// at an earlier piece; the fallen trunk/branches are separate Item_Wood_* non-biome instances. A
// stump returns CanProvideItem → 6/8 firewood, which is what the woodcutter acts on (every other gate
// already reads depleted — see architecture.md). Earlier gates that keyed on Plugin.PendingRespawns
// failed: the HarvestInteraction transform position differs from BiomeItemInstance.GetPosition(), and
// registration races the CanProvideItem query — so we gate STRUCTURALLY instead.
//
// Stump = multi-piece BiomeItemInstance at its last piece. This deliberately does NOT touch:
//   - standing trees (same object, but not at the last piece) → woodcutter still fells them;
//   - fallen logs / branches (Item_Wood_*, non-biome) → woodcutter still hauls the wood.
// The player clears a stump with axe damage (TakeDamage), not this AI query, so manual clearing —
// and the "cleared stump = permanent" control — still works.
[HarmonyPatch(typeof(HarvestInteraction), nameof(HarvestInteraction.CanProvideItem))]
internal static class StumpProtectionPatch
{
    // Confirm-log once per stump instance so it doesn't spam the per-frame AI search.
    static readonly HashSet<int> _logged = new();

    static void Postfix(HarvestInteraction __instance, ref int __result)
    {
        try
        {
            if (!Plugin.ProtectStumps.Value) return;
            if (__result <= 0 || __instance == null) return;

            var pieces = __instance.harvestPieces;
            if (pieces == null || pieces.Count < 2) return;               // single-piece resource — no stump
            if (__instance.GetCurrentPieceIndex() != pieces.Count - 1) return; // not at the stump piece
            if (__instance._worldInstance?.TryCast<BiomeItemInstance>() == null) return; // not a tree (a loose log/debris)

            __result = 0; // stump provides nothing to villagers → woodcutter leaves it to regrow

            if (Plugin.EnableDiagnostics.Value && _logged.Add(__instance.GetInstanceID()))
                Plugin.Logger.LogInfo(
                    "[TreeRespawnMod] Stump left to regrow — hidden from woodcutter firewood search.");
        }
        catch (Exception ex)
        {
            try { Plugin.Logger.LogError($"[TreeRespawnMod] StumpProtectionPatch: {ex}"); } catch { }
        }
    }
}
