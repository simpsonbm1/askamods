using System.Collections.Generic;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Unity.IL2CPP;
using BepInEx.Logging;
using HarmonyLib;
using Il2CppInterop.Runtime.Injection;
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

// A hostile spawner (den / enemy camp) captured as it spawns. Pos is world (x, z).
internal struct HostileHit
{
    public Vector2 Pos;
    public string Kind;
    public HostileHit(Vector2 pos, string kind) { Pos = pos; Kind = kind; }
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
    internal static SandSailorStudio.RNG.RandomGeneratorManager? Rng;
    internal static ConfigEntry<bool> EnableMarkers = null!;
    internal static readonly List<CaveHit> RegisteredCaves = new();
    internal static readonly List<LakeHit> Lakes = new();
    internal static readonly List<HostileHit> Hostiles = new();

    public override void Load()
    {
        Logger = base.Log;

        EnableMarkers = Config.Bind("SeedScout", "EnableMapMarkers", true,
            "Draw cave (mine) dots on the in-game map. Pure UI overlay — no game objects touched.");

        ClassInjector.RegisterTypeInIl2Cpp<ScoutTracker>();
        var go = new GameObject("SeedScoutMod_Tracker");
        Object.DontDestroyOnLoad(go);
        go.AddComponent<ScoutTracker>();

        new Harmony(MyPluginInfo.PLUGIN_GUID).PatchAll();

        Logger.LogInfo($"SeedScoutMod {MyPluginInfo.PLUGIN_VERSION} loaded (probe). " +
                       "Load a world; the dump walks the area tree and reports registered caves.");
    }
}
