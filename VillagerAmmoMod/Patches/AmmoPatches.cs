using System;
using HarmonyLib;
using SSSGame;
using SSSGame.Combat;

namespace VillagerAmmoMod.Patches;

// SSSGame.Combat.RangedManager is a Fusion NetworkBehaviour - NEVER patch its
// CopyBackingFieldsToState/CopyStateToBackingFields (state-sync hang gotcha).
//
// v0.1.2: v0.1.0 AND v0.1.1 both hard-crashed the game during plugin loading (native AV in
// il2cpp class-init of the inventory type family). Root cause: ANY Harmony patch on
// RangedManager._OnAmmoRemoved is fatal - Harmony must resolve the target method's parameter
// types (ItemCollection, Item, ItemEventContext) to build the detour, which forces those il2cpp
// classes to initialize far too early, regardless of what the postfix itself binds. This mod
// therefore has ZERO patches on ammo-event methods. Detection moved to polling
// (see AmmoTracker.cs): the only patches below target Awake() (zero parameters, no risky type
// resolution) and the proven-safe PlayerCharacter.Spawned/Despawned pair.
[HarmonyPatch(typeof(RangedManager), "Awake")]
internal static class RangedManagerAwakePatch
{
    private static bool _fired;

    static void Postfix(RangedManager __instance)
    {
        try
        {
            lock (Plugin.RegistryLock)
            {
                Plugin.Registry.Add(__instance);
            }

            if (!_fired)
            {
                _fired = true;
                Plugin.Logger.LogInfo("[VillagerAmmo] RangedManager.Awake capture active.");
            }
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"[VillagerAmmo] RangedManagerAwakePatch: {ex}");
        }
    }
}

// v0.2.0: ProjectileTargetHelper is a plain MonoBehaviour (NOT a NetworkBehaviour), and Awake() is
// parameterless - safe to patch under the same "no inventory-family types in the target method's
// signature" rule that keeps the RangedManager patch above safe. Captures non-player targets into a
// registry so AmmoTracker can periodically cull stuck arrows via the game's own
// ReleaseAllStuckObjects().
[HarmonyPatch(typeof(ProjectileTargetHelper), "Awake")]
internal static class ProjectileTargetHelperAwakePatch
{
    private static bool _fired;

    static void Postfix(ProjectileTargetHelper __instance)
    {
        try
        {
            lock (Plugin.TargetRegistryLock)
            {
                Plugin.TargetRegistry.Add(__instance);
            }

            if (!_fired)
            {
                _fired = true;
                Plugin.Logger.LogInfo("[VillagerAmmo] ProjectileTargetHelper capture active.");
            }
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"[VillagerAmmo] ProjectileTargetHelperAwakePatch: {ex}");
        }
    }
}

[HarmonyPatch(typeof(PlayerCharacter), nameof(PlayerCharacter.Spawned))]
internal static class PlayerSpawnedPatch
{
    static void Postfix(PlayerCharacter __instance)
    {
        try
        {
            if (!__instance.HasAuthority) return; // the local player, not remote peers
            Plugin.LocalPlayer = __instance;
            Plugin.Logger.LogInfo("[VillagerAmmo] Local player registered.");
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"[VillagerAmmo] PlayerSpawnedPatch: {ex}");
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
                // World-leave: drop ALL per-world state so no stale RangedManager/ItemContainer/
                // ItemInfo wrapper survives into the next world/session (stale-wrapper native-AV
                // gotcha).
                lock (Plugin.RegistryLock)
                {
                    Plugin.Registry.Clear();
                }
                lock (Plugin.TargetRegistryLock)
                {
                    Plugin.TargetRegistry.Clear();
                }
                Plugin.Baselines.Clear();
                Plugin.LastShootingSeen.Clear();
                Plugin.InfoCache.Clear();
                Plugin.Logger.LogInfo("[VillagerAmmo] Local player cleared; registry, target registry, baselines, last-shooting-seen, and info cache dropped.");
            }
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"[VillagerAmmo] PlayerDespawnedPatch: {ex}");
        }
    }
}
