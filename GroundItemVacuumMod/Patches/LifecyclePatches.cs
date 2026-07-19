using System;
using HarmonyLib;
using SSSGame;

namespace GroundItemVacuumMod.Patches;

// Track live ground items in our own managed set via the object's OWN Unity lifecycle messages.
// OnEnable/OnDisable are runtime-dispatched Unity messages: guaranteed to fire and immune to the
// IL2CPP AOT-inlining trap that can silently kill small-method patches. Membership brackets exactly
// the window in which the object is a live, safe-to-touch ground item. We NEVER traverse the game's
// intrusive linked list - doing so (v1.0.0) called native getters on a mid-teardown node and hard-
// crashed the game (native AV, coreclr+0x1d1fdd fatal-error signature, no managed exception).
[HarmonyPatch(typeof(DynamicItemObject), nameof(DynamicItemObject.OnEnable))]
internal static class ItemEnablePatch
{
    static void Postfix(DynamicItemObject __instance)
    {
        try
        {
            if (__instance == null) return;
            lock (Plugin.TrackedItems)
            {
                if (Plugin.TrackedItems.Add(__instance) && !Plugin.TrackingConfirmed)
                {
                    Plugin.TrackingConfirmed = true;
                    Plugin.Logger.LogInfo("[Vacuum] Ground-item tracking active (DynamicItemObject.OnEnable firing).");
                }
            }
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"[Vacuum] ItemEnablePatch: {ex}");
        }
    }
}

[HarmonyPatch(typeof(DynamicItemObject), nameof(DynamicItemObject.OnDisable))]
internal static class ItemDisablePatch
{
    // Prefix: remove BEFORE the game tears the object down, while its wrapper is still valid.
    static void Prefix(DynamicItemObject __instance)
    {
        try
        {
            if (__instance == null) return;
            lock (Plugin.TrackedItems)
            {
                Plugin.TrackedItems.Remove(__instance);
            }
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"[Vacuum] ItemDisablePatch: {ex}");
        }
    }
}

// Enemy/animal corpses are dead SSSGame.Monster instances awaiting despawn (confirmed in-game
// 2026-07-18 - CharacterRemains never fires for enemies; see Plugin.TrackedCorpses comment).
// We patch ONLY Monster's own lifecycle overrides - never Creature/Den/Pet - so dens (which must
// NEVER be despawned; a defeated den legitimately reads IsDead and despawning it would
// permanently destroy the spawner) and pets are never tracked at all.
[HarmonyPatch(typeof(Monster), nameof(Monster.Spawned))]
internal static class MonsterSpawnedPatch
{
    static void Postfix(Monster __instance)
    {
        try
        {
            if (__instance == null) return;
            lock (Plugin.TrackedCorpses)
            {
                if (Plugin.TrackedCorpses.Add(__instance) && !Plugin.CorpseTrackingConfirmed)
                {
                    Plugin.CorpseTrackingConfirmed = true;
                    Plugin.Logger.LogInfo("[Vacuum] Corpse tracking active (Monster.Spawned firing).");
                }
            }
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"[Vacuum] MonsterSpawnedPatch: {ex}");
        }
    }
}

// Prefix: remove BEFORE the game tears the object down, while its wrapper is still valid (same
// pattern as ItemDisablePatch above).
[HarmonyPatch(typeof(Monster), nameof(Monster.Despawned))]
internal static class MonsterDespawnedPatch
{
    static void Prefix(Monster __instance)
    {
        try
        {
            if (__instance == null) return;
            lock (Plugin.TrackedCorpses)
            {
                Plugin.TrackedCorpses.Remove(__instance);
            }
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"[Vacuum] MonsterDespawnedPatch: {ex}");
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
            Plugin.Logger.LogInfo("[Vacuum] Local player registered.");
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"[Vacuum] PlayerSpawnedPatch: {ex}");
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
                // World-leave: drop every tracked wrapper so nothing stale survives into the next
                // world/session (stale per-world wrapper reads AV in native code - the gotcha).
                lock (Plugin.TrackedItems)
                {
                    Plugin.TrackedItems.Clear();
                }
                Plugin.TrackingConfirmed = false;
                lock (Plugin.TrackedCorpses)
                {
                    Plugin.TrackedCorpses.Clear();
                }
                Plugin.CorpseTrackingConfirmed = false;
                Plugin.Logger.LogInfo("[Vacuum] Local player cleared; tracked ground items and corpses dropped.");
            }
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"[Vacuum] PlayerDespawnedPatch: {ex}");
        }
    }
}
