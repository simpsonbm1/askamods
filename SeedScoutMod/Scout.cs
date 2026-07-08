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

// SeedScout core: reveal the game's OWN native map pins for every POI on the map at world load,
// without moving the player.
//
// The pin mechanism (v0.17/v0.18, confirmed in-game 2026-07-04): a marker-bearing AreaInstance
// carries an AreaInstanceMarkerHandler (~450 exist natively at load); once isExplored is set and
// RefreshExploration() runs, the handler spawns a persistent MarkerItem — the native pin (correct
// icon + name, survives save/reload, renders at any distance). Caves lack a handler natively but
// one can be CONSTRUCTED via the public (AreaInstance, MarkerInfo) ctor.
//
// TILE RESIDENCY is the coverage limiter (log-confirmed 2026-07-04): most pins only materialize
// once the POI's world tile is streamed in (v1.0.1's cave-ring force-load pinned exactly the areas
// inside its 104 requested tiles and nothing else). Residency-independent pins: caves, islands,
// and whatever happens to sit in already-resident tiles. So the sweep below requests EXACTLY the
// tiles of areas that failed to pin — targeted, seed-layout-independent — instead of blind rings
// around caves. There is no request-release API on WorldStreamingManager; requested tiles stay
// resident for the session (v0.18 ran ~104 resident tiles without issue; SweepMaxTiles caps it).
//
// The old scorer / base-placement recommender / colored map-dot overlay were removed for the MVP
// (git history ≤ v0.18.0 has them).
internal static class Scout
{
    private static float _hbTimer = 1.5f;
    private static float _throttle;
    private const float ThrottleInterval = 0.5f;   // 2 Hz — pin reveal is a load-time activity
    private static bool _areasDumped;
    private static bool _loggedError;
    private static Vector3 _spawnPos;
    private static bool _haveSpawn;

    // Reveal loop state: passes run every few seconds until every enabled POI has a pin, progress
    // stalls out, or the attempt cap hits. Sweep waves stream in the tiles of unpinned areas.
    private static bool _discoverDone;
    private static float _discoverTimer;
    private static int _discoverAttempts;
    private static int _lastOk = -1;
    private static int _staleAttempts;
    private static bool _sweepWave1Done;
    private static bool _sweepWave2Done;
    private static bool _sweepCapLogged;
    private static bool _loggedInfoNotIl2cpp;
    private static readonly HashSet<uint> _sweepRequested = new();
    private static readonly List<GameObject> _reqGos = new();
    private static int _reqId = 1000;

    private const float DiscoverInterval = 2f;
    private const int MaxAttempts = 30;          // × interval ≈ 60s hard ceiling
    private const int StaleLimit = 5;            // exit after this many no-progress passes post-sweep
    private const int Wave1Attempt = 2;          // first pass catches residency-free pins, then sweep
    private const int Wave2Attempt = 8;          // ring fallback for areas whose own tile wasn't enough

    // Every live area (with category) — the reveal iterates this; refs stay valid because the
    // world holds them resident in _areaInstances for the whole session.
    private static readonly List<(AreaInstance Area, string Cat)> _allAreas = new();
    private static readonly List<Vector2> _caves = new(); // world (x,z), for the load-time log

    // Home-island scope (HomeIslandOnly): the spawn island's expanded bounds rect, resolved from
    // the "MainIsland" AreaInstance at dump time; falls back to a radius around spawn if the
    // island/bounds can't be resolved. _lastHomeOnly detects a live toggle flip (config file is
    // re-read every heartbeat) so the reveal can re-arm and widen to the rest of the world.
    private static float _homeMinX, _homeMinZ, _homeMaxX, _homeMaxZ;
    private static bool _homeIsRect;
    private static bool _lastHomeOnly;
    private static float _cfgReloadTimer;
    private const float HomeMarginMeters = 250f;   // island bounds grow by this — catches its fish ring
    private const float HomeRadiusFallback = 1600f; // 1200 visibly clipped a real island's edges (2026-07-04)

    // POI type buckets for the per-type config toggles. Name stems confirmed from a live seed's
    // reveal log (2026-07-04): Habitat_Settlement* (enemy camps), Biome_FishingGrounds_*,
    // LakeBiome_*, Biome_ExplorationTower / *Shipwreck* / Habitat_Heads (landmarks),
    // Habitat_*/BiomeDraugars/HabitatBear/... (creature dens), *Island* under Terrain (islands).
    private enum PoiType { Cave, Island, Lake, FishingGround, EnemyCamp, CreatureDen, Landmark, Unknown }

    internal static void Tick()
    {
        _throttle += Time.deltaTime;
        if (_throttle < ThrottleInterval) return;
        float dt = _throttle;   // real elapsed since last processed tick
        _throttle = 0f;

        _hbTimer += dt;
        bool heartbeat = _hbTimer >= 5f;
        if (heartbeat) _hbTimer = 0f;

        var biomes = Plugin.Biomes;
        if (biomes == null)
        {
            _areasDumped = false; _haveSpawn = false;
            _caves.Clear(); _allAreas.Clear();
            _discoverDone = false; _discoverTimer = 0f; _discoverAttempts = 0;
            _lastOk = -1; _staleAttempts = 0;
            _sweepWave1Done = false; _sweepWave2Done = false; _sweepCapLogged = false;
            _sweepRequested.Clear(); DestroyReqGos();
            _homeIsRect = false;
            Plugin.RegisteredCaves.Clear();
            Plugin.Caves = null; // stale manager from an unloaded world — recaptured on next RegisterCaves
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
                                  $"areas={_allAreas.Count} caves={_caves.Count} " +
                                  $"player={(havePlayer ? playerPos.ToString("0.#") : "none")}");

        if (map == null || !_haveSpawn) return;

        if (!_areasDumped)
        {
            try { DumpAreas(map, _spawnPos); _areasDumped = true; }
            catch (Exception e) { LogErrorOnce("area dump", e); }
            try { ResolveHomeIsland(_spawnPos); } catch (Exception e) { LogErrorOnce("home island", e); }
            _lastHomeOnly = Plugin.HomeIslandOnly.Value;
        }

        // Live config pickup: BepInEx does NOT re-read an edited cfg on its own — reload it every
        // few seconds so HomeIslandOnly can be flipped mid-session. On a true→false flip, re-arm
        // the reveal loop (keeping _sweepRequested, so resident tiles aren't re-requested) to
        // widen coverage to the rest of the world.
        _cfgReloadTimer += dt;
        if (_cfgReloadTimer >= 5f)
        {
            _cfgReloadTimer = 0f;
            try { Plugin.Cfg?.Reload(); } catch { }
            bool homeOnly = Plugin.HomeIslandOnly.Value;
            if (_lastHomeOnly && !homeOnly && _areasDumped)
            {
                Plugin.Logger.LogInfo("SeedScout: HomeIslandOnly flipped off — widening the reveal to the greater world.");
                _discoverDone = false; _discoverAttempts = 0;
                _lastOk = -1; _staleAttempts = 0;
                _sweepWave1Done = false; _sweepWave2Done = false;
            }
            _lastHomeOnly = homeOnly;
        }

        // The reveal loop. Pass 1 (a few seconds after the areas are known) catches every pin that
        // doesn't need tile residency. Then sweep wave 1 streams in the exact tiles of the areas
        // that failed, later passes re-reveal them as tiles localize, and wave 2 adds a 3x3 ring
        // for stragglers whose assigned tile didn't contain them.
        if (_areasDumped && !_discoverDone && Plugin.RevealNativePins.Value)
        {
            _discoverTimer += dt;
            if (_discoverTimer >= DiscoverInterval)
            {
                _discoverTimer = 0f;
                try
                {
                    int ok = DiscoverPois(out int pinnable, out var unpinned);
                    _discoverAttempts++;

                    if (Plugin.SweepStreamTiles.Value && unpinned.Count > 0)
                    {
                        if (!_sweepWave1Done && _discoverAttempts >= Wave1Attempt)
                        {
                            _sweepWave1Done = true;
                            try { SweepTiles(unpinned, ring: false); }
                            catch (Exception e) { LogErrorOnce("sweep wave 1", e); }
                        }
                        else if (!_sweepWave2Done && _discoverAttempts >= Wave2Attempt)
                        {
                            _sweepWave2Done = true;
                            try { SweepTiles(unpinned, ring: true); }
                            catch (Exception e) { LogErrorOnce("sweep wave 2", e); }
                        }
                    }

                    // Exit: full coverage, or no progress for a while after both waves had their shot.
                    _staleAttempts = (ok == _lastOk) ? _staleAttempts + 1 : 0;
                    _lastOk = ok;
                    bool covered = pinnable > 0 && ok >= pinnable;
                    bool stalled = (_sweepWave2Done || !Plugin.SweepStreamTiles.Value || unpinned.Count == 0)
                                   && _staleAttempts >= StaleLimit;
                    if (covered || stalled || _discoverAttempts >= MaxAttempts)
                    {
                        _discoverDone = true;
                        Plugin.Logger.LogInfo($"SeedScout: reveal finished — {ok}/{pinnable} pins after " +
                                              $"{_discoverAttempts} pass(es), {_sweepRequested.Count} tile(s) streamed " +
                                              $"({(covered ? "full coverage" : stalled ? "stalled" : "attempt cap")}).");
                    }
                }
                catch (Exception e) { _discoverDone = true; LogErrorOnce("discover", e); }
            }
        }
    }

    // One reveal pass — see the class comment for the mechanism. Honors the per-type config
    // toggles. Returns pins materialized; pinnable = enabled areas that can carry a pin;
    // unpinned = the subset still missing a marker object (the sweep's target list).
    private static int DiscoverPois(out int pinnable, out List<(AreaInstance Area, string Cat)> unpinned)
    {
        int okMarkers = 0, built = 0, native = 0, typeDisabled = 0, scopeSkipped = 0;
        bool homeOnly = Plugin.HomeIslandOnly.Value;
        pinnable = 0;
        unpinned = new List<(AreaInstance, string)>();
        var noTemplateTally = new Dictionary<string, int>();
        var typeTally = new Dictionary<PoiType, (int ok, int total)>();

        foreach (var (area, cat) in _allAreas)
        {
            if (area == null) continue;
            string aname = "?";
            try { aname = area.name ?? "?"; } catch { }
            try
            {
                var poiType = Classify(cat, aname);
                if (!IsTypeEnabled(poiType)) { typeDisabled++; continue; }
                if (homeOnly && !InHomeScope(area)) { scopeSkipped++; continue; }

                var h = area.areaInstanceMarkerHandler;
                bool builtNow = false;
                if (h == null)
                {
                    var mi = ResolveMarkerInfo(area, cat);
                    if (mi == null)
                    {
                        noTemplateTally[cat] = noTemplateTally.TryGetValue(cat, out var n) ? n + 1 : 1;
                        continue;
                    }
                    h = BuildHandler(area, mi).Cast<IAreaInstanceMarkerHandler>();
                    area.areaInstanceMarkerHandler = h;
                    built++; builtNow = true;
                }
                else native++;
                pinnable++;

                try { area.isExplored = true; } catch { }

                // Only refresh while no marker object exists — RefreshExploration is what materializes
                // the pin, and re-firing it on a live pin risks spawning a duplicate MarkerItem.
                bool hadMarker = false;
                try { hadMarker = h.GetMarkerObject() != null; } catch { }
                if (!hadMarker)
                {
                    try { h.RefreshExploration(); } catch (Exception e) { Plugin.LogInfo($"SeedScout discover: [{cat}] '{aname}' RefreshExploration err: {e.Message}"); }
                }

                bool pinned = false;
                try { pinned = h.GetMarkerObject() != null; } catch { }
                var tt = typeTally.TryGetValue(poiType, out var t) ? t : (0, 0);
                typeTally[poiType] = (tt.Item1 + (pinned ? 1 : 0), tt.Item2 + 1);
                if (pinned) okMarkers++;
                else unpinned.Add((area, cat));
                // Per-area line only while it's still interesting (unpinned or just handled).
                if (!pinned || builtNow || !hadMarker)
                    Plugin.LogInfo($"SeedScout discover: [{cat}] '{aname}' pos={area.position} " +
                                   $"type={poiType} handler={(builtNow ? "BUILT" : "native")} markerObj={(pinned ? "OK" : "null")}");
            }
            catch (Exception e)
            {
                Plugin.LogInfo($"SeedScout discover: [{cat}] '{aname}' err: {e.Message}");
            }
        }

        string types = string.Join(" ", typeTally.OrderBy(k => k.Key.ToString())
                                                 .Select(k => $"{k.Key}={k.Value.ok}/{k.Value.total}"));
        string skipped = noTemplateTally.Count > 0
            ? string.Join(" ", noTemplateTally.OrderByDescending(k => k.Value).Select(k => $"{k.Key}={k.Value}"))
            : "none";
        Plugin.LogInfo($"SeedScout discover: markerObj OK {okMarkers}/{pinnable} pinnable area(s) " +
                              $"[{types}] (handlers: {native} native, {built} built; type-disabled: {typeDisabled}; " +
                              $"{(homeOnly ? $"outside home island: {scopeSkipped}; " : "")}" +
                              $"no-template skipped: {skipped}; attempt {_discoverAttempts + 1}). Open the map.");
        return okMarkers;
    }

    // Resolve the spawn island's bounds for HomeIslandOnly: the area named "MainIsland*" whose
    // bounds (probed under four Vector4 conventions) contain the spawn point, expanded by a margin
    // so the island's surrounding fishing grounds count as home. Falls back to a fixed radius
    // around spawn when no interpretation checks out — spawn MUST lie inside its own island, so
    // the containment test doubles as the bounds-format validator.
    //
    // LOGGING GOTCHA (bit us in v1.2.0): BepInEx's interpolated-string log handler throws
    // VerificationException on Vector3/Vector4 arguments — always .ToString() struct args before
    // interpolating into a direct Logger.Log* call.
    private static void ResolveHomeIsland(Vector3 spawn)
    {
        _homeIsRect = false;
        foreach (var (area, _) in _allAreas)
        {
            if (area == null) continue;
            string n;
            try { n = area.name ?? ""; } catch { continue; }
            if (!n.StartsWith("MainIsland", StringComparison.OrdinalIgnoreCase)) continue;

            Vector4 b;
            try { b = area.GetBounds(); } catch { continue; }
            // Interpretations: A (xMin,zMin,xMax,zMax) · B (x,z,width,height) ·
            // C (centerX,centerZ,width,height) · D (centerX,centerZ,extentX,extentZ).
            var cands = new (float minX, float minZ, float maxX, float maxZ)[]
            {
                (b.x, b.y, b.z, b.w),
                (b.x, b.y, b.x + b.z, b.y + b.w),
                (b.x - b.z * 0.5f, b.y - b.w * 0.5f, b.x + b.z * 0.5f, b.y + b.w * 0.5f),
                (b.x - b.z, b.y - b.w, b.x + b.z, b.y + b.w),
            };
            for (int i = 0; i < cands.Length; i++)
            {
                var c = cands[i];
                float spanX = c.maxX - c.minX, spanZ = c.maxZ - c.minZ;
                if (spanX < 200f || spanZ < 200f || spanX > 8000f || spanZ > 8000f) continue; // not island-sized
                if (spawn.x < c.minX || spawn.x > c.maxX || spawn.z < c.minZ || spawn.z > c.maxZ) continue;
                _homeMinX = c.minX - HomeMarginMeters; _homeMaxX = c.maxX + HomeMarginMeters;
                _homeMinZ = c.minZ - HomeMarginMeters; _homeMaxZ = c.maxZ + HomeMarginMeters;
                _homeIsRect = true;
                Plugin.Logger.LogInfo($"SeedScout: home island '{n}' bounds=({_homeMinX:0},{_homeMinZ:0})-({_homeMaxX:0},{_homeMaxZ:0}) " +
                                      $"(raw={b.ToString()}, interpretation {(char)('A' + i)}, margin={HomeMarginMeters:0}m).");
                return;
            }
            Plugin.Logger.LogWarning($"SeedScout: '{n}' raw bounds {b.ToString()} don't contain spawn {spawn.ToString("0.#")} " +
                                     "under any interpretation (A rect / B corner+size / C center+size / D center+extents).");
        }
        Plugin.Logger.LogWarning($"SeedScout: no MainIsland bounds resolved — HomeIslandOnly falls back to " +
                                 $"{HomeRadiusFallback:0}m around spawn.");
    }

    private static bool InHomeScope(AreaInstance area)
    {
        Vector2Int p;
        try { p = area.position; } catch { return false; }
        if (_homeIsRect)
            return p.x >= _homeMinX && p.x <= _homeMaxX && p.y >= _homeMinZ && p.y <= _homeMaxZ;
        float dx = _spawnPos.x - p.x, dz = _spawnPos.z - p.y;
        return dx * dx + dz * dz <= HomeRadiusFallback * HomeRadiusFallback;
    }

    private static PoiType Classify(string cat, string name)
    {
        if (cat == "Cave") return PoiType.Cave;
        if (cat == "Terrain" || cat == "PolyTerrain") return PoiType.Island;
        string n = name ?? "";
        bool Has(string kw) => n.IndexOf(kw, StringComparison.OrdinalIgnoreCase) >= 0;
        if (Has("FishingGrounds")) return PoiType.FishingGround;
        if (Has("Lake")) return PoiType.Lake;
        if (Has("Settlement")) return PoiType.EnemyCamp;
        if (Has("ExplorationTower") || Has("Shipwreck") || Has("Heads")) return PoiType.Landmark;
        if (Has("Habitat") || Has("Draugar") || Has("Biome")) return PoiType.CreatureDen;
        return PoiType.Unknown;
    }

    private static bool IsTypeEnabled(PoiType t) => t switch
    {
        PoiType.Cave => Plugin.RevealCaves.Value,
        PoiType.Island => Plugin.RevealIslands.Value,
        PoiType.Lake => Plugin.RevealLakes.Value,
        PoiType.FishingGround => Plugin.RevealFishingGrounds.Value,
        PoiType.EnemyCamp => Plugin.RevealEnemyCamps.Value,
        PoiType.CreatureDen => Plugin.RevealCreatureDens.Value,
        PoiType.Landmark => Plugin.RevealLandmarks.Value,
        _ => Plugin.RevealUnknown.Value,
    };

    // Stream in the tiles of the areas that failed to pin. Each area contributes its own tile
    // (from its position — WORLD METERS, confirmed; FLOOR tile convention, confirmed 2026-06-24),
    // plus its GetWorldTileCoordinates() rect when that reads as sane tile-grid coords. ring=true
    // (wave 2) adds the 3x3 neighbourhood — the net for areas whose assigned tile doesn't contain
    // their position (the known cave-tile gotcha, may apply to other area kinds too).
    private static void SweepTiles(List<(AreaInstance Area, string Cat)> unpinned, bool ring)
    {
        var mgr = Plugin.Streaming;
        if (mgr == null) { Plugin.Logger.LogWarning("SeedScout sweep: no WorldStreamingManager captured — skipping."); return; }

        float ts = ReadTileSize();
        int cap = Mathf.Max(Plugin.SweepMaxTiles.Value, 0);
        int loaded = 0, nullOrErr = 0, capSkipped = 0;
        bool loggedRectDiag = false;

        foreach (var (area, cat) in unpinned)
        {
            if (area == null) continue;
            foreach (var (wx, wz) in CandidateTilePoints(area, ts, ring, ref loggedRectDiag))
            {
                WorldTileId tileId;
                try { tileId = WorldTileId.GetLowest(wx, wz, ts); }
                catch { nullOrErr++; continue; }
                if (_sweepRequested.Contains(tileId.Value)) continue;
                if (_sweepRequested.Count >= cap) { capSkipped++; continue; }
                _sweepRequested.Add(tileId.Value);

                try
                {
                    var go = new GameObject("SeedScout_TileReq");
                    go.transform.position = new Vector3(wx, 0f, wz);
                    UnityEngine.Object.DontDestroyOnLoad(go);
                    _reqGos.Add(go);

                    var wt = mgr.RequestLoadWorldTile(_reqId++, go.transform, tileId);
                    if (wt != null) loaded++; else nullOrErr++;
                }
                catch (Exception e)
                {
                    nullOrErr++;
                    Plugin.Logger.LogWarning($"SeedScout sweep: request err at ({wx:0},{wz:0}): {e.Message}");
                }
            }
        }

        if (capSkipped > 0 && !_sweepCapLogged)
        {
            _sweepCapLogged = true;
            Plugin.Logger.LogWarning($"SeedScout sweep: SweepMaxTiles cap ({cap}) hit — {capSkipped} tile request(s) " +
                                     "skipped; the farthest POIs may stay unpinned. Raise the cap in the config if desired.");
        }
        Plugin.LogInfo($"SeedScout sweep{(ring ? " (ring wave)" : "")}: {unpinned.Count} unpinned area(s) -> " +
                              $"{loaded} tile(s) newly requested ({nullOrErr} null/err, total resident requests {_sweepRequested.Count}).");
    }

    // World-space probe points whose containing tiles should cover the area. Always the area's own
    // position; plus the centres of its tile-coordinate rect when it looks like sane tile-grid
    // coords (small span, small magnitudes); plus the 3x3 ring around the position when ring=true.
    private static IEnumerable<(float x, float z)> CandidateTilePoints(AreaInstance area, float ts, bool ring, ref bool loggedRectDiag)
    {
        var pts = new List<(float, float)>();
        Vector2Int pos = default;
        try { pos = area.position; } catch { }
        pts.Add((pos.x, pos.y));

        try
        {
            var rc = area.GetWorldTileCoordinates();
            if (!loggedRectDiag)
            {
                loggedRectDiag = true;
                Plugin.LogInfo($"SeedScout sweep diag: first area pos=({pos.x},{pos.y}) tileRect={rc} tileSize={ts:0}");
            }
            // Tile-grid coords for a ~50-tile world are small ints; world-meter rects are huge.
            if (rc.width >= 1 && rc.height >= 1 && rc.width <= 6 && rc.height <= 6 &&
                Mathf.Abs(rc.xMin) <= 200 && Mathf.Abs(rc.yMin) <= 200)
            {
                for (int gx = rc.xMin; gx < rc.xMax; gx++)
                for (int gy = rc.yMin; gy < rc.yMax; gy++)
                    pts.Add(((gx + 0.5f) * ts, (gy + 0.5f) * ts));
            }
        }
        catch { }

        if (ring)
        {
            for (int dx = -1; dx <= 1; dx++)
            for (int dy = -1; dy <= 1; dy++)
                if (dx != 0 || dy != 0)
                    pts.Add((pos.x + dx * ts, pos.y + dy * ts));
        }
        return pts;
    }

    // The pin template for an area. Caves: the known-good CavesManager.caveMarkerInfo (v0.17-proven).
    // Everything else: the area's own AreaData.itemInfo — but only when its NATIVE class is (a subclass
    // of) MarkerInfo. ItemInfo is a UnityEngine.Object, so TryCast<MarkerInfo>() is unavailable
    // (base chain is System.Object) — check the il2cpp class name and rewrap via new MarkerInfo(ptr).
    private static SSSGame.MarkerInfo? ResolveMarkerInfo(AreaInstance area, string cat)
    {
        if (cat == "Cave")
        {
            try { var mi = Plugin.Caves?.caveMarkerInfo; if (mi != null) return mi; } catch { }
        }
        try
        {
            var info = area.area?.itemInfo;
            if (info == null) return null;
            // Compile-time, ItemInfo's base chain runs through the unity-libs CoreModule stub
            // (UnityEngine.Object : System.Object — the known TryCast gotcha), so we can't type this
            // as Il2CppObjectBase in source. At runtime the interop CoreModule is what's loaded and
            // its UnityEngine.Object IS an Il2CppObjectBase — box-cast to find out. (Confirmed
            // working in-game 2026-07-04.)
            if ((object)info is not Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase objBase)
            {
                if (!_loggedInfoNotIl2cpp)
                {
                    _loggedInfoNotIl2cpp = true;
                    Plugin.Logger.LogWarning("SeedScout discover: AreaData.itemInfo is not an Il2CppObjectBase at runtime — " +
                                             "cannot native-type-check pin templates; only caves will get pins.");
                }
                return null;
            }
            string ncls = NativeClassName(objBase);
            if (ncls.EndsWith("MarkerInfo")) return new SSSGame.MarkerInfo(objBase.Pointer);
        }
        catch { }
        return null;
    }

    // Match the handler subclass to the area type (each subclass overrides display/notification
    // behavior for its kind); base handler for anything else (proven sufficient for caves).
    private static AreaInstanceMarkerHandler BuildHandler(AreaInstance area, SSSGame.MarkerInfo mi)
    {
        try { if (area.TryCast<BiomeAreaInstance>() != null) return new BiomeAreaMarkerHandler(area, mi); } catch { }
        try { if (area.TryCast<TerrainAreaInstance>() != null) return new TerrainAreaMarkerHandler(area, mi); } catch { }
        return new AreaInstanceMarkerHandler(area, mi);
    }

    private static string NativeClassName(Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase o)
    {
        try
        {
            var cls = IL2CPP.il2cpp_object_get_class(o.Pointer);
            return System.Runtime.InteropServices.Marshal.PtrToStringAnsi(IL2CPP.il2cpp_class_get_name(cls)) ?? "?";
        }
        catch { return "?"; }
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

    private static void DestroyReqGos()
    {
        foreach (var go in _reqGos)
            if (go != null) UnityEngine.Object.Destroy(go);
        _reqGos.Clear();
    }

    private static void DumpAreas(WorldDataMap map, Vector3 spawn)
    {
        _caves.Clear();
        _allAreas.Clear();
        var sb = new StringBuilder();
        sb.AppendLine("==================== SeedScout: AREA INSTANCES ====================");
        sb.AppendLine($"seed='{SafeRealSeed()}'");
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
                _allAreas.Add((a, cat));
                if (cat == "Cave")
                {
                    try
                    {
                        var p = a.position;
                        _caves.Add(new Vector2(p.x, p.y));
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

    private static string Fmt(float f) => f == float.MaxValue ? "n/a" : f.ToString("0");
    private static string SafeCount(Func<int> f) { try { return f().ToString(); } catch { return "<err>"; } }

    private static string SafeRealSeed()
    {
        try
        {
            var session = UnityEngine.Object.FindAnyObjectByType<SSSGame.Network.NetworkSession>();
            if (session != null && session.Parameters != null && !string.IsNullOrEmpty(session.Parameters.seed))
            {
                return session.Parameters.seed;
            }
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"SeedScout: NetworkSession seed read error: {e.Message}"); }
        return "<unknown>";
    }

    private static void LogErrorOnce(string where, Exception e)
    {
        if (_loggedError) return;
        _loggedError = true;
        Plugin.Logger.LogWarning($"SeedScout: error in {where}: {e}");
    }
}
