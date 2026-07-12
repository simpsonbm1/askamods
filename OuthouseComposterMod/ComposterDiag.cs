using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using SandSailorStudio.Inventory;
using SSSGame;
using UnityEngine;

namespace OuthouseComposterMod;

// v0.1.x: read-only Phase 0 recon (structure/container/compost dump, F10 + auto-dump). v0.2.0
// (Phase 1) adds the actual composter: a converter half that WRITES game state (host/solo-gated) —
// see ConverterTick() and everything below NoteWorldLeft(). The dump/recon methods above that are
// still unchanged and still read-only. v0.3.0 moves the converter's timers from real time
// (DateTime.UtcNow) onto the in-game clock (SSSGame.Weather.WeatherSystem anchor +
// GetTimeDifferenceFromCurrentGameTimeInSeconds — opaque-units anchor+measure pattern, see
// TreeRespawnMod.WellRefill.Tick()) so TimeWarp-style acceleration speeds up composting. See
// Plugin.cs for the "why". v0.4.0 adds a second conversion mode: DoConvert (default, "sequential")
// pools across slots and performs exactly one conversion per fire; DoConvertSimultaneous ("true"
// mode, Plugin.SimultaneousConversion) evaluates every occupied slot independently and converts
// each one that individually holds a full ratio, all within the same fire — see TickTimer's fire
// branch for the dispatch.
public class ComposterDiag : MonoBehaviour
{
    private const float PollIntervalSeconds = 5f;

    private KeyCode _dumpKey = KeyCode.F10;
    private float _nextPollAt;

    // Never cache per-world native wrappers across world sessions (project-wide gotcha) — only
    // StorageManager itself (a persistent, cross-world singleton) is cached; everything else is
    // re-resolved on every dump.
    private SandSailorStudio.Storage.StorageManager? _storage;
    private string? _currentWorldId;
    private bool _autoDumpedThisSession;
    private bool _loggedCensusThisSession;

    // ── Converter state (v0.2.0) ────────────────────────────────────────────────────────────────
    private const float ConverterIntervalSeconds = 2f;
    private const float ContainerResolveIntervalSeconds = 30f;
    private float _nextConverterPollAt;

    // Resolved outhouse ItemContainers, keyed by structure world-position string (never UniqueId —
    // project-wide gotcha: UniqueId indices restart per spatial chunk). Re-resolved at most every
    // ContainerResolveIntervalSeconds so a newly-built outhouse is picked up without re-walking the
    // whole settlement every converter tick.
    private readonly Dictionary<string, ItemContainer> _outhouseContainers = new();
    private DateTime _lastContainerResolveAt = DateTime.MinValue;

    // Per-outhouse in-game-time anchors (v0.3.0). No entry = unarmed (waiting for its pool to
    // first become non-empty). Value is a game-time anchor assigned ONLY from
    // ws.NetworkedCurrentGameTime — never from real time, never arithmetic'd directly (opaque
    // internal units; project-wide gotcha, see TreeRespawnMod.WellRefill). These are NOT persisted
    // across save/load — timers restart on world load (documented limitation, unchanged from
    // v0.2.0; a per-slot day-stamp store would be the upgrade if this matters later).
    private readonly Dictionary<string, float> _foodAnchor = new();
    private readonly Dictionary<string, float> _seedAnchor = new();

    private ItemInfo? _compostInfo;
    private bool _loggedCompostUnresolved;
    private bool _loggedWeatherUnavailable;

    private void Start()
    {
        _dumpKey = ParseKey(Plugin.DumpKey.Value, KeyCode.F10);
    }

    private static KeyCode ParseKey(string raw, KeyCode fallback)
    {
        if (Enum.TryParse<KeyCode>(raw, true, out var parsed))
            return parsed;
        Plugin.Logger.LogWarning($"[OuthouseComposter] Could not parse DumpKey '{raw}'. Using default '{fallback}'.");
        return fallback;
    }

    private void Update()
    {
        try
        {
            if (Plugin.EnableDiagnostics.Value && !IsTextInputFocused() && Input.GetKeyDown(_dumpKey))
            {
                try { DumpAll("hotkey"); }
                catch (Exception ex) { Plugin.Logger.LogError($"[OuthouseComposter] DumpAll(hotkey) error: {ex}"); }
            }
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"[OuthouseComposter] Hotkey check error: {ex}");
        }

        // World-session tracking (StorageManager/ActiveSessionID + world-leave detection) always
        // runs — the converter depends on _currentWorldId being current regardless of
        // EnableDiagnostics. Only the auto-dump/census logging inside PollWorldSession is gated.
        if (Time.time >= _nextPollAt)
        {
            _nextPollAt = Time.time + PollIntervalSeconds;
            try { PollWorldSession(); }
            catch (Exception ex) { Plugin.Logger.LogError($"[OuthouseComposter] PollWorldSession error: {ex}"); }
        }

        // The converter runs independently of EnableDiagnostics — it's the actual feature, not a
        // diagnostic. EnableDiagnostics only gates how much it logs.
        if (Time.time >= _nextConverterPollAt)
        {
            _nextConverterPollAt = Time.time + ConverterIntervalSeconds;
            try { ConverterTick(); }
            catch (Exception ex) { Plugin.Logger.LogError($"[OuthouseComposter] ConverterTick error: {ex}"); }
        }
    }

    // ── World session tracking ──────────────────────────────────────────────────────────────
    private void PollWorldSession()
    {
        if (_storage == null)
        {
            try { _storage = UnityEngine.Object.FindAnyObjectByType<SandSailorStudio.Storage.StorageManager>(); }
            catch { _storage = null; }
        }
        if (_storage == null) return;

        string id;
        try { id = _storage.ActiveSessionID; }
        catch { _storage = null; return; }

        if (string.IsNullOrEmpty(id))
        {
            if (_currentWorldId != null)
                Plugin.Logger.LogInfo("[OuthouseComposter] World session ended — dropping per-world state.");
            NoteWorldLeft();
            return;
        }

        if (id != _currentWorldId)
        {
            _currentWorldId = id;
            _autoDumpedThisSession = false;
            _loggedCensusThisSession = false;
            Plugin.Logger.LogInfo($"[OuthouseComposter] World session active: {id}");
        }

        // Retry the auto-dump every poll until a matching structure is actually found (buildings
        // load a beat after the world/settlement does), then stop. Gated by EnableDiagnostics —
        // world-session tracking itself (above) always runs regardless, since the converter depends
        // on it.
        if (Plugin.EnableDiagnostics.Value && !_autoDumpedThisSession)
        {
            try
            {
                int matches = DumpAll("auto");
                if (matches > 0) _autoDumpedThisSession = true;
            }
            catch (Exception ex) { Plugin.Logger.LogError($"[OuthouseComposter] DumpAll(auto) error: {ex}"); }
        }
    }

    private void NoteWorldLeft()
    {
        _storage = null;
        _currentWorldId = null;
        _autoDumpedThisSession = false;
        _loggedCensusThisSession = false;

        // v0.2.0: drop all converter per-world state too — per-world native wrappers (the
        // ItemContainer cache here, plus OuthouseGate's own container-identity cache) must never
        // survive across world sessions (project-wide gotcha; a stale wrapper read AVs natively).
        _outhouseContainers.Clear();
        _foodAnchor.Clear();
        _seedAnchor.Clear();
        _compostInfo = null;
        _loggedCompostUnresolved = false;
        _loggedWeatherUnavailable = false;
        _lastContainerResolveAt = DateTime.MinValue;
        OuthouseGate.ClearCache();
    }

    // ── Converter (v0.2.0, Phase 1 — the actual feature; v0.3.0 — in-game-clock timers) ────────────
    // Runs only when a world session is active, the local player is registered, AND this client has
    // host/solo authority (co-op safety — copied verbatim from GroundItemVacuumMod.VacuumTracker's
    // IsHost() pattern). EnableDiagnostics never gates whether this runs, only how much it logs.
    private void ConverterTick()
    {
        if (_currentWorldId == null) return;
        if (Plugin.LocalPlayer == null) return;
        if (!IsHostOrSolo()) return;

        SSSGame.Weather.WeatherSystem? ws = null;
        try { ws = SSSGame.Weather.WeatherSystem.Instance; } catch { ws = null; }
        if (ws == null || ws.dayLength <= 0f)
        {
            if (Plugin.EnableDiagnostics.Value && !_loggedWeatherUnavailable)
            {
                _loggedWeatherUnavailable = true;
                Plugin.Logger.LogInfo("[OuthouseComposter][convert] WeatherSystem not yet available — conversions waiting.");
            }
            return;
        }
        _loggedWeatherUnavailable = false;

        ResolveOuthouseContainersIfDue();
        if (_outhouseContainers.Count == 0) return;

        ResolveCompostInfoIfNeeded();

        foreach (var kv in _outhouseContainers)
        {
            string posKey = kv.Key;
            ItemContainer container = kv.Value;

            try { TickTimer(posKey, container, isFood: true, ws); }
            catch (Exception ex) { Plugin.Logger.LogError($"[OuthouseComposter] TickTimer(food) error: {ex}"); }

            try { TickTimer(posKey, container, isFood: false, ws); }
            catch (Exception ex) { Plugin.Logger.LogError($"[OuthouseComposter] TickTimer(seed) error: {ex}"); }
        }
    }

    private static bool IsHostOrSolo()
    {
        try
        {
            var p = Plugin.LocalPlayer;
            if (p == null || p.NetworkObject == null || p.NetworkObject.Runner == null) return false;
            var runner = p.NetworkObject.Runner;
            return runner.IsServer || runner.IsSharedModeMasterClient;
        }
        catch { return false; }
    }

    // Re-resolves which structures match Plugin.StructureNameMatch and caches their first
    // ItemContainerComponent's ItemContainer, keyed by world position. Drops timer entries for
    // outhouses that disappeared (dismantled) so the dicts don't leak forever.
    private void ResolveOuthouseContainersIfDue()
    {
        var nowUtc = DateTime.UtcNow;
        if (_outhouseContainers.Count > 0 && (nowUtc - _lastContainerResolveAt).TotalSeconds < ContainerResolveIntervalSeconds)
            return;

        _lastContainerResolveAt = nowUtc;
        try
        {
            var structures = ResolveStructures();
            string filter = Plugin.StructureNameMatch.Value ?? "Outhouse";

            var newMap = new Dictionary<string, ItemContainer>();
            foreach (var s in structures)
            {
                if (!StructureMatches(s, filter)) continue;

                Transform root;
                try { root = s.transform; } catch { continue; }

                var containers = new List<ItemContainerComponent>();
                CollectContainersOnly(root, containers);
                if (containers.Count == 0) continue;

                string posKey;
                try { posKey = s.GetPosition().ToString(); } catch { continue; }

                ItemContainer? container = null;
                try { container = containers[0].container; } catch { }
                if (container != null) newMap[posKey] = container;
            }

            var stale = new List<string>();
            foreach (var key in _foodAnchor.Keys) if (!newMap.ContainsKey(key)) stale.Add(key);
            foreach (var key in _seedAnchor.Keys) if (!newMap.ContainsKey(key) && !stale.Contains(key)) stale.Add(key);
            foreach (var key in stale) { _foodAnchor.Remove(key); _seedAnchor.Remove(key); }

            _outhouseContainers.Clear();
            foreach (var kv in newMap) _outhouseContainers[kv.Key] = kv.Value;

            if (Plugin.EnableDiagnostics.Value)
                Plugin.Logger.LogInfo($"[OuthouseComposter][convert] Resolved {_outhouseContainers.Count} outhouse container(s).");
        }
        catch (Exception ex) { Plugin.Logger.LogError($"[OuthouseComposter] ResolveOuthouseContainersIfDue error: {ex}"); }
    }

    // Resolves the Compost ItemInfo from FarmingStation.composts (existing v0.1.x accessor), falling
    // back to a name-scan of any resolved outhouse container's current contents. Retried every
    // converter tick until it succeeds; conversions wait until then.
    private void ResolveCompostInfoIfNeeded()
    {
        if (_compostInfo != null) return;

        try
        {
            var fs = UnityEngine.Object.FindAnyObjectByType<FarmingStation>();
            ItemInfoList? composts = null;
            if (fs != null) { try { composts = fs.composts; } catch { } }

            if (composts != null)
            {
                var list = composts.itemInfoList;
                int n = list != null ? list.Count : 0;
                for (int i = 0; i < n; i++)
                {
                    try
                    {
                        var info = list![i];
                        if (info != null && string.Equals(SafeItemName(info), Plugin.CompostItemName.Value, StringComparison.OrdinalIgnoreCase))
                        {
                            _compostInfo = info;
                            break;
                        }
                    }
                    catch { }
                }
            }
        }
        catch (Exception ex) { Plugin.Logger.LogError($"[OuthouseComposter] ResolveCompostInfoIfNeeded (FarmingStation) error: {ex}"); }

        if (_compostInfo == null)
        {
            try
            {
                foreach (var container in _outhouseContainers.Values)
                {
                    var info = FindItemInfoByNameInContainer(container, Plugin.CompostItemName.Value);
                    if (info != null) { _compostInfo = info; break; }
                }
            }
            catch (Exception ex) { Plugin.Logger.LogError($"[OuthouseComposter] ResolveCompostInfoIfNeeded (container scan) error: {ex}"); }
        }

        if (_compostInfo == null)
        {
            if (!_loggedCompostUnresolved)
            {
                _loggedCompostUnresolved = true;
                Plugin.Logger.LogInfo("[OuthouseComposter][convert] compost item not yet resolved — conversions waiting.");
            }
        }
        else
        {
            _loggedCompostUnresolved = false;
        }
    }

    private static ItemInfo? FindItemInfoByNameInContainer(ItemContainer container, string name)
    {
        try
        {
            var items = container.GetItems();
            int capacity = -1;
            try { capacity = container.capacity; } catch { }
            int bound = capacity > 0 ? capacity : 64;
            for (int i = 0; i < bound; i++)
            {
                Item? item = null;
                try { item = items != null ? items[i] : null; } catch { break; }
                if (item == null) continue;
                ItemInfo? info = null;
                try { info = item.info; } catch { }
                if (info == null) continue;
                if (string.Equals(SafeItemName(info), name, StringComparison.OrdinalIgnoreCase)) return info;
            }
        }
        catch { }
        return null;
    }

    // Advances one outhouse's food-or-seed timer on the IN-GAME clock (v0.3.0). No entry in the
    // dict = unarmed: only starts counting once the relevant pool first becomes non-empty. Once
    // armed, every fire re-arms to a fresh ws.NetworkedCurrentGameTime anchor REGARDLESS of whether
    // a conversion actually happened (a below-ratio pool just skips that cycle) — locked semantics
    // from the delegation spec. Anchor is opaque-units (project-wide gotcha): only ever assigned
    // from NetworkedCurrentGameTime, only ever consumed through
    // GetTimeDifferenceFromCurrentGameTimeInSeconds — never arithmetic'd directly.
    private void TickTimer(string posKey, ItemContainer container, bool isFood, SSSGame.Weather.WeatherSystem ws)
    {
        var dict = isFood ? _foodAnchor : _seedAnchor;
        bool enabled = isFood ? Plugin.AcceptFood.Value : Plugin.AcceptSeeds.Value;
        if (!enabled) return;

        int ratio = isFood ? Plugin.FoodToCompostRatio.Value : Plugin.SeedsToCompostRatio.Value;
        double hours = isFood ? Plugin.FoodGameHours.Value : Plugin.SeedGameHours.Value;

        float thresholdSeconds;
        try { thresholdSeconds = (float)(hours * ws.dayLength / 24.0); }
        catch (Exception ex) { Plugin.Logger.LogError($"[OuthouseComposter] TickTimer dayLength read error: {ex}"); return; }

        int pool = CountPool(container, isFood);

        if (!dict.TryGetValue(posKey, out var anchor))
        {
            if (pool <= 0) return; // stay unarmed until the pool has something in it
            try { dict[posKey] = ws.NetworkedCurrentGameTime; } catch { }
            return;
        }

        float elapsed;
        try { elapsed = ws.GetTimeDifferenceFromCurrentGameTimeInSeconds(anchor); }
        catch (Exception ex) { Plugin.Logger.LogError($"[OuthouseComposter] TickTimer elapsed-time read error: {ex}"); return; }

        if (elapsed < thresholdSeconds) return;

        // Fire.
        if (_compostInfo == null)
        {
            try { dict[posKey] = ws.NetworkedCurrentGameTime; } catch { } // re-arm and wait for the compost item to resolve
            return;
        }

        if (pool >= ratio)
        {
            if (Plugin.SimultaneousConversion.Value)
                DoConvertSimultaneous(container, isFood, ratio, pool);
            else
                DoConvert(container, isFood, ratio, pool);
        }
        else if (Plugin.EnableDiagnostics.Value)
        {
            string kind = isFood ? "food" : "seeds";
            Plugin.Logger.LogInfo($"[OuthouseComposter][convert] {kind} timer fired but pool insufficient (pool={pool} need={ratio}) — skipped.");
        }

        try { dict[posKey] = ws.NetworkedCurrentGameTime; } catch { } // re-arm regardless
    }

    private static int CountPool(ItemContainer container, bool isFood)
    {
        int total = 0;
        try
        {
            var items = container.GetItems();
            int capacity = -1;
            try { capacity = container.capacity; } catch { }
            int bound = capacity > 0 ? capacity : 64;
            for (int i = 0; i < bound; i++)
            {
                Item? item = null;
                try { item = items != null ? items[i] : null; } catch { break; }
                if (item == null) continue;
                ItemInfo? info = null;
                try { info = item.info; } catch { }
                if (info == null) continue;
                bool matches = isFood ? OuthouseGate.IsFood(info) : OuthouseGate.IsSeed(info);
                if (!matches) continue;
                int count = 0;
                try { count = item.count; } catch { }
                total += count;
            }
        }
        catch (Exception ex) { Plugin.Logger.LogError($"[OuthouseComposter] CountPool error: {ex}"); }
        return total;
    }

    // ── Conversion (v0.2.0 DoConvert = "sequential" mode; v0.4.0 DoConvertSimultaneous = "true"
    // mode) ──────────────────────────────────────────────────────────────────────────────────────
    // Sequential (default, Plugin.SimultaneousConversion=false): pools across slots, removing the
    // ratio's worth starting from the first matching slot(s) in container order, and produces
    // exactly 1 Compost per fire. Simultaneous (Plugin.SimultaneousConversion=true): every occupied
    // matching slot is evaluated independently — each slot holding at least a full ratio converts
    // on its own (no cross-slot pooling), possibly producing multiple Compost in one fire.
    //
    // No partial conversions: only called when pool >= ratio. Requires HasSpace(compost, 1) BEFORE
    // removing anything (compost is natively accepted by the outhouse — this is the real game
    // answer, not our override). Snapshots matching items before removing (RemoveItem may
    // reindex/compact the container mid-walk).
    private void DoConvert(ItemContainer container, bool isFood, int ratio, int poolBefore)
    {
        if (_compostInfo == null) return;
        string kind = isFood ? "food" : "seeds";

        bool hasSpace = false;
        try { hasSpace = container.HasSpace(_compostInfo, 1); }
        catch (Exception ex) { Plugin.Logger.LogError($"[OuthouseComposter] HasSpace(compost) error: {ex}"); }

        if (!hasSpace)
        {
            if (Plugin.EnableDiagnostics.Value)
                Plugin.Logger.LogInfo($"[OuthouseComposter][convert] {kind}: skipped — container has no space for Compost (pool={poolBefore}).");
            return;
        }

        int remaining = ratio;
        try
        {
            var items = container.GetItems();
            int capacity = -1;
            try { capacity = container.capacity; } catch { }
            int bound = capacity > 0 ? capacity : 64;

            var matching = new List<Item>();
            for (int i = 0; i < bound; i++)
            {
                Item? item = null;
                try { item = items != null ? items[i] : null; } catch { break; }
                if (item == null) continue;
                ItemInfo? info = null;
                try { info = item.info; } catch { }
                if (info == null) continue;
                bool matches = isFood ? OuthouseGate.IsFood(info) : OuthouseGate.IsSeed(info);
                if (matches) matching.Add(item);
            }

            foreach (var item in matching)
            {
                if (remaining <= 0) break;
                int itemCount = 0;
                try { itemCount = item.count; } catch { }
                if (itemCount <= 0) continue;
                int take = Math.Min(remaining, itemCount);

                bool removed = false;
                try { removed = container.RemoveItem(item, take, ItemEventContext.Default); }
                catch (Exception ex) { Plugin.Logger.LogError($"[OuthouseComposter] RemoveItem error: {ex}"); }
                if (removed) remaining -= take;
            }
        }
        catch (Exception ex) { Plugin.Logger.LogError($"[OuthouseComposter] DoConvert removal walk error: {ex}"); }

        int taken = ratio - remaining;
        if (taken < ratio)
        {
            Plugin.Logger.LogWarning($"[OuthouseComposter][convert] {kind}: only removed {taken}/{ratio} unit(s) (pool was {poolBefore}) — partial removal; adding Compost anyway.");
        }

        int added = 0;
        try { added = container.AddItems(_compostInfo, 1); }
        catch (Exception ex) { Plugin.Logger.LogError($"[OuthouseComposter] AddItems(compost) error: {ex}"); }

        if (added == 0)
        {
            Plugin.Logger.LogWarning($"[OuthouseComposter][convert] {kind}: AddItems(Compost) returned 0 after removing {taken} unit(s) (pool was {poolBefore}) — Compost NOT added despite HasSpace=true. Known v0.2.0 limitation, accepted for now.");
        }
        else if (Plugin.EnableDiagnostics.Value)
        {
            int capacityNow = -1;
            try { capacityNow = container.capacity; } catch { }
            int emptySlots = -1;
            try { emptySlots = container.GetEmptySlots(); } catch { }
            int used = (capacityNow >= 0 && emptySlots >= 0) ? capacityNow - emptySlots : -1;
            Plugin.Logger.LogInfo($"[OuthouseComposter][convert] {kind}: -{taken} (pool was {poolBefore}) → +1 Compost (container now {used}/{capacityNow} slots).");
        }
    }

    // v0.4.0: "simultaneous" mode — every occupied matching slot is evaluated independently. A slot
    // converts on its own only if it individually holds >= a full ratio (no cross-slot pooling: two
    // below-ratio stacks never combine here, unlike DoConvert's sequential pooling). Snapshots
    // matching items before removing anything, same reason as DoConvert (RemoveItem may
    // reindex/compact the container mid-walk).
    private void DoConvertSimultaneous(ItemContainer container, bool isFood, int ratio, int poolBefore)
    {
        if (_compostInfo == null) return;
        string kind = isFood ? "food" : "seeds";

        var matching = new List<Item>();
        try
        {
            var items = container.GetItems();
            int capacity = -1;
            try { capacity = container.capacity; } catch { }
            int bound = capacity > 0 ? capacity : 64;

            for (int i = 0; i < bound; i++)
            {
                Item? item = null;
                try { item = items != null ? items[i] : null; } catch { break; }
                if (item == null) continue;
                ItemInfo? info = null;
                try { info = item.info; } catch { }
                if (info == null) continue;
                bool matches = isFood ? OuthouseGate.IsFood(info) : OuthouseGate.IsSeed(info);
                if (matches) matching.Add(item);
            }
        }
        catch (Exception ex) { Plugin.Logger.LogError($"[OuthouseComposter] DoConvertSimultaneous snapshot walk error: {ex}"); }

        int conversions = 0;
        foreach (var item in matching)
        {
            int count = 0;
            try { count = item.count; } catch { }
            if (count < ratio) continue;

            bool hasSpace = false;
            try { hasSpace = container.HasSpace(_compostInfo, 1); }
            catch (Exception ex) { Plugin.Logger.LogError($"[OuthouseComposter] HasSpace(compost) error: {ex}"); }

            if (!hasSpace)
            {
                if (Plugin.EnableDiagnostics.Value)
                    Plugin.Logger.LogInfo($"[OuthouseComposter][convert] {kind}: container full — stopped after {conversions} simultaneous conversion(s).");
                break;
            }

            bool removed = false;
            try { removed = container.RemoveItem(item, ratio, ItemEventContext.Default); }
            catch (Exception ex) { Plugin.Logger.LogError($"[OuthouseComposter] RemoveItem error: {ex}"); }
            if (!removed) continue;

            int added = 0;
            try { added = container.AddItems(_compostInfo, 1); }
            catch (Exception ex) { Plugin.Logger.LogError($"[OuthouseComposter] AddItems(compost) error: {ex}"); }

            if (added == 0)
            {
                Plugin.Logger.LogWarning($"[OuthouseComposter][convert] {kind}: AddItems(Compost) returned 0 after removing {ratio} unit(s) (pool was {poolBefore}) — Compost NOT added despite HasSpace=true. Known v0.2.0 limitation, accepted for now.");
            }

            conversions++;
        }

        if (Plugin.EnableDiagnostics.Value)
        {
            if (conversions > 0)
                Plugin.Logger.LogInfo($"[OuthouseComposter][convert] {kind}: simultaneous fire — {conversions} slot(s) converted, +{conversions} Compost (pool was {poolBefore}).");
            else
                Plugin.Logger.LogInfo($"[OuthouseComposter][convert] {kind}: simultaneous fire — no single slot held >= {ratio}, nothing converted (pool was {poolBefore}).");
        }
    }

    // Typing guard: copied from TimeWarpMod's IsTextInputFocused — ignore hotkeys while the
    // game's own text fields (e.g. structure rename) are focused (confirmed leak 2026-07-10).
    private static bool IsTextInputFocused()
    {
        try
        {
            var es = UnityEngine.EventSystems.EventSystem.current;
            var go = es != null ? es.currentSelectedGameObject : null;
            if (go == null) return false;
            return go.GetComponent<TMPro.TMP_InputField>() != null
                || go.GetComponent<UnityEngine.UI.InputField>() != null;
        }
        catch { return false; }
    }

    // ── The dump ─────────────────────────────────────────────────────────────────────────────
    // Returns the number of structures that matched Plugin.StructureNameMatch, so the caller can
    // decide whether to keep retrying the auto-dump.
    private int DumpAll(string trigger)
    {
        Plugin.Logger.LogInfo($"[OuthouseComposter] === Dump start (trigger={trigger}) ===");

        var structures = ResolveStructures();
        Plugin.Logger.LogInfo($"[OuthouseComposter] Resolved {structures.Count} structures.");

        if (structures.Count > 0 && !_loggedCensusThisSession)
        {
            LogStructureCensus(structures);
            _loggedCensusThisSession = true;
        }

        string filter = Plugin.StructureNameMatch.Value ?? "";
        var matches = new List<Structure>();
        foreach (var s in structures)
        {
            if (StructureMatches(s, filter)) matches.Add(s);
        }

        Plugin.Logger.LogInfo($"[OuthouseComposter] Match count for '{filter}': {matches.Count}");

        foreach (var s in matches)
        {
            string posStr = "?";
            try { posStr = s.GetPosition().ToString(); } catch { }
            string dispName = SafeName(s);
            string defName = SafeDefaultName(s);
            Plugin.Logger.LogInfo($"[OuthouseComposter] Match: display='{dispName}' default='{defName}' pos={posStr}");

            try { DumpStructureComponents(s); }
            catch (Exception ex) { Plugin.Logger.LogError($"[OuthouseComposter] DumpStructureComponents error: {ex}"); }
        }

        try { DumpContainerTypeUniquenessScan(structures); }
        catch (Exception ex) { Plugin.Logger.LogError($"[OuthouseComposter] DumpContainerTypeUniquenessScan error: {ex}"); }

        try { DumpCompostSources(); }
        catch (Exception ex) { Plugin.Logger.LogError($"[OuthouseComposter] DumpCompostSources error: {ex}"); }

        Plugin.Logger.LogInfo($"[OuthouseComposter] === Dump end (trigger={trigger}) ===");
        return matches.Count;
    }

    // ── Settlement/structure walk (StructureQuery.cs proven recipe) ────────────────────────────
    private static List<Structure> ResolveStructures()
    {
        var result = new List<Structure>();
        try
        {
            var sm = UnityEngine.Object.FindAnyObjectByType<SettlementManager>();
            if (sm == null) return result;

            // Settlements via the getter methods — the raw `settlements` list accessor stays
            // null even in a loaded world (project-wide gotcha).
            var candidates = new List<Settlement>();
            try { var s = sm.GetPlayerSettlement(); if (s != null) candidates.Add(s); } catch { }
            try { var s = sm.GetCurrentSettlement(); if (s != null) candidates.Add(s); } catch { }
            try { var s = sm.worldSettlement; if (s != null) candidates.Add(s); } catch { }

            var seen = new HashSet<string>();
            foreach (var settlement in candidates)
            {
                if (settlement == null) continue;
                Il2CppSystem.Collections.Generic.List<Structure> structures;
                try { structures = settlement.GetStructures(); } catch { continue; }
                if (structures == null) continue;
                foreach (var st in structures)
                {
                    if (st == null) continue;
                    string key;
                    try { key = st.GetPosition().ToString(); } catch { continue; }
                    if (!seen.Add(key)) continue; // same building reachable via multiple settlement accessors
                    result.Add(st);
                }
            }
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"[OuthouseComposter] ResolveStructures error: {ex}");
        }
        return result;
    }

    private static void LogStructureCensus(List<Structure> structures)
    {
        var seen = new HashSet<string>();
        Plugin.Logger.LogInfo("[OuthouseComposter] --- One-shot structure name census ---");
        foreach (var s in structures)
        {
            string disp = SafeName(s);
            string def = SafeDefaultName(s);
            string key = disp + "|" + def;
            if (!seen.Add(key)) continue;
            Plugin.Logger.LogInfo($"[OuthouseComposter][census] display='{disp}' default='{def}'");
        }
        Plugin.Logger.LogInfo($"[OuthouseComposter] --- Census end ({seen.Count} distinct names) ---");
    }

    // DefaultName is the pristine type name (survives player renames — DVN-proven); StructureName/
    // GetName() is the display name.
    private static string SafeName(Structure s)
    {
        try { var n = s.GetName(); if (!string.IsNullOrEmpty(n)) return n; } catch { }
        try { var n = s.StructureName; if (!string.IsNullOrEmpty(n)) return n; } catch { }
        return "?";
    }

    private static string SafeDefaultName(Structure s)
    {
        try { return s.DefaultName ?? "?"; } catch { return "?"; }
    }

    private static bool StructureMatches(Structure s, string filter)
    {
        if (string.IsNullOrEmpty(filter)) return false;
        string disp = SafeName(s);
        string def = SafeDefaultName(s);
        return disp.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0
            || def.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    // ── Per-structure component census ──────────────────────────────────────────────────────
    private void DumpStructureComponents(Structure s)
    {
        Transform root;
        try { root = s.transform; } catch (Exception ex) { Plugin.Logger.LogError($"[OuthouseComposter] structure.transform error: {ex}"); return; }

        var containers = new List<ItemContainerComponent>();
        var storageInteractions = new List<StorageInteraction>();
        WalkNode(root, containers, storageInteractions);

        Plugin.Logger.LogInfo($"[OuthouseComposter]   ItemContainerComponent count: {containers.Count}, StorageInteraction count: {storageInteractions.Count}");
        foreach (var icc in containers)
        {
            try { DumpContainer(icc); }
            catch (Exception ex) { Plugin.Logger.LogError($"[OuthouseComposter] DumpContainer error: {ex}"); }
        }

        try
        {
            DumpAcceptanceProbe(
                containers.Count > 0 ? containers[0] : null,
                storageInteractions.Count > 0 ? storageInteractions[0] : null);
        }
        catch (Exception ex) { Plugin.Logger.LogError($"[OuthouseComposter] DumpAcceptanceProbe error: {ex}"); }
    }

    // Manual hierarchy walk — the plural generic GetComponentsInChildren<T> is missing through the
    // interop trampoline (project-wide gotcha); per-node GetComponent<T>() + child recursion is the
    // proven replacement (StructureQuery.CollectColliders / WellRefill pattern).
    //
    // v0.1.0 tried the non-generic plural GameObject.GetComponents(typeof(Component)) first — CONFIRMED
    // DEAD END in-game 2026-07-11: System.MissingMethodException ("Method not found:
    // UnityEngine.Component[] UnityEngine.GameObject.GetComponents(System.Type)") escaped the walk and
    // aborted the whole per-structure dump (the exception unwound past WalkNode's own try/catch into
    // DumpStructureComponents's catch, so NO ItemContainer details were logged at all that run). v0.1.1
    // removes that call entirely — do not retry variants of it — and replaces broad enumeration with
    // three independently-try/caught, KNOWN-WORKING targeted probes per node (base-typed
    // GetComponent<T>() correctly returns the derived instance when one is present — same pattern as
    // MineRefreshMod/WellRefill's per-node lookups):
    //   - ItemContainerComponent — the primary target; runs the full container detail dump on a hit.
    //   - Fusion.NetworkBehaviour — catches any Fusion-networked component (e.g. a
    //     NetworkSimpleResourceStorage_N) so its NATIVE class name (via il2cpp_object_get_class) can
    //     reveal a baked Fusion slot count.
    //   - SSSGame.Interaction — catches any interaction component (base class confirmed via Cecil dump
    //     of Assembly-CSharp.dll: FarmCropInteraction/GatherInteraction/StorageInteraction etc. all
    //     derive SSSGame.Interaction).
    // Each probe is in its own try/catch so one failure can never abort the walk or the dump — the
    // exact bug this rewrite fixes.
    //
    // v0.1.2: when the Interaction probe's native class name is exactly "StorageInteraction", also
    // rewrap it via the pointer (managed casts lie for interop objects materialized under a base
    // declared type — Interaction here) and collect it, so the acceptance-gate probe can call its
    // Check(ItemInfo) alongside the container's own gates.
    private void WalkNode(Transform node, List<ItemContainerComponent> containers, List<StorageInteraction> storageInteractions)
    {
        if (node == null) return;

        try
        {
            var icc = node.GetComponent<ItemContainerComponent>();
            if (icc != null)
            {
                containers.Add(icc);
                Plugin.Logger.LogInfo($"[OuthouseComposter][component] node='{SafeGoName(node)}' component=ItemContainerComponent");
            }
        }
        catch (Exception ex) { Plugin.Logger.LogError($"[OuthouseComposter] GetComponent<ItemContainerComponent> probe error: {ex}"); }

        try
        {
            var nb = node.GetComponent<Fusion.NetworkBehaviour>();
            if (nb != null)
            {
                string clsName = NativeOrManagedClassName(nb, out _);
                Plugin.Logger.LogInfo($"[OuthouseComposter][component] node='{SafeGoName(node)}' component={clsName}");
            }
        }
        catch (Exception ex) { Plugin.Logger.LogError($"[OuthouseComposter] GetComponent<Fusion.NetworkBehaviour> probe error: {ex}"); }

        try
        {
            var ia = node.GetComponent<SSSGame.Interaction>();
            if (ia != null)
            {
                string clsName = NativeOrManagedClassName(ia, out var iaPtr);
                Plugin.Logger.LogInfo($"[OuthouseComposter][component] node='{SafeGoName(node)}' component={clsName}");

                if (clsName == "StorageInteraction" && iaPtr != IntPtr.Zero)
                {
                    try { storageInteractions.Add(new StorageInteraction(iaPtr)); }
                    catch (Exception ex) { Plugin.Logger.LogError($"[OuthouseComposter] StorageInteraction rewrap error: {ex}"); }
                }
            }
        }
        catch (Exception ex) { Plugin.Logger.LogError($"[OuthouseComposter] GetComponent<SSSGame.Interaction> probe error: {ex}"); }

        int childCount;
        try { childCount = node.childCount; } catch { return; }
        for (int i = 0; i < childCount; i++)
        {
            Transform? child = null;
            try { child = node.GetChild(i); } catch { }
            if (child != null) WalkNode(child, containers, storageInteractions);
        }
    }

    // Lightweight variant used by the settlement-wide containerType uniqueness scan — collects
    // ItemContainerComponents only, without the per-node Fusion/Interaction log lines (which would be
    // extremely spammy across every structure in the settlement; those are reserved for the matched
    // outhouse structure(s) via WalkNode above).
    private static void CollectContainersOnly(Transform node, List<ItemContainerComponent> containers)
    {
        if (node == null) return;

        try
        {
            var icc = node.GetComponent<ItemContainerComponent>();
            if (icc != null) containers.Add(icc);
        }
        catch { }

        int childCount;
        try { childCount = node.childCount; } catch { return; }
        for (int i = 0; i < childCount; i++)
        {
            Transform? child = null;
            try { child = node.GetChild(i); } catch { }
            if (child != null) CollectContainersOnly(child, containers);
        }
    }

    // ── containerType uniqueness scan (v0.1.1, settlement-wide) ─────────────────────────────────
    // Walks EVERY structure in the settlement (not just the outhouse match), collects each one's
    // ItemContainerComponents, and groups by containerType asset name so we can see whether the
    // outhouse's ItemContainerType ScriptableObject is shared with other buildings (⇒ needs a
    // per-instance patch, an asset-edit would affect every building using it) or unique to the
    // outhouse (⇒ a direct asset edit is safe for Phase 1).
    private void DumpContainerTypeUniquenessScan(List<Structure> structures)
    {
        Plugin.Logger.LogInfo("[OuthouseComposter] --- containerType uniqueness scan (all structures) ---");

        // Keyed by containerType asset NAME (native ScriptableObject identity isn't reliably
        // comparable across wrapper instances through this interop layer) — good enough for "is this
        // asset shared" recon. Keeps one representative ItemContainerType reference per key so its
        // storageClasses can be logged once.
        var byType = new Dictionary<string, int>();               // typeName -> capacity (first seen)
        var usedBy = new Dictionary<string, HashSet<string>>();    // typeName -> distinct structure display names
        var representative = new Dictionary<string, ItemContainerType>();

        foreach (var s in structures)
        {
            try
            {
                Transform root;
                try { root = s.transform; } catch { continue; }

                var containers = new List<ItemContainerComponent>();
                CollectContainersOnly(root, containers);
                if (containers.Count == 0) continue;

                string structDisp = SafeName(s);

                foreach (var icc in containers)
                {
                    ItemContainer? container = null;
                    try { container = icc.container; } catch { }
                    if (container == null) continue;

                    ItemContainerType? ct = null;
                    try { ct = container.containerType; } catch { }
                    string typeName = SafeAssetName(ct);

                    int capacity = -1;
                    try { capacity = container.capacity; } catch { }

                    if (!byType.ContainsKey(typeName))
                    {
                        byType[typeName] = capacity;
                        usedBy[typeName] = new HashSet<string>();
                        if (ct != null) representative[typeName] = ct;
                    }
                    usedBy[typeName].Add(structDisp);
                }
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError($"[OuthouseComposter] uniqueness scan per-structure error: {ex}");
            }
        }

        Plugin.Logger.LogInfo($"[OuthouseComposter] {byType.Count} distinct containerType(s) found across the settlement.");
        foreach (var kv in byType)
        {
            string typeName = kv.Key;
            int capacity = kv.Value;
            var names = usedBy.TryGetValue(typeName, out var n) ? n : new HashSet<string>();
            Plugin.Logger.LogInfo($"[OuthouseComposter]   containerType='{typeName}' capacity={capacity} usedBy=[{string.Join(", ", names)}]");

            if (representative.TryGetValue(typeName, out var ct) && ct != null)
            {
                try
                {
                    var classes = ct.storageClasses;
                    int cn = classes != null ? classes.Count : 0;
                    for (int i = 0; i < cn; i++)
                    {
                        try
                        {
                            var sc = classes![i];
                            string scName = sc.storageClass != null ? SafeAssetName(sc.storageClass) : "null";
                            Plugin.Logger.LogInfo($"[OuthouseComposter]     storageClass[{i}]='{scName}' stackSize={sc.stackSize}");
                        }
                        catch (Exception ex) { Plugin.Logger.LogError($"[OuthouseComposter] uniqueness storageClasses[{i}] error: {ex}"); }
                    }
                }
                catch (Exception ex) { Plugin.Logger.LogError($"[OuthouseComposter] uniqueness storageClasses error: {ex}"); }
            }
        }

        Plugin.Logger.LogInfo("[OuthouseComposter] --- containerType uniqueness scan end ---");
    }

    private static string SafeGoName(Transform t)
    {
        try { return t.gameObject.name ?? "?"; } catch { return "?"; }
    }

    // Managed casts LIE for interop objects materialized under a base declared type (project-wide
    // gotcha) — always read the NATIVE class name via IL2CPP.il2cpp_object_get_class, never as/is.
    // Falls back to the managed wrapper's own type name for non-Il2Cpp objects.
    private static string NativeOrManagedClassName(object obj, out IntPtr pointer)
    {
        pointer = IntPtr.Zero;
        if (obj == null) return "null";
        try
        {
            if (obj is Il2CppObjectBase b)
            {
                pointer = b.Pointer;
                IntPtr cls = IL2CPP.il2cpp_object_get_class(pointer);
                return Marshal.PtrToStringAnsi(IL2CPP.il2cpp_class_get_name(cls)) ?? "?";
            }
        }
        catch { }
        return obj.GetType().Name;
    }

    // ── Container details ────────────────────────────────────────────────────────────────────
    private void DumpContainer(ItemContainerComponent icc)
    {
        ItemContainer? container = null;
        try { container = icc.container; } catch (Exception ex) { Plugin.Logger.LogError($"[OuthouseComposter] icc.container error: {ex}"); }
        if (container == null)
        {
            Plugin.Logger.LogInfo("[OuthouseComposter]   ItemContainerComponent has null container.");
            return;
        }

        int capacity = -1;
        try { capacity = container.capacity; } catch (Exception ex) { Plugin.Logger.LogError($"[OuthouseComposter] capacity error: {ex}"); }

        bool isHidden = false;
        try { isHidden = container.IsHidden; } catch (Exception ex) { Plugin.Logger.LogError($"[OuthouseComposter] IsHidden error: {ex}"); }

        bool noActionTargets = false;
        try { noActionTargets = container.NoActionTargets; } catch (Exception ex) { Plugin.Logger.LogError($"[OuthouseComposter] NoActionTargets error: {ex}"); }

        ItemContainerType? ct = null;
        string containerTypeName = "?";
        try
        {
            ct = container.containerType;
            containerTypeName = SafeAssetName(ct);
        }
        catch (Exception ex) { Plugin.Logger.LogError($"[OuthouseComposter] containerType error: {ex}"); }

        Plugin.Logger.LogInfo($"[OuthouseComposter]   Container: capacity={capacity} IsHidden={isHidden} NoActionTargets={noActionTargets} containerType='{containerTypeName}'");

        if (ct != null)
        {
            try
            {
                bool overrideStackSizes = false;
                try { overrideStackSizes = ct.overrideStackSizes; } catch { }
                bool overrideWithMaxSize = false;
                try { overrideWithMaxSize = ct.overrideWithMaxSize; } catch { }
                Plugin.Logger.LogInfo($"[OuthouseComposter]     overrideStackSizes={overrideStackSizes} overrideWithMaxSize={overrideWithMaxSize}");
            }
            catch (Exception ex) { Plugin.Logger.LogError($"[OuthouseComposter] override flags error: {ex}"); }

            try
            {
                var classes = ct.storageClasses;
                int n = classes != null ? classes.Count : 0;
                Plugin.Logger.LogInfo($"[OuthouseComposter]     storageClasses: {n} entries");
                for (int i = 0; i < n; i++)
                {
                    try
                    {
                        var sc = classes![i];
                        string scName = sc.storageClass != null ? SafeAssetName(sc.storageClass) : "null";
                        Plugin.Logger.LogInfo($"[OuthouseComposter]       [{i}] storageClass='{scName}' stackSize={sc.stackSize}");
                    }
                    catch (Exception ex) { Plugin.Logger.LogError($"[OuthouseComposter] storageClasses[{i}] error: {ex}"); }
                }
            }
            catch (Exception ex) { Plugin.Logger.LogError($"[OuthouseComposter] storageClasses error: {ex}"); }
        }

        // Contents. GetItems() returns Il2CppSystem.Collections.Generic.IReadOnlyList<Item> — through
        // our compile-time stub this interop wrapper exposes only an indexer (get_Item), no Count and
        // no enumerator, so we bound the scan by the container's own capacity (or a fallback cap) and
        // stop early if the indexer throws (past the real slot count).
        try
        {
            var items = container.GetItems();
            int bound = capacity > 0 ? capacity : 64;
            int found = 0;
            var seenInfoNames = new HashSet<string>();
            var logLines = new List<string>();
            for (int i = 0; i < bound; i++)
            {
                Item? item = null;
                try { item = items != null ? items[i] : null; }
                catch { break; } // past the real slot count
                if (item == null) continue;
                found++;

                ItemInfo? info = null;
                try { info = item.info; } catch (Exception ex) { Plugin.Logger.LogError($"[OuthouseComposter] item.info error: {ex}"); }
                int count = 0;
                try { count = item.count; } catch (Exception ex) { Plugin.Logger.LogError($"[OuthouseComposter] item.count error: {ex}"); }

                string infoName = info != null ? SafeItemName(info) : "?";
                logLines.Add($"[OuthouseComposter]     item[{i}]: '{infoName}' count={count}");

                if (info != null && seenInfoNames.Add(infoName))
                {
                    try { DumpItemInfoDecay(info); }
                    catch (Exception ex) { Plugin.Logger.LogError($"[OuthouseComposter] DumpItemInfoDecay error: {ex}"); }
                }
            }

            Plugin.Logger.LogInfo($"[OuthouseComposter]   Contents: {found} item(s) (scanned bound={bound})");
            foreach (var line in logLines) Plugin.Logger.LogInfo(line);
        }
        catch (Exception ex) { Plugin.Logger.LogError($"[OuthouseComposter] GetItems error: {ex}"); }
    }

    // ── Acceptance-gate probe (v0.1.2) ──────────────────────────────────────────────────────────
    // The puzzle: raw-food shelves (containerType 'Storage_SmallItems_FoodShelf1') ALSO list
    // storageClass 'SmallItem' — the same asset NAME as the outhouse's 'Storage_SmallItems_Outhouse'
    // containerType — yet the outhouse UI refuses fresh food while accepting Spoiled Food. Since
    // ItemStorageClass is an empty ScriptableObject (Cecil-confirmed: no fields beyond its base), any
    // real acceptance check must compare ASSET REFERENCES, not names — two 'SmallItem'-named assets
    // could be different objects. This probe logs, per item, every gate the game could plausibly use
    // (ItemContainer.CanStoreItemType/Check/GetStackSize/HasSpace, StorageInteraction.Check) AND each
    // item's storageClass native POINTER next to the containerType's own storageClass pointer(s) —
    // matching pointers ⇒ same asset (gate is elsewhere); matching NAMES but different pointers ⇒
    // duplicate-named assets (the class-list membership check itself is the gate).
    private void DumpAcceptanceProbe(ItemContainerComponent? icc, StorageInteraction? si)
    {
        if (Plugin.LocalPlayer == null)
        {
            Plugin.Logger.LogInfo("[OuthouseComposter] Acceptance probe skipped: LocalPlayer not resolved.");
            return;
        }

        ItemContainer? container = null;
        try { container = icc?.container; } catch (Exception ex) { Plugin.Logger.LogError($"[OuthouseComposter] probe icc.container error: {ex}"); }
        if (container == null)
        {
            Plugin.Logger.LogInfo("[OuthouseComposter] Acceptance probe skipped: outhouse container not resolved.");
            return;
        }

        bool canAddItems = false;
        try { canAddItems = container.canAddItems; } catch (Exception ex) { Plugin.Logger.LogError($"[OuthouseComposter] probe canAddItems error: {ex}"); }
        Plugin.Logger.LogInfo($"[OuthouseComposter] Acceptance probe: container.canAddItems={canAddItems} storageInteraction={(si != null ? "found" : "n/a")}");

        // containerType storageClasses with native pointer identity.
        try
        {
            ItemContainerType? ct = null;
            try { ct = container.containerType; } catch { }
            if (ct != null)
            {
                var classes = ct.storageClasses;
                int cn = classes != null ? classes.Count : 0;
                for (int i = 0; i < cn; i++)
                {
                    try
                    {
                        var sc = classes![i];
                        string scName = sc.storageClass != null ? SafeAssetName(sc.storageClass) : "null";
                        string scPtr = PointerHex(sc.storageClass);
                        Plugin.Logger.LogInfo($"[OuthouseComposter]   [probe] outhouse containerType storageClass[{i}]='{scName}' ptr={scPtr}");
                    }
                    catch (Exception ex) { Plugin.Logger.LogError($"[OuthouseComposter] probe containerType.storageClasses[{i}] error: {ex}"); }
                }
            }
        }
        catch (Exception ex) { Plugin.Logger.LogError($"[OuthouseComposter] probe containerType.storageClasses error: {ex}"); }

        // Distinct ItemInfos from the player's inventory.
        var playerItems = new List<ItemInfo>();
        try
        {
            var inv = Plugin.LocalPlayer.Inventory;
            var coll = inv != null ? inv.GetItemCollection() : null;
            var infos = coll != null ? coll.GetItemInfos() : null;
            int n = infos != null ? infos.Count : 0;
            for (int i = 0; i < n; i++)
            {
                try { var info = infos![i]; if (info != null) playerItems.Add(info); }
                catch (Exception ex) { Plugin.Logger.LogError($"[OuthouseComposter] probe player inventory[{i}] error: {ex}"); }
            }
        }
        catch (Exception ex) { Plugin.Logger.LogError($"[OuthouseComposter] probe player inventory resolve error: {ex}"); }
        Plugin.Logger.LogInfo($"[OuthouseComposter] Acceptance probe: {playerItems.Count} distinct item(s) in player inventory.");

        // Distinct ItemInfos currently in the outhouse container.
        var containerItems = new List<ItemInfo>();
        try
        {
            var items = container.GetItems();
            int capacity = -1;
            try { capacity = container.capacity; } catch { }
            int bound = capacity > 0 ? capacity : 64;
            var seenNames = new HashSet<string>();
            for (int i = 0; i < bound; i++)
            {
                Item? item = null;
                try { item = items != null ? items[i] : null; } catch { break; }
                if (item == null) continue;
                ItemInfo? info = null;
                try { info = item.info; } catch { }
                if (info == null) continue;
                if (seenNames.Add(SafeItemName(info))) containerItems.Add(info);
            }
        }
        catch (Exception ex) { Plugin.Logger.LogError($"[OuthouseComposter] probe outhouse contents resolve error: {ex}"); }
        Plugin.Logger.LogInfo($"[OuthouseComposter] Acceptance probe: {containerItems.Count} distinct item(s) currently in outhouse container.");

        // Probe the union (player inventory ∪ outhouse contents), deduped by name.
        var probeSet = new List<ItemInfo>();
        var probedNames = new HashSet<string>();
        foreach (var info in playerItems) { if (probedNames.Add(SafeItemName(info))) probeSet.Add(info); }
        foreach (var info in containerItems) { if (probedNames.Add(SafeItemName(info))) probeSet.Add(info); }

        foreach (var info in probeSet)
        {
            string name = SafeItemName(info);

            string classAssetName = "?";
            string classPtr = "n/a";
            try
            {
                var sc = info.storageClass;
                classAssetName = sc != null ? SafeAssetName(sc) : "null";
                classPtr = PointerHex(sc);
            }
            catch (Exception ex) { classAssetName = "ERR"; Plugin.Logger.LogError($"[OuthouseComposter] probe item.storageClass error ({name}): {ex}"); }

            string canStore = "ERR";
            try { canStore = container.CanStoreItemType(info).ToString(); }
            catch (Exception ex) { Plugin.Logger.LogError($"[OuthouseComposter] probe CanStoreItemType error ({name}): {ex}"); }

            string check = "ERR";
            try { check = container.Check(info).ToString(); }
            catch (Exception ex) { Plugin.Logger.LogError($"[OuthouseComposter] probe container.Check error ({name}): {ex}"); }

            string stack = "ERR";
            try { stack = container.GetStackSize(info).ToString(); }
            catch (Exception ex) { Plugin.Logger.LogError($"[OuthouseComposter] probe GetStackSize error ({name}): {ex}"); }

            string hasSpace1 = "ERR";
            try { hasSpace1 = container.HasSpace(info, 1).ToString(); }
            catch (Exception ex) { Plugin.Logger.LogError($"[OuthouseComposter] probe HasSpace error ({name}): {ex}"); }

            string siCheck = "n/a";
            if (si != null)
            {
                try { siCheck = si.Check(info).ToString(); }
                catch (Exception ex) { siCheck = "ERR"; Plugin.Logger.LogError($"[OuthouseComposter] probe storageInteraction.Check error ({name}): {ex}"); }
            }

            Plugin.Logger.LogInfo($"[OuthouseComposter]   [probe] item='{name}' class='{classAssetName}' classPtr={classPtr} canStore={canStore} check={check} stack={stack} hasSpace1={hasSpace1} siCheck={siCheck}");
        }
    }

    // Native pointer of an interop ScriptableObject/asset, hex-formatted for identity comparison
    // (managed casts lie for interop objects materialized under a base declared type — box to
    // Il2CppObjectBase first, same pattern as NativeOrManagedClassName).
    private static string PointerHex(UnityEngine.Object? o)
    {
        if (o == null) return "n/a";
        try
        {
            if ((object)o is Il2CppObjectBase b) return "0x" + b.Pointer.ToString("X");
        }
        catch { }
        return "n/a";
    }

    // Managed casts LIE for interop objects materialized under a base declared type — never as/is;
    // rewrap via the native class name only when it's exactly "ResourceInfo".
    private void DumpItemInfoDecay(ItemInfo info)
    {
        string clsName = NativeOrManagedClassName(info, out var ptr);
        if (clsName != "ResourceInfo" || ptr == IntPtr.Zero) return;

        try
        {
            var ri = new ResourceInfo(ptr);

            string scName = "?";
            try { scName = ri.storageClass != null ? SafeAssetName(ri.storageClass) : "null"; }
            catch (Exception ex) { Plugin.Logger.LogError($"[OuthouseComposter] ResourceInfo.storageClass error: {ex}"); }

            var junk = ri.junk;
            int jn = junk != null ? junk.Count : 0;
            Plugin.Logger.LogInfo($"[OuthouseComposter]     [decay] ResourceInfo '{SafeItemName(info)}' storageClass='{scName}' junkEntries={jn}");

            for (int i = 0; i < jn; i++)
            {
                try
                {
                    var jq = junk![i];
                    string jName = jq.itemInfo != null ? SafeItemName(jq.itemInfo) : "?";
                    Plugin.Logger.LogInfo($"[OuthouseComposter]       junk[{i}]: '{jName}' x{jq.quantity}");
                }
                catch (Exception ex) { Plugin.Logger.LogError($"[OuthouseComposter] junk[{i}] error: {ex}"); }
            }
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"[OuthouseComposter] ResourceInfo rewrap error: {ex}");
        }
    }

    private static string SafeAssetName(UnityEngine.Object? o)
    {
        if (o == null) return "null";
        try { return o.name ?? "?"; } catch { return "?"; }
    }

    private static string SafeItemName(ItemInfo info)
    {
        try { return info.Name ?? "?"; } catch { return "?"; }
    }

    // ── Native compost item discovery ───────────────────────────────────────────────────────
    private void DumpCompostSources()
    {
        try
        {
            var fs = UnityEngine.Object.FindAnyObjectByType<FarmingStation>();
            if (fs == null)
            {
                Plugin.Logger.LogInfo("[OuthouseComposter] No FarmingStation found.");
            }
            else
            {
                ItemInfoList? composts = null;
                try { composts = fs.composts; } catch (Exception ex) { Plugin.Logger.LogError($"[OuthouseComposter] FarmingStation.composts error: {ex}"); }
                LogItemInfoList("FarmingStation.composts", composts);
            }
        }
        catch (Exception ex) { Plugin.Logger.LogError($"[OuthouseComposter] FarmingStation lookup error: {ex}"); }

        try
        {
            var fci = UnityEngine.Object.FindAnyObjectByType<FarmCropInteraction>();
            if (fci == null)
            {
                Plugin.Logger.LogInfo("[OuthouseComposter] No FarmCropInteraction found.");
            }
            else
            {
                ItemInfoList? compat = null;
                try { compat = fci.compatibleComposts; } catch (Exception ex) { Plugin.Logger.LogError($"[OuthouseComposter] FarmCropInteraction.compatibleComposts error: {ex}"); }
                LogItemInfoList("FarmCropInteraction.compatibleComposts", compat);
            }
        }
        catch (Exception ex) { Plugin.Logger.LogError($"[OuthouseComposter] FarmCropInteraction lookup error: {ex}"); }
    }

    private static void LogItemInfoList(string label, ItemInfoList? list)
    {
        if (list == null) { Plugin.Logger.LogInfo($"[OuthouseComposter] {label}: null"); return; }

        Il2CppSystem.Collections.Generic.List<ItemInfo>? items = null;
        try { items = list.itemInfoList; } catch (Exception ex) { Plugin.Logger.LogError($"[OuthouseComposter] {label}.itemInfoList error: {ex}"); }
        if (items == null) { Plugin.Logger.LogInfo($"[OuthouseComposter] {label}: itemInfoList null"); return; }

        int n = items.Count;
        Plugin.Logger.LogInfo($"[OuthouseComposter] {label}: {n} entries");
        for (int i = 0; i < n; i++)
        {
            try
            {
                var info = items[i];
                string name = info != null ? SafeItemName(info) : "null";
                Plugin.Logger.LogInfo($"[OuthouseComposter]   [{i}] '{name}'");
            }
            catch (Exception ex) { Plugin.Logger.LogError($"[OuthouseComposter] {label}[{i}] error: {ex}"); }
        }
    }
}
