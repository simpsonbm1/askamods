using System;
using HarmonyLib;
using SSSGame;
using SSSGame.AI;
using SSSGame.AI.FSM;

namespace CraftFromStorageMod.Patches;

// v0.8.0 (idea-17 Phase 2b) - see FetchQuestSuppression.cs (project root) for the full design
// rationale and shared logic. Two patch classes, matching two separate native declarations
// (confirmed via Cecil, _explore/cecil_craft_from_storage7_out.txt PASS D):
//   SSSGame.CrafterFetchQuest         :: Single GetPriority(QuestData questData) [virt,pub]
//   SSSGame.CrafterSpecificFetchQuest :: Single GetPriority(QuestData questData) [virt,pub] (re-declared)
// CrafterSpecificFetchQuest extends CrafterFetchQuest but RE-DECLARES GetPriority instead of
// inheriting it, so patching only the base type would miss every CrafterSpecificFetchQuest instance -
// same reasoning GatePatches.cs already documents for the three separate BeginCraftingSequence patch
// classes (CraftInteraction/AnvilInteraction/DyeingInteraction). SSSGame.StudyFetchQuest ALSO
// re-declares GetPriority (same Cecil pass) but is deliberately left unpatched here - studying is not
// crafting, out of scope for this change.
//
// GetNeededSuppliesManifest() (ItemManifest return type - CALLED, never patched, per the project's
// inventory-family-parameter crash rule) is declared on all three CrafterFetchQuest/
// CrafterSpecificFetchQuest/StudyFetchQuest, virtual, so instance.GetNeededSuppliesManifest() in
// FetchQuestSuppression.HandleGetPriority dispatches correctly for whichever concrete type owns the
// patched GetPriority that called it.
//
// Patch application is gated in Plugin.cs Load() on EnableForVillagers alone (the only config flag
// this feature answers to) - matching this mod's established per-flag harmony.PatchAll() convention.
[HarmonyPatch(typeof(CrafterFetchQuest), nameof(CrafterFetchQuest.GetPriority))]
internal static class CrafterFetchQuestGetPriorityPatch
{
    static void Postfix(CrafterFetchQuest __instance, QuestData questData, ref float __result)
    {
        try { FetchQuestSuppression.HandleGetPriority(__instance, "CrafterFetchQuest", questData, ref __result); }
        catch (Exception ex) { Plugin.Logger.LogError($"[CFS] [CFS-FQ] CrafterFetchQuestGetPriorityPatch: {ex}"); }
    }
}

[HarmonyPatch(typeof(CrafterSpecificFetchQuest), nameof(CrafterSpecificFetchQuest.GetPriority))]
internal static class CrafterSpecificFetchQuestGetPriorityPatch
{
    static void Postfix(CrafterSpecificFetchQuest __instance, QuestData questData, ref float __result)
    {
        // __instance is a CrafterSpecificFetchQuest, passed into HandleGetPriority's CrafterFetchQuest
        // parameter - an ordinary safe upcast (see FetchQuestSuppression.cs header comment), not the
        // base-declared-type cast gotcha (which only bites DOWNcasts).
        try { FetchQuestSuppression.HandleGetPriority(__instance, "CrafterSpecificFetchQuest", questData, ref __result); }
        catch (Exception ex) { Plugin.Logger.LogError($"[CFS] [CFS-FQ] CrafterSpecificFetchQuestGetPriorityPatch: {ex}"); }
    }
}
