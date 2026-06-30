using HarmonyLib;
using SSSGame;

namespace TreeRespawnMod.Patches;

// Capture-only (no behavior change). Holds the BiomesManager so the gather-registration
// diagnostic can read the local player's position (BiomesManager._localPlayerTransform) and
// log how far the player was from a node at harvest time. This is the v1.2.7 probe for the
// open question in TREERESPAWN_HANDOFF.md: do villagers harvest a node whose tile is NOT
// streamed in near the host? Mirrors the capture WarpTour/SeedScout already use (repo rule:
// never FindObjectsByType — grab the instance from its own lifecycle).
[HarmonyPatch(typeof(BiomesManager), "Awake")]
internal static class BiomesAwakePatch
{
    static void Postfix(BiomesManager __instance) => Plugin.Biomes = __instance;
}

[HarmonyPatch(typeof(BiomesManager), "OnDisable")]
internal static class BiomesOnDisablePatch
{
    static void Postfix(BiomesManager __instance)
    {
        if (ReferenceEquals(Plugin.Biomes, __instance)) Plugin.Biomes = null;
    }
}

[HarmonyPatch(typeof(PlayerCharacter), nameof(PlayerCharacter.Spawned))]
internal static class PlayerSpawnedPatch
{
    static void Postfix(PlayerCharacter __instance)
    {
        try
        {
            if (!__instance.HasAuthority) return;
            Plugin.LocalPlayer = __instance;
        }
        catch { }
    }
}

[HarmonyPatch(typeof(PlayerCharacter), nameof(PlayerCharacter.Despawned))]
internal static class PlayerDespawnedPatch
{
    static void Postfix(PlayerCharacter __instance)
    {
        try
        {
            if (Plugin.LocalPlayer == __instance)
                Plugin.LocalPlayer = null;
        }
        catch { }
    }
}
