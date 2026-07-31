using System;
using HarmonyLib;
using SSSGame.AI;
using SSSGame.AI.FSM;

namespace CraftFromStorageMod.Patches;

// v0.15.0 (idea-17 next feature groundwork): READ-ONLY diagnostic spike on the bloomery/kiln supply
// FSM chain - see BloomeryTrace.cs's header for the full rationale and the questions this spike
// answers. Four postfix patches, attached INDIVIDUALLY in Plugin.Load() (not PatchAll on this whole
// class) so a per-patch try/catch and a load-time "attached" log line exist for each, matching
// StationStockPatches.cs / VillagerFetchPatches.cs's established style. All gated on the same
// Probe/EnableBloomeryTrace config bind - both the load-time attach AND a cheap in-handler re-check,
// mirroring how other Probe patches in this mod gate themselves.
//
// FSM_FetchBloomerySupplies and FSM_FetchKilnSupplies are both FSM_QuestAction subclasses (same base
// and OnStateEnter/OnStateExit signature family as the already-safely-patched
// FSM_FetchCraftingSupplies - Cecil-confirmed 2026-07-31), so BloomeryTrace's handlers accept the
// common FSM_QuestAction base type and are shared by all four patches below.
[HarmonyPatch(typeof(FSM_FetchBloomerySupplies), nameof(FSM_FetchBloomerySupplies.OnStateEnter))]
internal static class FetchBloomerySuppliesEnterPatch
{
    static void Postfix(FSM_FetchBloomerySupplies __instance, IFSMBehaviourController fsmBehaviour)
    {
        try
        {
            if (!VillagerFetchTrace.SafeGetBool(Plugin.EnableBloomeryTrace, true)) return;
            BloomeryTrace.HandleFetchEnter(__instance, fsmBehaviour, "bloomery");
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"[CFS] [CFS-BLM] FetchBloomerySuppliesEnterPatch: {ex}");
        }

        // v0.16.0 (bloomery delivery feature, toggle 2): own try/catch, independent of the trace call
        // above - a throw here must never break the FSM state transition.
        try
        {
            BloomeryTrace.TryStockBloomery(__instance, fsmBehaviour, "bloomery");
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"[CFS] [CFS-BLM] FetchBloomerySuppliesEnterPatch TryStockBloomery: {ex}");
        }
    }
}

[HarmonyPatch(typeof(FSM_FetchBloomerySupplies), nameof(FSM_FetchBloomerySupplies.OnStateExit))]
internal static class FetchBloomerySuppliesExitPatch
{
    static void Postfix(FSM_FetchBloomerySupplies __instance, IFSMBehaviourController fsmBehaviour)
    {
        try
        {
            if (!VillagerFetchTrace.SafeGetBool(Plugin.EnableBloomeryTrace, true)) return;
            BloomeryTrace.HandleFetchExit(__instance, fsmBehaviour, "bloomery");
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"[CFS] [CFS-BLM] FetchBloomerySuppliesExitPatch: {ex}");
        }
    }
}

[HarmonyPatch(typeof(FSM_FetchKilnSupplies), nameof(FSM_FetchKilnSupplies.OnStateEnter))]
internal static class FetchKilnSuppliesEnterPatch
{
    static void Postfix(FSM_FetchKilnSupplies __instance, IFSMBehaviourController fsmBehaviour)
    {
        try
        {
            if (!VillagerFetchTrace.SafeGetBool(Plugin.EnableBloomeryTrace, true)) return;
            BloomeryTrace.HandleFetchEnter(__instance, fsmBehaviour, "kiln");
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"[CFS] [CFS-BLM] FetchKilnSuppliesEnterPatch: {ex}");
        }

        // v0.16.0 (bloomery delivery feature, toggle 2): own try/catch, independent of the trace call
        // above - a throw here must never break the FSM state transition.
        try
        {
            BloomeryTrace.TryStockBloomery(__instance, fsmBehaviour, "kiln");
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"[CFS] [CFS-BLM] FetchKilnSuppliesEnterPatch TryStockBloomery: {ex}");
        }
    }
}

[HarmonyPatch(typeof(FSM_FetchKilnSupplies), nameof(FSM_FetchKilnSupplies.OnStateExit))]
internal static class FetchKilnSuppliesExitPatch
{
    static void Postfix(FSM_FetchKilnSupplies __instance, IFSMBehaviourController fsmBehaviour)
    {
        try
        {
            if (!VillagerFetchTrace.SafeGetBool(Plugin.EnableBloomeryTrace, true)) return;
            BloomeryTrace.HandleFetchExit(__instance, fsmBehaviour, "kiln");
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"[CFS] [CFS-BLM] FetchKilnSuppliesExitPatch: {ex}");
        }
    }
}
