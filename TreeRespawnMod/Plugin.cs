using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Unity.IL2CPP;
using BepInEx.Logging;
using HarmonyLib;
using Il2CppInterop.Runtime.Injection;
using UnityEngine;

namespace TreeRespawnMod;

[BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
public class Plugin : BasePlugin
{
    internal static ManualLogSource Logger = null!;
    internal static ConfigEntry<float> RespawnDays = null!;

    // Key: world position string — genuinely unique, stable across saves.
    // UniqueId is NOT globally unique (it's a per-buffer index; every chunk restarts at 0).
    internal static readonly Dictionary<string, float> PendingRespawns = new();
    internal static readonly HashSet<string> RegisteredStumps = new();

    // Position → live instance, populated by BiomeInstancePatch as the world loads.
    internal static readonly Dictionary<string, SSSGame.BiomeItemInstance> ActiveInstances = new();

    private static string _saveFilePath = null!;

    public override void Load()
    {
        Logger = base.Log;

        RespawnDays = Config.Bind(
            section: "TreeRespawn",
            key: "RespawnDays",
            defaultValue: 3.0f,
            description: "In-game days before a felled tree respawns (stump must remain). Supports decimals — e.g. 0.05 ≈ 1 real minute in a 20-min day.");

        _saveFilePath = Path.Combine(Paths.ConfigPath, "com.askamods.treerespawn.save");
        LoadPending();

        ClassInjector.RegisterTypeInIl2Cpp<DayTracker>();
        var go = new GameObject("TreeRespawnMod_DayTracker");
        UnityEngine.Object.DontDestroyOnLoad(go);
        go.AddComponent<DayTracker>();

        var harmony = new Harmony(MyPluginInfo.PLUGIN_GUID);
        harmony.PatchAll();

        Logger.LogInfo($"TreeRespawnMod loaded. RespawnDays={RespawnDays.Value}");
    }

    // Rounds to 0.1m precision — enough to uniquely identify individual trees.
    internal static string PosKey(Vector3 pos) =>
        $"{pos.x:F1}:{pos.y:F1}:{pos.z:F1}";

    internal static void SavePending()
    {
        try
        {
            var lines = new List<string>(PendingRespawns.Count);
            foreach (var kvp in PendingRespawns)
                lines.Add($"{kvp.Key},{kvp.Value.ToString("R", CultureInfo.InvariantCulture)}");
            File.WriteAllLines(_saveFilePath, lines);
        }
        catch (Exception ex)
        {
            Logger.LogError($"[TreeRespawnMod] Failed to write save: {ex.Message}");
        }
    }

    private static void LoadPending()
    {
        try
        {
            if (!File.Exists(_saveFilePath)) return;
            foreach (var line in File.ReadAllLines(_saveFilePath))
            {
                // Format: "x:y:z,gameTimeFelled"  (posKey uses ':', save separator is ',')
                var comma = line.LastIndexOf(',');
                if (comma < 0) continue;
                var posKey = line[..comma];
                if (!float.TryParse(line[(comma + 1)..], NumberStyles.Float,
                        CultureInfo.InvariantCulture, out float gameTime)) continue;
                PendingRespawns[posKey] = gameTime;
            }
            if (PendingRespawns.Count > 0)
                Logger.LogInfo($"[TreeRespawnMod] Loaded {PendingRespawns.Count} pending respawn(s) from previous session.");
        }
        catch (Exception ex)
        {
            Logger.LogError($"[TreeRespawnMod] Failed to read save: {ex.Message}");
        }
    }
}
