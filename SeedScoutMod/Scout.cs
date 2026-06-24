using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Il2CppInterop.Runtime;
using SandSailorStudio.Streaming;
using SandSailorStudio.WorldGen;
using SSSGame;
using UnityEngine;

namespace SeedScoutMod;

// In-world scorer foundation. At world-ready: enumerate _areaInstances (full distribution)
// for caves (mines) + spawn distance — immediate and complete. Lakes stream in per-tile, so
// the score readout re-fires as lakes/caves register, filling in lake-near-mine proximity.
internal static class Scout
{
    private static float _hbTimer = 1.5f;
    private static bool _areasDumped;
    private static int _lastScoreSig = -1;
    private static bool _loggedError;
    private static Vector3 _spawnPos;
    private static bool _haveSpawn;
    private static readonly List<Vector2> _caves = new(); // world (x,z) of every cave, from _areaInstances

    // Force-load: each cave's streaming tile id (read off CaveAreaInstance), the temp anchor GOs we
    // hand RequestLoadWorldTile, and one-shot guard + settle timer.
    private static readonly List<CaveTile> _caveTiles = new();
    private static readonly List<GameObject> _reqGos = new();
    private static int _reqId = 1000;
    private static bool _forceLoadDone;
    private static float _settleTimer;

    private struct CaveTile
    {
        public Vector2 Pos;
        public WorldTileId Id;
        public CaveTile(Vector2 pos, WorldTileId id) { Pos = pos; Id = id; }
    }

    // Read by MapOverlay to draw cave dots on the map.
    internal static IReadOnlyList<Vector2> Caves => _caves;

    internal static void Tick()
    {
        _hbTimer += Time.deltaTime;
        bool heartbeat = _hbTimer >= 5f;
        if (heartbeat) _hbTimer = 0f;

        var biomes = Plugin.Biomes;
        if (biomes == null)
        {
            _areasDumped = false; _haveSpawn = false;
            _lastScoreSig = -1; _caves.Clear(); _caveTiles.Clear();
            _forceLoadDone = false; _settleTimer = 0f; DestroyReqGos();
            Plugin.Lakes.Clear(); Plugin.Hostiles.Clear(); Plugin.RegisteredCaves.Clear();
            if (heartbeat) Plugin.Logger.LogInfo("SeedScout hb: not in a loaded world (BiomesManager = null)");
            return;
        }

        WorldGenerator? worldGen = null;
        WorldDataMap? map = null;
        bool ready = false;
        Vector3 playerPos = Vector3.zero;
        bool havePlayer = false;
        try
        {
            worldGen = biomes._worldGenerator;
            var pt = biomes._localPlayerTransform;
            if (pt != null) { playerPos = pt.position; havePlayer = true; }
            if (worldGen != null)
            {
                ready = worldGen.WorldDataReady();
                if (ready) map = worldGen.GetDataMap();
            }
        }
        catch (Exception e) { LogErrorOnce("reading world state", e); }

        if (havePlayer && !_haveSpawn) { _spawnPos = playerPos; _haveSpawn = true; } // first known pos ≈ spawn

        if (heartbeat)
            Plugin.Logger.LogInfo($"SeedScout hb: ready={ready} map={(map == null ? "null" : "ok")} " +
                                  $"caves={_caves.Count} lakes={Plugin.Lakes.Count} regCaves={Plugin.RegisteredCaves.Count} " +
                                  $"player={(havePlayer ? playerPos.ToString("0.#") : "none")}");

        if (map == null || !_haveSpawn) return;

        if (!_areasDumped)
        {
            try { DumpAreas(map, _spawnPos); _areasDumped = true; }
            catch (Exception e) { LogErrorOnce("area dump", e); }
        }

        int sig = Plugin.Lakes.Count * 10000 + Plugin.Hostiles.Count;
        if (_areasDumped && sig != _lastScoreSig)
        {
            _lastScoreSig = sig;
            try { DumpScore(_spawnPos); }
            catch (Exception e) { LogErrorOnce("score dump", e); }
        }

        // Force-load: once caves are known, give the world a few seconds to settle, then ask the
        // streamer to load each cave's tile in place so lakes/hostiles spawn without exploring.
        if (_areasDumped && !_forceLoadDone && Plugin.ForceLoadTiles.Value)
        {
            _settleTimer += Time.deltaTime;
            if (_settleTimer >= 3f)
            {
                _forceLoadDone = true;
                try { ForceLoad(); }
                catch (Exception e) { LogErrorOnce("force-load", e); }
            }
        }
    }

    // Ask WorldStreamingManager to stream the tile under each cave. RequestLoadWorldTile takes a
    // caller id (so the request can be tracked/released) and an anchor transform — we park a hidden
    // GO at the cave so the streamer has a stable position to associate. We deliberately keep the
    // requests resident (no release) so the spawned lake/den markers stick for the session.
    private static void ForceLoad()
    {
        var mgr = Plugin.Streaming;
        if (mgr == null) { Plugin.Logger.LogWarning("SeedScout force-load: no WorldStreamingManager captured — skipping."); return; }
        if (_caveTiles.Count == 0) { Plugin.Logger.LogInfo("SeedScout force-load: no cave tiles recorded — skipping."); return; }

        int ok = 0;
        foreach (var ct in _caveTiles)
        {
            try
            {
                var go = new GameObject("SeedScout_TileReq");
                go.transform.position = new Vector3(ct.Pos.x, 0f, ct.Pos.y);
                UnityEngine.Object.DontDestroyOnLoad(go);
                _reqGos.Add(go);

                var wt = mgr.RequestLoadWorldTile(_reqId++, go.transform, ct.Id);
                Plugin.Logger.LogInfo($"SeedScout force-load: requested tile {ct.Id} at ({ct.Pos.x:0},{ct.Pos.y:0}) -> {(wt == null ? "null" : "WorldTile")}");
                ok++;
            }
            catch (Exception e)
            {
                Plugin.Logger.LogWarning($"SeedScout force-load: request err at ({ct.Pos.x:0},{ct.Pos.y:0}): {e.Message}");
            }
        }
        Plugin.Logger.LogInfo($"SeedScout force-load: issued {ok}/{_caveTiles.Count} cave-tile request(s). " +
                              "Watch for lake/hostile spawn lines below, then open the map.");
    }

    private static void DestroyReqGos()
    {
        foreach (var go in _reqGos)
            if (go != null) UnityEngine.Object.Destroy(go);
        _reqGos.Clear();
    }

    private static void DumpAreas(WorldDataMap map, Vector3 spawn)
    {
        _caves.Clear();
        _caveTiles.Clear();
        var sb = new StringBuilder();
        sb.AppendLine("==================== SeedScout: AREA INSTANCES ====================");
        sb.AppendLine($"REAL seed (RngManager)='{SafeRealSeed()}'   (config default='{SafeActiveSeed()}')");
        sb.AppendLine($"spawn={spawn:0.#}  (distances are horizontal meters from spawn)");

        var dict = map._areaInstances;
        sb.AppendLine($"_areaInstances count = {SafeCount(() => dict.Count)}");

        var typeTally = new Dictionary<string, int>();
        int total = 0;
        try
        {
            var en = dict.Values.GetEnumerator();
            while (en.MoveNext())
            {
                AreaInstance a = en.Current;
                if (a == null) continue;
                total++;
                string cat = Categorize(a);
                typeTally[cat] = typeTally.TryGetValue(cat, out var tc) ? tc + 1 : 1;
                if (cat == "Cave")
                {
                    try
                    {
                        var p = a.position;
                        var pos = new Vector2(p.x, p.y);
                        _caves.Add(pos);
                        var ca = a.TryCast<CaveAreaInstance>();
                        if (ca != null) _caveTiles.Add(new CaveTile(pos, ca.WorldTileId));
                    }
                    catch { }
                }
            }
        }
        catch (Exception e) { sb.AppendLine($"<enumeration error: {e.Message}>"); }

        sb.AppendLine($"areas enumerated: {total}");
        sb.AppendLine("type tally: " + string.Join("  ", typeTally.OrderByDescending(k => k.Value).Select(k => $"{k.Key}={k.Value}")));
        sb.AppendLine($"CAVES (mines): {_caves.Count}");
        foreach (var c in _caves.Select(c => (d: Horiz(spawn, c.x, c.y), c)).OrderBy(t => t.d).Take(30))
            sb.AppendLine($"   cave ({c.c.x:0},{c.c.y:0})  dist={Fmt(c.d)}m");
        sb.AppendLine("===================================================================");
        Plugin.Logger.LogInfo(sb.ToString());
    }

    private static void DumpScore(Vector3 spawn)
    {
        var sb = new StringBuilder();
        sb.AppendLine("---------- SeedScout: SCORE (lake coverage grows as you explore) ----------");
        sb.AppendLine($"seed='{SafeRealSeed()}'");

        var caves = _caves.Select(c => (pos: c, spawnDist: Horiz(spawn, c.x, c.y)))
                          .OrderBy(c => c.spawnDist).ToList();
        float nearestCave = caves.Count > 0 ? caves[0].spawnDist : float.MaxValue;
        int within500 = caves.Count(c => c.spawnDist <= 500f);
        int within1000 = caves.Count(c => c.spawnDist <= 1000f);

        var lakes = Plugin.Lakes;
        var hostiles = Plugin.Hostiles;
        float nearestLakeToSpawn = lakes.Count > 0
            ? lakes.Min(l => Horiz(spawn, l.Pos.x, l.Pos.y)) : float.MaxValue;

        sb.AppendLine($"caves: {caves.Count}  nearestToSpawn={Fmt(nearestCave)}m  within500m={within500}  within1000m={within1000}");
        sb.AppendLine($"lakes known: {lakes.Count}  nearestToSpawn={Fmt(nearestLakeToSpawn)}m");
        sb.AppendLine($"hostiles known: {hostiles.Count}  (lake/danger coverage grows as you explore)");

        // Per near-spawn cave: nearest KNOWN lake (good) and nearest KNOWN hostile (bad).
        // Only meaningful where tiles have streamed in.
        foreach (var c in caves.Take(10))
        {
            float lakeDist = lakes.Count > 0 ? lakes.Min(l => Horiz2(c.pos, l.Pos)) : float.MaxValue;
            float dangerDist = hostiles.Count > 0 ? hostiles.Min(h => Horiz2(c.pos, h.Pos)) : float.MaxValue;
            sb.AppendLine($"   cave ({c.pos.x:0},{c.pos.y:0})  spawnDist={Fmt(c.spawnDist)}m  " +
                          $"nearestLake={Fmt(lakeDist)}m  nearestHostile={Fmt(dangerDist)}m");
        }
        sb.AppendLine("--------------------------------------------------------------------------");
        Plugin.Logger.LogInfo(sb.ToString());
    }

    // AreaInstance is a plain Il2CppSystem.Object — TryCast is valid (gotcha is only UnityEngine.Object).
    private static string Categorize(AreaInstance a)
    {
        try
        {
            if (a.TryCast<CaveAreaInstance>() != null) return "Cave";
            if (a.TryCast<SeaAreaInstance>() != null) return "Sea";
            if (a.TryCast<PolygonalTerrainAreaInstance>() != null) return "PolyTerrain";
            if (a.TryCast<TerrainAreaInstance>() != null) return "Terrain";
            if (a.TryCast<WorldAreaInstance>() != null) return "WorldArea";
        }
        catch { return "<casterr>"; }
        return "Other";
    }

    private static float Horiz(Vector3 spawn, float wx, float wz)
    {
        float dx = spawn.x - wx, dz = spawn.z - wz;
        return Mathf.Sqrt(dx * dx + dz * dz);
    }

    private static float Horiz2(Vector2 a, Vector2 b) => (a - b).magnitude;

    private static string Fmt(float f) => f == float.MaxValue ? "n/a" : f.ToString("0");
    private static string SafeCount(Func<int> f) { try { return f().ToString(); } catch { return "<err>"; } }

    private static string SafeRealSeed()
    {
        try
        {
            var rng = Plugin.Rng;
            if (rng == null) return "<rng-null>";
            string phrase = rng.seedPhrase ?? "<null>";
            string padded = rng._paddedSeedPhrase ?? "";
            return padded.Length > 0 && padded != phrase ? $"{phrase}  (padded='{padded}')" : phrase;
        }
        catch (Exception e) { return $"<err {e.Message}>"; }
    }

    private static string SafeActiveSeed()
    {
        try
        {
            var cfg = WorldConfiguration.GetActive();
            return cfg?.randomGeneratorConfig?.seedPhrase ?? "<null>";
        }
        catch (Exception e) { return $"<err {e.Message}>"; }
    }

    private static void LogErrorOnce(string where, Exception e)
    {
        if (_loggedError) return;
        _loggedError = true;
        Plugin.Logger.LogWarning($"SeedScout: error in {where}: {e}");
    }
}
