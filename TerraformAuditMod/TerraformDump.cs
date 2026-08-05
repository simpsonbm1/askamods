using System;
using System.Collections.Generic;
using BepInEx.Logging;
using SSSGame;
using UnityEngine;

namespace TerraformAuditMod;

// The probe itself. Every step is independently try/caught so one failure still lets the rest of
// the dump print - a probe must never go silent partway through the one run that answers the
// question.
internal static class TerraformDump
{
    private const int MaxScanResolution = 2048;

    public static void Run()
    {
        var log = Plugin.Logger;
        log.LogInfo("========== TERRAFORM AUDIT DUMP BEGIN ==========");

        // Step 1 - player position.
        Vector3 pos = default;
        bool havePos = false;
        try
        {
            var pc = UnityEngine.Object.FindAnyObjectByType<PlayerCharacter>();
            if (pc == null)
            {
                log.LogWarning("[TerraformAudit] No PlayerCharacter found - not in a loaded world?");
                log.LogInfo("========== TERRAFORM AUDIT DUMP END ==========");
                return;
            }
            pos = pc.transform.position;
            havePos = true;
        }
        catch (Exception ex)
        {
            log.LogWarning($"[TerraformAudit] Player position lookup failed: {ex.Message}");
        }
        if (!havePos)
        {
            log.LogInfo("========== TERRAFORM AUDIT DUMP END ==========");
            return;
        }

        // Step 2 - banner.
        List<StreamingTerrainVS> chunks = new();
        try
        {
            chunks = TerrainCapture.GetChunks();
            string posStr = pos.ToString();
            log.LogInfo($"[TerraformAudit] player pos = {posStr}, registered chunks = {chunks.Count}");
        }
        catch (Exception ex)
        {
            log.LogError($"[TerraformAudit] banner step failed: {ex}");
        }

        if (chunks.Count == 0)
        {
            log.LogWarning("[TerraformAudit] No terrain chunks captured - the Awake/Init capture "
                            + "patch never fired for any chunk. That failure IS the finding: fix "
                            + "the capture point before trusting any TerraformingMap read.");
            log.LogInfo("========== TERRAFORM AUDIT DUMP END ==========");
            return;
        }

        // Step 3 - choose the chunk containing the player, else nearest by rect centre.
        StreamingTerrainVS? chosen = null;
        Rect chosenRect = default;
        bool chosenByContains = false;
        try
        {
            Vector2 flatPos = new Vector2(pos.x, pos.z);
            float bestDist = float.MaxValue;
            StreamingTerrainVS? nearest = null;
            Rect nearestRect = default;

            foreach (var vs in chunks)
            {
                if (vs == null) continue;
                Rect rect;
                try { rect = vs._terrainRect; }
                catch { continue; }

                if (rect.Contains(flatPos))
                {
                    chosen = vs;
                    chosenRect = rect;
                    chosenByContains = true;
                    break;
                }

                Vector2 centre = rect.center;
                float d = Vector2.Distance(centre, flatPos);
                if (d < bestDist)
                {
                    bestDist = d;
                    nearest = vs;
                    nearestRect = rect;
                }
            }

            if (chosen == null && nearest != null)
            {
                chosen = nearest;
                chosenRect = nearestRect;
                chosenByContains = false;
                string distStr = bestDist.ToString();
                log.LogInfo("[TerraformAudit] No chunk's rect contains the player - using nearest "
                            + "chunk by rect-centre distance (" + distStr + ").");
            }
        }
        catch (Exception ex)
        {
            log.LogError($"[TerraformAudit] chunk selection failed: {ex}");
        }

        if (chosen == null)
        {
            log.LogWarning("[TerraformAudit] Could not choose a terrain chunk (all rect reads failed).");
            log.LogInfo("========== TERRAFORM AUDIT DUMP END ==========");
            return;
        }

        // Step 3b - print chunk identity immediately, before any map resolution can fail and
        // return early. This is the "chosen chunk rect never printed" bug from v0.2.0: the old
        // code printed the rect only after a successful map read, so a null-map return skipped it.
        try
        {
            string rectStr = chosenRect.ToString();
            string reasonStr = chosenByContains ? "rect contains player" : "chosen as nearest-centre";
            string nameStr = "?";
            try { nameStr = chosen.gameObject.name; } catch { }
            log.LogInfo($"[TerraformAudit] chosen chunk: rect={rectStr} reason=({reasonStr}) name={nameStr}");
        }
        catch (Exception ex)
        {
            log.LogError($"[TerraformAudit] chunk identity print failed: {ex}");
        }

        // Step 4 - resolve the TerraformingMap through multiple routes, since interop
        // link-properties can read null even when the underlying data exists (project rule:
        // reach the object by another route). Try each route in order, log every attempt.
        TerraformingMap? map = ResolveTerraformingMap(chosen, pos, log);
        if (map == null)
        {
            log.LogWarning("[TerraformAudit] All routes failed to resolve a TerraformingMap - "
                            + "giving up for this run.");
            log.LogInfo("========== TERRAFORM AUDIT DUMP END ==========");
            return;
        }

        try
        {
            log.LogInfo($"[TerraformAudit] map.Resolution={map.Resolution} map._resolution={map._resolution} "
                        + $"map._cellResolution={map._cellResolution} map._samplesPerCell={map._samplesPerCell} "
                        + $"map.IsLocked={map.IsLocked}");
        }
        catch (Exception ex)
        {
            log.LogError($"[TerraformAudit] map header read failed: {ex}");
            log.LogInfo("========== TERRAFORM AUDIT DUMP END ==========");
            return;
        }

        // Step 5 - sample index under the player.
        int res = 0;
        int sx = 0, sz = 0;
        try
        {
            res = map.Resolution;
            float u = (pos.x - chosenRect.xMin) / chosenRect.width;
            float v = (pos.z - chosenRect.yMin) / chosenRect.height;
            sx = Mathf.Clamp((int)(u * res), 0, res - 1);
            sz = Mathf.Clamp((int)(v * res), 0, res - 1);
            log.LogInfo($"[TerraformAudit] sample index under player: ({sx},{sz}) of resolution {res}");
        }
        catch (Exception ex)
        {
            log.LogError($"[TerraformAudit] sample index math failed: {ex}");
        }

        // Determine which read route works, once, and reuse it below.
        bool useGetData = TryReadOneSample(map, 0, 0, out _, out _, out _, out _, out _, out string route);
        log.LogInfo($"[TerraformAudit] sample read route: {route}");

        // Step 5b - the single-sample read at the player's exact position is the primary result:
        // the whole-map modified-sample count can't distinguish two leveling methods applied to
        // the same chunk, so this line (and not the whole-map scan below) is what answers the
        // question. Made unmissable with its own ALL-CAPS prefix.
        try
        {
            if (TryReadOneSample(map, sx, sz, out bool hm, out bool cv, out string tt, out bool dirty, out int rawFlags, out _))
            {
                log.LogInfo($"[TerraformAudit] AT PLAYER: sample=({sx},{sz}) rawFlags={rawFlags} "
                            + $"heightmapModified={hm} clearVegetationSet={cv} terrainType={tt}");
            }
            else
            {
                log.LogWarning($"[TerraformAudit] AT PLAYER: sample=({sx},{sz}) read failed via all routes.");
            }
        }
        catch (Exception ex)
        {
            log.LogError($"[TerraformAudit] player-sample read failed: {ex}");
        }

        // Step 6 - 3x3 neighbourhood.
        try
        {
            for (int dz = -1; dz <= 1; dz++)
            {
                for (int dx = -1; dx <= 1; dx++)
                {
                    int nx = Mathf.Clamp(sx + dx, 0, res - 1);
                    int nz = Mathf.Clamp(sz + dz, 0, res - 1);
                    if (TryReadOneSample(map, nx, nz, out bool hm, out bool cv, out string tt, out bool dirty, out _, out _))
                    {
                        log.LogInfo($"[TerraformAudit] neighbour ({nx},{nz}): heightmapModified={hm} "
                                    + $"clearVegetationSet={cv} terrainType={tt} tileDirty={dirty}");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            log.LogError($"[TerraformAudit] neighbourhood scan failed: {ex}");
        }

        // Step 8 - the decisive whole-map scan.
        try
        {
            RunWholeMapScan(map, chosenRect, res, useGetData, log);
        }
        catch (Exception ex)
        {
            log.LogError($"[TerraformAudit] whole-map scan failed: {ex}");
        }

        log.LogInfo("========== TERRAFORM AUDIT DUMP END ==========");
    }

    // Tries GetData first, falls back to IsTileDirtyAt if GetData throws. Returns which route
    // actually succeeded via `route` ("GetData" / "IsTileDirtyAt" / "none"). `rawFlags` is the
    // unmodified integer from GetData, or 0 when only the IsTileDirtyAt fallback route ran (that
    // route has no flags integer to report).
    private static bool TryReadOneSample(TerraformingMap map, int x, int y,
        out bool heightmapModified, out bool clearVegetationSet, out string terrainType,
        out bool tileDirty, out int rawFlags, out string route)
    {
        heightmapModified = false;
        clearVegetationSet = false;
        terrainType = "?";
        tileDirty = false;
        rawFlags = 0;
        route = "none";

        try
        {
            map.GetData(x, y, out int flags);
            rawFlags = flags;
            heightmapModified = TerraformingMap.IsHeightmapModified(flags);
            clearVegetationSet = TerraformingMap.IsClearVegetationSet(flags);
            try { terrainType = TerraformingMap.GetTerrainType(flags).ToString(); } catch { terrainType = "?"; }
            try { tileDirty = map.IsTileDirtyAt(x, y); } catch { }
            route = "GetData";
            return true;
        }
        catch
        {
            // fall through to IsTileDirtyAt-only fallback
        }

        try
        {
            tileDirty = map.IsTileDirtyAt(x, y);
            heightmapModified = tileDirty; // best available proxy when GetData is unusable
            route = "IsTileDirtyAt";
            return true;
        }
        catch
        {
            return false;
        }
    }

    // Change 1 - resolve the TerraformingMap through several routes, in order, stopping at the
    // first that returns non-null. Each route is independently try/caught and logs one line
    // stating the route name and whether it returned a map, null, or threw - this per-route
    // logging is the point of the exercise, so it is never collapsed into a single summary line.
    private static TerraformingMap? ResolveTerraformingMap(StreamingTerrainVS vs, Vector3 playerPos, ManualLogSource log)
    {
        // Route A - the current (v0.2.0) direct property read. Kept so we confirm it is still null.
        try
        {
            TerraformingMap? map = vs._terraformingMap;
            if (map != null)
            {
                log.LogInfo("[TerraformAudit] route chunk._terraformingMap: returned a map.");
                return map;
            }
            log.LogInfo("[TerraformAudit] route chunk._terraformingMap: null.");
        }
        catch (Exception ex)
        {
            log.LogInfo($"[TerraformAudit] route chunk._terraformingMap: threw {ex.GetType().Name}: {ex.Message}");
        }

        // Route B - go through the host TerrainChunk's DataHandler instead of the VS's own
        // (possibly stale/unset) link property.
        try
        {
            TerrainChunk? hostChunk = vs._hostTerrainChunk;
            if (hostChunk == null)
            {
                log.LogInfo("[TerraformAudit] route hostChunk.DataHandler: hostChunk (_hostTerrainChunk) is null.");
            }
            else
            {
                TerrainDataHandler? handler = hostChunk.DataHandler;
                if (handler == null)
                {
                    log.LogInfo("[TerraformAudit] route hostChunk.DataHandler: DataHandler is null.");
                }
                else
                {
                    WorldTileData? tile = null;
                    string tileSource = "none";

                    try { tile = vs.GetTileData(); if (tile != null) tileSource = "GetTileData"; }
                    catch { }

                    if (tile == null)
                    {
                        try { tile = vs.WorldData; if (tile != null) tileSource = "WorldData"; }
                        catch { }
                    }

                    if (tile == null)
                    {
                        try { tile = hostChunk.GetWorldTileData(); if (tile != null) tileSource = "hostChunk.GetWorldTileData"; }
                        catch { }
                    }

                    if (tile == null)
                    {
                        log.LogInfo("[TerraformAudit] route hostChunk.DataHandler: no tile source "
                                    + "(GetTileData/WorldData/hostChunk.GetWorldTileData) returned non-null.");
                    }
                    else
                    {
                        TerraformingMap? map = handler.GetTerraformingData(tile, DataAccessMode.FETCH);
                        if (map != null)
                        {
                            log.LogInfo($"[TerraformAudit] route hostChunk.DataHandler: returned a map "
                                        + $"(tile from {tileSource}).");
                            return map;
                        }
                        log.LogInfo($"[TerraformAudit] route hostChunk.DataHandler: null (tile from {tileSource}).");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            log.LogInfo($"[TerraformAudit] route hostChunk.DataHandler: threw {ex.GetType().Name}: {ex.Message}");
        }

        // Route C - go through the world-level WorldDataManager, bypassing the chunk entirely.
        try
        {
            var manager = UnityEngine.Object.FindAnyObjectByType<WorldDataManager>();
            if (manager == null)
            {
                log.LogInfo("[TerraformAudit] route WorldDataManager.OpenData: no WorldDataManager found.");
            }
            else
            {
                TerrainDataHandler? handler = null;
                try
                {
                    handler = manager.GetDataHandler<TerrainDataHandler>(WorldDataSlot.TERRAIN);
                }
                catch (Exception ex)
                {
                    log.LogInfo("[TerraformAudit] route WorldDataManager.OpenData: GetDataHandler<TerrainDataHandler> "
                                + $"threw {ex.GetType().Name}: {ex.Message}");
                }

                if (handler == null)
                {
                    log.LogInfo("[TerraformAudit] route WorldDataManager.OpenData: handler is null.");
                }
                else
                {
                    WorldTileData? tile = null;
                    try
                    {
                        tile = manager.OpenData(playerPos.x, playerPos.z, DataAccessMode.FETCH);
                    }
                    catch (Exception ex)
                    {
                        log.LogInfo($"[TerraformAudit] route WorldDataManager.OpenData: OpenData threw "
                                    + $"{ex.GetType().Name}: {ex.Message}");
                    }

                    if (tile == null)
                    {
                        log.LogInfo("[TerraformAudit] route WorldDataManager.OpenData: OpenData returned null tile.");
                    }
                    else
                    {
                        TerraformingMap? map = handler.GetTerraformingData(tile, DataAccessMode.FETCH);
                        if (map != null)
                        {
                            log.LogInfo("[TerraformAudit] route WorldDataManager.OpenData: returned a map.");
                            return map;
                        }
                        log.LogInfo("[TerraformAudit] route WorldDataManager.OpenData: null.");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            log.LogInfo($"[TerraformAudit] route WorldDataManager.OpenData: threw {ex.GetType().Name}: {ex.Message}");
        }

        log.LogWarning("[TerraformAudit] All three TerraformingMap routes failed "
                        + "(chunk._terraformingMap, hostChunk.DataHandler, WorldDataManager.OpenData).");
        return null;
    }

    private static void RunWholeMapScan(TerraformingMap map, Rect rect, int res, bool getDataWorks, BepInEx.Logging.ManualLogSource log)
    {
        if (res <= 0)
        {
            log.LogWarning("[TerraformAudit] whole-map scan skipped: resolution <= 0.");
            return;
        }
        if (res > MaxScanResolution)
        {
            log.LogWarning($"[TerraformAudit] whole-map scan skipped: resolution {res} exceeds "
                            + $"the {MaxScanResolution} safety cap (would risk hanging the game).");
            return;
        }

        long total = (long)res * res;
        long modifiedCount = 0;
        bool usedFallback = false;

        float minX = float.MaxValue, maxX = float.MinValue;
        float minZ = float.MaxValue, maxZ = float.MinValue;

        for (int y = 0; y < res; y++)
        {
            for (int x = 0; x < res; x++)
            {
                bool hm;
                if (getDataWorks)
                {
                    try
                    {
                        map.GetData(x, y, out int flags);
                        hm = TerraformingMap.IsHeightmapModified(flags);
                    }
                    catch
                    {
                        usedFallback = true;
                        try { hm = map.IsTileDirtyAt(x, y); } catch { continue; }
                    }
                }
                else
                {
                    usedFallback = true;
                    try { hm = map.IsTileDirtyAt(x, y); } catch { continue; }
                }

                if (hm)
                {
                    modifiedCount++;
                    float wx = rect.xMin + (x / (float)res) * rect.width;
                    float wz = rect.yMin + (y / (float)res) * rect.height;
                    if (wx < minX) minX = wx;
                    if (wx > maxX) maxX = wx;
                    if (wz < minZ) minZ = wz;
                    if (wz > maxZ) maxZ = wz;
                }
            }
        }

        string label = usedFallback ? "IsTileDirtyAt (GetData fallback)" : (getDataWorks ? "GetData" : "IsTileDirtyAt");
        log.LogInfo($"[TerraformAudit] WHOLE-MAP SCAN ({label}): modified={modifiedCount} of total={total}");

        if (modifiedCount > 0)
        {
            string boundsStr = new Bounds(
                new Vector3((minX + maxX) / 2f, 0f, (minZ + maxZ) / 2f),
                new Vector3(maxX - minX, 0f, maxZ - minZ)).ToString();
            log.LogInfo($"[TerraformAudit] modified-sample world bounds: min=({minX},{minZ}) max=({maxX},{maxZ}) "
                        + $"bounds={boundsStr}");
        }
        else
        {
            log.LogInfo("[TerraformAudit] no modified samples found on this chunk.");
        }
    }
}
