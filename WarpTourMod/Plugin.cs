using System.Collections.Generic;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Unity.IL2CPP;
using BepInEx.Logging;
using HarmonyLib;
using Il2CppInterop.Runtime.Injection;
using SandSailorStudio.Streaming;
using SSSGame;
using SSSGame.Controllers;
using UnityEngine;

namespace WarpTourMod;

// Mod 10 — WarpTour. Split out of SeedScoutMod (Mod 9, which stays the manual colored-overlay seed
// scorer). On world load, after force-loading the cave tiles so dens/lakes stream in, it briefly
// teleports the local player to every POI (cave / hostile / lake) so the GAME discovers them for real
// and drops its OWN native map pins (correct icon + name), then returns to spawn. Player HP is held
// steady so the accumulated fall-damage from the landings can't kill you.
//
// Entirely gated by the master switch config `WarpTour/Enabled` — flip it off to leave the mod
// installed but dormant (play normally), on to scout a fresh seed.
[BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
public class Plugin : BasePlugin
{
    internal static ManualLogSource Logger = null!;

    // Captured game singletons / instances (grabbed from their own lifecycle, never FindObjectsByType).
    internal static BiomesManager? Biomes;
    internal static WorldStreamingManager? Streaming;
    internal static PlayerDrive? Drive;
    internal static PlayerCharacter? Player;

    // POIs that stream in per-tile (caves are read from the area map directly in Warp).
    internal static readonly List<Vector2> Hostiles = new();
    internal static readonly List<Vector2> Lakes = new();

    internal static ConfigEntry<bool> Enabled = null!;
    internal static ConfigEntry<bool> ForceLoadTiles = null!;
    internal static ConfigEntry<int> ForceLoadRadius = null!;
    internal static ConfigEntry<float> Dwell = null!;
    internal static ConfigEntry<float> Drain = null!;

    public override void Load()
    {
        Logger = base.Log;

        Enabled = Config.Bind("WarpTour", "Enabled", false,
            "MASTER SWITCH (default OFF). true = on world load, run the warp tour (teleport to every POI so " +
            "the game drops its native map pins, then return to spawn). false = do nothing (mod stays " +
            "installed but dormant — play normally). Editable live: flip it true before loading a seed to scout.");

        ForceLoadTiles = Config.Bind("WarpTour", "ForceLoadCaveTiles", true,
            "Before the tour, force the streamer to load the tiles around each cave so dens/lakes are known " +
            "up front (seeds the route). Leave true for best coverage.");

        ForceLoadRadius = Config.Bind("WarpTour", "ForceLoadRadius", 2,
            "Tile rings around each cave to force-load (0 = cave tile only, 1 = 3x3, 2 = 5x5, 3 = 7x7). " +
            "Tile ~128m. Clamped 0-3. Editable live.");

        Dwell = Config.Bind("WarpTour", "DwellSeconds", 0.75f,
            "Seconds to linger at each POI so discovery fires before moving on. Lower = faster tour. " +
            "Clamped to a 0.5s floor: ~0.2s CRASHES the game (teleporting faster than the streamer settles). " +
            "0.5s is confirmed safe. Editable live.");

        Drain = Config.Bind("WarpTour", "DrainSeconds", 8f,
            "After the last reachable POI, how long to keep waiting for more dens to stream in (the timer " +
            "resets each time a new one appears) before returning to spawn. Dens keep streaming ~30s, so " +
            "higher = better coverage but slower finish. Editable live.");

        ClassInjector.RegisterTypeInIl2Cpp<WarpTracker>();
        var go = new GameObject("WarpTourMod_Tracker");
        Object.DontDestroyOnLoad(go);
        go.AddComponent<WarpTracker>();

        new Harmony(MyPluginInfo.PLUGIN_GUID).PatchAll();

        Logger.LogInfo($"WarpTour {MyPluginInfo.PLUGIN_VERSION} loaded. Enabled={Enabled.Value}. " +
                       "Load a world with Enabled=true to warp-tour every POI for native map pins.");
    }
}
