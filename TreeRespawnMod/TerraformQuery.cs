using System;
using System.Collections.Generic;
using SSSGame;
using UnityEngine;

namespace TreeRespawnMod;

// Reusable spatial query: "has this world position been terraformed (heightmap-modified) by the
// player, via either the vanilla hoe or TerrainLevelerMod's bulldozer?" Both write the same
// per-cell TerraformingMap flag, so one read covers both.
//
// Built for TreeRespawnMod v1.8.0 (stop a chosen gatherable respawning on leveled ground) but
// written as a standalone helper, mirroring StructureQuery.cs in this folder: same "clear cache on
// world switch" contract, same per-read try/catch hardening.
//
// Ground truth (confirmed in-game 2026-08-05, see docs/architecture.md → Terrain/Terraforming):
//  - StreamingTerrainVS._terraformingMap reads null on a live chunk — do not use it.
//  - TerraformingMap.IsTileDirtyAt is NOT a terraformed-ground test (read false where
//    IsHeightmapModified read true on all 9 samples of a 3x3 patch) — do not use it.
//  - Patching StreamingTerrainVS.Init() floods the log with NREs and breaks world load — NEVER.
//    StreamingTerrainVS.Awake() is public, non-virtual, and confirmed safe to postfix.
//  - The map can be unresolvable for a chunk even when the player stands on it — a query can
//    legitimately come back "don't know yet", which is why Query() returns a 3-way result.
//
// Reference implementation this is modeled on: TerraformAuditMod/TerrainCapture.cs (registry +
// pooled-wrapper hardening) and TerraformAuditMod/TerraformDump.cs (map resolution + sampling).
internal static class TerraformQuery
{
    internal enum TerraformState { Unknown, Natural, Terraformed }

    // Every StreamingTerrainVS seen via the Awake postfix. Terrain chunks are pooled and reused by
    // the streaming system, so this list accumulates wrappers whose native object has since been
    // freed — GetChunks() below tests each entry's null-check independently and drops it on either
    // a false null-check or a throw (copied from TerraformAuditMod/TerrainCapture.cs).
    private static readonly List<StreamingTerrainVS> _chunks = new();

    // Fire-verification — a patch that silently never runs is indistinguishable from a wrong
    // approach (project rule).
    private static bool _awakeFireLogged;

    // Positions confirmed Terraformed — the only state worth caching. A cached Natural or Unknown
    // answer would blind later queries: ground can be terraformed later in the session (Natural),
    // and a chunk that isn't loaded yet may resolve on a later tick (Unknown). Terraformed is
    // permanent for a given world position, so it's safe (and cheap) to remember forever.
    private static readonly HashSet<string> _confirmedTerraformed = new();

    // Called from the StreamingTerrainVS.Awake postfix (Patches/Captures.cs).
    internal static void RegisterChunk(StreamingTerrainVS? instance)
    {
        if (!_awakeFireLogged)
        {
            _awakeFireLogged = true;
            Plugin.Logger.LogInfo("[TreeRespawnMod] StreamingTerrainVS.Awake postfix FIRED (first) — terraform query available.");
        }

        if (instance == null) return;
        lock (_chunks)
        {
            foreach (var existing in _chunks)
                if (ReferenceEquals(existing, instance)) return;
            _chunks.Add(instance);
        }
    }

    // Snapshot of currently-registered chunks, pruning entries whose native object has been freed.
    private static List<StreamingTerrainVS> GetChunks()
    {
        lock (_chunks)
        {
            for (int i = _chunks.Count - 1; i >= 0; i--)
            {
                try
                {
                    if (_chunks[i] == null) _chunks.RemoveAt(i);
                }
                catch
                {
                    _chunks.RemoveAt(i);
                }
            }
            return new List<StreamingTerrainVS>(_chunks);
        }
    }

    // Drop the chunk registry AND the positive-answer cache. Must be called on every world
    // switch/leave (see Plugin.NoteWorldLeft / OnWorldChanged) — both hold per-world state, and
    // reading a stale wrapper through a freed world's native object is this project's documented
    // native-crash class (no managed exception, WER coreclr.dll+0x1d1fdd).
    internal static void ClearCache()
    {
        lock (_chunks) { _chunks.Clear(); }
        _confirmedTerraformed.Clear();
    }

    // Has this world position been terraformed (heightmap-modified)? Three-valued on purpose:
    // Unknown must never be treated as Natural by the caller (see DayTracker's servicing rule) —
    // failing open there would respawn the very node the player asked to suppress, on any tick the
    // relevant chunk happens not to be loaded.
    internal static TerraformState Query(float x, float z)
    {
        string key = $"{x:F1}:{z:F1}";
        if (_confirmedTerraformed.Contains(key)) return TerraformState.Terraformed;

        try
        {
            var chunks = GetChunks();
            if (chunks.Count == 0) return TerraformState.Unknown;

            Vector2 flatPos = new Vector2(x, z);
            foreach (var vs in chunks)
            {
                if (vs == null) continue;
                Rect rect;
                try { rect = vs._terrainRect; }
                catch { continue; }

                if (!rect.Contains(flatPos)) continue;

                TerraformState result = QueryChunk(vs, rect, x, z);
                if (result == TerraformState.Terraformed)
                    _confirmedTerraformed.Add(key);
                return result;
            }

            return TerraformState.Unknown; // no chunk's rect covered this position
        }
        catch
        {
            return TerraformState.Unknown;
        }
    }

    // Resolve the TerraformingMap for a chunk known to contain (x, z) and sample it. Any failure
    // along the way is Unknown, not Natural — see the rationale on Query() above.
    private static TerraformState QueryChunk(StreamingTerrainVS vs, Rect rect, float x, float z)
    {
        try
        {
            TerrainChunk? hostChunk;
            try { hostChunk = vs._hostTerrainChunk; } catch { return TerraformState.Unknown; }
            if (hostChunk == null) return TerraformState.Unknown;

            TerrainDataHandler? handler;
            try { handler = hostChunk.DataHandler; } catch { return TerraformState.Unknown; }
            if (handler == null) return TerraformState.Unknown;

            WorldTileData? tile;
            try { tile = vs.GetTileData(); } catch { return TerraformState.Unknown; }
            if (tile == null) return TerraformState.Unknown;

            TerraformingMap? map;
            try { map = handler.GetTerraformingData(tile, DataAccessMode.FETCH); } catch { return TerraformState.Unknown; }
            if (map == null) return TerraformState.Unknown;

            int res;
            try { res = map.Resolution; } catch { return TerraformState.Unknown; }
            if (res <= 0) return TerraformState.Unknown;

            int sx = Mathf.Clamp((int)(((x - rect.xMin) / rect.width) * res), 0, res - 1);
            int sz = Mathf.Clamp((int)(((z - rect.yMin) / rect.height) * res), 0, res - 1);

            try
            {
                map.GetData(sx, sz, out int flags);
                bool modified = TerraformingMap.IsHeightmapModified(flags);
                return modified ? TerraformState.Terraformed : TerraformState.Natural;
            }
            catch
            {
                return TerraformState.Unknown;
            }
        }
        catch
        {
            return TerraformState.Unknown;
        }
    }
}
