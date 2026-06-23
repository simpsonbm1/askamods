using HarmonyLib;
using SSSGame;          // CookingQuest, CookingStockpileQuest
using SSSGame.AI;       // QuestData
using SSSGame.AI.FSM;

namespace CookingStationFixMod.Patches;

// PHASE 1 diagnostics. Every cooking FSM state derives from FSM_QuestDecision / FSM_QuestAction, both of
// which expose GetQuestData(fsmBehaviour). We postfix the entry points of the cook's work loop (log-only)
// so the BepInEx log shows the exact state sequence a stalled cook cycles through plus the stall latches
// on its quest data. Patching these cooking-specific FSM types means every call IS a cook — no extra
// filtering needed. Nothing is modified; __result is read by value, never written.

// THE GATE. FSM_CanStartCooking decides whether the cook may begin. If this keeps returning false, the
// sit/stand loop is the cook re-asking and re-failing — and the logged latches say why.
[HarmonyPatch(typeof(FSM_CanStartCooking), nameof(FSM_CanStartCooking.Decide))]
internal static class CanStartCookingDiag
{
    static void Postfix(FSM_CanStartCooking __instance, IFSMBehaviourController fsmBehaviour, bool __result)
        => Plugin.LogCookState(fsmBehaviour, __instance.GetQuestData(fsmBehaviour), $"CanStartCooking.Decide={__result}");
}

// Cook gathers raw ingredients into the station stockpile.
[HarmonyPatch(typeof(FSM_FetchCookingStockpile), nameof(FSM_FetchCookingStockpile.OnStateEnter))]
internal static class FetchStockpileDiag
{
    static void Postfix(FSM_FetchCookingStockpile __instance, IFSMBehaviourController fsmBehaviour)
        => Plugin.LogCookState(fsmBehaviour, __instance.GetQuestData(fsmBehaviour), "FetchCookingStockpile.Enter");
}

// Cook fetches the specific supplies a recipe needs.
[HarmonyPatch(typeof(FSM_FetchCookingSupplies), nameof(FSM_FetchCookingSupplies.OnStateEnter))]
internal static class FetchSuppliesDiag
{
    static void Postfix(FSM_FetchCookingSupplies __instance, IFSMBehaviourController fsmBehaviour)
        => Plugin.LogCookState(fsmBehaviour, __instance.GetQuestData(fsmBehaviour), "FetchCookingSupplies.Enter");
}

// Cook decides WHERE in the oven each ingredient goes — the "mixing up which ingredients go where" step.
[HarmonyPatch(typeof(FSM_SetCookingPlacePosition), nameof(FSM_SetCookingPlacePosition.OnStateEnter))]
internal static class PlacePositionDiag
{
    static void Postfix(FSM_SetCookingPlacePosition __instance, IFSMBehaviourController fsmBehaviour)
        => Plugin.LogCookState(fsmBehaviour, __instance.GetQuestData(fsmBehaviour), "SetCookingPlacePosition.Enter");
}

// Cook returns the finished food to storage (only reached if cooking actually completes).
[HarmonyPatch(typeof(FSM_ReturnCookingResults), nameof(FSM_ReturnCookingResults.OnStateEnter))]
internal static class ReturnResultsDiag
{
    static void Postfix(FSM_ReturnCookingResults __instance, IFSMBehaviourController fsmBehaviour)
        => Plugin.LogCookState(fsmBehaviour, __instance.GetQuestData(fsmBehaviour), "ReturnCookingResults.Enter");
}

// PRIORITY PROBES. The QuestRunner picks the cook's active quest by priority. If CookingQuest.GetPriority
// never logs, there is no active cook project (nothing queued to cook). If it logs but lower than the
// stockpile quest, the satisfied stockpile quest is winning arbitration and starving the cook quest — the
// stuck loop. Both GetPriority are Single GetPriority(QuestData) — safe to read __result by value.
[HarmonyPatch(typeof(CookingQuest), nameof(CookingQuest.GetPriority))]
internal static class CookingQuestPriorityDiag
{
    static void Postfix(float __result) => Plugin.LogPriority("CookingQuest", __result);
}

[HarmonyPatch(typeof(CookingStockpileQuest), nameof(CookingStockpileQuest.GetPriority))]
internal static class CookingStockpilePriorityDiag
{
    static void Postfix(float __result) => Plugin.LogPriority("CookingStockpileQuest", __result);
}
