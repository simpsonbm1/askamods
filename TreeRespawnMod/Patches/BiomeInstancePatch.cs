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
            string posKey = Plugin.PosKey(pos);
            bool firstSeenThisWorld = !Plugin.ActiveInstances.ContainsKey(posKey);
            Plugin.ActiveInstances[posKey] = __instance;

            // Capture the persistent data handler once — the v1.2.9 experimental deactivated-replenish path
            // uses it to resolve a usable instance for an unloaded node (handler.GetInstance(onlyIfActive:false)).
            if (Plugin.DataHandler == null)
            {
                try { Plugin.DataHandler = BiomeItemInstance.Handler; } catch { }   // Handler is a static (shared) field
            }

            // Tells us whether a distant/villager-only area streams in on the host at all —
            // see TREERESPAWN_HANDOFF.md Issue C. Logged once per position per world (not every
            // re-stream) to keep EnableDiagnostics usable for a real play session instead of
            // flooding the log (a single run-through logged the same handful of positions 90+
            // times each — confirmed in-game 2026-06-28).
            if (firstSeenThisWorld && Plugin.EnableDiagnostics.Value)
                Plugin.Logger.LogInfo($"[TreeRespawnMod] [diag] init {posKey}");
        }
        catch { }
    }
}
