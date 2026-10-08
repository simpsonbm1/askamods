using System;
using System.Collections.Generic;
using HarmonyLib;
using SSSGame;

namespace ItemCountAuditMod;

// Villager capture - the DynamicVillagerNeedsMod pattern (VillagerSurvival.Spawned postfix, no
// parameters, no inventory-family types). FindObjectsByType<T>() throws through the trampoline,
// so instances are collected here as they spawn and dropped lazily when they go null.
internal static class VillagerRegistry
{
    internal static readonly List<VillagerSurvival> Tracked = new();
}

[HarmonyPatch(typeof(VillagerSurvival), nameof(VillagerSurvival.Spawned))]
internal static class VillagerSurvivalSpawnedPatch
{
    static void Postfix(VillagerSurvival __instance)
    {
        try
        {
            if (!VillagerRegistry.Tracked.Contains(__instance))
                VillagerRegistry.Tracked.Add(__instance);
        }
        catch (Exception ex) { Plugin.Logger.LogError($"[ItemCountAudit] SpawnedPatch: {ex}"); }
    }
}
