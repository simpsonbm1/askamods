using System;
using System.Text;
using UnityEngine;
using SSSGame;
using SSSGame.Weather;
using SandSailorStudio.Inventory;

namespace TreeRespawnMod;

// Read-only research diagnostic (v1.4.6) for the "mushrooms year-round / rain-independent" idea.
//
// The game gates seasonal/weather-restricted resources through ONE system:
//   BiomeItemDescriptor  →  WeatherManager._descriptors : Dictionary<ItemInfo, BiomeItemAvailabilityData>
//   BiomeItemAvailabilityData.availabilityProcess : AvailabilityProcess
//   AvailabilityProcess.CheckAll(WeatherEventData) ANDs its four condition lists:
//     SeasonAvailabilityConditions (List<SeasonConfig>)          ← which seasons  (winter cull)
//     TimeAvailabilityConditions   (List<TimeCondition>)         ← NormalizedSeasonTime / DayOfYear
//     OtherAvailabilityConditions  (List<OtherCondition>)        ← IsRaining / IsWet (rain gate)
//     ProgressingWeatherConditionAvailabilityConditions
//   WeatherManager.HandleDescriptors()/IsAvailable(process) drive it on each weather/season change.
//
// This dump enumerates every registered descriptor, prints its gate conditions, and calls the game's
// own evaluators (CheckAll / IsAvailable) so we can SEE — in-game, per season/weather — whether wild
// mushrooms are actually gated here. It writes NOTHING; it only reads. Nothing is confirmed until we
// read this in a live world (see NEW_MOD_IDEAS_PLAN.md → "Mushrooms year-round / rain-independent").
internal static class MushroomDiag
{
    private static bool _autoDumpDone;
    private static float _firstSeenTime = -1f;

    // Called from DayTracker.ClearTransientState() on every world switch / quit-to-menu so the
    // one-shot auto-dump re-arms for the next world.
    internal static void ResetForWorld()
    {
        _autoDumpDone = false;
        _firstSeenTime = -1f;
    }

    // One-shot auto-dump, ~5s after WeatherManager first reports registered descriptors (gives the
    // game time to finish registering everything). Gated by the MushroomDiagnostics config.
    internal static void MaybeAutoDump()
    {
        if (_autoDumpDone) return;
        if (!Plugin.MushroomDiagnostics.Value) { _autoDumpDone = true; return; }

        var wm = GetWeatherManager();
        if (wm == null) return;

        int count = 0;
        try { count = wm._descriptors != null ? wm._descriptors.Count : 0; } catch { }
        if (count <= 0) return;

        if (_firstSeenTime < 0f) { _firstSeenTime = Time.realtimeSinceStartup; return; }
        if (Time.realtimeSinceStartup - _firstSeenTime < 5f) return;

        _autoDumpDone = true;
        Dump("auto (world load)");
    }

    internal static WeatherManager? GetWeatherManager()
    {
        try { return UnityEngine.Object.FindAnyObjectByType<WeatherManager>(); }
        catch { return null; }
    }

    // Full read-only dump. Fully details mushroom entries + anything carrying a gate; skips ungated
    // non-mushroom noise. Calls the game's own CheckAll/IsAvailable to show the LIVE verdict.
    internal static void Dump(string reason)
    {
        var log = Plugin.Logger;
        var wm = GetWeatherManager();
        if (wm == null) { log.LogWarning("[MushroomDiag] WeatherManager not found — is a world loaded?"); return; }

        WeatherEventData? summary = null;
        try { summary = wm.GetSummary(); } catch { }

        string ctx = "?";
        try
        {
            if (summary != null)
                ctx = "season=" + SeasonName(summary.CurrentSeason)
                    + " normSeasonTime=" + summary.NormalizedSeasonTime.ToString("F3")
                    + " dayOfYear=" + summary.DayOfYear
                    + " raining=" + summary.IsRaining
                    + " wet=" + summary.IsWet
                    + " snowing=" + summary.IsSnowing
                    + " cold=" + summary.IsCold
                    + " night=" + summary.IsNight;
        }
        catch (Exception e) { ctx = "err:" + e.GetType().Name; }

        int total = 0;
        try { total = wm._descriptors.Count; } catch { }
        log.LogInfo($"[MushroomDiag] ===== dump ({reason}) — {total} registered descriptor(s) — context: {ctx} =====");

        int scanned = 0, mushrooms = 0;
        try
        {
            var dict = wm._descriptors;
            var en = dict.GetEnumerator();
            while (en.MoveNext())
            {
                scanned++;
                try
                {
                    var cur = en.Current;
                    var item = cur.Key;
                    var data = cur.Value;
                    string name = SafeName(item);
                    bool isMushroom = name.IndexOf("Mushroom", StringComparison.OrdinalIgnoreCase) >= 0;
                    if (isMushroom) mushrooms++;

                    var proc = SafeProc(data);
                    int seasonN = SafeCount(() => proc!.SeasonAvailabilityConditions.Count);
                    int timeN   = SafeCount(() => proc!.TimeAvailabilityConditions.Count);
                    int otherN  = SafeCount(() => proc!.OtherAvailabilityConditions.Count);
                    int progN   = SafeCount(() => proc!.ProgressingWeatherConditionAvailabilityConditions.Count);
                    bool gated  = (seasonN + timeN + otherN + progN) > 0;

                    // Skip ungated non-mushroom entries to keep the log readable.
                    if (!isMushroom && !gated) continue;

                    var sb = new StringBuilder();
                    sb.Append("[MushroomDiag] item=\"").Append(name).Append('"');
                    try { sb.Append(" id=").Append(item.id); } catch { }
                    if (isMushroom) sb.Append("  <== MUSHROOM");

                    if (data != null)
                    {
                        try
                        {
                            sb.Append("\n    availData: remainingDays=").Append(data.remainingDays)
                              .Append(" replenishOnAvail=").Append(data.ReplenishOnAvailable)
                              .Append(" lastResult=").Append(data.LastResult)
                              .Append(" curDescState=").Append(data.CurrentDescriptorsState);
                        }
                        catch { }
                    }

                    if (proc != null)
                    {
                        try
                        {
                            sb.Append("\n    process: lifespan=").Append(proc.Lifespan)
                              .Append(" replenishWhenAvail=").Append(proc.ReplenishWhenAvailable)
                              .Append(" canRun=").Append(proc.CanRun);
                        }
                        catch { }
                        sb.Append("\n    Season[").Append(seasonN).Append("]: ").Append(SeasonList(proc, seasonN));
                        sb.Append("\n    Time[").Append(timeN).Append("]: ").Append(TimeList(proc, timeN));
                        sb.Append("\n    Other[").Append(otherN).Append("]: ").Append(OtherList(proc, otherN));
                        if (progN > 0) sb.Append("\n    ProgWeather[").Append(progN).Append("]");

                        string liveEval;
                        try
                        {
                            bool checkAll = summary != null && proc.CheckAll(summary);
                            bool isAvail  = wm.IsAvailable(proc);
                            liveEval = "CheckAll=" + checkAll + " IsAvailable=" + isAvail;
                        }
                        catch (Exception e) { liveEval = "err:" + e.GetType().Name; }
                        sb.Append("\n    live: ").Append(liveEval);
                    }
                    else sb.Append("\n    process: <null>");

                    log.LogInfo(sb.ToString());
                }
                catch (Exception e)
                {
                    log.LogWarning("[MushroomDiag] entry skipped: " + e.GetType().Name);
                }
            }
        }
        catch (Exception e)
        {
            log.LogError("[MushroomDiag] enumerate failed: " + e);
        }

        log.LogInfo($"[MushroomDiag] ===== end dump — {mushrooms} mushroom entr(y/ies), {scanned} descriptor(s) scanned =====");
        if (mushrooms == 0)
            log.LogWarning("[MushroomDiag] No 'Mushroom' item in WeatherManager._descriptors — either mushrooms " +
                "aren't gated by this availability system, or aren't registered yet. Re-run the hotkey after the " +
                "world is fully loaded, ideally in a different season/weather.");
    }

    // ── formatting helpers (all guarded — a throw on one element must not abort the dump) ──────────

    private static string SeasonName(SeasonConfig? sc)
    {
        if (sc == null) return "<null>";
        try { var n = sc.name; return string.IsNullOrEmpty(n) ? "?" : n; } catch { return "?"; }
    }

    private static string SafeName(ItemInfo item)
    {
        try { var n = item.Name; if (!string.IsNullOrEmpty(n)) return n; } catch { }
        try { var n = item.localizedName; if (!string.IsNullOrEmpty(n)) return n; } catch { }
        return "?";
    }

    private static AvailabilityProcess? SafeProc(BiomeItemAvailabilityData? data)
    {
        if (data == null) return null;
        try { return data.availabilityProcess; } catch { return null; }
    }

    private static int SafeCount(Func<int> f)
    {
        try { return f(); } catch { return 0; }
    }

    private static string SeasonList(AvailabilityProcess proc, int n)
    {
        if (n <= 0) return "(none)";
        var sb = new StringBuilder();
        try
        {
            var list = proc.SeasonAvailabilityConditions;
            for (int i = 0; i < n; i++)
            {
                if (i > 0) sb.Append(", ");
                try { sb.Append(SeasonName(list[i])); } catch { sb.Append('?'); }
            }
        }
        catch { return "(err)"; }
        return sb.ToString();
    }

    private static string TimeList(AvailabilityProcess proc, int n)
    {
        if (n <= 0) return "(none)";
        var sb = new StringBuilder();
        try
        {
            var list = proc.TimeAvailabilityConditions;
            for (int i = 0; i < n; i++)
            {
                if (i > 0) sb.Append("; ");
                try
                {
                    var tc = list[i];
                    sb.Append(tc.condition.ToString())
                      .Append(" mandatory=").Append(tc.isMandatory)
                      .Append(" negate=").Append(tc.negateCondition);
                    try { if (tc.subcondition != null) sb.Append(" +sub"); } catch { }
                }
                catch { sb.Append('?'); }
            }
        }
        catch { return "(err)"; }
        return sb.ToString();
    }

    private static string OtherList(AvailabilityProcess proc, int n)
    {
        if (n <= 0) return "(none)";
        var sb = new StringBuilder();
        try
        {
            var list = proc.OtherAvailabilityConditions;
            for (int i = 0; i < n; i++)
            {
                if (i > 0) sb.Append("; ");
                try
                {
                    var oc = list[i];
                    sb.Append(oc.condition.ToString())
                      .Append(" mandatory=").Append(oc.isMandatory)
                      .Append(" negate=").Append(oc.negateCondition);
                }
                catch { sb.Append('?'); }
            }
        }
        catch { return "(err)"; }
        return sb.ToString();
    }
}
