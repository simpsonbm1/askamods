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
    internal static ConfigEntry<bool> ProtectStumps = null!;
    internal static ConfigEntry<bool> EnableDiagnostics = null!;
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

    // The current world's StorageManager.ActiveSessionID, set once a world loads (see PollWorldId).
    // Until it's known we don't know WHICH world we're in, so no pending-respawn file is read/written.
    internal static string? CurrentWorldId = null;

    private static string? _saveFilePath = null;

    public override void Load()
    {
        Logger = base.Log;

        RespawnDays = Config.Bind(
            section: "TreeRespawn",
            key: "RespawnDays",
            defaultValue: 3.0f,
            description: "In-game days before a felled tree respawns (stump must remain). Supports decimals — e.g. 0.05 ≈ 1 real minute in a 20-min day.");

        ProtectStumps = Config.Bind(
            section: "TreeRespawn",
            key: "ProtectStumpsFromWoodcutters",
            defaultValue: true,
            description: "When true, village woodcutters won't harvest the stumps left by felled trees, so those stumps survive to regrow into a renewable forest. You can still clear a stump yourself by hand to make that spot permanent — clearing a stump cancels its respawn.");

        EnableDiagnostics = Config.Bind(
            section: "TreeRespawn",
            key: "EnableDiagnostics",
            defaultValue: false,
            description: "Verbose diagnostic logging. Logs when the mod hides a stump from a woodcutter, and when a gather/harvest worker goes idle for lack of an allowed target (NoResourcesFound / NoGatherTask — usually a work-priority issue, not a bug). Off by default; turn on only when troubleshooting woodcutter behaviour.");

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

        // NOTE: Mining/stone-clump respawn is intentionally NOT implemented — the clumps
        // (Harvest_Stone4) are Fusion scene objects with no mod-reachable respawn path.
        // See STONE_RESPAWN_HANDOFF.md for the full investigation and why it was abandoned.

        // The pending-respawn save file is per-world (keyed by StorageManager.ActiveSessionID) and is
        // selected only once a world loads — DayTracker calls PollWorldId until it resolves. So
        // singleplayer and co-op worlds never share or cross-contaminate respawn state (world
        // positions collide across worlds, so a single shared file would respawn/cancel wrong nodes).

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

    // ── Per-world save selection ────────────────────────────────────────────────────────────
    // World identity comes from SandSailorStudio.Storage.StorageManager.ActiveSessionID — a unique
    // per-save id that is populated for LOADED saves (the world seed is not: it's null on a loaded
    // save, only set during fresh world generation). DayTracker polls this. We need a per-world save
    // file because pending respawns are keyed by world position, and positions collide across worlds
    // (worldgen reuses the same coordinate space) — a single shared file let one world's entries
    // respawn or cancel the wrong nodes in another (singleplayer ⇄ co-op cross-contamination).
    private static SandSailorStudio.Storage.StorageManager? _storage;

    // Read the active session id (cheap once the manager is cached) and switch worlds if it changed.
    internal static void PollWorldId()
    {
        try
        {
            if (_storage == null)
                _storage = UnityEngine.Object.FindAnyObjectByType<SandSailorStudio.Storage.StorageManager>();
            if (_storage == null) return;

            string id = _storage.ActiveSessionID;
            if (string.IsNullOrEmpty(id)) return;   // at main menu / no session loaded yet
            if (id == CurrentWorldId) return;        // same world still active

            string name = "";
            try { name = _storage._activeSessionName ?? ""; } catch { }
            OnWorldChanged(id, name);
        }
        catch { _storage = null; /* re-find next poll */ }
    }

    private static void OnWorldChanged(string id, string name)
    {
        // Leaving a previous world: flush it, then drop its in-memory state + stale live pointers so
        // the new world can't Replenish/cancel against the old world's instances. (On the very first
        // resolve there is no previous world, so we keep any instances BiomeInstancePatch already
        // registered during early load.)
        if (CurrentWorldId != null)
        {
            SavePending();
            PendingRespawns.Clear();
            PendingGatherRespawns.Clear();
            RegisteredStumps.Clear();
            ActiveInstances.Clear();
        }

        CurrentWorldId = id;
        _saveFilePath = Path.Combine(Paths.ConfigPath,
            $"com.askamods.treerespawn.{SanitizeId(id)}.save");

        LoadPending();

        Logger.LogInfo(
            $"[TreeRespawnMod] World '{name}' (session {id}) active → {Path.GetFileName(_saveFilePath)} " +
            $"({PendingRespawns.Count} tree + {PendingGatherRespawns.Count} gather pending).");
    }

    // Turn an arbitrary session id into a safe, collision-free file-name fragment.
    private static string SanitizeId(string id)
    {
        var sb = new System.Text.StringBuilder(id.Length + 9);
        foreach (char c in id)
            sb.Append(char.IsLetterOrDigit(c) ? c : '_');
        sb.Append('-');
        sb.Append(StableHash(id).ToString("x8")); // disambiguates ids that sanitize alike
        return sb.ToString();
    }

    // FNV-1a 32-bit — deterministic across processes (unlike String.GetHashCode, which is randomized).
    private static uint StableHash(string s)
    {
        uint hash = 2166136261u;
        foreach (char c in s) { hash ^= c; hash *= 16777619u; }
        return hash;
    }

    internal static void SavePending()
    {
        var path = _saveFilePath;
        if (path == null) return; // no world loaded yet — nothing to persist
        try
        {
            var lines = new List<string>();
            lines.Add("# tree");
            foreach (var kvp in PendingRespawns)
                lines.Add($"{kvp.Key},{kvp.Value.ToString("R", CultureInfo.InvariantCulture)}");
            lines.Add("# gather");
            foreach (var kvp in PendingGatherRespawns)
                lines.Add($"{kvp.Key},{kvp.Value.gameTime.ToString("R", CultureInfo.InvariantCulture)},{kvp.Value.itemName}");
            File.WriteAllLines(path, lines);
        }
        catch (Exception ex)
        {
            Logger.LogError($"[TreeRespawnMod] Failed to write save: {ex.Message}");
        }
    }

    private static void LoadPending()
    {
        var path = _saveFilePath;
        if (path == null || !File.Exists(path)) return;
        try
        {
            string section = "tree"; // default for files written before sections were added
            foreach (var line in File.ReadAllLines(path))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                if (line.StartsWith("# ")) { section = line[2..].Trim(); continue; }

                // Old save files may contain a "# mining" section — silently drop it
                // (mining respawn was removed; see STONE_RESPAWN_HANDOFF.md).
                if (section == "mining") continue;

                if (section == "gather")
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
                    PendingGatherRespawns[posKey] = (gameTime, name);
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
