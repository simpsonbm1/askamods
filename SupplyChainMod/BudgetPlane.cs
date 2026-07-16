using System;
using System.Collections.Generic;
using System.Diagnostics;
using SandSailorStudio.Inventory;

namespace SupplyChainMod;

// BudgetPlane (v0.14.2, DEMAND_MODEL_PLAN.md "v0.14.x — SURPLUS v2") — the READ-ONLY classifier,
// built on VERIFIED container data (WarehouseWatch's per-poll physical container map) + structural
// demand (DemandGraph's direct+derived table) + a settlement-wide flow/awake clock (this file).
// Fully dry-run: no quota write, no DropItem eviction, no SetPriority — every proposal below is
// LOGGED ONLY.
//
// Two orthogonal predicates decide what may be evicted or reported as SURPLUS:
//   - OverTarget(item) = settlement-wide stock exceeds keepTarget, keepTarget = RatchetTotal (the
//     SESSION HIGH-WATER MARK of StructTotal, cleared on world-leave) x SurplusFactor. A
//     task-completion dip in structural demand no longer condemns stock sized against a higher
//     demand moments earlier — the ratchet errs toward keeping.
//   - Verdict(item) — directional, threshold-free, read over the last InertMinutes of AWAKE time
//     (the settlement-wide clock advances only on polls where >= 1 item's total moved, so vanilla
//     night/sleep, pauses, and menu time age nothing):
//       ALIVE   — any observed DECREASE within the window (consumption or in-flight hauling).
//                 Never surplus; one decrease instantly rescues a DEAD/GROWING item — dead is
//                 never a permanent judgment. Consumption memory (v0.14.2): a REAL observed
//                 decrease (not the warmup seed below) keeps the item ALIVE for 6x InertMinutes,
//                 not just one window — accumulation is continuous but consumption is EVENTFUL
//                 (bursty, task-cadenced), so a single-window read misjudged slow-consumed base
//                 materials like Fibers as GROWING (v0.14.1 in-game finding). Never-consumed
//                 co-products (bones) are still never rescued by this.
//       GROWING — increases only (co-products with no consumer: bones, resin trickle).
//       DEAD    — no movement at all within the window.
//     Warmup: a freshly-seen item starts "assumed alive" — no SURPLUS/HOG verdict can exist until
//     InertMinutes of observed awake inactivity. Closes the old demand-blind post-load window.
//   SurplusV2(item) = OverTarget(item) AND Verdict(item) != alive. Flow-awareness applies ONLY to
//   "what may be evicted" — HOG-victim neediness and the BLOCKAGE/pass-3 exclusions stay on
//   OverTarget alone (magnitude-based), preserving the classifier's verified false-hog suppression
//   (seeds crowding Fibers stays correctly excluded).
//
// Classes, evaluated in this priority order:
//   1. HOG (eviction) — a container with >= 2 effective items (per-CONTAINER Off rank, v0.14.0 —
//      see containerItemMinRank below, not the structure-wide MinRank) holds a SurplusV2 item
//      crowding out a demanded, NOT-OverTarget, blocked victim (IsBlocked =
//      GetRemainingCapacity==0, own try/catch, falls back to FillRatio>=0.999). Sizing is
//      DEMAND-GROUNDED (BuildEvictionProposal): reduce the hog toward
//      max(CoProductFloorSlots, its own RATCHETED demand).
//   2. BLOCKAGE (diagnostic) — a demanded, NOT-OverTarget material genuinely stuck: SITTING
//      (stored>0) in a full, static container while downstream BOM consumers rely on it. A
//      per-container Off row (v0.14.0) is skipped as player-frozen stock, not a blockage. One line
//      per ITEM with a cause token (no-route / destination-full / priority-shadowed).
//   2b. SURPLUS (informational) — a SurplusV2 item NOT currently crowding a demanded victim.
//   3. SHADOWED — warehouse-only: demanded, unmet, room, rank Med/Low (never Off), static-or-slow
//      fill, real upstream evidence (hut stock >= ShadowMinSourceStock). One destination per item.
//   4. OFF-CONFLICT (informational only) — demanded item whose row is player-Off while supply
//      visibly wants to flow there.
//   5. STARVED (informational only) — demanded, already High, unmet, room, static, hut source
//      exists despite the top rank — a hauler-capacity symptom, not a priority problem.
//   6. healthy / no-data.
//
// v0.14.0 riders (DEMAND_MODEL_PLAN.md, same section):
//   - Storage-full distress tag — a HOG/BLOCKAGE/SHADOWED line whose relevant PosKey (HOG=victim
//     group, BLOCKAGE=stuck group, SHADOWED=winner group) reads WarehouseWatch.IsStorageFullActive
//     gets " distress=storage-full" appended and +100000 severity (floats to the top of the sort).
//     SURPLUS lines never get the tag (informational only).
//   - Per-container Off overlay — HOG effective-membership and the BLOCKAGE stuck-scan key off the
//     per-(container,item) row rank (containerItemMinRank), not the structure-wide MinRank, so an
//     Off'd container reads as the player's manual dedication, not a stuck/crowding signal.
//
// Config: SurplusFactor (the keep-target multiplier applied to the ratchet) is the only
// user-facing tuning knob; InertMinutes (default 20, awake-minutes) is the verdict window. Still
// fully READ-ONLY — no arming path exists in this version.
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
        // v0.14.0 — session high-water mark of StructTotal (set from the Evaluate ratchet step);
        // the keep-target basis for OverTarget, so a mid-session structural-demand dip (a task
        // batch completing) never condemns stock sized against a higher demand moments earlier.
        public int RatchetTotal;
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

    // v0.14.0 — per-item flow memory (plain CLR types only — never interop wrappers in statics).
    // AwakeAtLast{Decrease,Increase} are snapshots of _awakeSeconds at the moment a settlement-wide
    // total last moved in that direction; VerdictFor() diffs them against the CURRENT _awakeSeconds.
    private sealed class ItemFlow
    {
        public int PrevTotal;
        public bool HasPrev;
        public float AwakeAtLastDecrease;
        public float AwakeAtLastIncrease;
        // v0.14.2 — consumption memory. Set ONLY on a REAL observed decrease (never by the load-time
        // "assumed alive" warmup seed, which stamps AwakeAtLastDecrease alone). VerdictFor() uses
        // these to extend ALIVE protection past the single-window default — see class header.
        public bool HasRealDecrease;
        public float AwakeAtLastRealDecrease;
    }

    // Fingerprint of the last-logged proposal set — proposal lines are only re-logged when this
    // changes, to keep log volume sane on a steady-state settlement (same throttle as v0.8.0/v0.9.x).
    private static string _lastProposalSet = "";

    // v0.14.0 static state (per-world; all plain CLR types — see ItemFlow note above).
    private static Dictionary<string, ItemFlow> _flows = new();        // keyed by item name
    private static Dictionary<string, int> _ratchetStruct = new();     // session high-water of StructTotal
    private static float _awakeSeconds;                                // cumulative AWAKE time this world
    private static float _lastEvalRealTime = -1f;                      // Time.time at the last Evaluate

    internal static void NoteWorldLeft()
    {
        try
        {
            _lastProposalSet = "";
            _flows = new Dictionary<string, ItemFlow>();
            _ratchetStruct = new Dictionary<string, int>();
            _awakeSeconds = 0f;
            _lastEvalRealTime = -1f;
        }
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
            // v0.14.0 — per-(container,item) MIN rank, keyed "containerKey|item". Lets HOG/BLOCKAGE
            // read a player's Off dedication at the PHYSICAL container level instead of the
            // structure-wide MinRank (a structure can have the same item Off in one container and
            // active in another — the ore/bloom two-container case from the design doc).
            var containerItemMinRank = new Dictionary<string, int>();
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
                if (row.ContainerKey.Length > 0)
                {
                    g.ContainerKeys.Add(row.ContainerKey);
                    string cik = row.ContainerKey + "|" + row.Item;
                    if (!containerItemMinRank.TryGetValue(cik, out int curMin) || row.Rank < curMin)
                        containerItemMinRank[cik] = row.Rank;
                }
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

            // ── v0.14.0 (a) Ratchet: session high-water mark of StructTotal, per item ───────────────
            foreach (var kv in itemSignals)
            {
                string item = kv.Key;
                var sig = kv.Value;
                _ratchetStruct.TryGetValue(item, out int existingRatchet);
                int newRatchet = Math.Max(existingRatchet, sig.StructTotal);
                _ratchetStruct[item] = newRatchet;
                sig.RatchetTotal = newRatchet;
            }

            // ── v0.14.0 (b) Movement / awake-clock tracking ─────────────────────────────────────────
            // The settlement-wide awake clock advances only on polls where >= 1 item's settlement
            // total actually moved (so vanilla night/sleep/pauses/menu time ages nothing), clamped to
            // 120s per poll so a long gap between evaluates can't credit huge awake time in one step.
            bool anyMovement = false;
            foreach (var kv in itemSignals)
            {
                if (_flows.TryGetValue(kv.Key, out var mf) && mf.HasPrev && kv.Value.SettleStored != mf.PrevTotal)
                {
                    anyMovement = true;
                    break;
                }
            }
            if (anyMovement && _lastEvalRealTime >= 0f)
                _awakeSeconds += Math.Min(Math.Max(0f, UnityEngine.Time.time - _lastEvalRealTime), 120f);

            foreach (var kv in itemSignals)
            {
                string item = kv.Key;
                var sig = kv.Value;
                if (!_flows.TryGetValue(item, out var flow))
                {
                    // Warmup — a freshly-seen item is "assumed alive" so no dead/growing verdict can
                    // exist until InertMinutes of subsequently-observed awake inactivity.
                    flow = new ItemFlow { AwakeAtLastDecrease = _awakeSeconds, AwakeAtLastIncrease = _awakeSeconds };
                    _flows[item] = flow;
                }
                else if (sig.SettleStored < flow.PrevTotal)
                {
                    flow.AwakeAtLastDecrease = _awakeSeconds;
                    // v0.14.2 — a REAL observed decrease (as opposed to the warmup "assumed alive"
                    // seed above, which only stamps AwakeAtLastDecrease) starts the consumption-memory
                    // window read by VerdictFor().
                    flow.HasRealDecrease = true;
                    flow.AwakeAtLastRealDecrease = _awakeSeconds;
                }
                else if (sig.SettleStored > flow.PrevTotal) flow.AwakeAtLastIncrease = _awakeSeconds;

                flow.PrevTotal = sig.SettleStored;
                flow.HasPrev = true;
            }
            _lastEvalRealTime = UnityEngine.Time.time;

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

                    // v0.14.0 — effective membership now keys off the PER-CONTAINER min rank for
                    // this item (falls back to the structure-wide MinRank if somehow missing), not
                    // the structure-wide MinRank alone — a structure can Off an item in ONE physical
                    // container while it stays active in another.
                    var effective = new List<BudgetGroup>();
                    foreach (var g in ckv.Value)
                    {
                        int rankHere = containerItemMinRank.TryGetValue(containerKey + "|" + g.Item, out int perContainerRank)
                            ? perContainerRank : g.MinRank;
                        if (rankHere != 3) effective.Add(g);
                    }
                    if (effective.Count < 2) continue;

                    BudgetGroup? bestVictim = null;
                    string bestVictimBlk = "fill";
                    foreach (var v in effective)
                    {
                        var vSig = itemSignals[v.Item];
                        if (!vSig.Demanded) continue;
                        if (OverTarget(vSig)) continue;       // victim must be genuinely needy, not itself over-target
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
                        if (!IsSurplusV2(x.Item, itemSignals[x.Item])) continue; // v0.14.0 — OverTarget AND dead/growing
                        if (x.Stored < Plugin.HogMinStored.Value) continue;

                        // No-rate counts as static here (see class header) — a fresh-load F9 before
                        // any metabolic samples exist would otherwise hide every hog candidate.
                        var xEval = evalByKey[x.PosKey + "|" + x.Item];
                        bool staticOk = !xEval.HasRate || Math.Abs(xEval.Rate) <= Plugin.HogMaxAbsRate.Value;
                        if (!staticOk) continue;

                        if (bestHog == null || x.Stored > bestHog.Stored) bestHog = x;
                    }
                    if (bestHog == null) continue;

                    string hogVerdict = VerdictFor(bestHog.Item);
                    bool hogDistress = WarehouseWatch.IsStorageFullActive(bestVictim.PosKey);
                    var proposal = BuildEvictionProposal(containerKey, snap, bestHog, itemSignals[bestHog.Item], bestVictim,
                        itemSignals[bestVictim.Item], bestVictimBlk, hogVerdict, hogDistress);
                    proposals.Add(proposal);
                    evalByKey[bestHog.PosKey + "|" + bestHog.Item].Class = "hog";
                    hogVictimItems.Add(bestVictim.Item);
                    hoggedItems.Add(bestHog.Item);
                    hogs++;
                }
                catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] [budget] HOG pass container '{ckv.Key}' error: {ex}"); }
            }

            // ── Pass 2: BLOCKAGE (diagnostic, v0.13.0) ──────────────────────────────────────────
            // A demanded, NOT-OverTarget material that is genuinely stuck: SITTING (stored>0) in a
            // full, static container while its downstream BOM consumers rely on it. A routing/
            // priority problem — NOT physical crowding (HOG) and NOT over-production (SURPLUS,
            // which is why OverTarget items are excluded here). One line per ITEM (deduped, reported
            // at its most-piled stuck location), carrying a cause token from the item's warehouse rows:
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
                    if (OverTarget(sig)) continue;                       // over-target is HOG/SURPLUS, not BLOCKAGE
                    if (hogVictimItems.Contains(g.Item)) continue;       // already rescued as a HOG victim

                    bool stuck = false; string stuckBlk = "fill"; string stuckContainer = "?";
                    foreach (var ck in g.ContainerKeys)
                    {
                        // v0.14.0 — an Off'd container for THIS item is the player's manual
                        // dedication, not a stuck signal, even if the group as a whole isn't Off.
                        if (containerItemMinRank.TryGetValue(ck + "|" + g.Item, out int ckRank) && ckRank == 3) continue;
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

                    // v0.14.0 — storage-full distress rider: keyed off the stuck group's OWN PosKey.
                    bool distress = WarehouseWatch.IsStorageFullActive(g.PosKey);
                    string distressStr = distress ? " distress=storage-full" : "";
                    int severity = sig.StructTotal + (distress ? 100000 : 0);

                    string structStr = $"{sig.StructTotal}({sig.StructDirect}/{sig.StructDerived})";
                    string line =
                        $"[SupplyChain] [budget] BLOCKAGE '{g.Item}' @ '{g.Warehouse}' [{stuckContainer}]: " +
                        $"demanded material stuck (stored={g.Stored} full+static blk={stuckBlk} structural={structStr}) " +
                        $"cause={cause} while downstream relies on it: [{wantedStr}] -> supply not routing to demand (dry-run){distressStr}";
                    proposals.Add((line, severity, $"blockage|{g.Item}"));
                }
                catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] [budget] BLOCKAGE emit '{bkv.Key}' error: {ex}"); }
            }

            // ── Pass 2b: SURPLUS (informational, v0.13.0) ───────────────────────────────────────
            // A SurplusV2 item (over-target AND dead/growing) that ISN'T currently crowding a
            // demanded victim — i.e. over-produced and just sitting around, no shared-container HOG
            // relationship. One line per item (deduped). Distinct from HOG (a surplus item that IS
            // crowding → actionable eviction) and BLOCKAGE (a genuinely-short demanded item stuck).
            // Informational only — never gets the storage-full distress tag (see file header).
            foreach (var skv in itemSignals)
            {
                try
                {
                    string item = skv.Key;
                    var sig = skv.Value;
                    if (!IsSurplusV2(item, sig)) continue;
                    if (sig.SettleStored <= 0) continue;
                    if (hoggedItems.Contains(item)) continue;            // already an actionable HOG

                    surplus++;
                    double keepTarget = sig.RatchetTotal * (double)Math.Max(0f, Plugin.SurplusFactor.Value);
                    string ratchetStr = sig.RatchetTotal > sig.StructTotal ? $" ratchet={sig.RatchetTotal}" : "";
                    string demStr = sig.StructTotal > 0 ? $"demand={sig.StructTotal}" : "undemanded";
                    string verdict = VerdictFor(item);
                    string line =
                        $"[SupplyChain] [budget] SURPLUS '{item}': settleStored={sig.SettleStored} {demStr}{ratchetStr} keepTarget={keepTarget:F0} " +
                        $"verdict={verdict} top='{sig.TopSource}' -> over-produced vs demand, reduce candidate (informational, dry-run)";
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
                // v0.13.0 — an item already classified at the item level (over-target, a HOG victim,
                // or a BLOCKAGE) doesn't earn a second SHADOWED/STARVED/OFF finding from its other rows.
                if (OverTarget(sig) || hogVictimItems.Contains(g.Item) || blockageBest.ContainsKey(g.Item)) continue;
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
                        // v0.14.0 — storage-full distress rider: keyed off the WINNER group's PosKey.
                        bool distress = WarehouseWatch.IsStorageFullActive(g.PosKey);
                        string distressStr = distress ? " distress=storage-full" : "";
                        int severity = sig.StructTotal + (distress ? 100000 : 0);
                        string line =
                            $"[SupplyChain] [budget] SHADOWED '{g.Item}' @ '{g.Warehouse}': stored={g.Stored}/quotaSum={g.QuotaSum} room={g.Room} " +
                            $"rank={g.MinRank} rate={FormatRate(ev.Rate)}/min structural={structStr} source='{sig.TopSource}' hutStock={sig.HutStock} " +
                            $"-> propose tier {g.MinRank}->0 (High) (dry-run){altsStr}{distressStr}";
                        proposals.Add((line, severity, $"shadowed|{g.PosKey}|{g.Item}"));
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

    // v0.14.0 — OverTarget(item) = settlement-wide stock exceeds the RATCHETED (session high-water
    // mark of StructTotal, set by Evaluate's ratchet step) keep-target: RatchetTotal x SurplusFactor.
    // Replaces v0.13.0's IsSurplus, which used the raw (dip-able) StructTotal directly — a
    // task-completion dip in structural demand no longer condemns stock that was legitimately sized
    // against a higher demand moments earlier. A zero-demand item is still OverTarget at any stock > 0.
    private static bool OverTarget(ItemSignal sig)
    {
        double keepTarget = sig.RatchetTotal * (double)Math.Max(0f, Plugin.SurplusFactor.Value);
        return sig.SettleStored > keepTarget;
    }

    // v0.14.0 — SurplusV2(item) = OverTarget AND Verdict(item) != alive. This is the HOG-eligibility
    // and SURPLUS-class gate. HOG-victim neediness and the BLOCKAGE/pass-3 exclusions deliberately
    // stay on OverTarget alone (magnitude-based) — see the file header for why.
    private static bool IsSurplusV2(string item, ItemSignal sig) => OverTarget(sig) && VerdictFor(item) != "alive";

    // v0.14.2 — consumption-memory multiplier: a REAL observed decrease protects an item as ALIVE
    // for this many InertMinutes windows (not just one), because consumption is bursty/task-cadenced
    // while accumulation is continuous (see class header + VerdictFor). Internal constant by design
    // — no config key.
    private const float AliveMemoryWindows = 6f; // consumption events follow task cadence — see class header

    // "alive" | "growing" | "dead". One observed decrease within the window = alive (instant
    // recovery from dead/growing — dead is never a permanent judgment). Only-increases = growing
    // (co-products with no consumer). No movement at all = dead. Window counts AWAKE seconds only.
    // v0.14.2 — a REAL decrease (not the warmup seed) additionally keeps the item ALIVE for
    // AliveMemoryWindows x the normal window, so a base material whose consumers run on a slower
    // task cadence than the observation window isn't misread as growing/dead between bursts.
    private static string VerdictFor(string item)
    {
        if (!_flows.TryGetValue(item, out var f)) return "alive";
        float window = Math.Max(1f, Plugin.InertMinutes.Value) * 60f;
        if (_awakeSeconds - f.AwakeAtLastDecrease < window) return "alive";
        if (f.HasRealDecrease && _awakeSeconds - f.AwakeAtLastRealDecrease < window * AliveMemoryWindows) return "alive";
        if (_awakeSeconds - f.AwakeAtLastIncrease < window) return "growing";
        return "dead";
    }

    // v0.11.1 fix 2 — blocked(item at container C) = C.GetRemainingCapacity(item)==0 (first runtime
    // use of this primitive; own try/catch). Falls back to FillRatio>=0.999 when the item is null or
    // the read throws. `primitive` tells the caller which one decided ("cap" vs "fill") for the
    // blk=cap|fill log token.
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

    // HOG eviction sizing — DEMAND-GROUNDED. A hog is a SurplusV2 item (over-target, dead/growing),
    // so it's reduced toward keepUnits = max(CoProductFloorSlots x stack, its own RATCHETED
    // structural demand) — v0.14.0 uses RatchetTotal here too (never below what the item itself
    // needed at its highest recent demand). Everything above keepUnits is surplus to reduce. The
    // FULL target and the per-cycle step (capped at MaxSlotsPerCycle so it self-paces,
    // response-gated at arming) are BOTH logged. The demanded, blocked VICTIM this hog crowds is
    // named as the justification, along with the hog's own flow `hogVerdict` (dead/growing) and an
    // optional `distress` (storage-full, keyed off the victim's PosKey) severity/tag rider.
    // `blkToken` ("cap"|"fill") is IsBlocked's primitive. Dry-run — no DropItem.
    private static (string Line, int Severity, string Key) BuildEvictionProposal(
        string containerKey, WarehouseWatch.ContainerSnapshot snap,
        BudgetGroup hogGroup, ItemSignal hogSig, BudgetGroup victimGroup, ItemSignal victimSig, string blkToken,
        string hogVerdict, bool distress)
    {
        int rowUnmet = Math.Max(0, victimGroup.QuotaSum - victimGroup.Stored);
        int structTotal = victimSig.StructTotal;

        int stackX = 1;
        try { if (hogGroup.Info != null) stackX = Math.Max(1, snap.Container.GetStackSize(hogGroup.Info)); } catch { }

        int floorUnits = Math.Max(0, Plugin.CoProductFloorSlots.Value) * stackX;
        int keepUnits = Math.Max(floorUnits, hogSig.RatchetTotal);      // v0.14.0 — ratcheted, never below the hog's own demand
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
        string distressStr = distress ? " distress=storage-full" : "";
        int severity = structTotal + (distress ? 100000 : 0);

        string line =
            $"[SupplyChain] [budget] HOG '{hogGroup.Item}' vs victim '{victimGroup.Item}' @ '{victimGroup.Warehouse}' [{containerKeyLast6}]: " +
            $"surplus X stored={stored} ({hogDemStr}) verdict={hogVerdict} crowds demanded V (structural={structStr} rowUnmet={rowUnmet} " +
            $"fill={snap.FillRatio:F2} blk={blkToken} stackX={stackX}) " +
            $"-> reduce X {stored}->{targetUnits} (surplus {surplusUnits}={fullSlots} slot(s)); this cycle {cycleUnits} ({cycleSlots}/{cap} slot(s)) gate=ready (dry-run){distressStr}";

        string key = $"hog|{containerKey}|{hogGroup.Item}|{victimGroup.Item}";
        return (line, severity, key);
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
