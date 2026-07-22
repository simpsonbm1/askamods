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
    internal static ConfigEntry<bool> AllowRespawnNearStructures = null!;
    internal static ConfigEntry<bool> ClearIgnoreRespawning = null!;
    internal static ConfigEntry<bool> ForceRespawnPopulations = null!;
    internal static ConfigEntry<bool> DenDiagnostics = null!;
    internal static ConfigEntry<float> DiagnosticsIntervalSeconds = null!;
    internal static ConfigEntry<bool> SuppressNaturalRespawns = null!;
    internal static ConfigEntry<string> AutoRespawnRules = null!;
    internal static ConfigEntry<string> MapReviveModifier = null!;
    internal static ConfigEntry<float> MapPinMatchRadius = null!;
    internal static ConfigEntry<bool> SpawnerRespawnEnable = null!;
    internal static ConfigEntry<string> SpawnerNames = null!;
    internal static ConfigEntry<float> SpawnerMatchRadius = null!;
    // Raw config text for the spawner-rule list. Named distinctly from the parsed
    // Plugin.SpawnerRules dictionary below (same split as AutoRespawnRules/AutoRules).
    internal static ConfigEntry<string> SpawnerRulesConfig = null!;

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

    // Parsed from SpawnerNames at Load() — trimmed, lowercased, non-empty gameObject.name prefixes
    // for standalone PopulationSpawners (bear dens / spires) the SpawnerRespawn path may act on.
    internal static List<string> SpawnerNameList = new();

    // Parsed from SpawnerRulesConfig at Load() — spawner name (case-insensitive) -> days-empty
    // before the SpawnerRespawn timer force-respawns any loaded matching spawner.
    internal static Dictionary<string, int> SpawnerRules = new(StringComparer.OrdinalIgnoreCase);

    // Locale-safe auto-rule lookup: exact match on the translated name (unchanged English
    // behavior) OR a normalized-substring match on the invariant dataSheet asset name, so a
    // config key like "Skeleton Den" / "Bear Den" resolves in every language (the asset name
    // "SkeletonDenDataSheet" contains it once spacing/case are normalized).
    internal static bool TryGetAutoRuleDays(DenRecord rec, out int days)
    {
        days = -1;
        if (rec == null) return false;
        if (!string.IsNullOrEmpty(rec.TypeName) && AutoRules.TryGetValue(rec.TypeName, out days)) return true;
        string an = NormalizeDenKey(rec.AssetName);
        if (an.Length == 0) return false;
        foreach (var kv in AutoRules)
        {
            string kn = NormalizeDenKey(kv.Key);
            if (kn.Length > 0 && an.Contains(kn)) { days = kv.Value; return true; }
        }
        return false;
    }

    private static string NormalizeDenKey(string s)
    {
        if (string.IsNullOrEmpty(s) || s == "?") return "";
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (char c in s) if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
        return sb.ToString();
    }

    public override void Load()
    {
        Logger = base.Log;

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
            defaultValue: false,
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

        SpawnerRespawnEnable = Config.Bind(
            section: "SpawnerRespawn",
            key: "SpawnerRespawnEnable",
            defaultValue: true,
            description: "Master on/off switch for force-respawning bear dens / spires (standalone PopulationSpawners, not SSSGame.Den).");

        SpawnerNames = Config.Bind(
            section: "SpawnerRespawn",
            key: "SpawnerNames",
            defaultValue: "HabitatBear, WightPopulationBig, WightPopulation, FollowerDen, Follower Population",
            description: "Comma-separated spawner gameObject.name prefixes for bear dens / spires to force-respawn.");

        SpawnerMatchRadius = Config.Bind(
            section: "SpawnerRespawn",
            key: "SpawnerMatchRadius",
            defaultValue: 40.0f,
            description: "Max distance (m) from a clicked pin / from a spawner for it to count as 'at this POI'.");

        SpawnerRulesConfig = Config.Bind(
            section: "SpawnerRespawn",
            key: "SpawnerRules",
            defaultValue: "",
            description: "Comma-separated <SpawnerName>:<days> rules, e.g. 'HabitatBear:3, WightPopulationBig:5'. Every N in-game days, any loaded matching spawner that is currently empty is force-respawned. Loaded POIs only.");

        ParseAutoRules();
        ParseSpawnerConfig();

        // Register our custom Mono class into IL2CPP
        ClassInjector.RegisterTypeInIl2Cpp<DenTracker>();

        // Spawn our persistent manager GameObject
        var go = new GameObject("DenRespawnMod_Tracker");
        UnityEngine.Object.DontDestroyOnLoad(go);
        go.AddComponent<DenTracker>();

        // Apply Harmony patches
        var harmony = new Harmony(MyPluginInfo.PLUGIN_GUID);
        harmony.PatchAll();

        Logger.LogInfo($"DenRespawnMod v{MyPluginInfo.PLUGIN_VERSION} loaded successfully. MapReviveModifier='{MapReviveModifier.Value}', AllowRespawnNearStructures={AllowRespawnNearStructures.Value}, SpawnerRespawnEnable={SpawnerRespawnEnable.Value}, SpawnerNames={SpawnerNameList.Count}, SpawnerRules={SpawnerRules.Count}");
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

    // Parses SpawnerNames ("<prefix>, <prefix>, ...") into SpawnerNameList (trimmed, lowercased,
    // empties dropped) and SpawnerRulesConfig ("<SpawnerName>:<days>, ...") into SpawnerRules
    // (reusing ParseAutoRules' malformed-entry-skip pattern) at Load().
    internal static void ParseSpawnerConfig()
    {
        var names = new List<string>();
        foreach (var entry in SpawnerNames.Value.Split(','))
        {
            string e = entry.Trim().ToLowerInvariant();
            if (e.Length == 0) continue;
            names.Add(e);
        }
        SpawnerNameList = names;

        var rules = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        string raw = SpawnerRulesConfig.Value;

        if (!string.IsNullOrWhiteSpace(raw))
        {
            foreach (var entry in raw.Split(','))
            {
                string e = entry.Trim();
                if (e.Length == 0) continue;

                var parts = e.Split(':');
                if (parts.Length != 2)
                {
                    Logger.LogWarning($"[DenRespawn] Malformed SpawnerRules rule '{e}' — expected '<SpawnerName>:<days>'. Skipped.");
                    continue;
                }

                string name = parts[0].Trim();
                if (name.Length == 0 || !int.TryParse(parts[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int days) || days < 0)
                {
                    Logger.LogWarning($"[DenRespawn] Malformed SpawnerRules rule '{e}' — days must be a non-negative integer. Skipped.");
                    continue;
                }

                rules[name] = days;
                Logger.LogInfo($"[DenRespawn] Accepted spawner-respawn rule: '{name}' -> {days} day(s).");
            }
        }

        SpawnerRules = rules;
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

            string assetName = "?";
            try { var ds = den.dataSheet; if (ds != null) assetName = ds.name ?? "?"; } catch { }

            if (havePos)
            {
                try { DenRegistry.Upsert(posVec, name, assetName); }
                catch (Exception ex) { Logger.LogError($"[DenRespawn] DenRegistry.Upsert error: {ex}"); }
            }

            Logger.LogInfo($"[DenRespawn] Tracking den '{name}' at {pos} isActive={isActive} asset='{assetName}' (total={TrackedDens.Count})");
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
        SpawnerRespawn.ClearTransientState();
        DayCounter.ClearTransientState();
    }
}
