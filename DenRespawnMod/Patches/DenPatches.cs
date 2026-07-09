using System;
using System.Collections.Generic;
using HarmonyLib;
using SandSailorStudio.Streaming;
using SSSGame;
using SSSGame.Combat;
using SSSGame.UI;
using UnityEngine;

namespace DenRespawnMod.Patches;

// Runtime-PROVEN to fire for dens (SeedScout mod used this hook already).
[HarmonyPatch(typeof(Den), nameof(Den.Start))]
internal static class DenStartPatch
{
    static void Postfix(Den __instance)
    {
        try
        {
            if (__instance == null) return;
            Plugin.TrackDen(__instance);
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"[DenRespawn] DenStartPatch: {ex}");
        }
    }
}

// Dedupe in Plugin.TrackDen makes a double-capture (Start + Spawned) harmless.
[HarmonyPatch(typeof(Den), nameof(Den.Spawned))]
internal static class DenSpawnedPatch
{
    static void Postfix(Den __instance)
    {
        try
        {
            if (__instance == null) return;
            Plugin.TrackDen(__instance);
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"[DenRespawn] DenSpawnedPatch: {ex}");
        }
    }
}

// Method returns Boolean with no params — safe signature (the project's by-ref gotcha is about
// the GAME method's own Single&/Int32&/Boolean& params, not Harmony's own "ref __result").
[HarmonyPatch(typeof(Den), nameof(Den.IsBlockedByStructures))]
internal static class DenIsBlockedByStructuresPatch
{
    private static bool _fireVerified;
    private static bool _suppressedLogged;

    static void Postfix(Den __instance, ref bool __result)
    {
        try
        {
            if (!_fireVerified)
            {
                _fireVerified = true;
                Plugin.Logger.LogInfo("[DenRespawn] FIRE-VERIFY: IsBlockedByStructures patch is live");
            }

            if (Plugin.AllowRespawnNearStructures.Value && __result)
            {
                __result = false;
                if (!_suppressedLogged)
                {
                    _suppressedLogged = true;
                    Plugin.Logger.LogInfo("[DenRespawn] Suppressed a den structure-block (IsBlockedByStructures -> false)");
                }
            }
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"[DenRespawn] DenIsBlockedByStructuresPatch: {ex}");
        }
    }
}

[HarmonyPatch(typeof(PopulationSpawner), "_UpdateBlockedByStructures")]
internal static class PopulationSpawnerBlockedByStructuresPatch
{
    private static bool _fireVerified;
    private static bool _clearedLogged;

    static void Postfix(PopulationSpawner __instance)
    {
        try
        {
            if (!_fireVerified)
            {
                _fireVerified = true;
                Plugin.Logger.LogInfo("[DenRespawn] FIRE-VERIFY: PopulationSpawner._UpdateBlockedByStructures patch is live");
            }

            if (__instance == null) return;

            if (Plugin.AllowRespawnNearStructures.Value && __instance.RespawningBlockedByStructures)
            {
                __instance.RespawningBlockedByStructures = false;
                if (!_clearedLogged)
                {
                    _clearedLogged = true;
                    Plugin.Logger.LogInfo("[DenRespawn] Cleared RespawningBlockedByStructures on a spawner");
                }
            }
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"[DenRespawn] PopulationSpawnerBlockedByStructuresPatch: {ex}");
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
            if (!__instance.HasAuthority) return;
            Plugin.LocalPlayer = __instance;
            Plugin.Logger.LogInfo("[DenRespawn] Local player character registered.");
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"[DenRespawn] PlayerSpawnedPatch: {ex}");
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
                Plugin.Logger.LogInfo("[DenRespawn] Local player character cleared.");
            }
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"[DenRespawn] PlayerDespawnedPatch: {ex}");
        }
    }
}

// Revive gate (v1.1.0). Den.Revive() is Void with no params — a safe signature (the project's
// by-ref gotcha is about the GAME method's own Single&/Int32&/Boolean& params, not this one).
// Distinguishes OUR calls (Plugin.AllowReviveCall, set only while DenTracker.RunRefresh is inside
// den.Revive()) from FOREIGN calls — the game's own ~yearly natural respawn driver, or any other
// unknown caller. The foreign-call log line is unconditional (our detector for the natural path);
// suppression only happens when SuppressNaturalRespawns is on. Whole body try/catch — on any
// exception, return true so we never accidentally block the game's own revival.
[HarmonyPatch(typeof(Den), nameof(Den.Revive))]
internal static class DenRevivePatch
{
    static bool Prefix(Den __instance)
    {
        try
        {
            if (Plugin.AllowReviveCall) return true;

            string name = "?";
            try { name = __instance != null ? __instance.GetName() : "?"; } catch { }

            Plugin.Logger.LogInfo($"[DenRespawn] Foreign Den.Revive() on '{name}' (day {DayCounter.CurrentDay}) — natural respawn driver?");

            if (Plugin.SuppressNaturalRespawns.Value)
            {
                Plugin.Logger.LogInfo($"[DenRespawn] BLOCKED natural revive of '{name}'");
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"[DenRespawn] DenRevivePatch error: {ex}");
            return true;
        }
    }
}

// Capture the WorldStreamingManager from its own lifecycle — never FindObjectsByType (IL2CPP
// trampoline crash). Mirrors SeedScoutMod's StreamingCapturePatch exactly. Feeds
// DenTracker.EnqueueRemoteRefresh's force-load path; refreshed on every world load.
[HarmonyPatch(typeof(WorldStreamingManager), "Awake")]
internal static class DenStreamingCapturePatch
{
    static void Postfix(WorldStreamingManager __instance)
    {
        try
        {
            Plugin.StreamingManager = __instance;
            Plugin.Logger.LogInfo("[DenRespawn] WorldStreamingManager captured.");
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"[DenRespawn] DenStreamingCapturePatch: {ex}");
        }
    }
}

// Map revive (v1.1.0): Shift+Left-click (configurable modifier) a den's map pin to revive that den
// remotely. Postfix — vanilla click behavior is left completely untouched.
[HarmonyPatch(typeof(MapMenu), "_OnMarkersLeftClick")]
internal static class DenMapClickPatch
{
    private static bool _fireVerified;
    private static bool _modifierParsed;
    private static KeyCode _modifierKey;
    private static bool _modifierEnabled;

    private static readonly HashSet<string> KnownDenTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Wulfar Den", "Skeleton Den", "Skeleton Den Cluster", "Wight Den", "Draugar den", "Baby Crawler Den", "Large Cemetery"
    };

    static void Postfix(WorldObjectiveMarker marker)
    {
        try
        {
            if (!_fireVerified)
            {
                _fireVerified = true;
                Plugin.Logger.LogInfo("[DenRespawn] FIRE-VERIFY: MapMenu._OnMarkersLeftClick patch is live");
            }

            if (!_modifierParsed)
            {
                _modifierParsed = true;
                string modStr = Plugin.MapReviveModifier.Value;
                if (string.IsNullOrWhiteSpace(modStr))
                {
                    _modifierEnabled = false;
                }
                else if (Enum.TryParse<KeyCode>(modStr, true, out var kc))
                {
                    _modifierKey = kc;
                    _modifierEnabled = true;
                }
                else
                {
                    _modifierEnabled = false;
                    Plugin.Logger.LogWarning($"[DenRespawn] MapReviveModifier '{modStr}' is not a valid KeyCode — map revive disabled.");
                }
            }

            if (!_modifierEnabled) return;
            if (!Input.GetKey(_modifierKey)) return;
            if (marker == null) return;

            bool havePos = false;
            Vector3 pos = default;
            try { pos = marker.transform.position; havePos = true; } catch { }
            if (!havePos) return;

            string name = "";
            try { name = marker.CustomName ?? ""; } catch { }

            var rec = DenRegistry.FindNearest(pos, Plugin.MapPinMatchRadius.Value);

            if (rec == null)
            {
                bool looksLikeDen = name.IndexOf("Den", StringComparison.OrdinalIgnoreCase) >= 0
                    || KnownDenTypes.Contains(name);
                if (looksLikeDen)
                {
                    // Provisional record — state unknown until scanned, so leave Defeated at its
                    // default (false) so a never-visited pinned den is still eligible for revive.
                    rec = DenRegistry.Upsert(pos, name);
                }
            }

            if (rec == null)
            {
                DenTracker.Instance?.Notify("No known den at this map pin");
                Plugin.Logger.LogInfo($"[DenRespawn] Map click: no known den near pin '{name}'.");
                return;
            }

            float dist = -1f;
            if (Plugin.LocalPlayer != null)
            {
                try { dist = Vector3.Distance(Plugin.LocalPlayer.transform.position, pos); } catch { }
            }

            DenTracker.Instance?.EnqueueRemoteRefresh(rec, "map");

            string msg = dist >= 0
                ? $"Reviving {rec.TypeName} ({dist:F0} m away)..."
                : $"Reviving {rec.TypeName}...";
            DenTracker.Instance?.Notify(msg);
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"[DenRespawn] DenMapClickPatch error: {ex}");
        }
    }
}
