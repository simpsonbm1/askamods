using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace SupplyChainMod;

// BudgetPlane (v0.8.0, Phase 2d) — the READ-ONLY dry-run budget engine. Per (warehouse, item)
// true-warehouse group (hut self-storage rows excluded — never a budgeting target) it computes a
// need-based quota target from MetabolicPlane's measured drain rate, then classifies the group as
// a HOG (quota crowding storage far beyond what's consumed — propose a quota reduce + eviction of
// the excess), SHADOWED (unmet, room to fill, but priority-shadowed so haulers aren't going —
// propose a tier bump), or healthy/no-data. Every classification and proposed number is LOGGED
// ONLY — this version never writes a quota, never calls DropItem, never touches priority. The
// write levers (quota write, DropItem eviction, tier SetPriority+HostUpdateTasks) were all proven
// individually in v0.5.0/v0.6.0/v0.7.0; wiring THIS module to actually pull them arrives later.
// Same discipline as every other module: no interop wrappers held across polls (state here is
// plain strings only — the previous poll's proposal-set fingerprint, for change-based log
// throttling), every body exception-wrapped.
internal static class BudgetPlane
{
    private sealed class BudgetGroup
    {
        public string Warehouse = "?";
        public string Item = "?";
        public string PosKey = "?";
        public int Stored;
        public int QuotaSum;
        public int Room;
        public int MaxCapacity = -1;
        public int RowCount;
        public int MinRank = int.MaxValue;
    }

    // Fingerprint of the last-logged proposal set ("class|posKey|item", sorted, comma-joined) —
    // proposal lines (HOG/SHADOWED) are only re-logged when this changes, to keep log volume sane
    // on a steady-state settlement where the same groups classify the same way poll after poll.
    private static string _lastProposalSet = "";

    internal static void NoteWorldLeft()
    {
        try { _lastProposalSet = ""; }
        catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] [budget] NoteWorldLeft error: {ex}"); }
    }

    internal static void Evaluate(List<WarehouseWatch.AllotmentRow> allRows, bool fullTable)
    {
        var sw = Stopwatch.StartNew();
        int groupCount = 0;
        try
        {
            // ── Group true-warehouse rows by (PosKey, Item) ─────────────────────────────────────
            var groups = new Dictionary<string, BudgetGroup>();
            foreach (var row in allRows)
            {
                if (!row.HasTaskContainer) continue;
                string key = row.PosKey + "|" + row.Item;
                if (!groups.TryGetValue(key, out var g))
                {
                    g = new BudgetGroup { Warehouse = row.Warehouse, Item = row.Item, PosKey = row.PosKey };
                    groups[key] = g;
                }
                g.Stored = row.Stored;
                g.QuotaSum += row.Qty;
                g.Room = row.Room;
                g.MaxCapacity = row.MaxCapacity;
                g.RowCount++;
                if (row.Rank < g.MinRank) g.MinRank = row.Rank;
            }

            groupCount = groups.Count;
            if (groupCount == 0) return; // summary line only fires when at least one group existed

            int noData = 0, healthy = 0, hogs = 0, shadowed = 0;
            var proposalLines = new List<string>();
            var proposalKeys = new List<string>();
            List<string>? tableLines = fullTable ? new List<string>() : null;
            List<string>? mismatchLines = fullTable ? new List<string>() : null;
            int maxCapChecked = 0;
            int maxCapMismatches = 0;

            foreach (var kv in groups)
            {
                var g = kv.Value;
                bool hasRate = MetabolicPlane.TryGetRatePerMinute(kv.Key, Plugin.BudgetWindowSeconds.Value, out float rate, out float span);
                string cls;

                if (!hasRate)
                {
                    cls = "no-data";
                    noData++;
                }
                else
                {
                    float drainRate = Math.Max(0f, -rate);
                    int target = Plugin.MinQuota.Value + (int)Math.Ceiling(drainRate * Plugin.HeadroomMinutes.Value);
                    bool maxCapKnown = g.MaxCapacity > 0;
                    if (maxCapKnown)
                    {
                        int cap = (int)(g.MaxCapacity * Plugin.MaxQuotaShareOfMaxPct.Value / 100f);
                        if (target > cap) target = cap;
                    }
                    if (target < Plugin.MinQuota.Value) target = Plugin.MinQuota.Value;

                    bool isHog = maxCapKnown
                        && g.QuotaSum >= g.MaxCapacity * Plugin.HogQuotaShareOfMaxPct.Value / 100f
                        && g.Stored >= Plugin.HogMinStored.Value
                        && Math.Abs(rate) <= Plugin.HogMaxAbsRate.Value;

                    bool isShadowed = !isHog
                        && g.Stored < g.QuotaSum
                        && g.Room > 0
                        && rate <= Plugin.ShadowMaxFillRate.Value
                        && rate >= -Plugin.ShadowMaxFillRate.Value
                        && g.MinRank >= 1;

                    if (isHog)
                    {
                        cls = "hog";
                        hogs++;
                        int evictExcess = Math.Max(0, g.Stored - target);
                        proposalLines.Add(
                            $"[SupplyChain] [budget] HOG '{g.Item}' @ '{g.Warehouse}': stored={g.Stored} quotaSum={g.QuotaSum} maxCap={g.MaxCapacity} " +
                            $"rate={FormatRate(rate)}/min -> propose quota {g.QuotaSum}->{target}; evict excess={evictExcess} (dry-run)");
                        proposalKeys.Add($"hog|{g.PosKey}|{g.Item}");
                    }
                    else if (isShadowed)
                    {
                        cls = "shadowed";
                        shadowed++;
                        proposalLines.Add(
                            $"[SupplyChain] [budget] SHADOWED '{g.Item}' @ '{g.Warehouse}': stored={g.Stored}/quotaSum={g.QuotaSum} room={g.Room} " +
                            $"rank={g.MinRank} rate={FormatRate(rate)}/min -> propose tier {g.MinRank}->0 (High) (dry-run)");
                        proposalKeys.Add($"shadowed|{g.PosKey}|{g.Item}");
                    }
                    else
                    {
                        cls = "healthy";
                        healthy++;
                    }
                }

                if (fullTable)
                {
                    string rateStr = hasRate ? FormatRate(rate) : "n/a";
                    tableLines!.Add(
                        $"[SupplyChain] [budget] row: '{g.Item}' @ '{g.Warehouse}' stored={g.Stored} quotaSum={g.QuotaSum} rows={g.RowCount} " +
                        $"room={g.Room} maxCap={g.MaxCapacity} rank={g.MinRank} rate={rateStr} class={cls}");

                    if (g.MaxCapacity >= 0)
                    {
                        maxCapChecked++;
                        int expected = g.Stored + g.Room;
                        if (g.MaxCapacity != expected)
                        {
                            maxCapMismatches++;
                            mismatchLines!.Add(
                                $"[SupplyChain] [budget] maxCap cross-check MISMATCH '{g.Item}' @ '{g.Warehouse}': maxCap={g.MaxCapacity} stored+room={expected}");
                        }
                    }
                }
            }

            Plugin.Logger.LogInfo($"[SupplyChain] [budget] groups={groupCount} noData={noData} healthy={healthy} hogs={hogs} shadowed={shadowed} (dry-run)");

            proposalKeys.Sort(StringComparer.Ordinal);
            string proposalSetString = string.Join(",", proposalKeys);
            bool proposalSetChanged = proposalSetString != _lastProposalSet;
            if (proposalSetChanged || fullTable)
            {
                foreach (var line in proposalLines) Plugin.Logger.LogInfo(line);
            }
            _lastProposalSet = proposalSetString;

            if (fullTable)
            {
                foreach (var line in tableLines!) Plugin.Logger.LogInfo(line);

                if (maxCapMismatches > 0)
                {
                    foreach (var line in mismatchLines!) Plugin.Logger.LogInfo(line);
                }
                else if (maxCapChecked > 0)
                {
                    Plugin.Logger.LogInfo($"[SupplyChain] [budget] maxCap cross-check: all {maxCapChecked} groups consistent (maxCap == stored+room)");
                }
            }
        }
        catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] [budget] Evaluate error: {ex}"); }
        finally
        {
            sw.Stop();
            double ms = sw.Elapsed.TotalMilliseconds;
            if (ms > 2.0 || fullTable)
                Plugin.Logger.LogInfo($"[SupplyChain] [Perf] BudgetPlane evaluate: {groupCount} group(s) in {ms:F1} ms");
        }
    }

    private static string FormatRate(float rate)
    {
        string sign = rate >= 0 ? "+" : "";
        return $"{sign}{rate:F1}";
    }
}
