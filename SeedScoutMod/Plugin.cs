using System.Collections.Generic;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Unity.IL2CPP;
using BepInEx.Logging;
using HarmonyLib;
using Il2CppInterop.Runtime.Injection;
using SandSailorStudio.Streaming;
using SSSGame;
using UnityEngine;

namespace SeedScoutMod;

// A cave (mine) placement captured at registration time (value copy — the instances
// themselves may be released/streamed out).
internal struct CaveHit
{
    public Vector2Int Pos;
    public Vector4 Bounds;
    public CaveHit(Vector2Int pos, Vector4 bounds) { Pos = pos; Bounds = bounds; }
}

// A lake captured as it spawns (per-tile/streaming). Pos is world (x, z).
internal struct LakeHit
{
    public Vector2 Pos;
    public float Water;
    public LakeHit(Vector2 pos, float water) { Pos = pos; Water = water; }
}

// A hostile spawner (den / enemy camp) captured as it spawns. Pos is world (x, z). Kind is the broad
// category ("Den"/"EnemyCamp"); Name is the specific type (e.g. localized den name); Threat is the
// CreatureDataSheet.baseThreatScore difficulty number (0 if unknown).
internal struct HostileHit
{
    public Vector2 Pos;
    public string Kind;
    public string Name;
    public float Threat;
    public HostileHit(Vector2 pos, string kind, string name, float threat)
    {
        Pos = pos; Kind = kind; Name = name; Threat = threat;
    }
}

// PROBE v0.4 — introspection only.
// The world streams; GetWorldAreaInstances() is empty post-load. Two angles:
//  (a) recursively walk the live area tree (WorldDataMap._worldInstances -> children),
//  (b) accumulate everything CavesManager.RegisterCaves() is handed.
[BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
public class Plugin : BasePlugin
{
    internal static ManualLogSource Logger = null!;

    internal static BiomesManager? Biomes;
    internal static WorldStreamingManager? Streaming;
    internal static SandSailorStudio.RNG.RandomGeneratorManager? Rng;
    internal static ConfigEntry<bool> EnableMarkers = null!;
    internal static ConfigEntry<bool> ForceLoadTiles = null!;
    internal static ConfigEntry<int> ForceLoadRadius = null!;
    internal static ConfigEntry<bool> RevealNativePins = null!;
    internal static ConfigEntry<bool> EnableLogging = null!;
    internal static readonly List<CaveHit> RegisteredCaves = new();
    internal static readonly List<LakeHit> Lakes = new();
    internal static readonly List<HostileHit> Hostiles = new();

    public override void Load()
    {
        Logger = base.Log;

        EnableMarkers = Config.Bind("SeedScout", "EnableMapMarkers", true,
            "Draw cave (mine) dots on the in-game map. Pure UI overlay — no game objects touched.");

        ForceLoadTiles = Config.Bind("SeedScout", "ForceLoadCaveTiles", true,
            "After world load, force the streamer to load the tile at each cave (RequestLoadWorldTile) " +
            "so lakes/hostiles near caves fill in without physically exploring. Experimental.");

        ForceLoadRadius = Config.Bind("SeedScout", "ForceLoadRadius", 2,
            "How many tile rings around each cave to force-load (0 = the cave's own tile only, " +
            "1 = 3x3, 2 = 5x5, 3 = 7x7). Higher = more of the map fills in but more streaming/memory. " +
            "Tile = ~128m. Clamped to 0-3. Editable live in this file (no rebuild needed).");

        RevealNativePins = Config.Bind("SeedScout", "RevealNativeCavePins", true,
            "Experimental: after force-load, mark each cave's area explored and refresh its marker handler " +
            "to reveal the game's OWN native map pin (correct icon + name) without walking there.");

        EnableLogging = Config.Bind("SeedScout", "EnableLogging", true,
            "Master switch for SeedScout's informational log output (the 5s heartbeat, per-spawn lake/den/" +
            "camp captures, area-tree dumps, etc.). Set false to silence the spam so other mods' log lines " +
            "are easy to find. Genuine warnings/errors and the one-time load line still show regardless.");

        ClassInjector.RegisterTypeInIl2Cpp<ScoutTracker>();
        var go = new GameObject("SeedScoutMod_Tracker");
        Object.DontDestroyOnLoad(go);
        go.AddComponent<ScoutTracker>();

        new Harmony(MyPluginInfo.PLUGIN_GUID).PatchAll();

        Logger.LogInfo($"SeedScoutMod {MyPluginInfo.PLUGIN_VERSION} loaded (probe). " +
                       $"EnableLogging={EnableLogging.Value}. " +
                       "Load a world; the dump walks the area tree and reports registered caves.");
    }

    // Gated informational logging. Routed through here so EnableLogging=false silences the chatty
    // heartbeat/per-spawn/dump output. Warnings/errors call Logger.LogWarning/LogError directly so
    // they always surface regardless of this switch.
    internal static void LogInfo(string msg)
    {
        if (EnableLogging.Value) Logger.LogInfo(msg);
    }
}
