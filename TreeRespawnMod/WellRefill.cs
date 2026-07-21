using System;
using System.Collections.Generic;
using UnityEngine;
using SSSGame;

namespace TreeRespawnMod;

// v1.4.0 — configurable refill for CONSTRUCTED water structures (the Well building etc.).
// The GatherRespawn "Water" entry only covers the wild Natural Water Collector, which is a biome
// gather node; a built well is a Structure whose water lives in a charge-based GatherInteraction
// (_networkedCount), so none of the biome-instance respawn machinery applies. Instead we top the
// charges up ourselves at a configured rate via the game's own ReplenishCharges call.
//
// Driven from DayTracker.Update() (host-gated there via TryGetServerWeather) — no new
// MonoBehaviour, no Action subscriptions, no FindObjectsByType (SettlementManager is resolved
// with FindAnyObjectByType, the same pattern PollWorldId uses for StorageManager).
internal static class WellRefill
{
    // Rescan the settlement structure list this often (realtime seconds). Structures are added
    // rarely; between scans we service the cached interactions only.
    private const float RescanSeconds = 30f;

    // Per-well refill accounting, keyed by the owning structure's position (stable for a placed
    // building; wells at identical rounded positions can't coexist).
    //
    // Time accounting is anchor+carry (v1.4.3): AnchorGameTime is ONLY ever assigned from
    // ws.NetworkedCurrentGameTime and ONLY ever consumed through
    // ws.GetTimeDifferenceFromCurrentGameTimeInSeconds(anchor) — its internal unit is opaque and
    // must never be mixed with seconds arithmetic (v1.4.2 advanced it by "+= n*secondsPerCharge",
    // an unverified-unit assumption). CarrySeconds banks the sub-charge remainder in the diff
    // method's proven seconds-space instead.
    private sealed class WellState
    {
        public GatherInteraction Interaction = null!;
        public string Name = "";         // structure display name, for logs
        public float AnchorGameTime;     // NetworkedCurrentGameTime at last grant/re-anchor
        public float CarrySeconds;       // elapsed seconds not yet converted into a charge
        public bool WarnedOnce;          // first silent-path failure logged (avoid per-frame spam)
    }

    private static readonly Dictionary<string, WellState> _wells = new();
    private static SettlementManager? _settlements;
    private static DateTime _lastScan = DateTime.MinValue;
    private static DateTime _lastDiagSummary = DateTime.MinValue;
    private static DateTime _lastTickDiag = DateTime.MinValue;
    private static string? _scannedWorldId;
    private static string? _lastScanState;

    // World switch: cached interactions belong to the old world's scene.
    internal static void ClearTransientState()
    {
        _wells.Clear();
        _settlements = null;
        _lastScan = DateTime.MinValue;
        _scannedWorldId = null;
        _lastScanState = null;
    }

    // Called every frame from DayTracker.Update() once the world id is known and server weather
    // is available (host/solo only). All game reads/writes wrapped — a well being dismantled
    // mid-tick must not take the tracker down.
    internal static void Tick(SSSGame.Weather.WeatherSystem ws)
    {
        float chargesPerDay = Plugin.WellChargesPerDay.Value;
        if (chargesPerDay <= 0f) return;   // disabled — pure vanilla behavior

        if (_scannedWorldId != Plugin.CurrentWorldId)
        {
            ClearTransientState();
            _scannedWorldId = Plugin.CurrentWorldId;
        }

        if ((DateTime.UtcNow - _lastScan).TotalSeconds >= RescanSeconds)
        {
            _lastScan = DateTime.UtcNow;
            ScanForWells(ws);
        }
        if (_wells.Count == 0) return;

        float dayLength = ws.dayLength;
        if (dayLength <= 0f) return;
        float secondsPerCharge = dayLength / chargesPerDay;

        bool diag = Plugin.WellDiagnostics.Value;
        bool tickDiagDue = diag && TickDiagDue();
        List<string>? tickDiag = tickDiagDue ? new List<string>() : null;
        List<string>? toDrop = null;

        foreach (var kvp in _wells)
        {
            var well = kvp.Value;
            var gi = well.Interaction;

            // Stale wrapper (well dismantled / chunk change) → drop; next rescan re-finds live ones.
            bool dead = false;
            try { dead = gi == null || gi.gameObject == null; } catch { dead = true; }
            if (dead) { (toDrop ??= new List<string>()).Add(kvp.Key); continue; }

            float elapsed;
            try { elapsed = ws.GetTimeDifferenceFromCurrentGameTimeInSeconds(well.AnchorGameTime) + well.CarrySeconds; }
            catch (Exception e)
            {
                if (!well.WarnedOnce)
                {
                    well.WarnedOnce = true;
                    Plugin.Logger.LogWarning($"[TreeRespawnMod] [well] elapsed-time read failed for '{well.Name}' at {kvp.Key}: {e.GetType().Name} {e.Message}");
                }
                continue;
            }

            int avail, max;
            try
            {
                avail = gi!.CheckAvailableItemCount();
                max = gi.CheckMaximumItemCount();
            }
            catch (Exception e)
            {
                if (!well.WarnedOnce)
                {
                    well.WarnedOnce = true;
                    Plugin.Logger.LogWarning($"[TreeRespawnMod] [well] count read failed for '{well.Name}' at {kvp.Key}: {e.GetType().Name} {e.Message}");
                }
                continue;
            }

            tickDiag?.Add($"'{well.Name}' {avail}/{max} elapsed={elapsed:F0}s");

            if (avail >= max)
            {
                // Full: re-anchor + drop carry so no refill burst accumulates while topped up.
                try { well.AnchorGameTime = ws.NetworkedCurrentGameTime; } catch { }
                well.CarrySeconds = 0f;
                continue;
            }

            if (elapsed < secondsPerCharge) continue;

            int earned = (int)(elapsed / secondsPerCharge);
            int grant = Math.Min(earned, max - avail);
            try
            {
                gi!.ReplenishCharges(grant);
            }
            catch (Exception e)
            {
                Plugin.Logger.LogError($"[TreeRespawnMod] [well] ReplenishCharges failed for '{well.Name}' at {kvp.Key}: {e.Message}");
                continue;
            }
            // Consume whole charges earned; bank the remainder (seconds-space only — never write
            // arithmetic onto AnchorGameTime, its unit is opaque). Cap the carry at one charge so
            // a capacity-clamped grant can't bank a burst.
            try { well.AnchorGameTime = ws.NetworkedCurrentGameTime; } catch { }
            well.CarrySeconds = Math.Min(elapsed - earned * secondsPerCharge, secondsPerCharge);

            int postAvail = avail;
            try { postAvail = gi!.CheckAvailableItemCount(); } catch { }
            if (postAvail > avail)
            {
                // Diag-gated since v1.4.4 (feature confirmed): at high ChargesPerDay this fires
                // up to once per well per second — far too chatty for an always-on line.
                if (diag)
                    Plugin.Logger.LogInfo(
                        $"[TreeRespawnMod] [well] '{well.Name}' at {kvp.Key}: +{grant} water ({avail}->{postAvail}/{max}, " +
                        $"{elapsed:F0}s elapsed @ {secondsPerCharge:F0}s/charge)");
            }
            else
                // The call went through but the count didn't move — wrong method for this
                // interaction (or replication-only effect). Loud so the next log answers it.
                Plugin.Logger.LogWarning(
                    $"[TreeRespawnMod] [well] '{well.Name}' at {kvp.Key}: ReplenishCharges({grant}) was a NO-OP " +
                    $"(count stayed {avail}/{max})");
        }

        if (tickDiag is { Count: > 0 })
            Plugin.Logger.LogInfo(
                $"[TreeRespawnMod] [well] tick: dayLength={dayLength:F0}s → {secondsPerCharge:F0}s/charge | " +
                string.Join(" · ", tickDiag));

        if (toDrop != null)
            foreach (var k in toDrop)
            {
                if (Plugin.WellDiagnostics.Value)
                    Plugin.Logger.LogInfo($"[TreeRespawnMod] [well] dropping stale well entry at {k} (dismantled or unloaded)");
                _wells.Remove(k);
            }
    }

    // Enumerate settlement structures and cache every GatherInteraction that yields "Water".
    // A structure-hosted water gather can only be a built well/water collector — wild Natural
    // Water Collectors are biome nodes and never appear in Settlement.GetStructures().
    //
    // v1.4.2: the v1.4.1 run produced ZERO [well] lines — every failure exit here was silent or
    // under-logged. The settlement is now resolved from ALL known accessors (the `settlements`
    // list can lag behind load — "wait for load before querying"), and the summary line reports
    // the resolution state of each so a failure names itself in the log.
    private static void ScanForWells(SSSGame.Weather.WeatherSystem ws)
    {
        bool diag = Plugin.WellDiagnostics.Value;
        try
        {
            if (_settlements == null)
                _settlements = UnityEngine.Object.FindAnyObjectByType<SettlementManager>();
            if (_settlements == null)
            {
                if (diag && DiagSummaryDue())
                    Plugin.Logger.LogInfo("[TreeRespawnMod] [well] scan: no SettlementManager yet — will rescan");
                return;
            }

            bool loading = false;
            try { loading = _settlements.IsLoading; } catch { }

            // Candidate settlements from every accessor; duplicates are harmless (the well cache
            // is keyed by structure position, so a re-visit just refreshes the same entry).
            var candidates = new List<Settlement?>();
            int listCount = -1;   // -1 = list itself null
            try
            {
                var list = _settlements.settlements;
                if (list != null)
                {
                    listCount = list.Count;
                    foreach (var s in list) candidates.Add(s);
                }
            }
            catch { }
            bool havePlayer = false, haveCurrent = false, haveWorld = false;
            try { var s = _settlements.GetPlayerSettlement(); if (s != null) { candidates.Add(s); havePlayer = true; } } catch { }
            try { var s = _settlements.GetCurrentSettlement(); if (s != null) { candidates.Add(s); haveCurrent = true; } } catch { }
            try { var s = _settlements.worldSettlement; if (s != null) { candidates.Add(s); haveWorld = true; } } catch { }

            int structuresSeen = 0, wellsLive = 0, wellsNew = 0;
            var seenPos = new HashSet<string>();   // dedupe structures across duplicate settlements
            foreach (var settlement in candidates)
            {
                if (settlement == null) continue;
                var structures = settlement.GetStructures();
                if (structures == null) continue;

                foreach (var structure in structures)
                {
                    if (structure == null) continue;

                    // Same structure reachable via several settlement accessors — count it once.
                    string structPos;
                    try { structPos = Plugin.PosKey(structure.transform.position); } catch { continue; }
                    if (!seenPos.Add(structPos)) continue;
                    structuresSeen++;

                    // v1.4.1: the generic PLURAL GetComponentsInChildren<T>(bool) is missing through
                    // the interop trampoline (MissingMethodException at runtime — the singular
                    // GetComponentInChildren<T>(bool) is fine). Walk the hierarchy manually instead,
                    // the same workaround MineRefreshMod uses.
                    var gis = new List<GatherInteraction>();
                    try { CollectGatherInteractions(structure.transform, gis); }
                    catch { continue; }

                    foreach (var gi in gis)
                    {
                        // Locale-proof (v1.7.1): match the well's gatherable against the display name
                        // OR the invariant asset name ("Item_Elements_NaturalWaterCollector1" contains
                        // "Water"). Previously matched only the localized .Name, so in a non-English
                        // game (German "Wasser") no well was ever recognised and the refill silently
                        // did nothing. Verified additive against the v0.3.0 locale audit.
                        var wgii = gi?.GetGatherableItemInfo();
                        bool isWater = false;
                        try { var n = wgii?.Name; if (!string.IsNullOrEmpty(n) && n.IndexOf("Water", StringComparison.OrdinalIgnoreCase) >= 0) isWater = true; } catch { }
                        if (!isWater) { try { var a = wgii?.name; if (!string.IsNullOrEmpty(a) && a.IndexOf("Water", StringComparison.OrdinalIgnoreCase) >= 0) isWater = true; } catch { } }
                        if (!isWater) continue;

                        wellsLive++;
                        string posKey;
                        try { posKey = Plugin.PosKey(structure.transform.position); } catch { continue; }

                        if (_wells.TryGetValue(posKey, out var existing))
                        {
                            existing.Interaction = gi!;   // refresh — wrapper may have been recreated
                            continue;
                        }

                        string name = "";
                        try { name = structure.GetName() ?? ""; } catch { }
                        float now = 0f;
                        try { now = ws.NetworkedCurrentGameTime; } catch { }
                        _wells[posKey] = new WellState { Interaction = gi!, Name = name, AnchorGameTime = now };
                        wellsNew++;

                        int avail = -1, max = -1;
                        try { avail = gi!.CheckAvailableItemCount(); max = gi.CheckMaximumItemCount(); } catch { }
                        Plugin.Logger.LogInfo(
                            $"[TreeRespawnMod] [well] tracking '{name}' at {posKey} (water {avail}/{max})");
                    }
                }
            }

            // Resolution-state fingerprint: log immediately whenever it CHANGES (first scan, a
            // settlement accessor coming online, wells appearing), else at the 180s cadence.
            string state =
                $"loading={loading} list={(listCount < 0 ? "null" : listCount.ToString())} " +
                $"player={havePlayer} current={haveCurrent} world={haveWorld} " +
                $"structures={structuresSeen} waterGIs={wellsLive} tracked={_wells.Count}";
            if (diag && (state != _lastScanState || DiagSummaryDue()))
                Plugin.Logger.LogInfo($"[TreeRespawnMod] [well] scan: {state} (+{wellsNew} new)");
            _lastScanState = state;
        }
        catch (Exception e)
        {
            _settlements = null;   // re-resolve next scan
            if (diag) Plugin.Logger.LogInfo($"[TreeRespawnMod] [well] scan failed ({e.GetType().Name}: {e.Message}) — will rescan");
        }
    }

    // Manual replacement for GetComponentsInChildren<T>(bool) — that generic plural overload is
    // NOT available through the IL2CPP interop trampoline (MissingMethodException at runtime,
    // v1.4.0 finding; same family as the FindObjectsByType<T> gotcha). Per-object GetComponent<T>
    // plus a child walk is the proven pattern (MineRefreshMod).
    private static void CollectGatherInteractions(Transform node, List<GatherInteraction> results)
    {
        if (node == null) return;

        var gi = node.GetComponent<GatherInteraction>();
        if (gi != null) results.Add(gi);

        int childCount = node.childCount;
        for (int i = 0; i < childCount; i++)
        {
            var child = node.GetChild(i);
            if (child != null) CollectGatherInteractions(child, results);
        }
    }

    // The scan summary repeats every scan while diagnostics are on; throttle it so an idle session
    // logs at most one line per few minutes once wells are (or aren't) found.
    private static bool DiagSummaryDue()
    {
        if ((DateTime.UtcNow - _lastDiagSummary).TotalSeconds < 180) return false;
        _lastDiagSummary = DateTime.UtcNow;
        return true;
    }

    // Per-well elapsed/needed progress line — the "why hasn't it refilled yet" answer. One line
    // per minute covers all wells; only while WellDiagnostics is on.
    private static bool TickDiagDue()
    {
        if ((DateTime.UtcNow - _lastTickDiag).TotalSeconds < 60) return false;
        _lastTickDiag = DateTime.UtcNow;
        return true;
    }
}
