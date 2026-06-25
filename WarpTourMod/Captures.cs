using System;
using HarmonyLib;
using SandSailorStudio.Streaming;
using SSSGame;
using SSSGame.Controllers;
using UnityEngine;

namespace WarpTourMod;

// All the instance/POI captures the tour needs, grabbed from each type's own lifecycle (repo rule:
// never FindObjectsByType). Mirrors SeedScoutMod's capture set, trimmed to what WarpTour uses.

[HarmonyPatch(typeof(BiomesManager), "Awake")]
internal static class BiomesAwakePatch
{
    static void Postfix(BiomesManager __instance)
    {
        Plugin.Biomes = __instance;
        Plugin.Logger.LogInfo("WarpTour: BiomesManager.Awake — captured (a world is loading).");
    }
}

[HarmonyPatch(typeof(BiomesManager), "OnDisable")]
internal static class BiomesOnDisablePatch
{
    static void Postfix(BiomesManager __instance)
    {
        if (ReferenceEquals(Plugin.Biomes, __instance)) Plugin.Biomes = null;
    }
}

[HarmonyPatch(typeof(WorldStreamingManager), "Awake")]
internal static class StreamingAwakePatch
{
    static void Postfix(WorldStreamingManager __instance)
    {
        Plugin.Streaming = __instance;
        Plugin.Logger.LogInfo("WarpTour: WorldStreamingManager.Awake — captured.");
    }
}

// Dens stream in per-tile; record world (x,z) so the tour has targets. Position only — den TYPE lives
// in SeedScoutMod; WarpTour just needs somewhere to teleport.
[HarmonyPatch(typeof(Den), "Start")]
internal static class DenStartPatch
{
    static void Postfix(Den __instance)
    {
        try
        {
            if (__instance == null) return;
            var p = __instance.transform.position;
            var pos = new Vector2(p.x, p.z);
            foreach (var h in Plugin.Hostiles)
                if ((h - pos).sqrMagnitude < 16f) return; // dedupe ~4m
            Plugin.Hostiles.Add(pos);
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"WarpTour: den capture err: {e.Message}"); }
    }
}

[HarmonyPatch(typeof(BiomeLakeStampMask), "_OnSpawnBiome")]
internal static class LakeSpawnPatch
{
    static void Postfix(BiomeLakeStampMask __instance)
    {
        try
        {
            var p = __instance.transform.position;
            var pos = new Vector2(p.x, p.z);
            foreach (var l in Plugin.Lakes)
                if ((l - pos).sqrMagnitude < 4f) return; // dedupe ~2m
            Plugin.Lakes.Add(pos);
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"WarpTour: lake capture err: {e.Message}"); }
    }
}

// The local player's PlayerDrive (Teleport) and PlayerCharacter (HP). HasAuthority = this client's own
// avatar, not a co-op peer's. Same patterns SeedScoutMod / HealthRegenMod use.
[HarmonyPatch(typeof(PlayerDrive), "Spawned")]
internal static class PlayerDriveSpawnedPatch
{
    static void Postfix(PlayerDrive __instance)
    {
        Plugin.Drive = __instance;
        Plugin.Logger.LogInfo("WarpTour: PlayerDrive captured (Spawned).");
    }
}

[HarmonyPatch(typeof(PlayerDrive), "OnDestroy")]
internal static class PlayerDriveOnDestroyPatch
{
    static void Postfix(PlayerDrive __instance)
    {
        if (ReferenceEquals(Plugin.Drive, __instance)) Plugin.Drive = null;
    }
}

[HarmonyPatch(typeof(PlayerCharacter), nameof(PlayerCharacter.Spawned))]
internal static class PlayerCharacterSpawnedPatch
{
    static void Postfix(PlayerCharacter __instance)
    {
        try
        {
            if (!__instance.HasAuthority) return;
            Plugin.Player = __instance;
            Plugin.Logger.LogInfo("WarpTour: local PlayerCharacter captured.");
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"WarpTour: player capture err: {e.Message}"); }
    }
}

[HarmonyPatch(typeof(PlayerCharacter), nameof(PlayerCharacter.Despawned))]
internal static class PlayerCharacterDespawnedPatch
{
    static void Postfix(PlayerCharacter __instance)
    {
        if (ReferenceEquals(Plugin.Player, __instance)) Plugin.Player = null;
    }
}
