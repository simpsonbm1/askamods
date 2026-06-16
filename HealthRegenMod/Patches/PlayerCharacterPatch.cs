using System;
using HarmonyLib;
using SSSGame;

namespace HealthRegenMod.Patches;

// HasAuthority distinguishes this client's own avatar from other players' characters
// in co-op (which also spawn locally but with HasAuthority == false).
[HarmonyPatch(typeof(PlayerCharacter), nameof(PlayerCharacter.Spawned))]
internal static class PlayerSpawnedPatch
{
    static void Postfix(PlayerCharacter __instance)
    {
        try
        {
            if (!__instance.HasAuthority) return;
            Plugin.LocalPlayer = __instance;
            Plugin.Logger.LogInfo("[HealthRegenMod] Local player character registered.");
        }
        catch (Exception ex) { Plugin.Logger.LogError($"[HealthRegenMod] PlayerSpawnedPatch: {ex}"); }
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
        catch (Exception ex) { Plugin.Logger.LogError($"[HealthRegenMod] PlayerDespawnedPatch: {ex}"); }
    }
}
