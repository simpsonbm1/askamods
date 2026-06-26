using System;
using HarmonyLib;
using SSSGame;

namespace MineRefreshMod.Patches;

[HarmonyPatch(typeof(PlayerCharacter), nameof(PlayerCharacter.Spawned))]
internal static class PlayerSpawnedPatch
{
    static void Postfix(PlayerCharacter __instance)
    {
        try
        {
            if (!__instance.HasAuthority) return;
            Plugin.LocalPlayer = __instance;
            Plugin.Logger.LogInfo("[MineRefreshMod] Local player character registered.");
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"[MineRefreshMod] PlayerSpawnedPatch: {ex}");
        }
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
            {
                Plugin.LocalPlayer = null;
                Plugin.Logger.LogInfo("[MineRefreshMod] Local player character cleared.");
            }
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"[MineRefreshMod] PlayerDespawnedPatch: {ex}");
        }
    }
}
