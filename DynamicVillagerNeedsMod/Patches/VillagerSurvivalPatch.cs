using System;
using HarmonyLib;
using SSSGame;

namespace DynamicVillagerNeedsMod.Patches;

// Register each villager's survival component as it spawns. NeedsController reads its rest pool and
// the owning Villager's happiness each tick to pick a need-driven behavior.
[HarmonyPatch(typeof(VillagerSurvival), nameof(VillagerSurvival.Spawned))]
internal static class VillagerSurvivalSpawnedPatch
{
    static void Postfix(VillagerSurvival __instance)
    {
        try
        {
            if (!Plugin.TrackedSurvivals.Contains(__instance))
                Plugin.TrackedSurvivals.Add(__instance);

            if (Plugin.DebugLogging.Value)
                Plugin.Logger.LogInfo($"[DynamicNeeds] Villager registered ({Plugin.TrackedSurvivals.Count} tracked).");
        }
        catch (Exception ex) { Plugin.Logger.LogError($"[DynamicNeeds] SpawnedPatch: {ex}"); }
    }
}
