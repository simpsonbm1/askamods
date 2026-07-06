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

// What kind of respawnable node a position holds. Recorded at interaction-bind time
// (SetWorldInstance) so the data-sync path can classify a node WITHOUT its GameObject —
// the interaction component often doesn't live on the BiomeItemInstance's own GameObject
// (v1.2.14 finding), and for a node the host never instantiated there is no GameObject at all.
// Other = confirmed not-respawnable (single-piece harvestable, decoration, etc.) — cached so the
// per-data-change classifier doesn't re-walk its hierarchy forever; overwritten if a tree/gather
// interaction ever binds at that position.
internal enum NodeKind { Tree, Gather, Other }

[BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
public class Plugin : BasePlugin
{
    internal static ManualLogSource Logger = null!;
    internal static ConfigEntry<float> RespawnDays = null!;
    internal static ConfigEntry<bool> ProtectStumps = null!;
    public static ConfigEntry<bool> EnableDiagnostics { get; private set; } = null!;
    public static ConfigEntry<bool> EnableInitDiagnostics { get; private set; } = null!;
    public static ConfigEntry<bool> RefillUnloadedNodes { get; private set; } = null!;
    internal static ConfigEntry<string> ManualRespawnHotkey = null!;
    internal static ConfigEntry<float> ManualRespawnRadius = null!;
    internal static ConfigEntry<bool> ManualRespawnIncludeGather = null!;
    internal static ConfigEntry<float> WellChargesPerDay = null!;
    internal static ConfigEntry<bool> WellDiagnostics = null!;
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

    // HarvestInteractions captured when their WorldInstance is set. Used by manual respawn to find stumps without FindObjectsOfType.
    internal static readonly HashSet<SSSGame.HarvestInteraction> LiveHarvestInteractions = new();

    // BiomesManager captured at world load (Patches/Captures.cs). Only used by the v1.2.7 gather-reg
    // diagnostic to read the local player's position; null until a world is loading.
    internal static SSSGame.BiomesManager? Biomes;

    // Live read of the game's own static handler (BiomeItemInstance.Handler) — resolved at USE time,
    // never cached. v1.4.4 and earlier cached a wrapper captured once per process; after a
    // quit-to-menu → reload of the same world that wrapper pointed at the OLD world's freed native
    // object, and the handler-refill path AV'd natively inside VegetationSystem
    // InstancesDataArrays.FindIndexOfUniqueId (no managed exception — hard process death with a
    // clean log). The v1.2.9 experimental deactivated-replenish path uses this to resolve a usable
    // instance for a DEACTIVATED node — handler.GetInstance(onlyIfActive:false).
    internal static SSSGame.BiomeProceduralDataHandler? DataHandler
    {
        get { try { return SSSGame.BiomeItemInstance.Handler; } catch { return null; } }
    }

    // posKey → the node's WorldItemInstanceId, captured at harvest (when the instance is valid). Needed to
    // re-resolve the instance via the handler later, because the cached ActiveInstances pointer goes stale
    // (deregistered, UniqueId reset to 0) once the chunk deactivates. Persisted per pending entry.
    internal static readonly Dictionary<string, SSSGame.WorldItemInstanceId> GatherWid = new();

    // Same for trees (v1.3.0). Trees previously had NO wid — so the tree loop couldn't tell a pooled
    // wrapper reused for a different node from a live pointer (wrong-node Destroyed reads could cancel
    // a live stump permanently), and an unloaded overdue tree had no handler-refill path at all.
    internal static readonly Dictionary<string, SSSGame.WorldItemInstanceId> TreeWid = new();

    // posKey → what kind of node lives there + the gather item it yields. Populated whenever an
    // interaction binds to its world instance (SetWorldInstance, Patches/Captures.cs) and on direct
    // harvests. This is the authoritative interaction↔instance mapping — it lets DataSyncPatch
    // register depletions from pure data changes (co-op client harvests) with no GameObject involved.
    internal static readonly Dictionary<string, (NodeKind kind, string itemName)> KnownNodes = new();

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
            description: "Verbose diagnostic logging. Logs when the mod hides a stump from a woodcutter, when a gather/harvest worker goes idle for lack of an allowed target (NoResourcesFound / NoGatherTask — usually a work-priority issue, not a bug), and a throttled summary of pending respawns stuck because their node isn't currently loaded. Off by default; very chatty — turn on only for a short troubleshooting session, not for normal play.");

        EnableInitDiagnostics = Config.Bind(
            section: "TreeRespawn",
            key: "EnableInitDiagnostics",
            defaultValue: false,
            description: "Extremely verbose diagnostic logging for every biome-instance Initialize. Tells us whether a distant/villager-only area streams in on the host at all. Separate from EnableDiagnostics because a single run-through can log the same handful of positions 90+ times each.");

        RefillUnloadedNodes = Config.Bind(
            section: "TreeRespawn",
            key: "RefillUnloadedGatherNodes",
            defaultValue: true,
            description: "When ON (default), a node whose respawn timer elapses while its chunk is " +
                "unloaded/deactivated is refilled through the data handler — GetInstance(onlyIfActive:false) + " +
                "Replenish() + SetDirty — instead of being faked against the stale, deregistered pointer. This is " +
                "what lets distant forage markers (e.g. the shoreline reeds) refill so villagers keep working them " +
                "while you're elsewhere. As of v1.3.0 this covers TREES as well as gather nodes (key name kept " +
                "for config compatibility). Host-only. Turn OFF to revert to the old near-node-only behavior. The " +
                "[diag] exp-replenish log lines require EnableDiagnostics.");

        ManualRespawnHotkey = Config.Bind(
            section: "TreeRespawn",
            key: "ManualRespawnHotkey",
            defaultValue: "t",
            description: "Hotkey (letter or Unity KeyCode name) that instantly respawns every tracked stump " +
                "— and, if ManualRespawnIncludeGather is on, every tracked exhausted gather node — within " +
                "ManualRespawnRadius meters of the player. Host-only. Bypasses RespawnDays and the per-resource " +
                "gather timers entirely (it's a deliberate manual override, not a faster timer). Useful for " +
                "catching up a backlog of stumps/depleted patches by hand instead of waiting for the automatic " +
                "tick-based respawn, which only services a node while you happen to be standing near it.");

        ManualRespawnRadius = Config.Bind(
            section: "TreeRespawn",
            key: "ManualRespawnRadius",
            defaultValue: 12.0f,
            description: "Radius in meters (horizontal ground distance from the player) that ManualRespawnHotkey " +
                "scans for tracked stumps/gather nodes to instantly respawn.");

        ManualRespawnIncludeGather = Config.Bind(
            section: "TreeRespawn",
            key: "ManualRespawnIncludeGather",
            defaultValue: true,
            description: "When true, ManualRespawnHotkey also instantly respawns tracked exhausted gather nodes " +
                "(reeds, berries, etc.) within range, not just tree stumps.");

        WellChargesPerDay = Config.Bind(
            section: "WellRefill",
            key: "ChargesPerDay",
            defaultValue: 24.0f,
            description: "How many water charges a CONSTRUCTED well/water-collector building regains per " +
                "in-game day, on top of whatever the vanilla refill does. 24 = one charge per in-game hour. " +
                "Set to 0 to disable (pure vanilla refill). This is separate from the GatherRespawn 'Water' " +
                "entry, which only covers the wild Natural Water Collector. Host-only in co-op.");

        WellDiagnostics = Config.Bind(
            section: "WellRefill",
            key: "WellDiagnostics",
            defaultValue: false,
            description: "Verbose well-refill logging: the settlement scan state, per-minute per-well " +
                "elapsed/needed progress lines, and stale-entry drops. (The +N water refill line and " +
                "error/no-op warnings always log regardless.) Feature confirmed in-game 2026-07-04; " +
                "turn on only to troubleshoot wells not being found or not refilling.");

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

        Logger.LogInfo($"TreeRespawnMod loaded. RespawnDays={RespawnDays.Value}, GatherDefault={_gatherDefaultDays.Value}, " +
            $"ManualRespawnHotkey={ManualRespawnHotkey.Value}, ManualRespawnRadius={ManualRespawnRadius.Value}m");
    }

    internal static string PosKey(Vector3 pos) =>
        $"{pos.x:F1}:{pos.y:F1}:{pos.z:F1}";

    // Local player character (captured via PlayerCharacter.Spawned/Despawned)
    internal static SSSGame.PlayerCharacter? LocalPlayer;

    // Local player's position via the captured BiomesManager (same source WarpTour trusts).
    // Diagnostic-only — used to log player→node distance at harvest time. Returns false if the
    // player transform isn't available yet (e.g. at the main menu).
    internal static bool TryGetPlayerPos(out Vector3 pos)
    {
        pos = default;
        try
        {
            var pt = Biomes?._localPlayerTransform;
            if (pt == null) return false;
            pos = pt.position;
            return true;
        }
        catch { return false; }
    }

    // v1.2.8 read-only probe (diagnostic-only): is this (possibly deactivated) instance still addressable
    // in its persistent VegetationSystem buffer? This decides whether a respawn can be written to the
    // persistent arrays directly while the node is deactivated (buffer live + find >= 0) or whether the
    // node is gone/disposed while deactivated (→ force-load is the only lever). All reads wrapped — a
    // throw on a disposed native array is itself a signal (len=disposed), and must not disturb anything.
    internal static string ProbeBuffer(SSSGame.BiomeItemInstance inst)
    {
        string buf = "?", len = "?", find = "?", reg = "?", act = "?";
        try { buf = inst._buffer != null ? "set" : "null"; } catch (Exception e) { buf = "err:" + e.GetType().Name; }
        try { reg = inst.RegisteredInstanceIdx.ToString(); } catch { }
        uint uid = 0; bool haveUid = false;
        try { uid = inst.UniqueId; haveUid = true; } catch { }
        try
        {
            var b = inst._buffer;
            if (b != null)
            {
                try { act = b.activeInstances.Count.ToString(); } catch { act = "err"; }
                var arr = b.instances;
                try { len = arr.Length.ToString(); } catch { len = "disposed"; }
                if (haveUid) { try { find = arr.FindIndexOfUniqueId(uid).ToString(); } catch { find = "err"; } }
            }
        }
        catch { }
        return $"buf={buf} reg={reg} uid={(haveUid ? uid.ToString() : "?")} find={find} len={len} active={act}";
    }

    // Returns respawn days for a gathered item. Checks per-resource entries first (substring match),
    // falls back to Default. Returns 0 if respawn is disabled for this item.
    internal static float GetGatherThreshold(string itemName)
    {
        foreach (var kvp in GatherOverridesMap)
            if (itemName.Contains(kvp.Key, StringComparison.OrdinalIgnoreCase))
                return kvp.Value;
        return _gatherDefaultDays.Value;
    }

    // Every call site that needs the host-authoritative weather/time singleton used to repeat
    // `weather == null || weather.Runner == null || !weather.Runner.IsServer` with no way to tell
    // which of those three failed — that ambiguity is what blocked diagnosing the 2026-06-29 co-op
    // report ("WeatherSystem not available" on the HOST for an entire session, paired with the world
    // id resolving to a stale/wrong session). `reason` distinguishes the three so the log can say which.
    internal static bool TryGetServerWeather(out SSSGame.Weather.WeatherSystem? weather, out string reason)
    {
        weather = SSSGame.Weather.WeatherSystem.Instance;
        if (weather == null) { reason = "Instance=null"; return false; }
        
        // Co-op authority check: fallback to WeatherSystem.Runner if LocalPlayer isn't spawned yet,
        // but prefer LocalPlayer.NetworkObject.Runner.IsServer since WeatherSystem's Runner.IsServer
        // can be unreliable/false for the host in some co-op setups.
        if (LocalPlayer != null && LocalPlayer.NetworkObject != null && LocalPlayer.NetworkObject.Runner != null)
        {
            var runner = LocalPlayer.NetworkObject.Runner;
            if (!runner.IsServer && !runner.IsSharedModeMasterClient)
            {
                reason = "LocalPlayer.IsServer=false";
                return false;
            }
            reason = "";
            return true;
        }

        if (weather.Runner == null) { reason = "Runner=null"; return false; }
        if (!weather.Runner.IsServer && !weather.Runner.IsSharedModeMasterClient) { reason = "IsServer=false"; return false; }
        reason = "";
        return true;
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
            if (string.IsNullOrEmpty(id)) { NoteWorldLeft(); return; }   // at main menu / no session loaded
            if (id == CurrentWorldId) return;        // same world still active

            string name = "";
            try { name = _storage._activeSessionName ?? ""; } catch { }
            OnWorldChanged(id, name);
        }
        catch { _storage = null; /* re-find next poll */ }
    }

    // Quit-to-menu: the (persistent) StorageManager reports an empty ActiveSessionID while we still
    // think a world is active. Flush + drop ALL per-world state, exactly like switching worlds.
    // Without this, reloading the SAME world in the same process kept every cached native pointer
    // (ActiveInstances wrappers, handler-retry cooldowns, well cache) aimed at the old world's freed
    // objects — the first handler-path retry after reload then crashed the whole game natively
    // (AV in VegetationSystem FindIndexOfUniqueId; no managed exception). Clearing here means the
    // reload re-registers everything fresh via OnWorldChanged, and pending respawns re-load from the
    // per-world save file.
    private static void NoteWorldLeft()
    {
        if (CurrentWorldId == null) return;
        SavePending();
        PendingRespawns.Clear();
        PendingGatherRespawns.Clear();
        RegisteredStumps.Clear();
        ActiveInstances.Clear();
        GatherWid.Clear();
        TreeWid.Clear();
        KnownNodes.Clear();
        LiveHarvestInteractions.Clear();
        Biomes = null;
        DayTracker.ClearTransientState();
        CurrentWorldId = null;
        _saveFilePath = null;
        Logger.LogInfo("[TreeRespawnMod] World session ended (quit to menu) — per-world state cleared.");
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
            GatherWid.Clear();
            TreeWid.Clear();
            KnownNodes.Clear();
            LiveHarvestInteractions.Clear();
            Biomes = null;
            DayTracker.ClearTransientState(); // handler-retry cooldowns — posKeys collide across worlds
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
            {
                // v1.3.0: persist the tree's WorldItemInstanceId too (same rationale as gather —
                // without it, an unloaded stump can't be re-resolved/refilled after a reload).
                ulong treeWidRaw = 0;
                if (TreeWid.TryGetValue(kvp.Key, out var twid)) { try { treeWidRaw = twid.Raw; } catch { } }
                lines.Add($"{kvp.Key},{kvp.Value.ToString("R", CultureInfo.InvariantCulture)},{treeWidRaw}");
            }
            lines.Add("# gather");
            foreach (var kvp in PendingGatherRespawns)
            {
                // Persist the node's WorldItemInstanceId (as its raw ulong) so a far/unloaded marker can still
                // be re-resolved and refilled after a reload — without it, a save-loaded entry has no way to
                // address its deactivated instance. 0 = unknown (older entries; falls back to near-node refill).
                ulong widRaw = 0;
                if (GatherWid.TryGetValue(kvp.Key, out var wid)) { try { widRaw = wid.Raw; } catch { } }
                lines.Add($"{kvp.Key},{kvp.Value.gameTime.ToString("R", CultureInfo.InvariantCulture)},{widRaw},{kvp.Value.itemName}");
            }
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
                    // Format (v1.2.10+): posKey,gameTime,widRaw,itemName
                    //         older:     posKey,gameTime,itemName        (no widRaw)
                    //         oldest:    posKey,gameTime
                    // posKey = "x:y:z" — no commas, so first comma ends posKey.
                    var c1 = line.IndexOf(',');
                    if (c1 < 0) continue;
                    var posKey = line[..c1];
                    var rest = line[(c1 + 1)..];
                    var c2 = rest.IndexOf(',');
                    string gameTimeStr = c2 >= 0 ? rest[..c2] : rest;
                    string afterGt     = c2 >= 0 ? rest[(c2 + 1)..] : "";
                    if (!float.TryParse(gameTimeStr, NumberStyles.Float,
                            CultureInfo.InvariantCulture, out float gameTime)) continue;

                    // Tell the new (widRaw,itemName) format from the old (itemName) one: the wid field is a
                    // ulong and no gather item name is purely numeric, so a ulong-parse of the field before the
                    // next comma disambiguates safely (and preserves any comma inside a legacy item name).
                    ulong widRaw = 0; string name;
                    int c3 = afterGt.IndexOf(',');
                    if (c3 >= 0 && ulong.TryParse(afterGt[..c3], NumberStyles.Integer, CultureInfo.InvariantCulture, out widRaw))
                        name = afterGt[(c3 + 1)..];
                    else { name = afterGt; widRaw = 0; }

                    PendingGatherRespawns[posKey] = (gameTime, name);
                    if (widRaw != 0)
                        try { GatherWid[posKey] = new SSSGame.WorldItemInstanceId(widRaw); } catch { }
                }
                else
                {
                    // Tree format (v1.3.0+): posKey,gameTime,widRaw
                    //         older:         posKey,gameTime
                    // posKey = "x:y:z" — no commas, so first comma ends posKey.
                    var c1 = line.IndexOf(',');
                    if (c1 < 0) continue;
                    var posKey = line[..c1];
                    var rest = line[(c1 + 1)..];
                    var c2 = rest.IndexOf(',');
                    string gameTimeStr = c2 >= 0 ? rest[..c2] : rest;
                    if (!float.TryParse(gameTimeStr, NumberStyles.Float,
                            CultureInfo.InvariantCulture, out float gameTime)) continue;
                    PendingRespawns[posKey] = gameTime;
                    if (c2 >= 0 && ulong.TryParse(rest[(c2 + 1)..], NumberStyles.Integer,
                            CultureInfo.InvariantCulture, out ulong treeWidRaw) && treeWidRaw != 0)
                        try { TreeWid[posKey] = new SSSGame.WorldItemInstanceId(treeWidRaw); } catch { }
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
