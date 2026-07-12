using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using SandSailorStudio.Inventory;

namespace SupplyChainMod;

// CompositionSweep (Phase 0, read-only) — logs each station's inventory composition. Rolling
// pacing (Plugin.SweepStationsPerTick stations per master tick) via SupplyChainTracker's cursor;
// a full pass also runs immediately on the dump hotkey. Uses ItemCollection.GetItemInfos() +
// GetItemQuantity(info) — the delegation spec's PREFERRED API — which compiles and matches the
// interop surface exactly (confirmed via Cecil sweep of SandSailorStudio.dll), so no fallback
// (ItemManifest.CreateFromFilter / GetAllItems) was needed.
internal static class CompositionSweep
{
    // Rolling pass: processes up to `perTick` stations starting at *cursor, advancing and wrapping
    // it. Each station's sweep is individually timed; a slice >2ms logs a [Perf] line.
    internal static void TickRolling(List<StationEntry> stations, int perTick, ref int cursor)
    {
        if (stations.Count == 0) { cursor = 0; return; }
        if (perTick <= 0) perTick = 1;

        var totalSw = Stopwatch.StartNew();
        int done = 0;
        int start = cursor;
        for (int i = 0; i < stations.Count && done < perTick; i++)
        {
            int idx = (start + i) % stations.Count;
            SweepOne(stations[idx]);
            done++;
        }
        cursor = (start + done) % stations.Count;

        totalSw.Stop();
        if (Plugin.EnableDiagnostics.Value)
            Plugin.Logger.LogInfo($"[SupplyChain] [Perf] CompositionSweep rolling pass: {done} station(s) in {totalSw.Elapsed.TotalMilliseconds:F1} ms.");
    }

    // Full pass — every resolved station, immediately (hotkey trigger). Logs a per-full-pass total
    // at Info level regardless of the >2ms threshold (delegation spec: measuring the sweep's real
    // cost is a Phase 0 deliverable).
    internal static void FullPass(List<StationEntry> stations)
    {
        var totalSw = Stopwatch.StartNew();
        foreach (var entry in stations)
        {
            try { SweepOne(entry); }
            catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] CompositionSweep FullPass '{entry.StructureName}' error: {ex}"); }
        }
        totalSw.Stop();
        Plugin.Logger.LogInfo($"[SupplyChain] [Perf] CompositionSweep full pass: {stations.Count} station(s) in {totalSw.Elapsed.TotalMilliseconds:F1} ms.");
    }

    private static void SweepOne(StationEntry entry)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var inv = entry.Ws.GetInventory();
            if (inv == null)
            {
                if (Plugin.EnableDiagnostics.Value)
                    Plugin.Logger.LogInfo($"[SupplyChain] [sweep] station='{entry.StructureName}': no inventory (GetInventory() returned null).");
                return;
            }

            var infos = inv.GetItemInfos();
            int n = infos != null ? infos.Count : 0;
            var sb = new StringBuilder();
            for (int i = 0; i < n; i++)
            {
                try
                {
                    var info = infos![i];
                    if (info == null) continue;
                    int qty = 0;
                    try { qty = inv.GetItemQuantity(info); } catch { }
                    if (sb.Length > 0) sb.Append(", ");
                    sb.Append(Common.SafeItemName(info)).Append(" x").Append(qty);
                }
                catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] sweep item[{i}] error: {ex}"); }
            }

            if (Plugin.EnableDiagnostics.Value)
                Plugin.Logger.LogInfo($"[SupplyChain] [sweep] station='{entry.StructureName}': {(sb.Length > 0 ? sb.ToString() : "<empty>")}");
        }
        finally
        {
            sw.Stop();
            double ms = sw.Elapsed.TotalMilliseconds;
            if (ms > 2.0)
                Plugin.Logger.LogInfo($"[SupplyChain] [Perf] sweep {entry.StructureName} took {ms:F1} ms");
        }
    }
}
