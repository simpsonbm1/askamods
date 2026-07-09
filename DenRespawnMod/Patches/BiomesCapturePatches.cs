using HarmonyLib;
using SSSGame;

namespace DenRespawnMod.Patches;

// Capture the world's BiomesManager from its own lifecycle (repo rule: never FindObjectsByType —
// grab instances from patches). _worldGenerator hangs off it once the world is up; feeds
// MarkerRefresher's area-instance lookup (v1.1.3 pin recolor). Mirrors SeedScoutMod's
// BiomesCapture.cs exactly.
[HarmonyPatch(typeof(BiomesManager), "Awake")]
internal static class BiomesAwakePatch
{
    static void Postfix(BiomesManager __instance)
    {
        Plugin.Biomes = __instance;
        Plugin.Logger.LogInfo("[DenRespawn] BiomesManager.Awake — captured (a world is loading).");
    }
}

[HarmonyPatch(typeof(BiomesManager), "OnDisable")]
internal static class BiomesOnDisablePatch
{
    static void Postfix(BiomesManager __instance)
    {
        if (ReferenceEquals(Plugin.Biomes, __instance))
            Plugin.Biomes = null;
    }
}
