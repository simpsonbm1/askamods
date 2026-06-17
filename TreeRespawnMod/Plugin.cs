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
    private static ConfigEntry<float> _gatherDefaultDays = null!;

    // Key: world position string — genuinely unique, stable across saves.
    // UniqueId is NOT globally unique (it's a per-buffer index; every chunk restarts at 0).
    internal static readonly Dictionary<string, float> PendingRespawns = new();
    internal static readonly HashSet<string> RegisteredStumps = new();

    // Gather resources — value is (gameTime at exhaustion, yielded item name for override lookup).
    internal static readonly Dictionary<string, (float gameTime, string itemName)> PendingGatherRespawns = new();

    // Populated at startup from per-resource Config.Bind calls. Key is substring to match (case-insensitive).
    internal static readonly Dictionary<string, float> GatherOverridesMap =
        new(StringComparer.OrdinalIgnoreCase);

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

        const string gs = "GatherRespawn";
        _gatherDefaultDays = Config.Bind(gs, "Default", 1.0f,
            "Respawn days for any gather resource not listed below. Set to 0 to disable respawn.");

        void Bind(string itemName, float defaultDays, string description)
        {
            var e = Config.Bind(gs, itemName, defaultDays, description);
            GatherOverridesMap[itemName] = e.Value;
        }

        // Item names below are the yielded item names logged by the mod on first exhaustion.
        // Entries marked (unconfirmed) are best guesses — check BepInEx/LogOutput.log to verify.
        Bind("Thatch",   1.0f, "Reeds. Set to 0 to disable respawn.");
        Bind("Berries",  2.0f, "Berry Bush. Set to 0 to disable respawn.");
        Bind("Stick",    1.0f, "Dwarf Spruce. Set to 0 to disable respawn.");
        Bind("Fibers",   1.5f, "Flax Bush. Set to 0 to disable respawn.");
        Bind("Stone",    0f,   "Small Stone on ground. Default 0 = one-time pickup, no respawn. (unconfirmed item name)");
        Bind("Mussel",   2.0f, "Mussels on rocks. Set to 0 to disable respawn. (unconfirmed item name)");
        Bind("Feathers", 2.0f, "Fallen Bird's Nest feathers. Set to 0 to disable respawn. (unconfirmed item name)");
        Bind("Egg",      2.0f, "Fallen Bird's Nest eggs. Set to 0 to disable respawn. (unconfirmed item name — may be 'Wild Egg')");
        Bind("Carrot",   3.0f, "Carrot. Set to 0 to disable respawn. (unconfirmed item name)");
        Bind("Cabbage",  3.0f, "Cabbage. Set to 0 to disable respawn. (unconfirmed item name)");
        Bind("Onion",    3.0f, "Onion. Set to 0 to disable respawn. (unconfirmed item name)");
        Bind("Garlic",   3.0f, "Garlic. Set to 0 to disable respawn. (unconfirmed item name)");
        Bind("Beetroot", 3.0f, "Beetroot. Set to 0 to disable respawn. (unconfirmed item name)");
        Bind("Mushroom", 1.5f, "Mushroom (substring match covers Gray Mushroom and Yellow Mushroom too). Set to 0 to disable respawn. (unconfirmed item name)");
        Bind("Water",    0.5f, "Natural Water Collector. Set to 0 to disable respawn. (unconfirmed item name)");

        _saveFilePath = Path.Combine(Paths.ConfigPath, "com.askamods.treerespawn.save");
        LoadPending();

        ClassInjector.RegisterTypeInIl2Cpp<DayTracker>();
        var go = new GameObject("TreeRespawnMod_DayTracker");
        UnityEngine.Object.DontDestroyOnLoad(go);
        go.AddComponent<DayTracker>();

        var harmony = new Harmony(MyPluginInfo.PLUGIN_GUID);
        harmony.PatchAll();

        Logger.LogInfo($"TreeRespawnMod loaded. RespawnDays={RespawnDays.Value}, GatherDefault={_gatherDefaultDays.Value}");
    }

    internal static string PosKey(Vector3 pos) =>
        $"{pos.x:F1}:{pos.y:F1}:{pos.z:F1}";

    // Returns respawn days for a gathered item. Checks per-resource entries first (substring match),
    // falls back to Default. Returns 0 if respawn is disabled for this item.
    internal static float GetGatherThreshold(string itemName)
    {
        foreach (var kvp in GatherOverridesMap)
            if (itemName.Contains(kvp.Key, StringComparison.OrdinalIgnoreCase))
                return kvp.Value;
        return _gatherDefaultDays.Value;
    }

    internal static void SavePending()
    {
        try
        {
            var lines = new List<string>();
            lines.Add("# tree");
            foreach (var kvp in PendingRespawns)
                lines.Add($"{kvp.Key},{kvp.Value.ToString("R", CultureInfo.InvariantCulture)}");
            lines.Add("# gather");
            foreach (var kvp in PendingGatherRespawns)
                lines.Add($"{kvp.Key},{kvp.Value.gameTime.ToString("R", CultureInfo.InvariantCulture)},{kvp.Value.itemName}");
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
            string section = "tree"; // default for files written before sections were added
            foreach (var line in File.ReadAllLines(_saveFilePath))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                if (line.StartsWith("# ")) { section = line[2..].Trim(); continue; }

                if (section == "gather")
                {
                    // Format: posKey,gameTime,itemName  (old saves: posKey,gameTime)
                    // posKey = "x:y:z" — no commas, so first comma ends posKey.
                    var c1 = line.IndexOf(',');
                    if (c1 < 0) continue;
                    var posKey = line[..c1];
                    var rest = line[(c1 + 1)..];
                    var c2 = rest.IndexOf(',');
                    string gameTimeStr = c2 >= 0 ? rest[..c2] : rest;
                    string itemName    = c2 >= 0 ? rest[(c2 + 1)..] : "";
                    if (!float.TryParse(gameTimeStr, NumberStyles.Float,
                            CultureInfo.InvariantCulture, out float gameTime)) continue;
                    PendingGatherRespawns[posKey] = (gameTime, itemName);
                }
                else
                {
                    // Tree format: posKey,gameTime — single comma, LastIndexOf is fine.
                    var comma = line.LastIndexOf(',');
                    if (comma < 0) continue;
                    var posKey = line[..comma];
                    if (!float.TryParse(line[(comma + 1)..], NumberStyles.Float,
                            CultureInfo.InvariantCulture, out float gameTime)) continue;
                    PendingRespawns[posKey] = gameTime;
                }
            }
            int total = PendingRespawns.Count + PendingGatherRespawns.Count;
            if (total > 0)
                Logger.LogInfo($"[TreeRespawnMod] Loaded {PendingRespawns.Count} tree + {PendingGatherRespawns.Count} gather pending respawn(s).");
        }
        catch (Exception ex)
        {
            Logger.LogError($"[TreeRespawnMod] Failed to read save: {ex.Message}");
        }
    }
}
