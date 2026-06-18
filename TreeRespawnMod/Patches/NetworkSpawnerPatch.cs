using HarmonyLib;

namespace TreeRespawnMod.Patches;

[HarmonyPatch(typeof(SSSGame.Network.NetworkSpawner), "Awake")]
internal static class NetworkSpawnerPatch
{
    private static bool _logged = false;

    static void Postfix(SSSGame.Network.NetworkSpawner __instance)
    {
        Plugin.GameNetworkSpawner = __instance;
        if (!_logged) { _logged = true; Plugin.Logger.LogInfo("[TreeRespawnMod] NetworkSpawner cached."); }
    }
}
