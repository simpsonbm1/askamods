using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Unity.IL2CPP;
using BepInEx.Logging;
using Fusion;
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
    private static ConfigEntry<float> _miningDefaultDays = null!;

    // Key: world position string — genuinely unique, stable across saves.
    // UniqueId is NOT globally unique (it's a per-buffer index; every chunk restarts at 0).
    internal static readonly Dictionary<string, float> PendingRespawns = new();
    internal static readonly HashSet<string> RegisteredStumps = new();

    // Gather resources — value is (gameTime at exhaustion, yielded item name for override lookup).
    internal static readonly Dictionary<string, (float gameTime, string itemName)> PendingGatherRespawns = new();

    // Mining nodes — value is (gameTime at depletion, game object name for override lookup).
    internal static readonly Dictionary<string, (float gameTime, string nodeName)> PendingMiningRespawns = new();

    // Populated at startup from per-resource Config.Bind calls. Key is substring to match (case-insensitive).
    internal static readonly Dictionary<string, float> GatherOverridesMap =
        new(StringComparer.OrdinalIgnoreCase);

    internal static readonly Dictionary<string, float> MiningOverridesMap =
        new(StringComparer.OrdinalIgnoreCase);

    // Position → live instance, populated by BiomeInstancePatch as the world loads.
    internal static readonly Dictionary<string, SSSGame.BiomeItemInstance> ActiveInstances = new();

    // Position → live WorldItemInstance for mining nodes (plain WorldItemInstance, not BiomeItemInstance).
    // Populated on every HarvestInteraction hit so the reference stays fresh.
    internal static readonly Dictionary<string, SSSGame.WorldItemInstance> ActiveMiningInstances = new();

    // Position → HarvestInteraction for stone clumps (_worldInstance is null on these).
    internal static readonly Dictionary<string, SSSGame.HarvestInteraction> ActiveStoneInstances = new();

    // Position → (prefab GUID, original rotation, stone name) captured from the fatal hit.
    // Populated by HarvestPatch.Prefix while NetworkObject is still Valid, then consumed at respawn.
    internal static readonly Dictionary<string, (NetworkObjectGuid guid, Quaternion rotation, string name)> CachedStoneData = new();

    // Cached NetworkSpawner — populated by NetworkSpawnerPatch on Awake().
    internal static SSSGame.Network.NetworkSpawner? GameNetworkSpawner = null;

    internal static Vector3 PosKeyToVector3(string posKey)
    {
        var parts = posKey.Split(':');
        return new Vector3(
            float.Parse(parts[0], CultureInfo.InvariantCulture),
            float.Parse(parts[1], CultureInfo.InvariantCulture),
            float.Parse(parts[2], CultureInfo.InvariantCulture));
    }

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
            "Respawn days for any gather resource NOT explicitly listed below (fallback only). " +
            "Does not affect the named entries below — each of those always uses its own value " +
            "no matter what this is set to. Set to 0 to disable respawn for unlisted resources only.");

        void Bind(string itemName, float defaultDays, string description)
        {
            var e = Config.Bind(gs, itemName, defaultDays, description);
            GatherOverridesMap[itemName] = e.Value;
        }

        // Item names below are the exact names shown in the game's inventory/storage UI,
        // confirmed in-game on 2026-06-17. Substring match is case-insensitive, so e.g.
        // key "Mushroom" also matches "Mushrooms", "Gray Mushrooms"/"Grey Mushrooms", and
        // "Yellow Mushrooms". Seeds and Wild Egg are bonus drops bundled with their parent
        // gather (plants / Bird's Nest Feathers respectively) — they're never their own
        // GatherInteraction, so they don't need (and can't use) an override entry here.
        Bind("Thatch",      1.0f, "Reeds. Set to 0 to disable respawn.");
        Bind("Berries",     2.0f, "Berry Bush. Set to 0 to disable respawn.");
        Bind("Stick",       1.0f, "Dwarf Spruce. Set to 0 to disable respawn.");
        Bind("Fiber",       1.5f, "Flax Bush. Set to 0 to disable respawn.");
        Bind("Small Stone", 0f,   "Small Stone on ground. Default 0 = one-time pickup, no respawn.");
        Bind("Mussels",     2.0f, "Mussels on rocks. Set to 0 to disable respawn.");
        Bind("Feathers",    2.0f, "Fallen Bird's Nest feathers. Set to 0 to disable respawn.");
        Bind("Carrot",      3.0f, "Carrot. Set to 0 to disable respawn.");
        Bind("Cabbage",     3.0f, "Cabbage. Set to 0 to disable respawn.");
        Bind("Onion",       3.0f, "Onion. Set to 0 to disable respawn.");
        Bind("Garlic",      3.0f, "Garlic. Set to 0 to disable respawn.");
        Bind("Beetroot",    3.0f, "Beetroot. Set to 0 to disable respawn.");
        Bind("Mushroom",    1.5f, "Mushrooms (substring also covers Gray/Grey Mushrooms and Yellow Mushrooms). Set to 0 to disable respawn.");
        Bind("Water",       0.5f, "Natural Water Collector. Set to 0 to disable respawn.");

        const string ms = "MiningRespawn";
        _miningDefaultDays = Config.Bind(ms, "Default", 3.0f,
            "Respawn days for any mining node NOT explicitly listed below (fallback only). " +
            "Set to 0 to disable respawn for unlisted nodes.");

        void BindMining(string nodeName, float defaultDays, string description)
        {
            var e = Config.Bind(ms, nodeName, defaultDays, description);
            MiningOverridesMap[nodeName] = e.Value;
        }

        // Keys are substring-matched against the game object name logged on first depletion —
        // check BepInEx/LogOutput.log to verify the exact GameObject name if a node isn't matching.
        BindMining("Small Stone Clump", 2.0f, "Small Stone Clump. Set to 0 to disable respawn.");
        BindMining("Large Stone Clump", 3.0f, "Large Stone Clump. Set to 0 to disable respawn.");
        BindMining("Small Jotun Clump", 4.0f, "Small Jotun Clump. Set to 0 to disable respawn.");
        BindMining("Large Jotun Clump", 5.0f, "Large Jotun Clump. Set to 0 to disable respawn.");
        BindMining("Jotun Blood Shard", 5.0f, "Jotun Blood Shard. Set to 0 to disable respawn.");
        BindMining("Large Rock",        0f,   "Large Rock. Default 0 = no respawn (likely a permanent terrain feature).");
        BindMining("Cave Stone",        3.0f, "Cave Stone. Set to 0 to disable respawn.");

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

    internal static float GetMiningThreshold(string nodeName)
    {
        foreach (var kvp in MiningOverridesMap)
            if (nodeName.Contains(kvp.Key, StringComparison.OrdinalIgnoreCase))
                return kvp.Value;
        return _miningDefaultDays.Value;
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
            lines.Add("# mining");
            foreach (var kvp in PendingMiningRespawns)
            {
                string guidStr = CachedStoneData.TryGetValue(kvp.Key, out var sd) ? sd.guid.ToString() : "";
                lines.Add($"{kvp.Key},{kvp.Value.gameTime.ToString("R", CultureInfo.InvariantCulture)},{kvp.Value.nodeName}" +
                          (string.IsNullOrEmpty(guidStr) ? "" : $",{guidStr}"));
            }
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

                if (section == "gather" || section == "mining")
                {
                    // Format: posKey,gameTime,name  (old saves: posKey,gameTime)
                    // posKey = "x:y:z" — no commas, so first comma ends posKey.
                    var c1 = line.IndexOf(',');
                    if (c1 < 0) continue;
                    var posKey = line[..c1];
                    var rest = line[(c1 + 1)..];
                    var c2 = rest.IndexOf(',');
                    string gameTimeStr = c2 >= 0 ? rest[..c2] : rest;
                    string name        = c2 >= 0 ? rest[(c2 + 1)..] : "";
                    if (!float.TryParse(gameTimeStr, NumberStyles.Float,
                            CultureInfo.InvariantCulture, out float gameTime)) continue;
                    if (section == "gather")
                    {
                        PendingGatherRespawns[posKey] = (gameTime, name);
                    }
                    else
                    {
                        // Mining: name may be followed by a GUID 4th field.
                        string nodeName = name;
                        string guidPart = "";
                        var c3 = name.IndexOf(',');
                        if (c3 >= 0) { nodeName = name[..c3]; guidPart = name[(c3 + 1)..]; }
                        PendingMiningRespawns[posKey] = (gameTime, nodeName);
                        if (!string.IsNullOrEmpty(guidPart) && NetworkObjectGuid.TryParse(guidPart, out var guid) && guid.IsValid)
                            CachedStoneData[posKey] = (guid, Quaternion.identity, nodeName);
                    }
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
            int total = PendingRespawns.Count + PendingGatherRespawns.Count + PendingMiningRespawns.Count;
            if (total > 0)
                Logger.LogInfo($"[TreeRespawnMod] Loaded {PendingRespawns.Count} tree + {PendingGatherRespawns.Count} gather + {PendingMiningRespawns.Count} mining pending respawn(s).");
        }
        catch (Exception ex)
        {
            Logger.LogError($"[TreeRespawnMod] Failed to read save: {ex.Message}");
        }
    }
}
