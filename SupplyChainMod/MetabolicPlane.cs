using System;
using System.Collections.Generic;
using UnityEngine;

namespace SupplyChainMod;

// MetabolicPlane (v0.6.0, Phase 2c) — a per-(warehouse,item) ring buffer of stored-count samples,
// feeding the future Phase 2d rate-based quota calibration. Purely a data layer: it never gates or
// actuates anything itself — callers (WarehouseWatch today, ClogController/QuotaSpike tomorrow) feed
// samples in, and anything wanting a rate reads it back out. Samples are plain floats/ints only — no
// interop wrappers are ever held here (project-wide gotcha).
internal static class MetabolicPlane
{
    private const int Capacity = 16;
    private const float MinSpanSeconds = 45f;

    // Fixed-capacity circular buffer of (Time.time, count) samples for one key. Time.time is
    // pause-robust (doesn't advance while the game is paused), unlike DateTime.UtcNow.
    private sealed class Ring
    {
        private readonly float[] _times = new float[Capacity];
        private readonly int[] _counts = new int[Capacity];
        private int _head; // index of the OLDEST sample when Size == Capacity; otherwise unused
        public int Size;

        public void Add(float time, int count)
        {
            int writeIndex = (_head + Size) % Capacity;
            _times[writeIndex] = time;
            _counts[writeIndex] = count;
            if (Size < Capacity) Size++;
            else _head = (_head + 1) % Capacity; // buffer full — advance the oldest pointer
        }

        // Logical index 0 = oldest, Size-1 = newest.
        public (float Time, int Count) At(int logicalIndex) => (_times[(_head + logicalIndex) % Capacity], _counts[(_head + logicalIndex) % Capacity]);
    }

    private static readonly Dictionary<string, Ring> _rings = new();
    private static float _lastMoverLogAt;

    // ── Public surface (all wrapped so no exception escapes the caller) ────────────────────────

    internal static void NoteSample(string key, int count)
    {
        try
        {
            float now = Time.time;
            if (!_rings.TryGetValue(key, out var ring))
            {
                ring = new Ring();
                _rings[key] = ring;
            }

            // Skip duplicates within the same frame/poll pass (multiple callers can feed the same
            // key in one poll — WarehouseWatch's allotment scan and its clog-dominance check both
            // touch the same station).
            if (ring.Size > 0)
            {
                var newest = ring.At(ring.Size - 1);
                if (newest.Time == now) return;
            }

            ring.Add(now, count);
        }
        catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] [metabolic] NoteSample error: {ex}"); }
    }

    // Endpoint-delta rate — deliberately simple, not a linear fit. Requires >= 2 samples within the
    // window AND a span (newest - oldest-in-window) of at least MinSpanSeconds, else returns false
    // (not enough signal yet to trust a rate).
    internal static bool TryGetRatePerMinute(string key, float windowSeconds, out float ratePerMin, out float spanSeconds)
    {
        ratePerMin = 0f;
        spanSeconds = 0f;
        try
        {
            if (!_rings.TryGetValue(key, out var ring) || ring.Size < 2) return false;

            float now = Time.time;
            float cutoff = now - windowSeconds;

            int oldestInWindowLogical = -1;
            for (int i = 0; i < ring.Size; i++)
            {
                var s = ring.At(i);
                if (s.Time >= cutoff) { oldestInWindowLogical = i; break; }
            }
            if (oldestInWindowLogical < 0) return false; // nothing in window

            int newestLogical = ring.Size - 1;
            if (newestLogical <= oldestInWindowLogical) return false; // need >= 2 samples in window

            var oldest = ring.At(oldestInWindowLogical);
            var newest = ring.At(newestLogical);

            float span = newest.Time - oldest.Time;
            if (span < MinSpanSeconds) return false;

            spanSeconds = span;
            ratePerMin = (newest.Count - oldest.Count) / span * 60f;
            return true;
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"[SupplyChain] [metabolic] TryGetRatePerMinute error: {ex}");
            return false;
        }
    }

    // Gross-churn rate (v0.9.0, Phase 2d BudgetPlane redesign) — same window/validity mechanics as
    // TryGetRatePerMinute (>= 2 samples in window, span >= MinSpanSeconds), but instead of an
    // endpoint delta it sums the ABSOLUTE deltas between each pair of CONSECUTIVE in-window
    // samples: churn = (sum |count[i+1] - count[i]|) / span * 60. This measures gross throughput —
    // an item consumed and refilled in equal measure has net rate ~0 but high churn.
    internal static bool TryGetChurnPerMinute(string key, float windowSeconds, out float churnPerMin, out float spanSeconds)
    {
        churnPerMin = 0f;
        spanSeconds = 0f;
        try
        {
            if (!_rings.TryGetValue(key, out var ring) || ring.Size < 2) return false;

            float now = Time.time;
            float cutoff = now - windowSeconds;

            int oldestInWindowLogical = -1;
            for (int i = 0; i < ring.Size; i++)
            {
                var s = ring.At(i);
                if (s.Time >= cutoff) { oldestInWindowLogical = i; break; }
            }
            if (oldestInWindowLogical < 0) return false; // nothing in window

            int newestLogical = ring.Size - 1;
            if (newestLogical <= oldestInWindowLogical) return false; // need >= 2 samples in window

            var oldest = ring.At(oldestInWindowLogical);
            var newest = ring.At(newestLogical);

            float span = newest.Time - oldest.Time;
            if (span < MinSpanSeconds) return false;

            int churnSum = 0;
            var prev = oldest;
            for (int i = oldestInWindowLogical + 1; i <= newestLogical; i++)
            {
                var cur = ring.At(i);
                churnSum += Math.Abs(cur.Count - prev.Count);
                prev = cur;
            }

            spanSeconds = span;
            churnPerMin = churnSum / span * 60f;
            return true;
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"[SupplyChain] [metabolic] TryGetChurnPerMinute error: {ex}");
            return false;
        }
    }

    // Convenience for other modules' log lines. Uses Plugin.MetabolicWindowSeconds as the window.
    internal static string DescribeRate(string key)
    {
        try
        {
            if (TryGetRatePerMinute(key, Plugin.MetabolicWindowSeconds.Value, out float rate, out float span))
            {
                string sign = rate >= 0 ? "+" : "";
                return $"rate={sign}{rate:F1}/min over {span:F0}s";
            }
        }
        catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] [metabolic] DescribeRate error: {ex}"); }
        return "rate=n/a";
    }

    // Throttled by Plugin.MetabolicStatusMinutes (0 = disabled): logs the top 5 fastest-moving keys
    // (|rate| >= 0.1/min) using the configured window, sorted by |rate| descending (plain sort — no
    // LINQ on this tick-driven path). Logs nothing when no key qualifies.
    internal static void MaybeLogMovers()
    {
        try
        {
            float statusMinutes = Plugin.MetabolicStatusMinutes.Value;
            if (statusMinutes <= 0f) return;

            float now = Time.time;
            if (now - _lastMoverLogAt < statusMinutes * 60f) return;
            _lastMoverLogAt = now;

            float window = Plugin.MetabolicWindowSeconds.Value;
            var movers = new List<(string Key, float Rate, float Span, int SamplesInWindow)>();

            foreach (var kv in _rings)
            {
                if (!TryGetRatePerMinute(kv.Key, window, out float rate, out float span)) continue;
                if (Math.Abs(rate) < 0.1f) continue;

                int samplesInWindow = CountSamplesInWindow(kv.Value, now - window);
                movers.Add((kv.Key, rate, span, samplesInWindow));
            }

            if (movers.Count == 0) return;

            // Plain manual sort by |rate| descending (short list, ticked infrequently).
            movers.Sort((a, b) => Math.Abs(b.Rate).CompareTo(Math.Abs(a.Rate)));

            int shown = Math.Min(5, movers.Count);
            for (int i = 0; i < shown; i++)
            {
                var m = movers[i];
                string sign = m.Rate >= 0 ? "+" : "";
                Plugin.Logger.LogInfo(
                    $"[SupplyChain] [metabolic] mover: '{m.Key}' rate={sign}{m.Rate:F1}/min over {m.Span:F0}s (n={m.SamplesInWindow})");
            }
        }
        catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] [metabolic] MaybeLogMovers error: {ex}"); }
    }

    private static int CountSamplesInWindow(Ring ring, float cutoff)
    {
        int n = 0;
        for (int i = 0; i < ring.Size; i++)
        {
            if (ring.At(i).Time >= cutoff) n++;
        }
        return n;
    }

    internal static void NoteWorldLeft()
    {
        try { _rings.Clear(); }
        catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] [metabolic] NoteWorldLeft error: {ex}"); }
    }
}
