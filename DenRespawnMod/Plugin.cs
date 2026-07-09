using System;
using System.Collections.Generic;
using System.Globalization;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Unity.IL2CPP;
using BepInEx.Logging;
using HarmonyLib;
using Il2CppInterop.Runtime.Injection;
using SandSailorStudio.Streaming;
using SSSGame;
using UnityEngine;
using DenRespawnMod.Patches;

namespace DenRespawnMod;

[BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
public class Plugin : BasePlugin
{
    internal static ManualLogSource Logger = null!;
    internal static ConfigEntry<string> ReviveHotkey = null!;
    internal static ConfigEntry<float> ReviveRadiusMeters = null!;
    internal static ConfigEntry<bool> AllowRespawnNearStructures = null!;
    internal static ConfigEntry<bool> ClearIgnoreRespawning = null!;
    internal static ConfigEntry<bool> ForceRespawnPopulations = null!;
    internal static ConfigEntry<bool> DenDiagnostics = null!;
    internal static ConfigEntry<float> DiagnosticsIntervalSeconds = null!;
    internal static ConfigEntry<bool> SuppressNaturalRespawns = null!;
    internal static ConfigEntry<string> AutoRespawnRules = null!;
    internal static ConfigEntry<string> MapReviveModifier = null!;
    internal static ConfigEntry<float> MapPinMatchRadius = null!;

    internal static readonly List<Den> TrackedDens = new();
    internal static PlayerCharacter? LocalPlayer;

    // Re-entrancy flag for the Den.Revive prefix gate (Patches/DenPatches.cs): true only while
    // OUR OWN code is inside a den.Revive() call, so the prefix can tell "our call" from a foreign
    // (game-driven natural respawn) call without any other signal.
    internal static bool AllowReviveCall;

    // Captured from WorldStreamingManager.Awake (Patches/DenPatches.cs), mirroring SeedScoutMod's
    // StreamingCapturePatch — feeds DenTracker's remote force-load path. Never keep across world
    // sessions (project-wide gotcha); cleared in NoteWorldLeft.
    internal static WorldStreamingManager? StreamingManager;

    // Captured from BiomesManager.Awake/OnDisable (Patches/BiomesCapturePatches.cs), mirroring
    // SeedScoutMod's BiomesCapture.cs — feeds MarkerRefresher's area-instance lookup (v1.1.3 pin
    // recolor). Never keep across world sessions (project-wide gotcha); cleared in NoteWorldLeft.
    internal static BiomesManager? Biomes;

    // Parsed from AutoRespawnRules at Load() — den type name (case-insensitive) -> days-after-defeat.
    internal static Dictionary<string, int> AutoRules = new(StringComparer.OrdinalIgnoreCase);

    public override void Load()
    {
        Logger = base.Log;

        ReviveHotkey = Config.Bind(
            section: "General",
            key: "ReviveHotkey",
            defaultValue: "j",
            description: "The hotkey (lowercase letter or Unity KeyCode name) pressed to revive defeated dens (wulfar/bear/skeleton dens, etc).");

        ReviveRadiusMeters = Config.Bind(
            section: "General",
            key: "ReviveRadiusMeters",
            defaultValue: 150.0f,
            description: "Only dens within this many meters of the player are refreshed by the hotkey. 0 = refresh ALL tracked dens on the whole map.");

        AllowRespawnNearStructures = Config.Bind(
            section: "General",
            key: "AllowRespawnNearStructures",
            defaultValue: true,
            description: "If true, neutralizes the vanilla suppression that blocks den respawning when player structures are built nearby.");

        ClearIgnoreRespawning = Config.Bind(
            section: "General",
            key: "ClearIgnoreRespawning",
            defaultValue: true,
            description: "On refresh, clear the vanilla 'never respawn again' flag (ignoreRespawning) on the den's node spawners so they resume repopulating.");

        ForceRespawnPopulations = Config.Bind(
            section: "General",
            key: "ForceRespawnPopulations",
            defaultValue: true,
            description: "On refresh, immediately respawn empty node spawner populations (SetActiveSpawner + RespawnAllPopulations) instead of waiting for the natural respawn timer.");

        DenDiagnostics = Config.Bind(
            section: "Diagnostics",
            key: "DenDiagnostics",
            defaultValue: true,
            description: "If true, periodically logs the state of all tracked dens and their spawners for debugging.");

        DiagnosticsIntervalSeconds = Config.Bind(
            section: "Diagnostics",
            key: "DiagnosticsIntervalSeconds",
            defaultValue: 30.0f,
            description: "How often (in seconds) to dump den diagnostics when DenDiagnostics is enabled.");

        SuppressNaturalRespawns = Config.Bind(
            section: "NaturalRespawns",
            key: "SuppressNaturalRespawns",
            defaultValue: false,
            description: "If true, blocks the game's own natural den revival (normally ~1 in-game year) so dens only come back via this mod's hotkey/map/timed respawns.");

        AutoRespawnRules = Config.Bind(
            section: "AutoRespawn",
            key: "Rules",
            defaultValue: "",
            description: "Comma-separated <Den Name>:<days> rules, e.g. 'Wulfar Den:2, Skeleton Den:5'. N in-game days after a den of that type is defeated, it auto-revives. Known names: Wulfar Den, Skeleton Den, Skeleton Den Cluster, Wight Den, Draugar den, Baby Crawler Den (case-insensitive match).");

        MapReviveModifier = Config.Bind(
            section: "MapRevive",
            key: "MapReviveModifier",
            defaultValue: "LeftShift",
            description: "Hold this key while left-clicking a den's map pin to revive that den remotely. Empty string disables map revive.");

        MapPinMatchRadius = Config.Bind(
            section: "MapRevive",
            key: "MapPinMatchRadius",
            defaultValue: 75.0f,
            description: "Max distance (m) between a clicked map pin and a known den for them to be considered the same POI.");

        ParseAutoRules();

        // Register our custom Mono class into IL2CPP
        ClassInjector.RegisterTypeInIl2Cpp<DenTracker>();

        // Spawn our persistent manager GameObject
        var go = new GameObject("DenRespawnMod_Tracker");
        UnityEngine.Object.DontDestroyOnLoad(go);
        go.AddComponent<DenTracker>();

        // Apply Harmony patches
        var harmony = new Harmony(MyPluginInfo.PLUGIN_GUID);
        harmony.PatchAll();

        Logger.LogInfo($"DenRespawnMod v{MyPluginInfo.PLUGIN_VERSION} loaded successfully. Hotkey='{ReviveHotkey.Value}', AllowRespawnNearStructures={AllowRespawnNearStructures.Value}");
    }

    // Parses AutoRespawnRules ("<Den Name>:<days>, ...") into AutoRules. Malformed entries are
    // logged and skipped rather than aborting the whole list.
    internal static void ParseAutoRules()
    {
        var rules = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        string raw = AutoRespawnRules.Value;

        if (!string.IsNullOrWhiteSpace(raw))
        {
            foreach (var entry in raw.Split(','))
            {
                string e = entry.Trim();
                if (e.Length == 0) continue;

                var parts = e.Split(':');
                if (parts.Length != 2)
                {
                    Logger.LogWarning($"[DenRespawn] Malformed AutoRespawn rule '{e}' — expected '<Den Name>:<days>'. Skipped.");
                    continue;
                }

                string name = parts[0].Trim();
                if (name.Length == 0 || !int.TryParse(parts[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int days) || days < 0)
                {
                    Logger.LogWarning($"[DenRespawn] Malformed AutoRespawn rule '{e}' — days must be a non-negative integer. Skipped.");
                    continue;
                }

                rules[name] = days;
                Logger.LogInfo($"[DenRespawn] Accepted auto-respawn rule: '{name}' -> {days} day(s).");
            }
        }

        AutoRules = rules;
    }

    // Dedupe by native pointer (managed wrapper instances can differ while wrapping the same
    // native object) and record the den for later revival. Every game-object read is guarded
    // individually so one bad field never drops the whole capture.
    //
    // Den's base chain passes through a unity-libs CoreModule stub, so `.Pointer` isn't visible
    // through the compile-time declared type even though the runtime object IS an
    // Il2CppObjectBase (project-wide gotcha) — box to object and pattern-match instead.
    internal static void TrackDen(Den den)
    {
        lock (TrackedDens)
        {
            IntPtr ptr;
            if ((object)den is Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase denBase) ptr = denBase.Pointer;
            else return;

            foreach (var existing in TrackedDens)
            {
                try
                {
                    if ((object)existing is Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase existingBase &&
                        existingBase.Pointer == ptr)
                        return; // already tracked
                }
                catch
                {
                    // stale wrapper — skip it for the comparison, dedupe pass elsewhere prunes it
                }
            }

            TrackedDens.Add(den);

            string name = "?";
            string pos = "?";
            bool isActive = false;
            bool havePos = false;
            Vector3 posVec = default;
            try { name = den.GetName(); } catch { }
            try { posVec = den.transform.position; pos = posVec.ToString(); havePos = true; } catch { }
            try { isActive = den.isActive; } catch { }

            if (havePos)
            {
                try { DenRegistry.Upsert(posVec, name); }
                catch (Exception ex) { Logger.LogError($"[DenRespawn] DenRegistry.Upsert error: {ex}"); }
            }

            Logger.LogInfo($"[DenRespawn] Tracking den '{name}' at {pos} isActive={isActive} (total={TrackedDens.Count})");
        }
    }

    // Never keep den wrappers across world sessions — reads through a stale native wrapper can
    // crash the process natively (project-wide gotcha). Called when the tracked world session
    // id goes empty or changes.
    internal static void NoteWorldLeft()
    {
        lock (TrackedDens)
        {
            if (TrackedDens.Count > 0)
            {
                TrackedDens.Clear();
                Logger.LogInfo("[DenRespawn] World session ended — tracked dens cleared.");
            }
        }

        DenRegistry.Save();
        DenRegistry.Clear();
        StreamingManager = null;
        Biomes = null;
        DenMapRevive.MapMenuInstance = null;
        DenMapRevive.HaveHovered = false;
        DenMapRevive.HoveredWidgetPtr = IntPtr.Zero;
        DenTracker.ClearTransientState(); // pending remote-refresh queue + anchor GameObjects
        DayCounter.ClearTransientState();
    }
}
