using HarmonyLib;
using SandSailorStudio.Streaming;

namespace SeedScoutMod;

// Capture the WorldStreamingManager so we can force-stream the tiles around each cave
// (RequestLoadWorldTile) instead of physically walking there to wake them up. Same pattern as
// BiomesCapture — grab the instance from its own Awake, never FindObjectsByType.
[HarmonyPatch(typeof(WorldStreamingManager), "Awake")]
internal static class StreamingAwakePatch
{
    static void Postfix(WorldStreamingManager __instance)
    {
        Plugin.Streaming = __instance;
        Plugin.LogInfo("SeedScout: WorldStreamingManager.Awake — captured.");
    }
}
