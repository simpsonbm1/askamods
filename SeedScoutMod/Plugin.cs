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
// themselves may be released/streamed out). Kept as a streamed cross-check diagnostic.
internal struct CaveHit
{
    public Vector2Int Pos;
    public Vector4 Bounds;
    public CaveHit(Vector2Int pos, Vector4 bounds) { Pos = pos; Bounds = bounds; }
}

// SeedScout — reveal the game's OWN native map pins for every POI at world load, without moving
// the player (see Scout.cs for the mechanism). The old seed scorer / base-placement recommender /
// colored map-dot overlay were removed for the MVP (git history ≤ v0.18.0 has them).
[BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
public class Plugin : BasePlugin
{
    internal static ManualLogSource Logger = null!;

    internal static BiomesManager? Biomes;
    internal static CavesManager? Caves;
    internal static SandSailorStudio.Streaming.WorldStreamingManager? Streaming;
    internal static ConfigEntry<bool> RevealNativePins = null!;
    internal static ConfigEntry<bool> HomeIslandOnly = null!;
    internal static ConfigEntry<bool> SweepStreamTiles = null!;
    internal static BepInEx.Configuration.ConfigFile? Cfg;
    internal static ConfigEntry<int> SweepMaxTiles = null!;
    internal static ConfigEntry<bool> RevealCaves = null!;
    internal static ConfigEntry<bool> RevealIslands = null!;
    internal static ConfigEntry<bool> RevealLakes = null!;
    internal static ConfigEntry<bool> RevealFishingGrounds = null!;
    internal static ConfigEntry<bool> RevealEnemyCamps = null!;
    internal static ConfigEntry<bool> RevealCreatureDens = null!;
    internal static ConfigEntry<bool> RevealLandmarks = null!;
    internal static ConfigEntry<bool> RevealUnknown = null!;
    internal static ConfigEntry<bool> EnableLogging = null!;
    internal static readonly List<CaveHit> RegisteredCaves = new();

    public override void Load()
    {
        Logger = base.Log;
        Cfg = Config; // Scout re-reads the file periodically so HomeIslandOnly can be flipped live

        RevealNativePins = Config.Bind("SeedScout", "RevealNativeCavePins", true,
            "Master switch: at world load, reveal the game's OWN native map pins (correct icon + name) " +
            "for every POI on the map — caves, dens, spires, cemeteries, lakes, islands — as if you had " +
            "discovered them, without moving the player. Pins persist in the save like real discoveries.");

        HomeIslandOnly = Config.Bind("SeedScout", "HomeIslandOnly", true,
            "Reveal only the POIs on (and just around) the initial island the player spawns on. " +
            "Flip to false to reveal the greater world — this is picked up LIVE (the config file is " +
            "re-read every few seconds in-game): save the file and the wider reveal starts within " +
            "seconds, no relaunch. Already-revealed pins always persist in the save either way.");

        SweepStreamTiles = Config.Bind("SeedScout", "SweepStreamTiles", true,
            "Stream in the world tile under each POI that needs it (the player never moves). Many pins " +
            "only materialize once their tile is resident — without this, only residency-independent " +
            "pins appear (caves, islands, some fishing grounds) and the map comes out sparse.");

        SweepMaxTiles = Config.Bind("SeedScout", "SweepMaxTiles", 400,
            "Safety cap on how many unique tiles the sweep may stream in per world (tiles stay resident " +
            "for the session; each is ~128m and carries its spawned entities). If the cap is hit, the " +
            "farthest POIs may stay unpinned — the log says so.");

        RevealCaves = Config.Bind("RevealTypes", "Caves", true, "Reveal cave (mine) pins.");
        RevealIslands = Config.Bind("RevealTypes", "Islands", true, "Reveal island pins (Desert/Marsh/Small/Tiny islands).");
        RevealLakes = Config.Bind("RevealTypes", "Lakes", true, "Reveal lake pins.");
        RevealFishingGrounds = Config.Bind("RevealTypes", "FishingGrounds", true, "Reveal fishing-ground pins (salmon/wolfish).");
        RevealEnemyCamps = Config.Bind("RevealTypes", "EnemyCamps", true, "Reveal enemy viking settlement pins (desert/marsh camps + strongholds).");
        RevealCreatureDens = Config.Bind("RevealTypes", "CreatureDens", true, "Reveal creature den/habitat pins (wulfars, skeletons, draugar, wights, bosses, wildlife).");
        RevealLandmarks = Config.Bind("RevealTypes", "Landmarks", true, "Reveal landmark pins (exploration towers, shipwrecks, stone heads).");
        RevealUnknown = Config.Bind("RevealTypes", "Unknown", true, "Reveal pins of any POI type not matched by the other toggles (safety net for game updates).");

        EnableLogging = Config.Bind("SeedScout", "EnableLogging", false,
            "Master switch for SeedScout's informational log output (the 5s heartbeat, the area dump, " +
            "per-POI reveal lines). Set false to silence the spam so other mods' log lines are easy to " +
            "find. Genuine warnings/errors and the one-time load line still show regardless.");

        ClassInjector.RegisterTypeInIl2Cpp<ScoutTracker>();
        var go = new GameObject("SeedScoutMod_Tracker");
        Object.DontDestroyOnLoad(go);
        go.AddComponent<ScoutTracker>();

        new Harmony(MyPluginInfo.PLUGIN_GUID).PatchAll();

        Logger.LogInfo($"SeedScoutMod {MyPluginInfo.PLUGIN_VERSION} loaded. " +
                       $"RevealNativePins={RevealNativePins.Value} EnableLogging={EnableLogging.Value}. " +
                       "Load a world; native POI pins appear on the map within ~10s.");
    }

    // Gated informational logging. Routed through here so EnableLogging=false silences the chatty
    // heartbeat/per-POI/dump output. Warnings/errors call Logger.LogWarning/LogError directly so
    // they always surface regardless of this switch.
    internal static void LogInfo(string msg)
    {
        if (EnableLogging.Value) Logger.LogInfo(msg);
    }
}
