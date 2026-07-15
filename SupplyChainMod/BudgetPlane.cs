using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace SupplyChainMod;

// BudgetPlane (v0.9.0, Phase 2d) — the READ-ONLY dry-run budget engine, redesigned after the
// v0.8.0 in-game test showed the first classifier was noise: 219/323 groups flagged SHADOWED
// (no demand gate — it bumped every non-High-rank row regardless of whether anything actually
// needed it), a single-type stick storage flagged HOG (no container-composition awareness — a
// container that can only ever hold one item can never crowd out another item), and in-demand
// items proposed for eviction (no settlement-wide demand signal at all).
//
// v0.9.0 groups ALL rows (warehouse AND hut/station self-storage — hut rows were previously
// filtered out entirely) by (PosKey, Item) and adds three new dimensions on top of the v0.8.0
// per-group rate/quota mechanics:
//   - demand-aware: a per-item, settlement-wide net-rate + gross-churn signal (fed by
//     WarehouseWatch's "ALL|<item>" MetabolicPlane key) marks an item "in demand" when it's
//     draining OR churning — demand vetoes HOG eviction and gates SHADOWED/STARVED on real
//     upstream supply (hutStock), instead of bumping every idle row.
//   - container-composition-aware: a group only counts as "multi-type" when at least one of its
//     rows' physical containers (keyed by PosKey+SupplyOwner) is known to accept >= 2 distinct
//     items — vanilla emits one quota row per item a container can accept, so the row set IS the
//     container's compatibility list. A single-type container (e.g. stick storage) can never
//     physically crowd out another item and is never eligible as a HOG.
//   - victim-gated eviction: a HOG proposal additionally requires a concrete victim — another
//     unmet item group at the same structure, sharing a physical container, with little room —
//     so eviction is only ever proposed when it can plausibly free space for something that needs
//     it, never as an abstract "this quota looks generous" judgment.
//
// v0.9.1 — the v0.9.0 in-game run showed three more false positives:
//   - SHADOWED fired on pure supply-push (stock piling at hunter lodges with nothing settlement-
//     wide actually consuming it, e.g. Feathers). Fix: SHADOWED/STARVED now also require a
//     demand-pull signal — drain/churn OR the game's own "not being produced, needed by X"
//     issue-tracker complaint (WidgetWatcher.TryGetNoProductionNeededBy) — before proposing.
//   - Tier bumps were proposed onto rank=3 groups — rank 3 is the UI's "Off"
//     (WorkstationTaskPriority.None), i.e. the player's own choice to disable that allotment,
//     which must never be second-guessed. Fix: SHADOWED's rank eligibility is now strictly
//     Med/Low (1-2); an Off-ranked group that would otherwise qualify is logged informationally
//     as class "off", never proposed. When several warehouse groups for the same item are all
//     SHADOWED-eligible, only the one with the most room is proposed (one destination per item);
//     the rest are logged as "shadowed-alt" and counted but not proposed.
//   - 7 of 12 HOG proposals were seed types at one warehouse accusing EACH OTHER as victims
//     (flax seeds hog with beetroot seeds as victim and vice versa) — evicting one undemanded
//     item to make room for another undemanded item is pointless. Fix: a HOG victim is only
//     eligible when the VICTIM item's own demand-pull is also true; a group with no
//     demand-passing victim is not a hog.
//
// Still fully READ-ONLY dry-run: every classification and proposed number is LOGGED ONLY. No
// quota write, no DropItem eviction, no SetPriority — same discipline as every other module in
// this mod (no interop wrappers held across polls; every body exception-wrapped).
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
        // true when NO row in this group has HasTaskContainer (a hut/station self-storage group).
        // A mixed group (shouldn't occur) counts as warehouse — cleared false the moment any row
        // with a task container is seen.
        public bool IsHut = true;
        public readonly HashSet<string> OwnerKeys = new();
    }

    // Per-item settlement-wide signals, computed once per Evaluate from the "ALL|<item>"
    // MetabolicPlane key (fed by WarehouseWatch.FeedMetabolicSamples) plus a pass over this
    // item's own groups (hut stock, settlement-wide stored total).
    private sealed class ItemSignal
    {
        public bool HasNet;
        public float Net;
        public bool HasChurn;
        public float Churn;
        public bool DemandActive;
        // v0.9.1 — the game's own "not being produced, needed by X" issue-tracker complaint for
        // this item (WidgetWatcher.TryGetNoProductionNeededBy); null when the widget has no
        // complaint for it right now.
        public string? NoProdNeededBy;
        // v0.9.1 — demand-pull gate for SHADOWED/STARVED/HOG-victim: true when EITHER the
        // drain/churn signal (DemandActive) fires OR the widget names this item as not being
        // produced. Pure supply-push at the huts (stock piling with no consumer anywhere) no
        // longer qualifies as demand.
        public bool DemandPull;
        public string DemandEvidence = "none";
        public int HutStock;
        public int SettleStored;
        public string TopSource = "?";
        public int TopSourceStored = -1;
    }

    // v0.9.1 — per-group scratch computed during pass A, read again in pass B (per-item SHADOWED
    // winner selection) and pass C (full-table dump) once every group's final class is settled.
    private sealed class GroupEval
    {
        public BudgetGroup Group = null!;
        public bool HasRate;
        public float Rate;
        public bool IsMultiType;
        public string Class = "healthy";
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
            // ── Group ALL rows (warehouse AND hut) by (PosKey, Item); build the container-
            // composition map alongside it ─────────────────────────────────────────────────────
            var groups = new Dictionary<string, BudgetGroup>();
            var containerItems = new Dictionary<string, HashSet<string>>();
            foreach (var row in allRows)
            {
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
                if (row.HasTaskContainer) g.IsHut = false;

                if (row.SupplyOwner != "?" && row.SupplyOwner.Length > 0)
                {
                    string ownerKey = row.PosKey + "|" + row.SupplyOwner;
                    g.OwnerKeys.Add(ownerKey);

                    if (!containerItems.TryGetValue(ownerKey, out var itemSet))
                    {
                        itemSet = new HashSet<string>();
                        containerItems[ownerKey] = itemSet;
                    }
                    itemSet.Add(row.Item);
                }
            }

            groupCount = groups.Count;
            if (groupCount == 0) return; // summary line only fires when at least one group existed

            // ── Groups-by-PosKey index (for the HOG victim search) ─────────────────────────────
            var groupsByPosKey = new Dictionary<string, List<BudgetGroup>>();
            foreach (var kv in groups)
            {
                var g = kv.Value;
                if (!groupsByPosKey.TryGetValue(g.PosKey, out var list))
                {
                    list = new List<BudgetGroup>();
                    groupsByPosKey[g.PosKey] = list;
                }
                list.Add(g);
            }

            // ── Per-item settlement signals (net/churn from "ALL|<item>"; no-production demand-
            // pull from the widget; hut stock + settlement stored total from a pass over this
            // item's own groups) ────────────────────────────────────────────────────────────────
            var itemSignals = new Dictionary<string, ItemSignal>();
            foreach (var kv in groups)
            {
                var g = kv.Value;
                if (itemSignals.ContainsKey(g.Item)) continue;
                var sig = new ItemSignal();
                sig.HasNet = MetabolicPlane.TryGetRatePerMinute("ALL|" + g.Item, Plugin.BudgetWindowSeconds.Value, out sig.Net, out _);
                sig.HasChurn = MetabolicPlane.TryGetChurnPerMinute("ALL|" + g.Item, Plugin.BudgetWindowSeconds.Value, out sig.Churn, out _);

                bool demandDrain = sig.HasNet && sig.Net <= -Plugin.DemandDrainPerMin.Value;
                bool demandChurn = sig.HasChurn && sig.Churn >= Plugin.DemandChurnPerMin.Value;
                sig.DemandActive = demandDrain || demandChurn;

                if (WidgetWatcher.TryGetNoProductionNeededBy(g.Item, out var neededBy))
                    sig.NoProdNeededBy = neededBy;

                sig.DemandPull = sig.DemandActive || sig.NoProdNeededBy != null;
                sig.DemandEvidence = demandDrain ? "drain"
                    : demandChurn ? "churn"
                    : sig.NoProdNeededBy != null ? $"noProd('{sig.NoProdNeededBy}')"
                    : "none";

                itemSignals[g.Item] = sig;
            }
            foreach (var kv in groups)
            {
                var g = kv.Value;
                var sig = itemSignals[g.Item];
                sig.SettleStored += g.Stored;
                if (g.IsHut)
                {
                    sig.HutStock += g.Stored;
                    if (g.Stored > sig.TopSourceStored)
                    {
                        sig.TopSourceStored = g.Stored;
                        sig.TopSource = g.Warehouse;
                    }
                }
            }

            // ── Classification pass A: no-data / hog / starved / off / healthy are decided here;
            // SHADOWED-eligible groups are collected per item and deferred to pass B ─────────────
            int noData = 0, healthy = 0, hogs = 0, shadowed = 0, starved = 0, off = 0;
            int warehouseGroups = 0, hutGroups = 0;
            var proposals = new List<(string Line, int Severity, string Key)>();
            var itemsWithFinding = new HashSet<string>();
            var evalByKey = new Dictionary<string, GroupEval>();
            var shadowedCandidatesByItem = new Dictionary<string, List<string>>();

            foreach (var kv in groups)
            {
                var g = kv.Value;
                if (g.IsHut) hutGroups++; else warehouseGroups++;

                bool hasRate = MetabolicPlane.TryGetRatePerMinute(kv.Key, Plugin.BudgetWindowSeconds.Value, out float rate, out float span);
                var sig = itemSignals[g.Item];

                bool isMultiType = false;
                foreach (var ok in g.OwnerKeys)
                {
                    if (containerItems.TryGetValue(ok, out var itemSet) && itemSet.Count >= 2) { isMultiType = true; break; }
                }

                var ev = new GroupEval { Group = g, HasRate = hasRate, Rate = rate, IsMultiType = isMultiType };

                if (!hasRate)
                {
                    ev.Class = "no-data";
                    noData++;
                }
                else
                {
                    bool hogEligible = isMultiType
                        && g.Stored >= Plugin.HogMinStored.Value
                        && Math.Abs(rate) <= Plugin.HogMaxAbsRate.Value
                        && sig.HasNet && sig.HasChurn
                        && !sig.DemandActive;

                    bool isHog = false;
                    List<string>? victimNames = null;
                    if (hogEligible)
                    {
                        victimNames = new List<string>();
                        if (groupsByPosKey.TryGetValue(g.PosKey, out var siblings))
                        {
                            foreach (var other in siblings)
                            {
                                if (other.Item == g.Item) continue; // excludes g itself (unique per PosKey|Item)
                                if (other.Stored >= other.QuotaSum) continue;
                                if (other.Room > Plugin.VictimMaxRoom.Value) continue;

                                // v0.9.1 — the victim item must itself be demand-pulled (drain/churn
                                // or a no-production complaint). Two undemanded items (e.g. two idle
                                // seed types) can never be victim of one another.
                                if (!itemSignals.TryGetValue(other.Item, out var otherSig) || !otherSig.DemandPull) continue;

                                bool shared = false;
                                foreach (var ok in g.OwnerKeys)
                                {
                                    if (other.OwnerKeys.Contains(ok)) { shared = true; break; }
                                }
                                if (!shared) continue;

                                if (victimNames.Count < 3) victimNames.Add(other.Item);
                            }
                        }
                        isHog = victimNames.Count > 0;
                    }

                    bool shadowedGateBase = !g.IsHut
                        && g.Stored < g.QuotaSum
                        && g.Room > 0
                        && rate <= Plugin.ShadowMaxFillRate.Value
                        && rate >= -Plugin.ShadowMaxFillRate.Value
                        && sig.HutStock >= Plugin.ShadowMinSourceStock.Value;

                    if (isHog)
                    {
                        ev.Class = "hog";
                        hogs++;
                        itemsWithFinding.Add(g.Item);

                        float drainRate = Math.Max(0f, -sig.Net);
                        int target = Plugin.MinQuota.Value + (int)Math.Ceiling(drainRate * Plugin.HeadroomMinutes.Value);
                        if (g.MaxCapacity > 0)
                        {
                            int cap = (int)(g.MaxCapacity * Plugin.MaxQuotaShareOfMaxPct.Value / 100f);
                            if (target > cap) target = cap;
                        }
                        if (target < Plugin.MinQuota.Value) target = Plugin.MinQuota.Value;
                        int evictExcess = Math.Max(0, g.Stored - target);

                        string kindStr = g.IsHut ? "hut" : "wh";
                        string victimsStr = victimNames!.Count > 0 ? string.Join(",", victimNames) : "none";
                        string line =
                            $"[SupplyChain] [budget] HOG '{g.Item}' @ '{g.Warehouse}' kind={kindStr}: stored={g.Stored} quotaSum={g.QuotaSum} " +
                            $"rate={FormatRate(rate)}/min churn={FormatOptional(sig.HasChurn, sig.Churn)}/min net={FormatOptional(sig.HasNet, sig.Net)}/min " +
                            $"victims={victimsStr} -> propose quota {g.QuotaSum}->{target}; evict excess={evictExcess} (dry-run)";
                        proposals.Add((line, evictExcess, $"hog|{g.PosKey}|{g.Item}"));
                    }
                    else if (shadowedGateBase && sig.DemandPull && g.MinRank >= 1 && g.MinRank <= 2)
                    {
                        // SHADOWED-eligible — one destination per item is decided in pass B below.
                        if (!shadowedCandidatesByItem.TryGetValue(g.Item, out var cands))
                        {
                            cands = new List<string>();
                            shadowedCandidatesByItem[g.Item] = cands;
                        }
                        cands.Add(kv.Key);
                    }
                    else if (shadowedGateBase && sig.DemandPull && g.MinRank == 0)
                    {
                        // STARVED — informational only: already High rank, demand-pull confirmed,
                        // yet not filling despite hut supply. No tier bump can fix this; it's a
                        // hauler-capacity symptom.
                        ev.Class = "starved";
                        starved++;
                        itemsWithFinding.Add(g.Item);
                    }
                    else if (shadowedGateBase && sig.DemandPull && g.MinRank == 3)
                    {
                        // OFF — informational only: demand-pull confirmed and supply is piling at
                        // producers, but the player switched this allotment's rank to Off. Their
                        // choice is respected; never proposed.
                        ev.Class = "off";
                        off++;
                        itemsWithFinding.Add(g.Item);
                    }
                    else
                    {
                        ev.Class = "healthy";
                        healthy++;
                    }
                }

                evalByKey[kv.Key] = ev;
            }

            // ── Classification pass B: per item, propose only the SHADOWED candidate with the
            // most room (tie-break: lower MinRank); the rest are "shadowed-alt" — counted, not
            // proposed ───────────────────────────────────────────────────────────────────────────
            foreach (var kv in shadowedCandidatesByItem)
            {
                var cands = kv.Value;
                string winnerKey = cands[0];
                var winnerGroup = evalByKey[winnerKey].Group;
                for (int i = 1; i < cands.Count; i++)
                {
                    var candGroup = evalByKey[cands[i]].Group;
                    if (candGroup.Room > winnerGroup.Room
                        || (candGroup.Room == winnerGroup.Room && candGroup.MinRank < winnerGroup.MinRank))
                    {
                        winnerKey = cands[i];
                        winnerGroup = candGroup;
                    }
                }

                int altCount = cands.Count - 1;
                foreach (var key in cands)
                {
                    var ev = evalByKey[key];
                    var g = ev.Group;
                    var sig = itemSignals[g.Item];
                    shadowed++;
                    itemsWithFinding.Add(g.Item);

                    if (key == winnerKey)
                    {
                        ev.Class = "shadowed";
                        string altsStr = altCount > 0 ? $" (+{altCount} other candidate destination(s))" : "";
                        string line =
                            $"[SupplyChain] [budget] SHADOWED '{g.Item}' @ '{g.Warehouse}': stored={g.Stored}/quotaSum={g.QuotaSum} room={g.Room} " +
                            $"rank={g.MinRank} rate={FormatRate(ev.Rate)}/min source='{sig.TopSource}' hutStock={sig.HutStock} demand={sig.DemandEvidence} " +
                            $"-> propose tier {g.MinRank}->0 (High) (dry-run){altsStr}";
                        proposals.Add((line, sig.HutStock, $"shadowed|{g.PosKey}|{g.Item}"));
                    }
                    else
                    {
                        ev.Class = "shadowed-alt";
                    }
                }
            }

            Plugin.Logger.LogInfo(
                $"[SupplyChain] [budget] groups={groupCount} (wh={warehouseGroups} hut={hutGroups}) noData={noData} healthy={healthy} " +
                $"hogs={hogs} shadowed={shadowed} starved={starved} off={off} (dry-run)");

            // Fingerprint on classification keys only (unaffected by the severity sort/truncation
            // below) — the same "did the proposal SET change" throttle as v0.8.0.
            var proposalKeys = new List<string>();
            foreach (var p in proposals) proposalKeys.Add(p.Key);
            proposalKeys.Sort(StringComparer.Ordinal);
            string proposalSetString = string.Join(",", proposalKeys);
            bool proposalSetChanged = proposalSetString != _lastProposalSet;

            // Plain manual sort by severity descending (short list, ticked infrequently) — no
            // LINQ on this poll path.
            proposals.Sort((a, b) => b.Severity.CompareTo(a.Severity));

            if (fullTable)
            {
                foreach (var p in proposals) Plugin.Logger.LogInfo(p.Line);
            }
            else if (proposalSetChanged)
            {
                int cap = Math.Max(0, Plugin.MaxProposalLinesPerPoll.Value);
                int shown = Math.Min(cap, proposals.Count);
                for (int i = 0; i < shown; i++) Plugin.Logger.LogInfo(proposals[i].Line);
                int suppressed = proposals.Count - shown;
                if (suppressed > 0)
                    Plugin.Logger.LogInfo($"[SupplyChain] [budget] ... and {suppressed} more proposal(s) suppressed (press F9 for the full set)");
            }
            _lastProposalSet = proposalSetString;

            if (fullTable)
            {
                foreach (var kv in groups)
                {
                    var g = kv.Value;
                    var ev = evalByKey[kv.Key];
                    var sig = itemSignals[g.Item];
                    string rateStr = ev.HasRate ? FormatRate(ev.Rate) : "n/a";
                    string churnStr = FormatOptional(sig.HasChurn, sig.Churn);
                    string netStr = FormatOptional(sig.HasNet, sig.Net);
                    string kindStr = g.IsHut ? "hut" : "wh";
                    Plugin.Logger.LogInfo(
                        $"[SupplyChain] [budget] row: '{g.Item}' @ '{g.Warehouse}' kind={kindStr} stored={g.Stored} quotaSum={g.QuotaSum} " +
                        $"rows={g.RowCount} room={g.Room} maxCap={g.MaxCapacity} rank={g.MinRank} rate={rateStr} churn={churnStr} net={netStr} " +
                        $"multi={(ev.IsMultiType ? "T" : "F")} class={ev.Class}");
                }

                int shownItems = 0;
                foreach (var item in itemsWithFinding)
                {
                    if (shownItems >= 40) break;
                    var sig = itemSignals[item];
                    Plugin.Logger.LogInfo(
                        $"[SupplyChain] [budget] item '{item}': settleStored={sig.SettleStored} net={FormatOptional(sig.HasNet, sig.Net)}/min " +
                        $"churn={FormatOptional(sig.HasChurn, sig.Churn)}/min hutStock={sig.HutStock} demand={(sig.DemandActive ? "T" : "F")}");
                    shownItems++;
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

    private static string FormatOptional(bool has, float val) => has ? FormatRate(val) : "n/a";
}
