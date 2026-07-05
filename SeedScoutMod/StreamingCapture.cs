using HarmonyLib;
using SandSailorStudio.Streaming;

namespace SeedScoutMod;

// Capture the WorldStreamingManager from its own lifecycle (never FindObjectsByType — IL2CPP
// trampoline crash). Feeds the force-load routine; refreshed by Awake on every world load.
[HarmonyPatch(typeof(WorldStreamingManager), "Awake")]
internal static class StreamingCapturePatch
{
    static void Postfix(WorldStreamingManager __instance)
    {
        Plugin.Streaming = __instance;
        Plugin.LogInfo("SeedScout: WorldStreamingManager captured.");
    }
}
