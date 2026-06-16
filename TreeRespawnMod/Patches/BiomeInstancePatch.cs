using HarmonyLib;
using SSSGame;

namespace TreeRespawnMod.Patches;

// Builds a position → live instance registry as the world loads biome items.
// Position is the only reliable unique identifier — UniqueId is a per-buffer index
// that restarts at 0 in every spatial chunk.
[HarmonyPatch(typeof(BiomeItemInstance), nameof(BiomeItemInstance.Initialize))]
internal static class BiomeInstancePatch
{
    static void Postfix(BiomeItemInstance __instance)
    {
        try
        {
            var pos = __instance.GetPosition();
            Plugin.ActiveInstances[Plugin.PosKey(pos)] = __instance;
        }
        catch { }
    }
}
