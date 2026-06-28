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

    // Native-pin discovery: live AreaInstance refs for caves (held resident in _areaInstances), plus a
    // post-force-load timer + one-shot/retry guard (marker handlers only exist once tiles have streamed).
    private static readonly List<AreaInstance> _caveAreas = new();
    private static bool _discoverDone;
    private static float _discoverTimer;
    private static int _discoverAttempts;
    
    private static bool _finalScoreLogged;
    private static float _finalScoreTimer;

    private struct CaveTile
    {
        public Vector2 Pos;
        public WorldTileId Id;
        public CaveTile(Vector2 pos, WorldTileId id) { Pos = pos; Id = id; }
    }

    // Read by MapOverlay to draw cave dots on the map.
    internal static IReadOnlyList<Vector2> Caves => _caves;
    internal static Vector2? BestVillageCenter { get; private set; }

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
            _caveAreas.Clear(); _discoverDone = false; _discoverTimer = 0f; _discoverAttempts = 0;
            _finalScoreLogged = false; _finalScoreTimer = 0f; BestVillageCenter = null;
            Plugin.Seas.Clear(); Plugin.Lakes.Clear(); Plugin.Hostiles.Clear(); Plugin.RegisteredCaves.Clear();
            if (heartbeat) Plugin.LogInfo("SeedScout hb: not in a loaded world (BiomesManager = null)");
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
            Plugin.LogInfo($"SeedScout hb: ready={ready} map={(map == null ? "null" : "ok")} " +
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

        // Native pins: once force-load has streamed the cave tiles, mark each cave area explored and
        // refresh its marker handler so the game's own pin shows. Retry a few times — the handlers only
        // exist after the tile localizes, which lags the force-load request by a few seconds.
        if (_forceLoadDone && !_discoverDone && Plugin.RevealNativePins.Value)
        {
            _discoverTimer += Time.deltaTime;
            if (_discoverTimer >= 3f)
            {
                _discoverTimer = 0f;
                try
                {
                    int ok = DiscoverCaves();
                    _discoverAttempts++;
                    if (ok >= _caveAreas.Count || _discoverAttempts >= 5) _discoverDone = true;
                }
                catch (Exception e) { _discoverDone = true; LogErrorOnce("discover", e); }
            }
        }

        if (_forceLoadDone && !_finalScoreLogged)
        {
            _finalScoreTimer += Time.deltaTime;
            if (_finalScoreTimer >= 10f)
            {
                _finalScoreLogged = true;
                string scoreStr = CalculateBestScore(_spawnPos);
                string seed = SafeRealSeed();
                if (Plugin.EnableSeedLogging.Value)
                {
                    Plugin.Logger.LogInfo($"SeedScout: Seed = {seed}, Score = {scoreStr}");
                }
                
                try 
                {
                    string path = System.IO.Path.Combine(BepInEx.Paths.ConfigPath, "SeedScout_Scores.txt");
                    System.IO.File.AppendAllText(path, $"Seed = {seed}, Score = {scoreStr}{Environment.NewLine}");
                }
                catch (Exception e) 
                {
                    Plugin.Logger.LogWarning($"SeedScout: Failed to write to scores file: {e.Message}");
                }
            }
        }
    }

    // Force the game's OWN native map pins for caves: set each cave area explored + refresh its marker
    // handler so the pin (correct icon + name) appears without walking there. Least-invasive levers only
    // (local UI/state) — no OnDiscovered fanfare yet. Returns how many caves had a live marker handler.
    private static int DiscoverCaves()
    {
        int ok = 0, noHandler = 0;
        foreach (var area in _caveAreas)
        {
            if (area == null) continue;
            try
            {
                try { area.isExplored = true; } catch { }
                var h = area.areaInstanceMarkerHandler;
                if (h != null) { try { h.RefreshExploration(); ok++; } catch { } }
                else noHandler++;
            }
            catch { }
        }
        Plugin.LogInfo($"SeedScout discover: refreshed {ok}/{_caveAreas.Count} cave marker(s) " +
                              $"(noHandler={noHandler}, attempt {_discoverAttempts + 1}). Open the map.");
        return ok;
    }

    // Ask WorldStreamingManager to stream the tile(s) around each cave. RequestLoadWorldTile takes a
    // caller id (so the request can be tracked/released) and an anchor transform — we park a hidden GO
    // at the tile so the streamer has a stable position to associate. We deliberately keep the requests
    // resident (no release) so the spawned lake/den markers stick for the session.
    //
    // Radius widens coverage past the cave's own ~128m tile: for each (dx,dy) in [-R..R] we request the
    // neighbour tile. The cave's own tile uses its known-good WorldTileId (from CaveAreaInstance);
    // neighbours are addressed by WorldTileId.GetClosest(offset world pos, tileSize). We round-trip each
    // cave's own position through GetClosest first — if that doesn't reproduce the known tile id, the
    // addressing (or tileSize) is wrong, so we fall back to cave-tile-only rather than request garbage.
    private static void ForceLoad()
    {
        var mgr = Plugin.Streaming;
        if (mgr == null) { Plugin.Logger.LogWarning("SeedScout force-load: no WorldStreamingManager captured — skipping."); return; }
        if (_caveTiles.Count == 0) { Plugin.LogInfo("SeedScout force-load: no cave tiles recorded — skipping."); return; }

        int radius = Mathf.Clamp(Plugin.ForceLoadRadius.Value, 0, 3);
        float ts = ReadTileSize();

        // Calibrate neighbour addressing: GetClosest rounds to the nearest tile CENTRE, which isn't the
        // tile that CONTAINS an off-centre cave — so it can miss. Find whichever of Lowest/Closest/Highest
        // maps every cave's own position back to its known-good tile id; that one is then safe to use with
        // ±tileSize offsets for neighbours. If none calibrates, drop to cave-tile-only.
        AddrFn? fn = radius > 0 ? CalibrateAddressing(ts) : (AddrFn?)null;
        LogAddressingDiag(ts, fn);
        if (radius > 0 && fn == null)
        {
            Plugin.Logger.LogWarning($"SeedScout force-load: tile addressing failed to calibrate (tileSize={ts:0}) — " +
                                     "falling back to cave-tile-only (radius 0).");
            radius = 0;
        }

        int its = Mathf.RoundToInt(ts);
        var requested = new HashSet<uint>();
        int loaded = 0, nullOrErr = 0;
        foreach (var ct in _caveTiles)
        {
            // Address neighbours off the cave TILE's own centre (GetWorldMidPosition), not the cave
            // entrance — the entrance can sit in a different tile than the one the generator assigned.
            Vector2Int mid;
            try { mid = ct.Id.GetWorldMidPosition(its); }
            catch { mid = new Vector2Int(Mathf.RoundToInt(ct.Pos.x), Mathf.RoundToInt(ct.Pos.y)); }

            for (int dx = -radius; dx <= radius; dx++)
            for (int dy = -radius; dy <= radius; dy++)
            {
                WorldTileId tileId;
                Vector2 anchorPos = new Vector2(mid.x + dx * ts, mid.y + dy * ts);
                if (dx == 0 && dy == 0) tileId = ct.Id;                                // known-good cave tile
                else
                {
                    try { tileId = TileAt(fn!.Value, anchorPos.x, anchorPos.y, ts); }
                    catch { nullOrErr++; continue; }
                }

                if (!requested.Add(tileId.Value)) continue;                            // dedupe overlaps

                try
                {
                    var go = new GameObject("SeedScout_TileReq");
                    go.transform.position = new Vector3(anchorPos.x, 0f, anchorPos.y);
                    UnityEngine.Object.DontDestroyOnLoad(go);
                    _reqGos.Add(go);

                    var wt = mgr.RequestLoadWorldTile(_reqId++, go.transform, tileId);
                    if (wt != null) loaded++; else nullOrErr++;
                }
                catch (Exception e)
                {
                    nullOrErr++;
                    Plugin.Logger.LogWarning($"SeedScout force-load: request err at ({anchorPos.x:0},{anchorPos.y:0}): {e.Message}");
                }
            }
        }
        Plugin.LogInfo($"SeedScout force-load: radius={radius} tileSize={ts:0} over {_caveTiles.Count} cave(s) " +
                              $"-> {requested.Count} unique tile(s) requested ({loaded} ok, {nullOrErr} null/err). " +
                              "Watch for lake/hostile spawn lines below, then open the map.");
    }

    // World tile size (metres) from the active world config; falls back to the known 128m default.
    private static float ReadTileSize()
    {
        try
        {
            var cfg = WorldConfiguration.GetActive();
            if (cfg != null && cfg.tileSize > 0) return cfg.tileSize;
        }
        catch { }
        return 128f;
    }

    private enum AddrFn { Lowest, Closest, Highest }

    private static WorldTileId TileAt(AddrFn fn, float x, float z, float ts)
    {
        switch (fn)
        {
            case AddrFn.Lowest:  return WorldTileId.GetLowest(x, z, ts);
            case AddrFn.Highest: return WorldTileId.GetHighest(x, z, ts);
            default:             return WorldTileId.GetClosest(x, z, ts);
        }
    }

    // Pick the addressing fn (if any) that maps every cave TILE's own centre back to its known id. Using
    // the tile centre (GetWorldMidPosition) — not the cave entrance — is what makes this round-trip
    // reliably: a cave's assigned tile does NOT necessarily contain its entrance position. Confirmed
    // in-game (2026-06-24) the convention is FLOOR — tile g spans [g·ts,(g+1)·ts), centre at (g+0.5)·ts,
    // so GetLowest reproduces it deterministically (no rounding tie). We still probe all three as a net.
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

    // One-line diagnostic so a calibration failure is debuggable straight from the log (no extra build):
    // shows the first cave's known tile id vs what each addressing fn produces.
    private static void LogAddressingDiag(float ts, AddrFn? chosen)
    {
        try
        {
            if (_caveTiles.Count == 0) return;
            var ct = _caveTiles[0];
            var mid = ct.Id.GetWorldMidPosition(Mathf.RoundToInt(ts));
            Plugin.LogInfo($"SeedScout force-load addr: cave({ct.Pos.x:0},{ct.Pos.y:0}) known={FmtTile(ct.Id)} mid=({mid.x},{mid.y}) " +
                                  $"low={FmtTile(WorldTileId.GetLowest(mid.x, mid.y, ts))} " +
                                  $"close={FmtTile(WorldTileId.GetClosest(mid.x, mid.y, ts))} " +
                                  $"high={FmtTile(WorldTileId.GetHighest(mid.x, mid.y, ts))} chosen={(chosen?.ToString() ?? "none")}");
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"SeedScout force-load addr diag err: {e.Message}"); }
    }

    private static string FmtTile(WorldTileId id)
    {
        try { var xy = id.WorldXY; return $"{id.Value}({xy.x},{xy.y})"; }
        catch { return id.Value.ToString(); }
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
        _caveAreas.Clear();
        Plugin.Seas.Clear();
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
                        _caveAreas.Add(a);                       // live ref for native-pin discovery
                        var ca = a.TryCast<CaveAreaInstance>();
                        if (ca != null) _caveTiles.Add(new CaveTile(pos, ca.WorldTileId));
                    }
                    catch { }
                }
                else if (cat == "Sea")
                {
                    try
                    {
                        var p = a.position;
                        Plugin.Seas.Add(new Vector2(p.x, p.y));
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
        Plugin.LogInfo(sb.ToString());
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
        Plugin.LogInfo(sb.ToString());
    }

    private static string CalculateBestScore(Vector3 spawn)
    {
        var caves = _caves;
        var lakes = Plugin.Lakes;
        var seas = Plugin.Seas;
        var hostiles = Plugin.Hostiles;

        if (caves.Count == 0) return "TBD (no caves)";

        float bestScore = -9999f;
        string bestScoreBreakdown = "TBD";
        Vector2? newVillageCenter = null;

        foreach (var c in caves)
        {
            float waterDist = float.MaxValue;
            Vector2 bestWater = Vector2.zero;

            if (lakes.Count > 0)
            {
                var l = lakes.OrderBy(x => Horiz2(c, x.Pos)).First();
                float d = Horiz2(c, l.Pos);
                if (d < waterDist) { waterDist = d; bestWater = l.Pos; }
            }
            if (seas.Count > 0)
            {
                var s = seas.OrderBy(x => Horiz2(c, x)).First();
                float d = Horiz2(c, s);
                if (d < waterDist) { waterDist = d; bestWater = s; }
            }

            if (waterDist == float.MaxValue) continue;

            float score = 100f; 
            float waterScore = 1000f - (waterDist * 2.5f);
            score += waterScore;

            Vector2 villageCenter = (c + bestWater) / 2f;
            float huntingScore = 0f;
            float dangerPenalty = 0f;
            int wulfarCount = 0;
            int nonWulfarCount = 0;

            foreach (var h in hostiles)
            {
                float dVillage = Horiz2(villageCenter, h.Pos);
                float dCave = Horiz2(c, h.Pos);
                
                // Penalty for being too close to the mine (workers get harassed)
                if (dCave < 150f) 
                {
                    dangerPenalty -= 40f;
                }

                if (h.IsWulfar)
                {
                    if (dVillage < 400f) { dangerPenalty -= 20f; wulfarCount++; } // Too close! Harassment threat.
                    else if (dVillage <= 700f) { huntingScore += 30f; wulfarCount++; } // Sweet spot for hunters
                }
                else
                {
                    if (dVillage <= 300f) { dangerPenalty -= 40f; nonWulfarCount++; }
                }
            }

            score += huntingScore + dangerPenalty;

            if (score > bestScore)
            {
                bestScore = score;
                newVillageCenter = villageCenter;
                bestScoreBreakdown = $"{score:0} (cave={c.x:0},{c.y:0} waterDist={waterDist:0}m wulfars={wulfarCount} hostiles={nonWulfarCount})";
            }
        }

        BestVillageCenter = newVillageCenter;

        if (bestScore == -9999f) return "TBD (no water found)";
        return bestScoreBreakdown;
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
            var session = UnityEngine.Object.FindObjectOfType<SSSGame.Network.NetworkSession>();
            if (session != null && session.Parameters != null && !string.IsNullOrEmpty(session.Parameters.seed))
            {
                return session.Parameters.seed;
            }
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"SeedScout: NetworkSession seed read error: {e.Message}"); }

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
