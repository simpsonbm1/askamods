using System;
using System.Collections.Generic;
using UnityEngine;
using SSSGame;

namespace TreeRespawnMod;

// Reusable spatial query: "is a world position inside (or within a margin of) a player-built structure's
// footprint?"
//
// Built for TreeRespawnMod v1.5.x (stop trees respawning up through buildings) but deliberately written as
// a standalone, mod-agnostic helper — any future mod that needs "don't do X where the player built
// something" can call IsBlockedByStructure(x, z, margin).
//
// v1.5.1 — FOOTPRINT-BASED (was center-distance in v1.5.0). v1.5.0 measured distance to each structure's
// origin, which is inconsistent by building size: a fixed radius blocks a tree next to a small hut (origin
// close) but MISSES a tree at the far edge of a longhouse (origin far) — confirmed in-game 2026-07-07, a
// tree regrew at a longhouse corner while the same distance blocked correctly at a small building. So we
// now compute each building's real footprint: the union of its non-trigger collider bounds as a horizontal
// AABB (rectangle), and test whether the point falls inside that rectangle (optionally grown by `margin`).
//
// How the building list is obtained: the settlement's own GetStructures() walk (the same authoritative
// list WellRefill uses — confirmed to return every placed building, ~239 in a mature base), then per
// structure a manual hierarchy walk collecting UnityEngine.Collider bounds (the plural
// GetComponentsInChildren<T> is missing through the interop trampoline — same manual-walk workaround
// WellRefill/MineRefresh use for their component searches). This reads host-side data present regardless of
// where the player stands or what terrain is streamed in, so it answers correctly even for a tree far away.
//
// Design choices / caveats:
//  - Footprint = union of non-trigger collider AABBs (walls/floor/fences). This is tighter and more correct
//    than renderer bounds (which include roof overhangs). An open pen with only perimeter-post colliders
//    still yields the rectangle spanning those posts, so its interior is covered.
//  - The AABB is axis-aligned, so a rotated rectangular building gets a slightly generous box at the
//    corners — harmless for a "keep trees out of the build" check, and `margin` is a deliberate extra
//    buffer on top.
//  - Fail-OPEN: any resolve/read failure returns false (not blocked). A backstop must never suppress a
//    legitimate respawn just because the settlement wasn't resolvable — the durable BlockedPositions set is
//    the primary guard; this is the secondary net.
//  - Footprints are cached (structures are static; new buildings are rare) so a burst of checks (many trees
//    due at once after a reload, or a world-load flood of catch-up registrations) walks the building list at
//    most once per cache window.
//  - Cache MUST be cleared on world switch (positions are world-specific) — Plugin does this in
//    NoteWorldLeft()/OnWorldChanged().
internal static class StructureQuery
{
    // Structures don't move and are placed rarely — a long cache keeps the per-structure collider walk
    // infrequent. A newly-placed building is recognised within this window, which is plenty (respawn
    // timers are in in-game days).
    private const float CacheSeconds = 15f;

    // Horizontal footprint rectangle (world space), min/max on X and Z.
    private readonly struct FootRect
    {
        public readonly float MinX, MinZ, MaxX, MaxZ;
        public FootRect(float minX, float minZ, float maxX, float maxZ) { MinX = minX; MinZ = minZ; MaxX = maxX; MaxZ = maxZ; }
        public bool Contains(float x, float z, float margin) =>
            x >= MinX - margin && x <= MaxX + margin && z >= MinZ - margin && z <= MaxZ + margin;
    }

    private static SettlementManager? _sm;
    private static List<FootRect>? _cache;
    private static DateTime _cacheTime = DateTime.MinValue;

    // Drop the cached manager + footprint snapshot. Call on every world switch/leave.
    internal static void ClearCache()
    {
        _sm = null;
        _cache = null;
        _cacheTime = DateTime.MinValue;
    }

    // True once a usable footprint snapshot exists (settlement resolved AND at least one building walked).
    // Building load lags the world load by a beat, so this reads false for the first few seconds of a
    // session — the caller (DayTracker) uses that to HOLD under-structure respawns until buildings are
    // known, instead of respawning a tree it can't yet check. A genuinely building-less world never sets
    // this true, so the caller must pair it with a time cap. Triggers a (cached) build attempt.
    internal static bool DataReady
    {
        get { var f = GetFootprints(); return f != null && f.Count > 0; }
    }

    // True if (x, z) is inside any placed structure's footprint, grown by `margin` metres. `margin` is a
    // buffer around the real footprint (0 = exactly the footprint). Fail-open on any error.
    internal static bool IsBlockedByStructure(float x, float z, float margin)
    {
        try
        {
            var rects = GetFootprints();
            if (rects == null || rects.Count == 0) return false;
            float m = margin > 0f ? margin : 0f;
            foreach (var r in rects)
                if (r.Contains(x, z, m)) return true;
            return false;
        }
        catch { return false; }
    }

    // Snapshot of every placed structure's footprint rectangle, refreshed at most every CacheSeconds.
    // Returns the last good snapshot (possibly slightly stale within the window, or null before first success).
    private static List<FootRect>? GetFootprints()
    {
        if (_cache != null && (DateTime.UtcNow - _cacheTime).TotalSeconds < CacheSeconds)
            return _cache;

        try
        {
            if (_sm == null)
                _sm = UnityEngine.Object.FindAnyObjectByType<SettlementManager>();
            if (_sm == null) return _cache;   // manager not up yet — keep whatever we had

            // Settlements via the getter methods (the list accessor stays null even when loaded).
            var candidates = new List<Settlement>();
            try { var s = _sm.GetPlayerSettlement();  if (s != null) candidates.Add(s); } catch { }
            try { var s = _sm.GetCurrentSettlement(); if (s != null) candidates.Add(s); } catch { }
            try { var s = _sm.worldSettlement;        if (s != null) candidates.Add(s); } catch { }

            var result = new List<FootRect>();
            var seen = new HashSet<string>();  // dedupe structures reachable via multiple settlement accessors
            foreach (var settlement in candidates)
            {
                if (settlement == null) continue;
                var structures = settlement.GetStructures();
                if (structures == null) continue;
                foreach (var structure in structures)
                {
                    if (structure == null) continue;
                    Vector3 origin;
                    try { origin = structure.GetPosition(); } catch { continue; }
                    if (!seen.Add(Plugin.PosKey(origin))) continue; // same building via player+world settlement → once
                    if (TryComputeFootprint(structure, origin, out var rect))
                        result.Add(rect);
                }
            }

            // Only cache a NON-EMPTY snapshot. During world load the settlement resolves a beat after the
            // manager does, so an early walk returns empty — caching that would blind us to buildings for a
            // full cache window (the load-race the structure check hit in v1.5.1). Retry each call until at
            // least one structure is found; a genuinely building-less world just keeps returning empty
            // (cheap) and the DayTracker grace cap stops it deferring forever.
            if (result.Count > 0)
            {
                _cache = result;
                _cacheTime = DateTime.UtcNow;
                return _cache;
            }
            return result; // empty, uncached — settlement not fully loaded yet, or no buildings
        }
        catch
        {
            _sm = null;   // re-resolve next time
            return _cache;
        }
    }

    // Union of the structure's non-trigger collider bounds as a horizontal rectangle. Falls back to a
    // degenerate point-rect at the structure origin if it has no usable colliders (so it still contributes).
    private static bool TryComputeFootprint(Structure structure, Vector3 origin, out FootRect rect)
    {
        rect = default;
        float minX = 0f, minZ = 0f, maxX = 0f, maxZ = 0f;
        bool have = false;
        try
        {
            var colliders = new List<Collider>();
            CollectColliders(structure.transform, colliders);
            foreach (var col in colliders)
            {
                if (col == null) continue;
                bool trigger = true;
                try { trigger = col.isTrigger; } catch { }
                if (trigger) continue;
                Bounds b;
                try { b = col.bounds; } catch { continue; }
                float cMinX = b.center.x - b.extents.x, cMaxX = b.center.x + b.extents.x;
                float cMinZ = b.center.z - b.extents.z, cMaxZ = b.center.z + b.extents.z;
                if (!have) { minX = cMinX; maxX = cMaxX; minZ = cMinZ; maxZ = cMaxZ; have = true; }
                else
                {
                    if (cMinX < minX) minX = cMinX;
                    if (cMaxX > maxX) maxX = cMaxX;
                    if (cMinZ < minZ) minZ = cMinZ;
                    if (cMaxZ > maxZ) maxZ = cMaxZ;
                }
            }
        }
        catch { }

        if (have) { rect = new FootRect(minX, minZ, maxX, maxZ); return true; }
        // No colliders resolved — degenerate rect at the origin so the structure still blocks its own
        // spot (± margin). Better than dropping it entirely.
        rect = new FootRect(origin.x, origin.z, origin.x, origin.z);
        return true;
    }

    // Manual replacement for GetComponentsInChildren<Collider>(true) — the generic plural overload is NOT
    // available through the interop trampoline (MissingMethodException at runtime; same family as the
    // FindObjectsByType<T> gotcha). Per-node GetComponent<Collider>() plus a child walk is the proven
    // pattern (WellRefill.CollectGatherInteractions).
    private static void CollectColliders(Transform node, List<Collider> results)
    {
        if (node == null) return;
        Collider col = null!;
        try { col = node.GetComponent<Collider>(); } catch { }
        if (col != null) results.Add(col);

        int childCount;
        try { childCount = node.childCount; } catch { return; }
        for (int i = 0; i < childCount; i++)
        {
            Transform? child = null;
            try { child = node.GetChild(i); } catch { }
            if (child != null) CollectColliders(child, results);
        }
    }
}
