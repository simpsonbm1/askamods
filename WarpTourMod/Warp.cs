using System;
using System.Collections.Generic;
using System.Linq;
using Il2CppInterop.Runtime;
using SandSailorStudio.Streaming;
using SandSailorStudio.WorldGen;
using SSSGame;
using SSSGame.Controllers;
using UnityEngine;

namespace WarpTourMod;

// The whole mod, as a plain static driven by WarpTracker.Update(). Flow per world:
//   1. capture caves from the area map (immediate) + their streaming tile ids;
//   2. force-load the tiles around each cave so dens/lakes stream in (seeds the route);
//   3. teleport-tour every POI (cave/hostile/lake) so the game discovers them -> native pins;
//   4. return to spawn, keeping HP topped through the landings.
// All gated by the master switch Plugin.Enabled.
internal static class Warp
{
    private static float _hbTimer;
    private static bool _cavesCollected;
    private static Vector3 _spawnPos;
    private static bool _haveSpawn;
    private static bool _loggedError;
    private static readonly List<Vector2> _caves = new();

    // Force-load state.
    private static readonly List<CaveTile> _caveTiles = new();
    private static readonly List<GameObject> _reqGos = new();
    private static int _reqId = 2000;
    private static bool _forceLoadDone;
    private static float _settleTimer;

    // Tour state.
    private enum TourPhase { Idle, Teleporting, Dwell, Draining, Returning, Done }
    private static TourPhase _tour = TourPhase.Idle;
    private static readonly List<Vector2> _visited = new();
    private static int _tourCount;
    private static float _tourTimer;
    private static float _tourStartTimer;
    private static float _tourDrainTimer;
    private static float _tourHp = -1f;
    private static float _postTourTimer;
    private static PlayerDrive? _drive;
    private static Vector3 _tourReturn;
    private const float TourMaxWait = 6f;
    private const float TourPostGuard = 10f;
    private const int TourMaxStops = 80;

    private struct CaveTile
    {
        public Vector2 Pos;
        public WorldTileId Id;
        public CaveTile(Vector2 pos, WorldTileId id) { Pos = pos; Id = id; }
    }

    internal static void Tick()
    {
        // Master switch off -> stay completely dormant (don't touch anything).
        if (!SafeEnabled())
        {
            if (_cavesCollected || _forceLoadDone || _tour != TourPhase.Idle) ResetAll();
            return;
        }

        _hbTimer += Time.deltaTime;
        bool heartbeat = _hbTimer >= 5f;
        if (heartbeat) _hbTimer = 0f;

        var biomes = Plugin.Biomes;
        if (biomes == null) { ResetAll(); return; }

        WorldGenerator? worldGen = null;
        WorldDataMap? map = null;
        Vector3 playerPos = Vector3.zero;
        bool havePlayer = false;
        try
        {
            worldGen = biomes._worldGenerator;
            var pt = biomes._localPlayerTransform;
            if (pt != null) { playerPos = pt.position; havePlayer = true; }
            if (worldGen != null && worldGen.WorldDataReady()) map = worldGen.GetDataMap();
        }
        catch (Exception e) { LogErrorOnce("reading world state", e); }

        if (havePlayer && !_haveSpawn) { _spawnPos = playerPos; _haveSpawn = true; }

        if (heartbeat)
            Plugin.Logger.LogInfo($"WarpTour hb: map={(map == null ? "null" : "ok")} caves={_caves.Count} " +
                                  $"hostiles={Plugin.Hostiles.Count} lakes={Plugin.Lakes.Count} tour={_tour} " +
                                  $"visited={_tourCount} player={(havePlayer ? playerPos.ToString("0.#") : "none")}");

        if (map == null || !_haveSpawn) return;

        if (!_cavesCollected)
        {
            try { CollectCaves(map); _cavesCollected = true; }
            catch (Exception e) { LogErrorOnce("collect caves", e); }
        }

        if (_cavesCollected && !_forceLoadDone && Plugin.ForceLoadTiles.Value)
        {
            _settleTimer += Time.deltaTime;
            if (_settleTimer >= 3f)
            {
                _forceLoadDone = true;
                try { ForceLoad(); }
                catch (Exception e) { LogErrorOnce("force-load", e); }
            }
        }
        else if (_cavesCollected && !Plugin.ForceLoadTiles.Value)
        {
            _forceLoadDone = true; // skip force-load; tour will discover as it goes
        }

        if (_forceLoadDone)
        {
            if (_tour != TourPhase.Done)
            {
                try { TourTick(); }
                catch (Exception e) { _tour = TourPhase.Done; _postTourTimer = TourPostGuard; LogErrorOnce("tour", e); }
            }
            if ((_tour != TourPhase.Idle && _tour != TourPhase.Done) || _postTourTimer > 0f) ProtectHp();
            if (_postTourTimer > 0f) _postTourTimer -= Time.deltaTime;
        }
    }

    private static bool SafeEnabled()
    {
        try { return Plugin.Enabled.Value; } catch { return false; }
    }

    private static void ResetAll()
    {
        _cavesCollected = false; _haveSpawn = false; _caves.Clear(); _caveTiles.Clear();
        _forceLoadDone = false; _settleTimer = 0f; DestroyReqGos();
        _tour = TourPhase.Idle; _visited.Clear(); _tourCount = 0; _tourTimer = 0f; _tourStartTimer = 0f;
        _tourDrainTimer = 0f; _tourHp = -1f; _postTourTimer = 0f; _drive = null;
        Plugin.Hostiles.Clear(); Plugin.Lakes.Clear();
    }

    // ---- Caves -------------------------------------------------------------------------------------
    // Caves are fully known at world-load; pull their world (x,z) + streaming tile id from the area map.
    private static void CollectCaves(WorldDataMap map)
    {
        _caves.Clear();
        _caveTiles.Clear();
        int total = 0;
        var dict = map._areaInstances;
        var en = dict.Values.GetEnumerator();
        while (en.MoveNext())
        {
            var a = en.Current;
            if (a == null) continue;
            total++;
            CaveAreaInstance? ca = null;
            try { ca = a.TryCast<CaveAreaInstance>(); } catch { }
            if (ca == null) continue;
            try
            {
                var p = a.position;
                var pos = new Vector2(p.x, p.y);
                _caves.Add(pos);
                _caveTiles.Add(new CaveTile(pos, ca.WorldTileId));
            }
            catch { }
        }
        Plugin.Logger.LogInfo($"WarpTour: collected {_caves.Count} cave(s) from {total} area instance(s). " +
                              "Force-loading their tiles, then the warp tour begins.");
    }

    // ---- Force-load (proven in SeedScoutMod) -------------------------------------------------------
    private static void ForceLoad()
    {
        var mgr = Plugin.Streaming;
        if (mgr == null) { Plugin.Logger.LogWarning("WarpTour force-load: no WorldStreamingManager — skipping."); return; }
        if (_caveTiles.Count == 0) { Plugin.Logger.LogInfo("WarpTour force-load: no cave tiles — skipping."); return; }

        int radius = Mathf.Clamp(Plugin.ForceLoadRadius.Value, 0, 3);
        float ts = ReadTileSize();
        AddrFn? fn = radius > 0 ? CalibrateAddressing(ts) : (AddrFn?)null;
        if (radius > 0 && fn == null)
        {
            Plugin.Logger.LogWarning($"WarpTour force-load: addressing failed to calibrate (tileSize={ts:0}) — radius 0.");
            radius = 0;
        }

        int its = Mathf.RoundToInt(ts);
        var requested = new HashSet<uint>();
        int loaded = 0, nullOrErr = 0;
        foreach (var ct in _caveTiles)
        {
            Vector2Int mid;
            try { mid = ct.Id.GetWorldMidPosition(its); }
            catch { mid = new Vector2Int(Mathf.RoundToInt(ct.Pos.x), Mathf.RoundToInt(ct.Pos.y)); }

            for (int dx = -radius; dx <= radius; dx++)
            for (int dy = -radius; dy <= radius; dy++)
            {
                WorldTileId tileId;
                Vector2 anchorPos = new Vector2(mid.x + dx * ts, mid.y + dy * ts);
                if (dx == 0 && dy == 0) tileId = ct.Id;
                else
                {
                    try { tileId = TileAt(fn!.Value, anchorPos.x, anchorPos.y, ts); }
                    catch { nullOrErr++; continue; }
                }

                if (!requested.Add(tileId.Value)) continue;

                try
                {
                    var go = new GameObject("WarpTour_TileReq");
                    go.transform.position = new Vector3(anchorPos.x, 0f, anchorPos.y);
                    UnityEngine.Object.DontDestroyOnLoad(go);
                    _reqGos.Add(go);

                    var wt = mgr.RequestLoadWorldTile(_reqId++, go.transform, tileId);
                    if (wt != null) loaded++; else nullOrErr++;
                }
                catch (Exception e)
                {
                    nullOrErr++;
                    Plugin.Logger.LogWarning($"WarpTour force-load: request err at ({anchorPos.x:0},{anchorPos.y:0}): {e.Message}");
                }
            }
        }
        Plugin.Logger.LogInfo($"WarpTour force-load: radius={radius} tileSize={ts:0} over {_caveTiles.Count} cave(s) " +
                              $"-> {requested.Count} unique tile(s) ({loaded} ok, {nullOrErr} null/err). Tour starts shortly.");
    }

    private static float ReadTileSize()
    {
        try { var cfg = WorldConfiguration.GetActive(); if (cfg != null && cfg.tileSize > 0) return cfg.tileSize; }
        catch { }
        return 128f;
    }

    private enum AddrFn { Lowest, Closest, Highest }

    private static WorldTileId TileAt(AddrFn fn, float x, float z, float ts) => fn switch
    {
        AddrFn.Lowest => WorldTileId.GetLowest(x, z, ts),
        AddrFn.Highest => WorldTileId.GetHighest(x, z, ts),
        _ => WorldTileId.GetClosest(x, z, ts),
    };

    private static AddrFn? CalibrateAddressing(float ts)
    {
        if (ts <= 0f) return null;
        int its = Mathf.RoundToInt(ts);
        foreach (AddrFn fn in new[] { AddrFn.Lowest, AddrFn.Highest, AddrFn.Closest })
        {
            bool all = true;
            foreach (var ct in _caveTiles)
            {
                try
                {
                    var mid = ct.Id.GetWorldMidPosition(its);
                    if (TileAt(fn, mid.x, mid.y, ts).Value != ct.Id.Value) { all = false; break; }
                }
                catch { all = false; break; }
            }
            if (all) return fn;
        }
        return null;
    }

    private static void DestroyReqGos()
    {
        foreach (var go in _reqGos) if (go != null) UnityEngine.Object.Destroy(go);
        _reqGos.Clear();
    }

    // ---- Teleport tour -----------------------------------------------------------------------------
    private static void TourTick()
    {
        switch (_tour)
        {
            case TourPhase.Idle:
                _tourStartTimer += Time.deltaTime;
                if (_tourStartTimer < 2f) return;
                _drive = ResolvePlayerDrive();
                if (_drive == null)
                {
                    Plugin.Logger.LogWarning("WarpTour: no PlayerDrive on the local player — cannot teleport, skipping.");
                    _tour = TourPhase.Done; break;
                }
                _tourReturn = _haveSpawn ? _spawnPos : ResolvePlayerPos();
                _visited.Clear(); _tourCount = 0; _tourDrainTimer = 0f;
                _tourHp = SnapshotHp();
                Plugin.Logger.LogInfo($"WarpTour: START (dynamic — nearest unvisited POI each hop, picks up dens " +
                                      $"as they stream in). dwell {DwellSeconds():0.##}s, drain {DrainSeconds():0.#}s, hpGuard={_tourHp:0}.");
                AdvanceTour();
                break;

            case TourPhase.Teleporting:
                _tourTimer += Time.deltaTime;
                if (!IsTeleporting() || _tourTimer >= TourMaxWait) { _tourTimer = 0f; _tour = TourPhase.Dwell; }
                break;

            case TourPhase.Dwell:
                _tourTimer += Time.deltaTime;
                if (_tourTimer >= DwellSeconds()) AdvanceTour();
                break;

            case TourPhase.Draining:
                if (FindNearestCandidate(out var dn, out var dk)) { GoTo(dn, dk); break; }
                _tourDrainTimer += Time.deltaTime;
                if (_tourDrainTimer >= DrainSeconds()) { SafeTeleport(_tourReturn, "return to spawn"); _tour = TourPhase.Returning; }
                break;

            case TourPhase.Returning:
                _tourTimer += Time.deltaTime;
                if (!IsTeleporting() || _tourTimer >= TourMaxWait)
                {
                    _tour = TourPhase.Done;
                    _postTourTimer = TourPostGuard;
                    Plugin.Logger.LogInfo($"WarpTour: DONE — visited {_tourCount} POI(s), back at spawn. " +
                                          $"HP guarded {TourPostGuard:0}s more. Open the map for native pins.");
                }
                break;
        }
    }

    private static void AdvanceTour()
    {
        _tourTimer = 0f;
        if (_tourCount >= TourMaxStops)
        {
            Plugin.Logger.LogWarning($"WarpTour: hit safety cap ({TourMaxStops} stops) — returning to spawn.");
            SafeTeleport(_tourReturn, "return to spawn"); _tour = TourPhase.Returning; return;
        }
        if (FindNearestCandidate(out var next, out var kind)) GoTo(next, kind);
        else { _tourDrainTimer = 0f; _tour = TourPhase.Draining; }
    }

    private static void GoTo(Vector2 xz, string kind)
    {
        _visited.Add(xz);
        _tourCount++;
        float y = ResolveHeight(xz.x, xz.y, _haveSpawn ? _spawnPos.y : 64f);
        SafeTeleport(new Vector3(xz.x, y, xz.y), $"POI #{_tourCount} [{kind}]");
        _tourTimer = 0f;
        _tour = TourPhase.Teleporting;
    }

    private static bool FindNearestCandidate(out Vector2 best, out string kind)
    {
        Vector2 b = default; string k = "?"; float bd = float.MaxValue; bool found = false;
        var fp = PlayerPosNow(); var f2 = new Vector2(fp.x, fp.z);

        foreach (var c in _caves)
            if (!IsVisited(c)) { float d = (c - f2).sqrMagnitude; if (d < bd) { bd = d; b = c; k = "cave"; found = true; } }
        foreach (var h in Plugin.Hostiles)
            if (!IsVisited(h)) { float d = (h - f2).sqrMagnitude; if (d < bd) { bd = d; b = h; k = "hostile"; found = true; } }
        foreach (var l in Plugin.Lakes)
            if (!IsVisited(l)) { float d = (l - f2).sqrMagnitude; if (d < bd) { bd = d; b = l; k = "lake"; found = true; } }

        best = b; kind = k; return found;
    }

    private static bool IsVisited(Vector2 p)
    {
        foreach (var v in _visited) if ((v - p).sqrMagnitude < 36f) return true; // ~6m
        return false;
    }

    private static Vector3 PlayerPosNow()
    {
        try { if (_drive != null) return _drive.transform.position; } catch { }
        return ResolvePlayerPos();
    }

    private static float DwellSeconds()
    {
        // 0.5s floor: ~0.2s crashes the game (teleporting faster than the streamer settles); confirmed 2026-06-24.
        try { return Mathf.Clamp(Plugin.Dwell.Value, 0.5f, 5f); } catch { return 0.75f; }
    }

    private static float DrainSeconds()
    {
        try { return Mathf.Clamp(Plugin.Drain.Value, 1f, 30f); } catch { return 8f; }
    }

    private static float SnapshotHp()
    {
        try { var pc = Plugin.Player; return pc != null ? pc.CurrentHealth : -1f; }
        catch { return -1f; }
    }

    // Each teleport landing deals a little fall damage; restore HP to the pre-tour snapshot so ~25 stops
    // can't accumulate into a death. Must persist a few seconds past DONE (the spawn landing settles late).
    private static void ProtectHp()
    {
        if (_tourHp <= 0f) return;
        try
        {
            var pc = Plugin.Player;
            if (pc == null || pc.IsDead) return;
            if (pc.CurrentHealth < _tourHp) pc.CurrentHealth = Mathf.Min(_tourHp, pc.MaxHealth);
        }
        catch { }
    }

    private static void SafeTeleport(Vector3 pos, string what)
    {
        try
        {
            Plugin.Logger.LogInfo($"WarpTour: -> {what} at {pos.ToString("0.#")}");
            _drive!.Teleport(pos);
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"WarpTour: teleport err ({what}): {e.Message}"); }
    }

    private static bool IsTeleporting()
    {
        try { return _drive != null && _drive.IsTeleporting(); }
        catch { return false; }
    }

    private static float ResolveHeight(float x, float z, float fallback)
    {
        try { if (Physics.Raycast(new Vector3(x, 2000f, z), Vector3.down, out var hit, 4000f)) return hit.point.y + 1.5f; }
        catch { }
        return fallback;
    }

    private static PlayerDrive? ResolvePlayerDrive()
    {
        if (Plugin.Drive != null) return Plugin.Drive;
        try
        {
            var pt = Plugin.Biomes?._localPlayerTransform;
            if (pt == null) return null;
            var root = pt.root != null ? pt.root : pt;
            return root.GetComponentInChildren<PlayerDrive>(true);
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"WarpTour: resolve drive err: {e.Message}"); return null; }
    }

    private static Vector3 ResolvePlayerPos()
    {
        try { var pt = Plugin.Biomes?._localPlayerTransform; if (pt != null) return pt.position; }
        catch { }
        return _spawnPos;
    }

    private static void LogErrorOnce(string where, Exception e)
    {
        if (_loggedError) return;
        _loggedError = true;
        Plugin.Logger.LogWarning($"WarpTour: error in {where}: {e}");
    }
}
