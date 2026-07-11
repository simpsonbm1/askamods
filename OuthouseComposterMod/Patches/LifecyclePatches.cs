using System;
using HarmonyLib;
using SSSGame;

namespace OuthouseComposterMod.Patches;

// Read-only local-player capture — copied from GroundItemVacuumMod's proven pattern
// (GroundItemVacuumMod\Patches\LifecyclePatches.cs). Neither patch mutates any game state; they
// only record/clear a reference so the acceptance-gate probe (ComposterDiag.DumpAcceptanceProbe)
// can read the local player's inventory.
[HarmonyPatch(typeof(PlayerCharacter), nameof(PlayerCharacter.Spawned))]
internal static class PlayerSpawnedPatch
{
    static void Postfix(PlayerCharacter __instance)
    {
        try
        {
            if (__instance == null) return;
            if (!__instance.HasAuthority) return; // the local player, not remote peers
            Plugin.LocalPlayer = __instance;
            Plugin.Logger.LogInfo("[OuthouseComposter] Local player registered.");
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"[OuthouseComposter] PlayerSpawnedPatch: {ex}");
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
                Plugin.Logger.LogInfo("[OuthouseComposter] Local player cleared.");
            }
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"[OuthouseComposter] PlayerDespawnedPatch: {ex}");
        }
    }
}
