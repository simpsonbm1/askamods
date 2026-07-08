using System;
using HarmonyLib;
using SSSGame;

namespace HealthRegenMod.Patches;

[HarmonyPatch(typeof(Villager), nameof(Villager.Spawned))]
internal static class VillagerSpawnedPatch
{
    static void Postfix(Villager __instance)
    {
        try
        {
            if (!Plugin.TrackedVillagers.Contains(__instance))
            {
                Plugin.TrackedVillagers.Add(__instance);
                if (Plugin.VillagerDebugLogging.Value)
                    Plugin.Logger.LogInfo($"[HealthRegenMod] Villager registered ({Plugin.TrackedVillagers.Count} tracked).");
            }
        }
        catch (Exception ex) { Plugin.Logger.LogError($"[HealthRegenMod] VillagerSpawnedPatch: {ex}"); }
    }
}

[HarmonyPatch(typeof(Villager), nameof(Villager.Despawned))]
internal static class VillagerDespawnedPatch
{
    static void Postfix(Villager __instance)
    {
        try
        {
            Plugin.TrackedVillagers.Remove(__instance);
            RegenTracker.ForgetVillager(__instance);
        }
        catch (Exception ex) { Plugin.Logger.LogError($"[HealthRegenMod] VillagerDespawnedPatch: {ex}"); }
    }
}
