using HarmonyLib;
using SandSailorStudio.RNG;

namespace SeedScoutMod;

// Capture the runtime seed manager. RandomGeneratorManager.seedPhrase holds the ACTUAL
// current world seed (the RandomGeneratorConfig asset only carries the default placeholder).
[HarmonyPatch(typeof(RandomGeneratorManager), "OnEnable")]
internal static class RngOnEnablePatch
{
    static void Postfix(RandomGeneratorManager __instance)
    {
        Plugin.Rng = __instance;
    }
}

// Log when/whether the seed is set during load — intel for the eventual automation loop.
[HarmonyPatch(typeof(RandomGeneratorManager), "SetSeedPhrase")]
internal static class RngSetSeedPatch
{
    static void Postfix(string seedPhrase)
    {
        Plugin.Logger.LogInfo($"SeedScout: RandomGeneratorManager.SetSeedPhrase('{seedPhrase}')");
    }
}
