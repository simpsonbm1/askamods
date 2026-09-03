using System;
using System.Collections.Generic;
using System.Diagnostics;
using HarmonyLib;
using SandSailorStudio.Inventory;
using SSSGame;
using UnityEngine;

namespace TreeRespawnMod.Patches;

// Two opposite levers on the same query, both keyed off what a stump structurally IS.
//
// 1. ProtectStumpsFromWoodcutters (v1.1.6): keep village woodcutters from grubbing firewood out of
//    leftover tree stumps (which destroys the stump and trips DayTracker's "stump cleared -> cancel
//    respawn", slowly deforesting the area).
//
//    Identity confirmed in-game (2026-06-26, TreeRespawnMod v1.1.3 diagnostic): the in-game "Tree Stump"
//    is NOT a separate object. It is the same Harvest_Wood_* BiomeItemInstance as the tree, sitting at
//    its LAST harvest piece (a Fir has pieces=2: trunk, then stump). Standing trees are the same object
//    at an earlier piece; the fallen trunk/branches are separate Item_Wood_* non-biome instances. A
//    stump returns CanProvideItem -> 6/8 firewood, which is what the woodcutter acts on (every other gate
//    already reads depleted; see architecture.md). Earlier gates that keyed on Plugin.PendingRespawns
//    failed: the HarvestInteraction transform position differs from BiomeItemInstance.GetPosition(), and
//    registration races the CanProvideItem query, so we gate STRUCTURALLY instead.
//
//    Stump = multi-piece BiomeItemInstance at its last piece. This deliberately does NOT touch:
//      - standing trees (same object, but not at the last piece): woodcutter still fells them;
//      - fallen logs / branches (Item_Wood_*, non-biome): woodcutter still hauls the wood.
//    The player clears a stump with axe damage (TakeDamage), not this AI query, so manual clearing,
//    and the "cleared stump = permanent" control, still works.
//
// 2. PreferStumpsForFirewood (v1.9.0, only when protection is OFF): the opposite wish, a player who
//    WANTS woodcutters to clear stumps. Vanilla only takes a stump once no loose log or long stick can
//    answer the firewood query (Nexus, tspringer5 2026-08-21: with nothing else left the workers
//    "immediately try to get firewood from a nearby stump"; 2026-09-03: with logs around "they're just
//    chopping up logs/etc. instead"). The game's ranking rule lives in native code we cannot read, so
//    instead of re-ranking we take the competition off the table: when the firewood query lands on a
//    NON-stump candidate (loose log, long stick, standing tree) and a live stump sits within
//    StumpPreferenceRadius metres of it, that candidate answers 0 and the search falls through to the
//    stump. Which item counts as "firewood" is learned from the stumps themselves (any item a stump
//    answers > 0 for), with an invariant-asset-name fallback ("Firewood"), so it is locale-safe and
//    never hides a log from a query for logs. Live stumps come from the same LiveHarvestInteractions
//    walk the manual-respawn hotkey has used since v1.2.x (structural stump test + !Destroyed), cached
//    for a couple of seconds so the per-candidate query stays cheap.
[HarmonyPatch(typeof(HarvestInteraction), nameof(HarvestInteraction.CanProvideItem))]
internal static class StumpProtectionPatch
{
    // Confirm-log once per instance so it doesn't spam the per-frame AI search.
    static readonly HashSet<int> _logged = new();
    static readonly HashSet<int> _loggedHidden = new();

    // Items a stump has answered > 0 for (ItemInfo.id): the locale-safe "firewood" identity.
    static readonly HashSet<int> _stumpItems = new();

    // Cached live-stump XZ positions (HarvestInteraction transform frame, the same frame as the
    // candidate we compare against, so the biome-vs-transform Y offset never matters).
    static readonly List<Vector2> _stumpXZ = new();
    static float _stumpCacheTime = -999f;
    const float StumpCacheSeconds = 2f;

    static void Postfix(HarvestInteraction __instance, ItemInfo itemInfo, ref int __result)
    {
        try
        {
            if (__result <= 0 || __instance == null) return;

            bool diag = Plugin.EnableDiagnostics.Value;
            bool isStump = IsStump(__instance);

            if (Plugin.ProtectStumps.Value)
            {
                if (!isStump) return;
                __result = 0; // stump provides nothing to villagers -> woodcutter leaves it to regrow
                if (diag && _logged.Add(__instance.GetInstanceID()))
                    Plugin.Logger.LogInfo(
                        "[TreeRespawnMod] Stump left to regrow - hidden from woodcutter firewood search.");
                return;
            }

            if (!Plugin.PreferStumps.Value) return;

            if (isStump)
            {
                // A stump answering > 0: this is an item stumps provide. Remember it (locale-safe id).
                int id = -1;
                try { if (itemInfo != null) id = itemInfo.id; } catch { }
                if (id >= 0 && _stumpItems.Add(id) && diag)
                {
                    string an = "", dn = "";
                    try { an = itemInfo!.name ?? ""; } catch { }
                    try { dn = itemInfo!.Name ?? ""; } catch { }
                    Plugin.Logger.LogInfo(
                        $"[TreeRespawnMod] [stump-first] stumps provide item id {id} asset=\"{an}\" display=\"{dn}\" (answered {__result}); non-stump sources of it now yield to nearby stumps.");
                }
                return;
            }

            if (!IsStumpItem(itemInfo)) return; // e.g. a query for logs to haul: never touched

            float radius = Plugin.StumpPreferenceRadius.Value;
            if (radius <= 0f) return;

            Vector3 pos;
            try { pos = __instance.transform.position; } catch { return; }
            float nearest = NearestStumpDistance(pos);
            bool hide = nearest <= radius;

            // Diagnostic: every NON-stump candidate that answers a stump item, once per instance, whether
            // or not it gets hidden. This is what tells us whether loose logs are firewood sources for the
            // world search at all (v1.9.0 run 2026-09-03 logged zero hides while stumps were being cleared).
            if (diag && _loggedHidden.Add(__instance.GetInstanceID()))
            {
                string nm = "", an = "";
                try { nm = __instance.gameObject?.name ?? ""; } catch { }
                try { an = itemInfo?.name ?? ""; } catch { }
                string near = float.IsPositiveInfinity(nearest) ? "no live stump" : $"nearest stump {nearest:0.0} m";
                Plugin.Logger.LogInfo(
                    $"[TreeRespawnMod] [stump-first] candidate \"{nm}\" at {Plugin.PosKey(pos)} answered {__result} for \"{an}\"; {near} of {_stumpXZ.Count}; {(hide ? "HID it" : "left it")} (radius {radius:0.#} m).");
            }

            if (!hide) return;
            __result = 0; // let the search fall through to the stump
        }
        catch (Exception ex)
        {
            try { Plugin.Logger.LogError($"[TreeRespawnMod] StumpProtectionPatch: {ex}"); } catch { }
        }
    }

    // Stump = multi-piece WOOD BiomeItemInstance at its last piece (single-piece = no stump; not-last = a
    // standing tree; non-biome = a loose log / debris). The wood check exists because the structural
    // test alone also matches a multi-piece ROCK at its last piece: in-game 2026-09-03 (v1.9.0 test)
    // such a node answered the "Item_Stone_Raw" (Large Stone) query with 2. Tree GameObjects are named
    // Harvest_Wood_<species><n> (confirmed in-game 2026-06-26, invariant asset name).
    static bool IsStump(HarvestInteraction hi)
    {
        var pieces = hi.harvestPieces;
        if (pieces == null || pieces.Count < 2) return false;
        if (hi.GetCurrentPieceIndex() != pieces.Count - 1) return false;
        var bi = hi._worldInstance?.TryCast<BiomeItemInstance>();
        if (bi == null) return false;
        return IsWood(hi, bi);
    }

    static readonly Dictionary<int, bool> _woodByInstance = new();
    static bool IsWood(HarvestInteraction hi, BiomeItemInstance bi)
    {
        int key = 0;
        try { key = hi.GetInstanceID(); } catch { }
        if (key != 0 && _woodByInstance.TryGetValue(key, out bool cached)) return cached;
        bool wood = false;
        try
        {
            var gn = hi.gameObject?.name;
            if (!string.IsNullOrEmpty(gn) && gn.IndexOf("Harvest_Wood", StringComparison.OrdinalIgnoreCase) >= 0) wood = true;
        }
        catch { }
        if (!wood)
        {
            try
            {
                var an = bi.Descriptor?.itemInfo?.name;
                if (!string.IsNullOrEmpty(an) && an.IndexOf("Wood", StringComparison.OrdinalIgnoreCase) >= 0) wood = true;
            }
            catch { }
        }
        if (key != 0) _woodByInstance[key] = wood;
        return wood;
    }

    static bool IsStumpItem(ItemInfo? info)
    {
        if (info == null) return false;
        try { if (_stumpItems.Contains(info.id)) return true; } catch { }
        // Fallback before any stump has been queried this session: invariant asset name.
        try
        {
            var an = info.name;
            if (!string.IsNullOrEmpty(an) && an.IndexOf("Firewood", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
        }
        catch { }
        return false;
    }

    // XZ distance to the nearest live stump, or +infinity when none is loaded.
    static float NearestStumpDistance(Vector3 pos)
    {
        float now = Time.unscaledTime;
        if (now - _stumpCacheTime > StumpCacheSeconds) RebuildStumpCache(now);
        float best = float.PositiveInfinity;
        for (int i = 0; i < _stumpXZ.Count; i++)
        {
            float dx = _stumpXZ[i].x - pos.x, dz = _stumpXZ[i].y - pos.z;
            float d2 = dx * dx + dz * dz;
            if (d2 < best) best = d2;
        }
        return float.IsPositiveInfinity(best) ? best : Mathf.Sqrt(best);
    }

    // Same walk as DayTracker's manual-respawn stump scan (confirmed in-game since v1.2.x).
    static void RebuildStumpCache(float now)
    {
        _stumpCacheTime = now;
        _stumpXZ.Clear();
        var sw = Plugin.EnableDiagnostics.Value ? Stopwatch.StartNew() : null;
        int walked = 0;
        try
        {
            var all = Plugin.LiveHarvestInteractions;
            if (all == null) return;
            all.RemoveWhere(hi => hi == null || hi.gameObject == null);
            foreach (var hi in all)
            {
                walked++;
                try
                {
                    if (hi == null || hi.gameObject == null || !hi.gameObject.scene.isLoaded) continue;
                    if (!IsStump(hi)) continue;
                    var bi = hi._worldInstance?.TryCast<BiomeItemInstance>();
                    if (bi == null || bi.Destroyed) continue;
                    var p = hi.transform.position;
                    _stumpXZ.Add(new Vector2(p.x, p.z));
                }
                catch { }
            }
        }
        catch { }
        if (sw != null && sw.Elapsed.TotalMilliseconds > 2.0)
            Plugin.Logger.LogInfo(
                $"[TreeRespawnMod] [Perf] stump-first cache rebuild {sw.Elapsed.TotalMilliseconds:0.0} ms; {walked} interactions walked, {_stumpXZ.Count} live stump(s).");
    }
}
