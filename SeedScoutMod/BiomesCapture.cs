using HarmonyLib;
using SSSGame;

namespace SeedScoutMod;

// Capture the world's BiomesManager from its own lifecycle (repo rule: never
// FindObjectsByType — grab instances from patches). _worldGenerator + _localPlayerTransform
// hang off it once the world is up.
[HarmonyPatch(typeof(BiomesManager), "Awake")]
internal static class BiomesAwakePatch
{
    static void Postfix(BiomesManager __instance)
    {
        Plugin.Biomes = __instance;
        Plugin.Logger.LogInfo("SeedScout: BiomesManager.Awake — captured (a world is loading).");
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
