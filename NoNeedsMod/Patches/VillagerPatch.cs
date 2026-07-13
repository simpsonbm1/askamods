using System;
using HarmonyLib;
using SSSGame;

namespace NoNeedsMod.Patches;

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
                if (Plugin.DebugLogging.Value)
                    Plugin.Logger.LogInfo($"[NoNeedsMod] Villager registered ({Plugin.TrackedVillagers.Count} tracked).");
            }
        }
        catch (Exception ex) { Plugin.Logger.LogError($"[NoNeedsMod] VillagerSpawnedPatch: {ex}"); }
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
        }
        catch (Exception ex) { Plugin.Logger.LogError($"[NoNeedsMod] VillagerDespawnedPatch: {ex}"); }
    }
}
