using System;
using SSSGame;

namespace MineRefreshMod.Patches;

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
