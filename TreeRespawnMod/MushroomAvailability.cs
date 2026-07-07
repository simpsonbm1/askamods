using System;
using System.Collections.Generic;
using UnityEngine;
using SSSGame;
using SSSGame.Weather;
using SandSailorStudio.Inventory;

namespace TreeRespawnMod;

// v1.4.7 — makes wild mushrooms year-round + rain-independent by editing the game's own seasonal/weather
// availability gate. This is the shipped counterpart to the v1.4.6 read-only MushroomDiag research, which
// proved the model in-game (2026-07-07):
//
//   WeatherManager._descriptors : Dictionary<ItemInfo, BiomeItemAvailabilityData>
//   BiomeItemAvailabilityData.availabilityProcess : AvailabilityProcess   ← a ScriptableObject
//   AvailabilityProcess ANDs four condition lists; wild mushrooms fail on exactly two of them:
//     SeasonAvailabilityConditions  (list omits Winter)          → the winter cull  (cleared by IgnoreSeason)
//     OtherAvailabilityConditions   (mandatory IsRaining)        → the rain gate    (cleared by IgnoreRain)
//
// Clearing those two lists lifts both gates. The game's own replenishWhenAvailable + lifespan=1 loop then
// keeps mushrooms present regardless of season/weather — no per-frame upkeep from us. Each mushroom has its
// OWN AvailabilityProcess (confirmed: season lists differ per mushroom), so there is no shared-asset
// collateral onto crops/fish; the ItemNames substring filter keeps us scoped to mushrooms anyway.
//
// GOTCHA — AvailabilityProcess is a ScriptableObject, i.e. a PROCESS-GLOBAL shared asset, NOT per-world
// state. Consequences we rely on:
//   * The edit persists for the whole process (survives world reloads) and is naturally idempotent —
//     clearing an already-empty list is a harmless no-op. So re-running per world only re-catches any
//     mushroom process a prior world hadn't registered yet.
//   * We snapshot each process's ORIGINAL Season/Other contents BEFORE its first clear (keyed by item
//     name, once only) so a double-apply can never overwrite the snapshot with empties. The originals are
//     the only in-process copy of the vanilla data; note that a *restart* reverts for free anyway (a fresh
//     process reloads the SOs from disk), which is why there's no runtime restore path here.
//
// Because the SOs are process-global assets (not per-world native objects), caching wrappers/originals of
// them across world sessions is safe — the "never cache per-world wrappers" gotcha does not apply here.
internal static class MushroomAvailability
{
    private static bool _appliedThisWorld;
    private static float _firstSeenTime = -1f;

    // Snapshot of each matched process's ORIGINAL condition lists, keyed by item name (case-insensitive),
    // taken once before its first clear. Preserved across worlds on purpose (the SO edits are global).
    private static readonly Dictionary<string, Snapshot> _originals =
        new(StringComparer.OrdinalIgnoreCase);

    private sealed class Snapshot
    {
        public readonly List<SeasonConfig> Seasons = new();
        public readonly List<OtherCondition> Others = new();
    }

    // Called from DayTracker.ClearTransientState() on every world switch / quit-to-menu so the once-per-world
    // apply re-arms. Keeps _originals (they must outlive world switches — the edits are process-global).
    internal static void ResetForWorld()
    {
        _appliedThisWorld = false;
        _firstSeenTime = -1f;
    }

    // Called every frame from DayTracker.Update(). Waits ~5s after WeatherManager first reports registered
    // descriptors (same readiness the diagnostic uses, so late-registering mushrooms are all present), then
    // applies once per world. Cheap no-op afterwards; not host-gated — it's a local SO edit, and in co-op the
    // host's copy is what drives biome spawning (client edits are harmless).
    internal static void MaybeApply()
    {
        if (_appliedThisWorld) return;
        if (!Plugin.MushroomIgnoreRain.Value && !Plugin.MushroomIgnoreSeason.Value) { _appliedThisWorld = true; return; }

        var wm = MushroomDiag.GetWeatherManager();
        if (wm == null) return;

        int count = 0;
        try { count = wm._descriptors != null ? wm._descriptors.Count : 0; } catch { }
        if (count <= 0) return;

        if (_firstSeenTime < 0f) { _firstSeenTime = Time.realtimeSinceStartup; return; }
        if (Time.realtimeSinceStartup - _firstSeenTime < 5f) return;

        _appliedThisWorld = true;   // latch before applying so a throw can't cause per-frame re-attempts
        Apply(wm);
    }

    // One full sweep of the descriptor table: match mushrooms by name, snapshot originals once, clear the
    // gate list(s) the config asks for. Everything guarded — a throw on one entry must not abort the sweep.
    internal static void Apply(WeatherManager wm)
    {
        var log = Plugin.Logger;
        if (wm == null) return;

        bool ignoreRain = Plugin.MushroomIgnoreRain.Value;
        bool ignoreSeason = Plugin.MushroomIgnoreSeason.Value;
        string filter = Plugin.MushroomItemNames.Value ?? "";

        int matched = 0, edited = 0;
        try
        {
            var dict = wm._descriptors;
            var en = dict.GetEnumerator();
            while (en.MoveNext())
            {
                try
                {
                    var cur = en.Current;
                    var item = cur.Key;
                    var data = cur.Value;

                    string name = SafeName(item);
                    if (!MatchesFilter(name, filter)) continue;
                    matched++;

                    AvailabilityProcess? proc = null;
                    try { if (data != null) proc = data.availabilityProcess; } catch { }
                    if (proc == null) { log.LogWarning($"[MushroomAvailability] \"{name}\": no availabilityProcess — skipped."); continue; }

                    // Snapshot the vanilla lists ONCE per item (double-apply / per-world re-run safety).
                    if (!_originals.ContainsKey(name)) SnapshotOriginals(name, proc);

                    int seasonBefore = SafeCount(() => proc.SeasonAvailabilityConditions.Count);
                    int otherBefore  = SafeCount(() => proc.OtherAvailabilityConditions.Count);

                    bool did = false;
                    if (ignoreRain && otherBefore > 0)
                    {
                        try { proc.OtherAvailabilityConditions.Clear(); did = true; }
                        catch (Exception e) { log.LogWarning($"[MushroomAvailability] \"{name}\": clearing Other failed ({e.GetType().Name})."); }
                    }
                    if (ignoreSeason && seasonBefore > 0)
                    {
                        try { proc.SeasonAvailabilityConditions.Clear(); did = true; }
                        catch (Exception e) { log.LogWarning($"[MushroomAvailability] \"{name}\": clearing Season failed ({e.GetType().Name})."); }
                    }
                    if (did) edited++;

                    log.LogInfo(
                        $"[MushroomAvailability] \"{name}\" — rain gate {(ignoreRain ? $"cleared (was {otherBefore})" : "kept")}, " +
                        $"season gate {(ignoreSeason ? $"cleared (was {seasonBefore})" : "kept")}.");
                }
                catch (Exception e) { log.LogWarning("[MushroomAvailability] entry skipped: " + e.GetType().Name); }
            }
        }
        catch (Exception e) { log.LogError("[MushroomAvailability] sweep failed: " + e); }

        log.LogInfo(
            $"[MushroomAvailability] applied — matched {matched} item(s) for filter \"{filter}\", edited {edited} " +
            $"(IgnoreRain={ignoreRain}, IgnoreSeason={ignoreSeason}). Mushrooms should now read IsAvailable=True (F8 to verify).");
        if (matched == 0)
            log.LogWarning(
                "[MushroomAvailability] No descriptor matched the ItemNames filter — mushrooms may not be registered yet, " +
                "or the filter doesn't match their names. Check the F8 diagnostic dump for the exact item names.");
    }

    // Capture the current (vanilla) contents of both gate lists before we touch them.
    private static void SnapshotOriginals(string name, AvailabilityProcess proc)
    {
        var snap = new Snapshot();
        try
        {
            var s = proc.SeasonAvailabilityConditions;
            int n = s.Count;
            for (int i = 0; i < n; i++) { try { snap.Seasons.Add(s[i]); } catch { } }
        }
        catch { }
        try
        {
            var o = proc.OtherAvailabilityConditions;
            int n = o.Count;
            for (int i = 0; i < n; i++) { try { snap.Others.Add(o[i]); } catch { } }
        }
        catch { }
        _originals[name] = snap;
    }

    // Comma-separated, case-insensitive substring filter — same matching spirit as GatherOverridesMap.
    private static bool MatchesFilter(string name, string filter)
    {
        if (string.IsNullOrEmpty(name) || string.IsNullOrWhiteSpace(filter)) return false;
        foreach (var tokenRaw in filter.Split(','))
        {
            var token = tokenRaw.Trim();
            if (token.Length == 0) continue;
            if (name.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0) return true;
        }
        return false;
    }

    private static string SafeName(ItemInfo item)
    {
        try { var n = item.Name; if (!string.IsNullOrEmpty(n)) return n; } catch { }
        try { var n = item.localizedName; if (!string.IsNullOrEmpty(n)) return n; } catch { }
        return "?";
    }

    private static int SafeCount(Func<int> f)
    {
        try { return f(); } catch { return 0; }
    }
}
