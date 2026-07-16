using System;
using System.Collections.Generic;
using System.Diagnostics;
using SandSailorStudio.Inventory;

namespace SupplyChainMod;

// BudgetPlane (v0.11.1, DEMAND_MODEL_PLAN.md step 3 part C) — classifier v3, rebuilt on VERIFIED
// container data (WarehouseWatch's per-poll physical container map, Part B) + structural demand
// (DemandGraph's direct+derived structural-demand table, Part A) instead of v0.9.x's inference
// (SupplyOwner display names, which fabricated shared pools out of separate physical bins — proven
// wrong in-game 2026-07-15, see DEMAND_MODEL_PLAN.md's "Container ground truth" section).
//
// v0.11.1 fixes 2 and 6 (DEMAND_MODEL_PLAN.md user review; fixes 1/3/4/5 live in DemandGraph.cs):
//   2. Blocked primitive + quantum clamp — v0.11.0's HasSpace(v,1)==false fired on containers at
//      fill 0.94-0.98 (its semantics evidently aren't "no free slot"). Replaced with
//      ItemContainer.GetRemainingCapacity(item)==0 (IsBlocked below; first runtime use of this
//      primitive, own try/catch, falls back to FillRatio>=0.999 on a read failure) for BOTH HOG
//      (checked against the victim V) and BLOCKAGE (checked against the dedicated item X itself —
//      BLOCKAGE has no shared-container victim-space question, it's "is X's own output container
//      full"). Which primitive decided is logged as a short blk=cap|fill token. Eviction sizing
//      also gained a hard clamp: q = min(slots*stackX, X's structure-wide Stored) — v0.11.0 could
//      propose evicting more of X than the structure actually held (e.g. 200 of a stored=100 item);
//      the displayed slot count is recomputed FROM the clamped q, never shown pre-clamp.
//   6. Logging polish — BLOCKAGE's proposal line is reworded to name the mechanism ("full container
//      of undemanded X stalls producer of demanded sibling V") instead of reusing HOG's generic
//      "X vs victim V" template; DemandGraph's per-item [demand] lines gain a derivedVia=[...] list
//      whenever derived>0 (previously only pure-derived items showed it — see DemandGraph.cs).
//
// Three signal layers per item, same roles as the demand-loop design doc:
//   - STRUCTURAL (DemandGraph.TryGetStructuralDemand: direct+derived) — the gate. An item is
//     Demanded() when structTotal > 0 OR the game's own "not being produced, needed by X" widget
//     complaint names it.
//   - FLOW (MetabolicPlane net/churn, same "ALL|<item>"-style per-group rate as v0.9.x) — used ONLY
//     for static checks (is this group actively filling/draining right now?), never the demand gate.
//   - DISTRESS (WidgetWatcher.TryGetNoProductionNeededBy) — folds into Demanded() above.
//
// Central signal (v0.13.0): SURPLUS. IsSurplus(item) = settleStored > structural × SurplusFactor —
// stock materially exceeds the item's own demand (zero-demand ⇒ surplus at any stock). This replaces
// the old binary demanded/undemanded gate and cleanly separates the three storage pathologies below.
//
// Classes, evaluated in this priority order:
//   1. HOG (eviction) — a container with >= 2 effective (non-Off) items holds a SURPLUS, static item
//      crowding out a demanded, NON-surplus, blocked one — they SHARE the container (real physical
//      crowding). Blocked = IsBlocked(victim) = GetRemainingCapacity(victim)==0 (own try/catch,
//      falls back to FillRatio>=0.999). Sizing DEMAND-GROUNDED: reduce the hog toward
//      max(CoProductFloorSlots, its own demand) (see BuildEvictionProposal). Catches Resin (demand 3,
//      stock 1900) crowding short Fibers — which the old undemanded-only gate misfiled as a BLOCKAGE.
//   2. BLOCKAGE (diagnostic) — a demanded, NON-surplus material genuinely stuck: SITTING (stored>0)
//      in a full, static container while its downstream BOM consumers rely on it (Hardwood Long
//      Sticks jammed at the Woodcutter's while Shafts/Beams demand them). A routing/priority problem
//      (supply at A, demand at B, not flowing) — lever is tier/priority, NOT eviction. One line per
//      ITEM with a cause token (no-route / destination-full / priority-shadowed from its warehouse
//      rows). Excludes surplus items (those are HOG/SURPLUS) and HOG victims (no double-report).
//   2b. SURPLUS (informational) — a surplus item over-produced but NOT currently crowding a demanded
//      victim (no shared-container HOG). "Stock ≫ demand, sitting around." One line per item.
//   3. SHADOWED — warehouse-only: item demanded, group unmet, room, rank Med/Low (never Off),
//      static-or-slow fill, and real upstream evidence (hut stock >= ShadowMinSourceStock). One
//      destination proposed per item (most room); others logged "shadowed-alt".
//   4. OFF-CONFLICT (informational only, no proposal/fingerprint) — demanded item whose row is
//      player-Off (rank 3) while supply visibly wants to flow there.
//   5. STARVED (informational only) — demanded, already High, unmet, room, static, hut source
//      exists despite the top rank — a hauler-capacity symptom, not a priority problem.
//   6. healthy / no-data — no-data now fires ONLY when a group is otherwise SHADOWED/STARVED-
//      ELIGIBLE but lacks rate data to confirm the static condition (HOG doesn't need this: a
//      group with no rate data counts as static there by design — see the HOG pass below — so a
//      quick post-load F9 before any metabolic samples exist doesn't hide every hog).
//   OFF-CONFLICT/STARVED (and SHADOWED, matching v0.9.1) inherit the "warehouse-only" scope: hut/
//   producer self-storage rows don't carry the "hauler destination, room, rank" semantics these
//   classes reason about.
//
// Eviction sizing (HOG only, v0.12.0 — DEMAND-GROUNDED): a hog is undemanded (structural==0), so its
// target is CoProductFloorSlots kept and everything above that floor is surplus. reduce = Stored -
// floorUnits; the per-cycle step is capped at MaxSlotsPerCycle (self-pacing, response-gated at
// arming). Both the full demand-grounded target AND the per-cycle step are logged. Replaces v0.11.1's
// victim-deficit sizing, which freed ~1 slot the hog immediately refilled. "gate=ready" is a fixed
// marker — this version has NO response-gated state machine (the separate "arming design" step in
// DEMAND_MODEL_PLAN.md); every proposal here is still LOGGED ONLY.
//
// Still fully READ-ONLY dry-run: no quota write, no DropItem eviction, no SetPriority — same
// discipline as every module in this mod. VictimMaxRoom (v0.9.1's crude victim-room gate) is
// retired — superseded by the real IsBlocked (GetRemainingCapacity/fill) check above.
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
        public bool IsHut = true;
        public readonly HashSet<string> ContainerKeys = new();
        // Interop wrapper — legal here ONLY because `groups` (the dictionary holding every
        // BudgetGroup) is a local variable of Evaluate(), never a static field; nothing here
        // survives past this one Evaluate() call (project-wide "call-stack only" rule).
        public ItemInfo? Info;
    }

    // Per-item settlement-wide signals, computed once per Evaluate: structural demand from
    // DemandGraph, distress from the widget, plus a pass over this item's own groups (hut stock,
    // settlement-wide stored total, top hut source).
    private sealed class ItemSignal
    {
        public int StructDirect;
        public int StructDerived;
        public int StructTotal => StructDirect + StructDerived;
        public string? NoProdNeededBy;
        public bool Demanded => StructTotal > 0 || NoProdNeededBy != null;
        public int HutStock;
        public int SettleStored;
        public string TopSource = "?";
        public int TopSourceStored = -1;
        // v0.13.0 — warehouse-destination signals for the BLOCKAGE cause token: does the item have
        // any true-warehouse (non-hut) row at all, and the most room across those rows.
        public bool HasWarehouseRow;
        public int MaxWarehouseRoom;
    }

    private sealed class GroupEval
    {
        public BudgetGroup Group = null!;
        public bool HasRate;
        public float Rate;
        public string Class = "healthy";
    }

    // Fingerprint of the last-logged proposal set — proposal lines are only re-logged when this
    // changes, to keep log volume sane on a steady-state settlement (same throttle as v0.8.0/v0.9.x).
    private static string _lastProposalSet = "";

    internal static void NoteWorldLeft()
    {
        try { _lastProposalSet = ""; }
        catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] [budget] NoteWorldLeft error: {ex}"); }
    }

    internal static void Evaluate(List<WarehouseWatch.AllotmentRow> allRows, Dictionary<string, WarehouseWatch.ContainerSnapshot> containerMap, bool fullTable)
    {
        try { DemandGraph.EnsureFresh(); }
        catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] [budget] DemandGraph.EnsureFresh error: {ex}"); }

        var sw = Stopwatch.StartNew();
        int groupCount = 0;
        try
        {
            // ── Group ALL rows (warehouse AND hut) by (PosKey, Item) ────────────────────────────
            var groups = new Dictionary<string, BudgetGroup>();
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
                if (row.Info != null) g.Info = row.Info;
                if (row.ContainerKey.Length > 0) g.ContainerKeys.Add(row.ContainerKey);
            }

            groupCount = groups.Count;
            if (groupCount == 0) return;

            // ── Groups-by-container (HOG shared-container search) ─────────────────────────────────
            var containerGroups = new Dictionary<string, List<BudgetGroup>>();
            foreach (var kv in groups)
            {
                var g = kv.Value;
                foreach (var ck in g.ContainerKeys)
                {
                    if (!containerGroups.TryGetValue(ck, out var clist))
                    {
                        clist = new List<BudgetGroup>();
                        containerGroups[ck] = clist;
                    }
                    clist.Add(g);
                }
            }

            // ── Per-item signals: structural (DemandGraph) + distress (widget) + hut/settle stock ─
            var itemSignals = new Dictionary<string, ItemSignal>();
            foreach (var kv in groups)
            {
                var g = kv.Value;
                if (itemSignals.ContainsKey(g.Item)) continue;
                var sig = new ItemSignal();

                try { DemandGraph.TryGetStructuralDemand(g.Item, out sig.StructDirect, out sig.StructDerived, out _); }
                catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] [budget] TryGetStructuralDemand '{g.Item}' error: {ex}"); }

                try { if (WidgetWatcher.TryGetNoProductionNeededBy(g.Item, out var neededBy)) sig.NoProdNeededBy = neededBy; }
                catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] [budget] TryGetNoProductionNeededBy '{g.Item}' error: {ex}"); }

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
                else
                {
                    sig.HasWarehouseRow = true;
                    if (g.Room > sig.MaxWarehouseRoom) sig.MaxWarehouseRoom = g.Room;
                }
            }

            // ── Per-group rate (flow — sizing/static checks only, never the demand gate) ─────────
            var evalByKey = new Dictionary<string, GroupEval>();
            foreach (var kv in groups)
            {
                bool hasRate = MetabolicPlane.TryGetRatePerMinute(kv.Key, Plugin.BudgetWindowSeconds.Value, out float rate, out _);
                evalByKey[kv.Key] = new GroupEval { Group = kv.Value, HasRate = hasRate, Rate = rate };
            }

            var proposals = new List<(string Line, int Severity, string Key)>();
            int hogs = 0, blockage = 0, surplus = 0, shadowed = 0, starved = 0, off = 0;
            // v0.13.0 — item-level dedup: a HOG victim (a genuinely-needy demanded item being rescued)
            // is never ALSO a BLOCKAGE; a hog item (surplus, actively crowding) is never ALSO a SURPLUS
            // informational line. Both are item-name sets so a finding is reported once per item.
            var hogVictimItems = new HashSet<string>();
            var hoggedItems = new HashSet<string>();

            // ── Pass 1: HOG (containers with >= 2 effective items) ──────────────────────────────
            foreach (var ckv in containerGroups)
            {
                try
                {
                    string containerKey = ckv.Key;
                    if (!containerMap.TryGetValue(containerKey, out var snap)) continue;

                    var effective = new List<BudgetGroup>();
                    foreach (var g in ckv.Value) if (g.MinRank != 3) effective.Add(g);
                    if (effective.Count < 2) continue;

                    BudgetGroup? bestVictim = null;
                    string bestVictimBlk = "fill";
                    foreach (var v in effective)
                    {
                        var vSig = itemSignals[v.Item];
                        if (!vSig.Demanded) continue;
                        if (IsSurplus(vSig)) continue;        // v0.13.0 — victim must be genuinely needy, not itself surplus
                        if (v.Stored >= v.QuotaSum) continue; // must be unmet

                        bool blocked = IsBlocked(snap, v.Info, out string vBlk);
                        if (!blocked) continue;

                        if (bestVictim == null || vSig.StructTotal > itemSignals[bestVictim.Item].StructTotal)
                        {
                            bestVictim = v;
                            bestVictimBlk = vBlk;
                        }
                    }
                    if (bestVictim == null) continue;

                    BudgetGroup? bestHog = null;
                    foreach (var x in effective)
                    {
                        if (ReferenceEquals(x, bestVictim)) continue;
                        if (!IsSurplus(itemSignals[x.Item])) continue;    // v0.13.0 — hog must be SURPLUS (was: undemanded)
                        if (x.Stored < Plugin.HogMinStored.Value) continue;

                        // No-rate counts as static here (see class header) — a fresh-load F9 before
                        // any metabolic samples exist would otherwise hide every hog candidate.
                        var xEval = evalByKey[x.PosKey + "|" + x.Item];
                        bool staticOk = !xEval.HasRate || Math.Abs(xEval.Rate) <= Plugin.HogMaxAbsRate.Value;
                        if (!staticOk) continue;

                        if (bestHog == null || x.Stored > bestHog.Stored) bestHog = x;
                    }
                    if (bestHog == null) continue;

                    var proposal = BuildEvictionProposal(containerKey, snap, bestHog, itemSignals[bestHog.Item], bestVictim, itemSignals[bestVictim.Item], bestVictimBlk);
                    proposals.Add(proposal);
                    evalByKey[bestHog.PosKey + "|" + bestHog.Item].Class = "hog";
                    hogVictimItems.Add(bestVictim.Item);
                    hoggedItems.Add(bestHog.Item);
                    hogs++;
                }
                catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] [budget] HOG pass container '{ckv.Key}' error: {ex}"); }
            }

            // ── Pass 2: BLOCKAGE (diagnostic, v0.13.0) ──────────────────────────────────────────
            // A demanded, NON-surplus material that is genuinely stuck: SITTING (stored>0) in a full,
            // static container while its downstream BOM consumers rely on it (Hardwood Long Sticks
            // jammed at the Woodcutter's while Shafts/Beams demand it at the Carpenter's). This is a
            // routing/priority problem — NOT physical crowding (HOG) and NOT over-production (SURPLUS,
            // which is why surplus items are excluded here). One line per ITEM (deduped, reported at
            // its most-piled stuck location), carrying a cause token from the item's warehouse rows:
            //   no-route        — the item has no true-warehouse allotment anywhere (nowhere to haul),
            //   destination-full — it has warehouse rows but all are full (room 0),
            //   priority-shadowed — a warehouse row has room but isn't filling (haulers not coming).
            // Diagnostic only — no eviction, no state write.
            var blockageBest = new Dictionary<string, (BudgetGroup Group, string Blk, string Container)>();
            foreach (var kv in groups)
            {
                try
                {
                    var g = kv.Value;
                    if (g.MinRank == 3) continue;                        // this row fully player-Off
                    if (g.Stored <= 0) continue;                         // must be SITTING here (piled)
                    var sig = itemSignals[g.Item];
                    if (!sig.Demanded) continue;                         // must have downstream reliance
                    if (IsSurplus(sig)) continue;                        // surplus is HOG/SURPLUS, not BLOCKAGE
                    if (hogVictimItems.Contains(g.Item)) continue;       // already rescued as a HOG victim

                    bool stuck = false; string stuckBlk = "fill"; string stuckContainer = "?";
                    foreach (var ck in g.ContainerKeys)
                    {
                        if (!containerMap.TryGetValue(ck, out var cs)) continue;
                        if (IsBlocked(cs, g.Info, out string blk)) { stuck = true; stuckBlk = blk; stuckContainer = LastSix(ck); break; }
                    }
                    if (!stuck) continue;

                    var ev = evalByKey[kv.Key];
                    if (ev.HasRate && Math.Abs(ev.Rate) > Plugin.HogMaxAbsRate.Value) continue; // static only

                    if (!blockageBest.TryGetValue(g.Item, out var cur) || g.Stored > cur.Group.Stored)
                        blockageBest[g.Item] = (g, stuckBlk, stuckContainer);
                }
                catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] [budget] BLOCKAGE scan '{kv.Key}' error: {ex}"); }
            }
            foreach (var bkv in blockageBest)
            {
                try
                {
                    var (g, stuckBlk, stuckContainer) = bkv.Value;
                    var sig = itemSignals[g.Item];
                    evalByKey[g.PosKey + "|" + g.Item].Class = "blockage";
                    blockage++;

                    string cause = !sig.HasWarehouseRow ? "no-route"
                        : sig.MaxWarehouseRoom > 0 ? "priority-shadowed"
                        : "destination-full";

                    string wantedStr = DemandGraph.TryGetWantedByLabels(g.Item, out var labels)
                        ? string.Join(", ", labels)
                        : $"{sig.NoProdNeededBy ?? "downstream demand"} (no direct-consumer labels — derived-only)";

                    string structStr = $"{sig.StructTotal}({sig.StructDirect}/{sig.StructDerived})";
                    string line =
                        $"[SupplyChain] [budget] BLOCKAGE '{g.Item}' @ '{g.Warehouse}' [{stuckContainer}]: " +
                        $"demanded material stuck (stored={g.Stored} full+static blk={stuckBlk} structural={structStr}) " +
                        $"cause={cause} while downstream relies on it: [{wantedStr}] -> supply not routing to demand (dry-run)";
                    proposals.Add((line, sig.StructTotal, $"blockage|{g.Item}"));
                }
                catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] [budget] BLOCKAGE emit '{bkv.Key}' error: {ex}"); }
            }

            // ── Pass 2b: SURPLUS (informational, v0.13.0) ───────────────────────────────────────
            // A surplus item (stock ≫ demand) that ISN'T currently crowding a demanded victim — i.e.
            // over-produced and just sitting around, no shared-container HOG relationship. One line
            // per item (deduped). Distinct from HOG (a surplus item that IS crowding → actionable
            // eviction) and BLOCKAGE (a genuinely-short demanded item stuck). Informational only.
            foreach (var skv in itemSignals)
            {
                try
                {
                    string item = skv.Key;
                    var sig = skv.Value;
                    if (!IsSurplus(sig)) continue;
                    if (sig.SettleStored <= 0) continue;
                    if (hoggedItems.Contains(item)) continue;            // already an actionable HOG

                    surplus++;
                    double keepTarget = sig.StructTotal * (double)Math.Max(0f, Plugin.SurplusFactor.Value);
                    string demStr = sig.StructTotal > 0 ? $"demand={sig.StructTotal}" : "undemanded";
                    string line =
                        $"[SupplyChain] [budget] SURPLUS '{item}': settleStored={sig.SettleStored} {demStr} keepTarget={keepTarget:F0} " +
                        $"top='{sig.TopSource}' -> over-produced vs demand, reduce candidate (informational, dry-run)";
                    proposals.Add((line, sig.StructTotal, $"surplus|{item}"));
                }
                catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] [budget] SURPLUS '{skv.Key}' error: {ex}"); }
            }

            // ── Pass 3: SHADOWED / OFF-CONFLICT / STARVED / healthy / no-data (remaining groups) ─
            int noData = 0, healthy = 0, warehouseGroups = 0, hutGroups = 0;
            var itemsWithFinding = new HashSet<string>();
            var shadowedCandidatesByItem = new Dictionary<string, List<string>>();

            foreach (var kv in groups)
            {
                var g = kv.Value;
                if (g.IsHut) hutGroups++; else warehouseGroups++;

                var ev = evalByKey[kv.Key];
                if (ev.Class == "hog" || ev.Class == "blockage") continue; // already decided, passes 1-2

                var sig = itemSignals[g.Item];
                // v0.13.0 — an item already classified at the item level (surplus, a HOG victim, or a
                // BLOCKAGE) doesn't earn a second SHADOWED/STARVED/OFF finding from its other rows.
                if (IsSurplus(sig) || hogVictimItems.Contains(g.Item) || blockageBest.ContainsKey(g.Item)) continue;
                bool unmet = g.Stored < g.QuotaSum;
                bool anyContainerFull = false;
                foreach (var ck in g.ContainerKeys)
                {
                    if (containerMap.TryGetValue(ck, out var s) && s.FillRatio >= 0.999f) { anyContainerFull = true; break; }
                }

                if (!g.IsHut && sig.Demanded && unmet && g.Room > 0 && g.MinRank >= 1 && g.MinRank <= 2)
                {
                    if (!ev.HasRate)
                    {
                        ev.Class = "no-data";
                        noData++;
                    }
                    else if (Math.Abs(ev.Rate) <= Plugin.ShadowMaxFillRate.Value && sig.HutStock >= Plugin.ShadowMinSourceStock.Value)
                    {
                        if (!shadowedCandidatesByItem.TryGetValue(g.Item, out var cands))
                        {
                            cands = new List<string>();
                            shadowedCandidatesByItem[g.Item] = cands;
                        }
                        cands.Add(kv.Key);
                    }
                    else
                    {
                        ev.Class = "healthy";
                        healthy++;
                    }
                }
                else if (!g.IsHut && sig.Demanded && g.MinRank == 3 && (sig.HutStock >= Plugin.ShadowMinSourceStock.Value || anyContainerFull))
                {
                    ev.Class = "off";
                    off++;
                    itemsWithFinding.Add(g.Item);
                }
                else if (!g.IsHut && sig.Demanded && g.MinRank == 0 && unmet && g.Room > 0)
                {
                    if (!ev.HasRate)
                    {
                        ev.Class = "no-data";
                        noData++;
                    }
                    else if (Math.Abs(ev.Rate) <= Plugin.ShadowMaxFillRate.Value && sig.HutStock > 0)
                    {
                        ev.Class = "starved";
                        starved++;
                        itemsWithFinding.Add(g.Item);
                    }
                    else
                    {
                        ev.Class = "healthy";
                        healthy++;
                    }
                }
                else
                {
                    ev.Class = "healthy";
                    healthy++;
                }
            }

            // ── SHADOWED winner selection (one destination per item; the rest = "shadowed-alt") ──
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
                        string structStr = $"{sig.StructTotal}";
                        string line =
                            $"[SupplyChain] [budget] SHADOWED '{g.Item}' @ '{g.Warehouse}': stored={g.Stored}/quotaSum={g.QuotaSum} room={g.Room} " +
                            $"rank={g.MinRank} rate={FormatRate(ev.Rate)}/min structural={structStr} source='{sig.TopSource}' hutStock={sig.HutStock} " +
                            $"-> propose tier {g.MinRank}->0 (High) (dry-run){altsStr}";
                        proposals.Add((line, sig.StructTotal, $"shadowed|{g.PosKey}|{g.Item}"));
                    }
                    else
                    {
                        ev.Class = "shadowed-alt";
                    }
                }
            }

            // ── Summary + throttled proposal logging (same fingerprint/severity-cap mechanics) ──
            int demandItems = 0, demandDirect = 0, demandDerived = 0;
            foreach (var kv in itemSignals)
            {
                if (!kv.Value.Demanded) continue;
                demandItems++;
                if (kv.Value.StructDirect > 0) demandDirect++;
                if (kv.Value.StructDerived > 0) demandDerived++;
            }

            Plugin.Logger.LogInfo(
                $"[SupplyChain] [budget] groups={groupCount} (wh={warehouseGroups} hut={hutGroups}) " +
                $"demandItems={demandItems}(direct={demandDirect} derived={demandDerived}) hogs={hogs} blockage={blockage} surplus={surplus} " +
                $"shadowed={shadowed} starved={starved} off={off} healthy={healthy} noData={noData} (dry-run)");

            var proposalKeys = new List<string>();
            foreach (var p in proposals) proposalKeys.Add(p.Key);
            proposalKeys.Sort(StringComparer.Ordinal);
            string proposalSetString = string.Join(",", proposalKeys);
            bool proposalSetChanged = proposalSetString != _lastProposalSet;

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
                    string kindStr = g.IsHut ? "hut" : "wh";
                    string containerStr = "-";
                    foreach (var ck in g.ContainerKeys) { containerStr = LastSix(ck); break; }
                    Plugin.Logger.LogInfo(
                        $"[SupplyChain] [budget] row: '{g.Item}' @ '{g.Warehouse}' kind={kindStr} stored={g.Stored} quotaSum={g.QuotaSum} " +
                        $"rows={g.RowCount} room={g.Room} maxCap={g.MaxCapacity} rank={g.MinRank} rate={rateStr} container={containerStr} " +
                        $"structural={sig.StructTotal}({sig.StructDirect}/{sig.StructDerived}) class={ev.Class}");
                }

                int shownItems = 0;
                foreach (var item in itemsWithFinding)
                {
                    if (shownItems >= 40) break;
                    var sig = itemSignals[item];
                    Plugin.Logger.LogInfo(
                        $"[SupplyChain] [budget] item '{item}': structural={sig.StructTotal}({sig.StructDirect}/{sig.StructDerived}) " +
                        $"settleStored={sig.SettleStored} hutStock={sig.HutStock} noProd={(sig.NoProdNeededBy ?? "none")}");
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

    // v0.11.1 fix 2 — blocked(item at container C) = C.GetRemainingCapacity(item)==0 (first runtime
    // use of this primitive; own try/catch). Falls back to FillRatio>=0.999 when the item is null or
    // the read throws. `primitive` tells the caller which one decided ("cap" vs "fill") for the
    // blk=cap|fill log token — replaces v0.11.0's HasSpace(item,1)==false, which fired on containers
    // at fill 0.94-0.98 (its semantics evidently aren't "no free slot").
    // v0.13.0 — surplus = settlement-wide stock exceeds the item's demand-grounded keep-target
    // (structural demand × SurplusFactor). A zero-demand item is surplus at any stock > 0. This is
    // the first-class hog-eligibility signal, replacing the binary demanded/undemanded gate: an item
    // with tiny demand but a huge pile (Resin: demand 3, stock 1900) is surplus and can be a hog,
    // while a genuinely short demanded item (Fibers: 300 vs 484×2) is not.
    private static bool IsSurplus(ItemSignal sig)
    {
        double keepTarget = sig.StructTotal * (double)Math.Max(0f, Plugin.SurplusFactor.Value);
        return sig.SettleStored > keepTarget;
    }

    private static bool IsBlocked(WarehouseWatch.ContainerSnapshot snap, ItemInfo? info, out string primitive)
    {
        if (info != null)
        {
            try
            {
                int remaining = snap.Container.GetRemainingCapacity(info);
                primitive = "cap";
                return remaining == 0;
            }
            catch { }
        }
        primitive = "fill";
        return snap.FillRatio >= 0.999f;
    }

    // HOG eviction sizing (v0.13.0) — DEMAND-GROUNDED. A hog is a SURPLUS item (stock ≫ demand), so
    // it's reduced toward keepUnits = max(CoProductFloorSlots × stack, its own structural demand) —
    // never below what the item itself needs (bones demand 0 → keep 1 slot; Resin demand 3 → still
    // keep ≥ 1 slot; a demand-500 item → keep 500). Everything above keepUnits is surplus to reduce.
    // The FULL target and the per-cycle step (capped at MaxSlotsPerCycle so it self-paces,
    // response-gated at arming) are BOTH logged. The demanded, blocked VICTIM this hog crowds is
    // named as the justification. `blkToken` ("cap"|"fill") is IsBlocked's primitive. Dry-run — no DropItem.
    private static (string Line, int Severity, string Key) BuildEvictionProposal(
        string containerKey, WarehouseWatch.ContainerSnapshot snap,
        BudgetGroup hogGroup, ItemSignal hogSig, BudgetGroup victimGroup, ItemSignal victimSig, string blkToken)
    {
        int rowUnmet = Math.Max(0, victimGroup.QuotaSum - victimGroup.Stored);
        int structTotal = victimSig.StructTotal;

        int stackX = 1;
        try { if (hogGroup.Info != null) stackX = Math.Max(1, snap.Container.GetStackSize(hogGroup.Info)); } catch { }

        int floorUnits = Math.Max(0, Plugin.CoProductFloorSlots.Value) * stackX;
        int keepUnits = Math.Max(floorUnits, hogSig.StructTotal);      // v0.13.0 — never below the hog's own demand
        int stored = Math.Max(0, hogGroup.Stored);
        int surplusUnits = Math.Max(0, stored - keepUnits);
        int targetUnits = stored - surplusUnits;                       // == min(stored, keepUnits)
        int fullSlots = stackX > 0 ? (int)Math.Ceiling(surplusUnits / (double)stackX) : 0;

        int cap = Math.Max(0, Plugin.MaxSlotsPerCycle.Value);
        int cycleSlots = Math.Min(Math.Max(0, fullSlots), cap);
        int cycleUnits = Math.Min(cycleSlots * stackX, surplusUnits);  // this cycle's paced step

        string containerKeyLast6 = LastSix(containerKey);
        string structStr = $"{structTotal}({victimSig.StructDirect}/{victimSig.StructDerived})";
        string hogDemStr = hogSig.StructTotal > 0 ? $"demand={hogSig.StructTotal}" : "undemanded";

        string line =
            $"[SupplyChain] [budget] HOG '{hogGroup.Item}' vs victim '{victimGroup.Item}' @ '{victimGroup.Warehouse}' [{containerKeyLast6}]: " +
            $"surplus X stored={stored} ({hogDemStr}) crowds demanded V (structural={structStr} rowUnmet={rowUnmet} fill={snap.FillRatio:F2} blk={blkToken} stackX={stackX}) " +
            $"-> reduce X {stored}->{targetUnits} (surplus {surplusUnits}={fullSlots} slot(s)); this cycle {cycleUnits} ({cycleSlots}/{cap} slot(s)) gate=ready (dry-run)";

        string key = $"hog|{containerKey}|{hogGroup.Item}|{victimGroup.Item}";
        return (line, structTotal, key);
    }

    private static string LastSix(string hexKey)
    {
        if (string.IsNullOrEmpty(hexKey)) return "?";
        string h = hexKey.StartsWith("0x") ? hexKey.Substring(2) : hexKey;
        return h.Length <= 6 ? h : h.Substring(h.Length - 6);
    }

    private static string FormatRate(float rate)
    {
        string sign = rate >= 0 ? "+" : "";
        return $"{sign}{rate:F1}";
    }
}
