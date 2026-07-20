using System;
using HarmonyLib;
using SSSGame;

namespace CraftFromStorageMod.Patches;

// Local-player capture, GroundItemVacuumMod pattern (Plugin.cs lines 109-145 equivalent). Always
// patched (safe param types, no inventory-family exposure) - needed for the StorageCensus hotkey
// regardless of which Trace flags are enabled.
[HarmonyPatch(typeof(PlayerCharacter), nameof(PlayerCharacter.Spawned))]
internal static class PlayerSpawnedPatch
{
    static void Postfix(PlayerCharacter __instance)
    {
        try
        {
            if (!__instance.HasAuthority) return; // the local player, not remote peers
            Plugin.LocalPlayer = __instance;
            Plugin.Logger.LogInfo("[CFS] Local player registered.");
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"[CFS] PlayerSpawnedPatch: {ex}");
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
                // World-leave: never cache interop wrappers of per-world objects across sessions -
                // reading a stale wrapper causes an unrecoverable native crash (project-wide gotcha).
                // v0.2.0: CraftWatcher now holds per-world ItemCollection wrappers while armed -
                // ClearWorldState() nulls all of them and disarms.
                CraftFromStorageMod.CraftWatcher.ClearWorldState();
                Plugin.Logger.LogInfo("[CFS] Local player cleared (world-leave).");
            }
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"[CFS] PlayerDespawnedPatch: {ex}");
        }
    }
}
