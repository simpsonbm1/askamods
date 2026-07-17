using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using SSSGame;
using SSSGame.AI;
using SandSailorStudio.Inventory;
using UnityEngine;

namespace SupplyChainMod;

// DemandGraph (v0.11.1, DEMAND_MODEL_PLAN.md step 3 part A) — READ-ONLY structural-demand
// extractor, widened from v0.10.0's flat CraftingStation-only join into a two-pass, transitively-
// propagating BOM graph:
//   Pass 1 — walk EVERY structure (not just ones with a CraftingStation component). Tasks come
//     from the structure's Workstation (base-typed singular GetComponent returns derived instances
//     — a CraftingStation IS a Workstation, so one FindComponent<Workstation> covers both). Recipe
//     TABLES come from CraftingStation._craftingTables when a CraftingStation exists (the proven
//     v0.10.0 path); otherwise a manual hierarchy walk collects CraftInteraction components
//     directly (same walker style as ContainerProbe) — this is what brings Bloomstation-style
//     stations and any station whose tables sit off the CraftingStation-owned list into reach.
//     Every station's LOCAL produced-name -> (inputs, outputPerCraft) lookup is also merged into a
//     GLOBAL UNION map (first station to define a produced name wins) BEFORE any task matching
//     happens, so a task at one station can resolve via a blueprint that only physically exists at
//     a DIFFERENT station (the Rope @ Workshop Pit 3 / Workshop House 4 case from the step-2 plan).
//   Pass 2 — walk the stations collected in pass 1 again (held only within this one Dump/EnsureFresh
//     call, never across polls), SKIPPING task-reading entirely for any structure whose native
//     class is in Plugin.WarehouseClassList (v0.11.1 fix 1 — see below), and match each remaining
//     configured task's output name in order: station-local blueprints -> the UNION -> a native
//     cooking-recipe read -> the transform map -> unmatched, accumulating DIRECT per-item demand.
//   Propagation — breadth-first over the UNION (and, as of v0.11.1, the transform map too): a
//     directly-demanded item that itself has a UNION recipe or a transform-map entry propagates its
//     qty to its own inputs as DERIVED demand, depth-capped at 8 and cycle-guarded (an item
//     propagates FROM itself at most once per rebuild). This is the fix for the step-2 finding that
//     flat name-join demand terminates at intermediates (Hot Iron Bloom) and never reaches their
//     own inputs (Iron Bloom, ore, coal) — also the documented skeleton of the long-term
//     chain-capacity rollup.
//
// v0.11.1 fixes (DEMAND_MODEL_PLAN.md user review, six items; this file covers four of them —
// fixes 2 and 6 live in BudgetPlane.cs):
//   1. Production-task filter — v0.11.0's Workstation-generic task reading (every structure, not
//      just crafting stations) swept in warehouse/hut STORAGE COLLECT tasks as if they were
//      production orders (929 tasks; Fibers structural inflated to 21204 vs a true ~810). Fixed two
//      ways: a structure whose native class is in Plugin.WarehouseClassList is skipped for task
//      reading ENTIRELY in Pass 2 (GetWorkstationTaskDatas is never even called on it), and
//      per-task, ResourceStorageTaskData (storage collect) / SSSGame.AI.FiltersTaskData (hidden
//      fuel/filler tiers, e.g. a Campfire's hidden filter task) rows are TryCast-detected and
//      skipped+tallied even on a station that DOES have real production tasks.
//   3. outputPerCraft PRIMARY source flips to BlueprintInfo.quantity — the v0.11.0 cross-check
//      (bpQuantityCrossCheck) proved quantity == the name-prefix batch parse on all 418 blueprints
//      seen (0 mismatches), so it's now trusted as authoritative; the name-prefix parse becomes the
//      fallback for a throwing/absent/<1 read. See ResolveOutputPerCraft.
//   4. Transform map (Plugin.TransformMap, format "Output<-Input[,Input2];Output2<-...") —
//      non-blueprint conversions (the curing rack: Cured Leather Hide <- Leather Hide by default)
//      are the last resolution step before a task output is logged unmatched, and also feed
//      transitive propagation (a demanded transform OUTPUT propagates to its inputs, same cycle
//      guard as the UNION).
//   5. Cooking demand — cooking/barbecue production tasks carry the meal as an ItemInfo whose
//      NATIVE class descends from SSSGame.CookingRecipeInfo (managed casts lie for a base-typed
//      reference, project-wide gotcha — task.itemInfoQuantity.itemInfo is declared ItemInfo
//      regardless of the native object's real class, so this needs the usual
//      il2cpp_object_get_class + il2cpp_class_get_parent ancestor walk). See TryMatchCookingRecipe
//      for the Cecil signature surprise found while wiring this up: cookingRequirements lives on
//      SSSGame.CrockpotRecipeInfo (a NESTED Il2CppReferenceArray<CrockpotRecipeInfo.
//      CookingRecipeRequirement>), NOT on CookingRecipeInfo itself, and not a top-level
//      CookingRecipeRequirement type as DEMAND_MODEL_PLAN.md's research phase had assumed — a
//      plain (non-Crockpot) CookingRecipeInfo instance has no such property at all.
//
// v0.15.0 (case layer) — the union+transform BOM edges (produced item -> its own input names) are
// now RETAINED across rebuilds as `_retainedInputs`, not just used transiently inside one
// RebuildCore call, so AreChainRelated (new) can walk the same graph PropagateTransitive walks —
// breadth-first, cycle-guarded, depth <=8 — to tell CaseTracker whether two OPEN cases' items are
// BOM-related (a stuck intermediate and its own stuck input, tagged with a shared chain=#k).
//
// v0.17.2 (DemandWatchlist rider) — a small configured item list (Plugin.DemandWatchlist,
// [Diagnostics]) gets a `[demand-watch]` reasoning line on EVERY finished build/refresh: structural
// demand (TryGetStructuralDemand), wantedBy consumers (TryGetWantedByLabels) or "none", and whether
// any recipe/transform PRODUCES the item at all (new GetProducedByKind, backed by a new
// producedMatchKind accumulator threaded through DumpStationTasks — the SAME per-task match cascade
// already computes matchKind, just retained now keyed by produced-item name instead of discarded).
// Diagnostics only; settles which processing-station items (ore/bloom/smelting chains) the graph
// actually covers, without needing an F9 press or restructuring anything.
//
// v0.16.0 (food-demand riders) — three closes on the v0.15.1 probe's food-pipeline gaps:
// TryMatchCookingRecipe gains a BlueprintInfo parts/cost fallback (matchKind="parts") for plain
// CookingRecipeInfo items (barbecue, curing rack) that have no cookingRequirements at all; the same
// method's Table/Unfiltered_Table requirement reads now feed a new `_anyOfProtected` set (eviction
// protection only, no structural demand — see IsAnyOfProtected / BudgetPlane.cs); and
// DumpStationTasks short-circuits FarmingStation/ForestryStation tasks into SEED demand (the task
// consumes its item rather than producing it) before the normal match cascade runs.
internal static class DemandGraph
{
    private static Dictionary<string, int> _directQty = new();
    private static Dictionary<string, int> _derivedQty = new();
    private static Dictionary<string, int> _wantedByCount = new();
    // v0.12.0 — retained DIRECT-consumer labels ("Output xQty", top few by qty) per input item, so
    // the BudgetPlane BLOCKAGE diagnostic can NAME what downstream relies on a stuck material
    // (the user's "Hardwood Long Sticks stuck while Shafts/Beams demand it" case). Plain strings
    // only (project-wide static-state rule); derived-only demand has no direct wantedBy label here.
    private static Dictionary<string, List<string>> _wantedByLabels = new();
    // v0.15.0 (case layer) — retained BOM edges: produced-item name -> its own recipe/transform
    // input names (union + transform map, flattened to just names, dedup'd). This is the SAME graph
    // PropagateTransitive already walks breadth-first each rebuild, just kept around afterward (it
    // was previously local-only and discarded) so AreChainRelated can walk it between rebuilds too.
    // Plain strings only (project-wide static-state rule).
    private static Dictionary<string, List<string>> _retainedInputs = new();
    // v0.16.0 — items accepted by an any-of cooking Table/Unfiltered_Table requirement: eviction
    // PROTECTION only (never selectable as a HOG), no structural demand (see TryMatchCookingRecipe
    // header / BudgetPlane.cs's hog-gate + SURPLUS-tail any-of handling). Plain strings only
    // (project-wide static-state rule).
    private static HashSet<string> _anyOfProtected = new();
    private static float _lastRebuildTime = -1f;
    // v0.14.0 — matched production-task count from the last completed EnsureFresh rebuild; -1 =
    // none yet this world. Feeds the zero-match retry cadence below (world-streaming race at load).
    private static int _lastMatchedCount = -1;
    // v0.17.2 — produced-item name -> how its own task's recipe was resolved this rebuild
    // ("local"/"union"/"cooking"/"parts"/"transform"/"none"). Feeds the DemandWatchlist rider's
    // producedBy= field (GetProducedByKind). Plain strings only (project-wide static-state rule).
    private static Dictionary<string, string> _producedMatchKind = new();

    internal static void NoteWorldLeft()
    {
        try
        {
            _directQty = new Dictionary<string, int>();
            _derivedQty = new Dictionary<string, int>();
            _wantedByCount = new Dictionary<string, int>();
            _wantedByLabels = new Dictionary<string, List<string>>();
            _retainedInputs = new Dictionary<string, List<string>>();
            _anyOfProtected = new HashSet<string>();
            _lastRebuildTime = -1f;
            _lastMatchedCount = -1;
            _producedMatchKind = new Dictionary<string, string>();
        }
        catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] [demand] NoteWorldLeft error: {ex}"); }
    }

    // v0.17.2 — DemandWatchlist rider accessor: how (if at all) itemName's OWN production task was
    // resolved on the last rebuild ("local"/"union"/"cooking"/"parts"/"transform"), or "none" if it
    // was never seen as a task output, or was seen but no recipe/transform matched it.
    internal static string GetProducedByKind(string itemName)
    {
        try
        {
            if (!string.IsNullOrEmpty(itemName) && _producedMatchKind.TryGetValue(itemName, out var kind)) return kind;
        }
        catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] [demand] GetProducedByKind error: {ex}"); }
        return "none";
    }

    // structTotal = directQty + derivedQty. Returns true when the item has ANY structural demand
    // (direct or derived) recorded for it; direct/derived/wantedByCount are always populated
    // (0 when absent) so callers can safely sum them without a second lookup.
    internal static bool TryGetStructuralDemand(string itemName, out int directQty, out int derivedQty, out int wantedByCount)
    {
        directQty = 0;
        derivedQty = 0;
        wantedByCount = 0;
        try
        {
            if (itemName == null) return false;
            bool hasDirect = _directQty.TryGetValue(itemName, out directQty);
            bool hasDerived = _derivedQty.TryGetValue(itemName, out derivedQty);
            _wantedByCount.TryGetValue(itemName, out wantedByCount);
            return hasDirect || hasDerived;
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"[SupplyChain] [demand] TryGetStructuralDemand error: {ex}");
            directQty = 0;
            derivedQty = 0;
            wantedByCount = 0;
            return false;
        }
    }

    // v0.12.0 — DIRECT-consumer labels ("Output xQty", top few) for a stuck input item, used by
    // BudgetPlane's BLOCKAGE diagnostic to name the downstream processes relying on it. Returns
    // false (labels empty) when the item has no recorded direct consumers (e.g. derived-only demand).
    internal static bool TryGetWantedByLabels(string itemName, out List<string> labels)
    {
        labels = new List<string>();
        try
        {
            if (itemName != null && _wantedByLabels.TryGetValue(itemName, out var l) && l.Count > 0)
            {
                labels = l;
                return true;
            }
        }
        catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] [demand] TryGetWantedByLabels error: {ex}"); }
        return false;
    }

    // v0.16.0 — true when `itemName` is accepted by a cooking Table/Unfiltered_Table any-of
    // requirement (see TryMatchCookingRecipe). Eviction PROTECTION only — callers must never
    // select a protected item as a HOG; it carries no structural demand of its own.
    internal static bool IsAnyOfProtected(string itemName)
    {
        try
        {
            if (string.IsNullOrEmpty(itemName)) return false;
            return _anyOfProtected.Contains(itemName);
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"[SupplyChain] [demand] IsAnyOfProtected error: {ex}");
            return false;
        }
    }

    // v0.15.0 (case layer) — true if EITHER item reaches the other walking blueprint inputs (the
    // same retained BOM graph PropagateTransitive walks each rebuild — breadth-first, cycle-guarded
    // via `visited`, depth-capped at 8). Used by CaseTracker to tag a BOM-related pair of OPEN
    // cases with a shared chain id — e.g. a stuck intermediate and its own stuck input.
    internal static bool AreChainRelated(string itemA, string itemB)
    {
        try
        {
            if (string.IsNullOrEmpty(itemA) || string.IsNullOrEmpty(itemB) || itemA == itemB) return false;
            return ReachesTransitively(itemA, itemB) || ReachesTransitively(itemB, itemA);
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"[SupplyChain] [demand] AreChainRelated error: {ex}");
            return false;
        }
    }

    // Breadth-first walk from `from` down through its OWN recipe inputs (never its consumers),
    // looking for `target`. Depth-capped at 8 to match PropagateTransitive's cap; `visited` is both
    // the cycle guard and the "already checked" set.
    private static bool ReachesTransitively(string from, string target)
    {
        var visited = new HashSet<string> { from };
        var queue = new Queue<(string item, int depth)>();
        queue.Enqueue((from, 0));

        while (queue.Count > 0)
        {
            var (item, depth) = queue.Dequeue();
            if (depth >= 8) continue;
            if (!_retainedInputs.TryGetValue(item, out var inputs)) continue;

            foreach (var input in inputs)
            {
                if (input == target) return true;
                if (visited.Add(input)) queue.Enqueue((input, depth + 1));
            }
        }
        return false;
    }

    // v0.11.0 — called by BudgetPlane at the start of every Evaluate. Rebuilds at most once per
    // Plugin.DemandRefreshMinutes (Time.time-based, pause-robust) or immediately if never built this
    // world; otherwise a no-op. Logs exactly one summary line on an actual rebuild — the verbose
    // per-station/rollup logging stays exclusive to Dump() (F9/hotkey/auto).
    // v0.14.0 — zero-match retry: if the LAST completed rebuild matched 0 production tasks (a
    // world-streaming race at load — structures not all instantiated yet), the next rebuild becomes
    // eligible after a fast fixed 60s instead of waiting a full DemandRefreshMinutes.
    internal static void EnsureFresh()
    {
        try
        {
            if (!Plugin.EnableDemandGraph.Value) return;

            float now = Time.time;
            float refreshSeconds = _lastMatchedCount == 0
                ? 60f
                : Math.Max(0f, Plugin.DemandRefreshMinutes.Value) * 60f;
            bool due = _lastRebuildTime < 0f || (refreshSeconds > 0f && (now - _lastRebuildTime) >= refreshSeconds);
            if (!due) return;

            var sw = Stopwatch.StartNew();
            var stats = RebuildCore(verbose: false, "auto-refresh");
            sw.Stop();
            Plugin.Logger.LogInfo(
                $"[SupplyChain] [demand] rebuilt: items={stats.ItemCount} (direct={stats.DirectCount} derived={stats.DerivedCount}) " +
                $"in {sw.Elapsed.TotalMilliseconds:F0} ms");

            // Logged only on the transition INTO a zero streak, not on every 60s retry while it persists.
            if (stats.MatchedCount == 0 && _lastMatchedCount != 0)
                Plugin.Logger.LogInfo("[SupplyChain] [demand] rebuild matched 0 tasks — world may still be streaming; retrying in 60 s");
            _lastMatchedCount = stats.MatchedCount;
        }
        catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] [demand] EnsureFresh error: {ex}"); }
    }

    internal static void Dump(string trigger)
    {
        try
        {
            Plugin.Logger.LogInfo($"[SupplyChain] [demand] === DemandGraph start (trigger={trigger}) ===");
            var sw = Stopwatch.StartNew();
            RebuildCore(verbose: true, trigger);
            Plugin.Logger.LogInfo($"[SupplyChain] [demand] === DemandGraph end (trigger={trigger}) ===");
            sw.Stop();
            Plugin.Logger.LogInfo($"[SupplyChain] [Perf] DemandGraph dump: {sw.Elapsed.TotalMilliseconds:F1} ms");
        }
        catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] [demand] Dump error: {ex}"); }
    }

    private readonly struct RebuildStats
    {
        public readonly int ItemCount;
        public readonly int DirectCount;
        public readonly int DerivedCount;
        // v0.14.0 — total matched production tasks (local+union+cooking+parts+transform+farm, as of
        // v0.16.0) this rebuild; feeds EnsureFresh's zero-match retry cadence.
        public readonly int MatchedCount;
        public RebuildStats(int itemCount, int directCount, int derivedCount, int matchedCount)
        {
            ItemCount = itemCount;
            DirectCount = directCount;
            DerivedCount = derivedCount;
            MatchedCount = matchedCount;
        }
    }

    // One recipe: produced-name is the dictionary key this lives under. Inputs are per-CRAFT
    // (manifest) quantities; OutputPerCraft is the batch-correction multiplier (see file header).
    private sealed class BlueprintEntry
    {
        public List<(string input, int qty)> Inputs = new();
        public int OutputPerCraft = 1;
        public string OutputPerCraftSource = "default";
        public string SourceStation = "?";
        public int BpQuantityRaw = -1; // Cecil `BlueprintInfo.quantity` — v0.11.1 PRIMARY source, see header
    }

    private sealed class WantedByEntry
    {
        public string Station = "?";
        public string Output = "?";
        public int Qty;
    }

    // Held only within one RebuildCore call (never across polls) — mirrors ContainerProbe's
    // discipline for per-poll interop wrapper lifetime.
    private sealed class StationWork
    {
        public string StructureName = "?";
        public string NativeClass = "?";
        public Workstation Ws = null!;
        public Dictionary<string, BlueprintEntry> LocalBlueprints = new();
        public int TableCount;
        public int LocalBlueprintCount;
        public int DupNames;
        public bool ViaWalk;
        // v0.11.1 fix 1 — this structure's native class is in Plugin.WarehouseClassList; Pass 2
        // skips task-reading for it entirely (storage-collect tasks are never production orders).
        public bool IsWarehouseClass;
    }

    private static RebuildStats RebuildCore(bool verbose, string trigger)
    {
        var union = new Dictionary<string, BlueprintEntry>();
        var stationWorks = new List<StationWork>();
        var noTablesClasses = new Dictionary<string, int>();
        int probed = 0, stationsWithTables = 0, stationsViaWalk = 0, noWorkstation = 0;
        int totalLocalBlueprints = 0, totalUnionAdded = 0, totalUnionDup = 0;
        int bpQuantityMismatches = 0, bpQuantityAvailable = 0;

        var warehouseClasses = ParseWarehouseClassSet(Plugin.WarehouseClassList.Value);
        var transformMap = ParseTransformMap(Plugin.TransformMap.Value);

        // ── Pass 1: walk every structure, build LOCAL blueprint lookups, merge into the UNION ────
        try
        {
            var structures = StationWalker.ResolveStructures();
            probed = structures.Count;

            foreach (var s in structures)
            {
                try
                {
                    Transform root;
                    try { root = s.transform; } catch { continue; }

                    Workstation? ws;
                    try { ws = StationWalker.FindComponent<Workstation>(root); } catch { ws = null; }
                    if (ws == null) { noWorkstation++; continue; }

                    CraftingStation? cs;
                    try { cs = StationWalker.FindComponent<CraftingStation>(root); } catch { cs = null; }

                    string structureName = Common.SafeName(s);
                    string nativeClass = Common.NativeClassName(ws);

                    var tables = new List<CraftInteraction>();
                    bool viaWalk = false;
                    if (cs != null)
                    {
                        Il2CppSystem.Collections.Generic.List<CraftInteraction>? csTables = null;
                        try { csTables = cs._craftingTables; } catch { }
                        int tc = csTables != null ? csTables.Count : 0;
                        for (int t = 0; t < tc; t++)
                        {
                            CraftInteraction? table = null;
                            try { table = csTables![t]; } catch { continue; }
                            if (table != null) tables.Add(table);
                        }
                    }
                    else
                    {
                        try { WalkForCraftInteractions(root, tables, 0); }
                        catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] [demand] table walk '{structureName}' error: {ex}"); }
                        viaWalk = tables.Count > 0;
                    }

                    if (tables.Count == 0)
                    {
                        noTablesClasses.TryGetValue(nativeClass, out int cnt);
                        noTablesClasses[nativeClass] = cnt + 1;
                    }
                    else
                    {
                        stationsWithTables++;
                        if (viaWalk) stationsViaWalk++;
                    }

                    var localLookup = BuildLocalBlueprints(tables, structureName, out int bpCount, out int dupNames,
                        ref bpQuantityAvailable, ref bpQuantityMismatches);
                    totalLocalBlueprints += bpCount;

                    foreach (var kv in localLookup)
                    {
                        if (union.ContainsKey(kv.Key)) { totalUnionDup++; continue; }
                        union[kv.Key] = kv.Value;
                        totalUnionAdded++;
                    }

                    stationWorks.Add(new StationWork
                    {
                        StructureName = structureName,
                        NativeClass = nativeClass,
                        Ws = ws,
                        LocalBlueprints = localLookup,
                        TableCount = tables.Count,
                        LocalBlueprintCount = bpCount,
                        DupNames = dupNames,
                        ViaWalk = viaWalk,
                        IsWarehouseClass = warehouseClasses.Contains(nativeClass),
                    });
                }
                catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] [demand] structure pass1 error: {ex}"); }
            }
        }
        catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] [demand] structure walk error: {ex}"); }

        if (verbose)
        {
            try { LogNoTables(noTablesClasses); }
            catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] [demand] LogNoTables error: {ex}"); }
        }

        // ── Pass 2: match each station's configured tasks: local -> union -> cooking -> transform ─
        var wantedBy = new Dictionary<string, List<WantedByEntry>>();
        // v0.16.0 any-of protection — items accepted by a cooking Table/Unfiltered_Table
        // requirement, collected during this pass (see TryMatchCookingRecipe / IsAnyOfProtected).
        var anyOfProtected = new HashSet<string>();
        var directQty = new Dictionary<string, int>();
        var stillUnmatched = new List<string>();
        // v0.17.2 — produced-item name -> matchKind ("local"/"union"/"cooking"/"parts"/"transform"/
        // "none"), accumulated across every station's tasks this rebuild. Feeds GetProducedByKind
        // (DemandWatchlist rider) — see DumpStationTasks for where this is populated.
        var producedMatchKind = new Dictionary<string, string>();
        var loggedCookingRecipes = new HashSet<string>();
        // v0.16.0 — dedup set for the once-per-distinct-table any-of protection log line.
        var loggedTables = new HashSet<string>();
        int totalTasks = 0, totalMatchedLocal = 0, totalMatchedUnion = 0, totalMatchedCooking = 0;
        int totalMatchedParts = 0, totalMatchedTransform = 0, totalMatchedFarm = 0;
        int totalUnmatched = 0, totalStorageTasks = 0, totalFilterTasks = 0, stationsSkippedWarehouseClass = 0;

        foreach (var sw in stationWorks)
        {
            try
            {
                if (sw.IsWarehouseClass)
                {
                    stationsSkippedWarehouseClass++;
                    continue;
                }

                DumpStationTasks(sw, union, transformMap, wantedBy, directQty, stillUnmatched, loggedCookingRecipes,
                    loggedTables, anyOfProtected, producedMatchKind, verbose,
                    ref totalTasks, ref totalMatchedLocal, ref totalMatchedUnion, ref totalMatchedCooking,
                    ref totalMatchedParts, ref totalMatchedTransform, ref totalMatchedFarm,
                    ref totalUnmatched, ref totalStorageTasks, ref totalFilterTasks, ref bpQuantityAvailable, ref bpQuantityMismatches);
            }
            catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] [demand] station '{sw.StructureName}' error: {ex}"); }
        }

        // ── Transitive propagation (breadth-first over the UNION + transform map, depth-capped) ──
        var derivedQty = new Dictionary<string, int>();
        var derivedVia = new Dictionary<string, List<string>>();
        var wantedByCount = new Dictionary<string, int>();
        foreach (var kv in wantedBy) wantedByCount[kv.Key] = kv.Value.Count;

        try { PropagateTransitive(union, transformMap, directQty, derivedQty, derivedVia, wantedByCount); }
        catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] [demand] propagation error: {ex}"); }

        // ── Logging (verbose only beyond this point) ────────────────────────────────────────────
        var allItems = new HashSet<string>();
        foreach (var k in directQty.Keys) allItems.Add(k);
        foreach (var k in derivedQty.Keys) allItems.Add(k);

        if (verbose)
        {
            try { LogRollup(allItems, directQty, derivedQty, derivedVia, wantedBy); }
            catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] [demand] LogRollup error: {ex}"); }

            try
            {
                string unmatchedStr = stillUnmatched.Count > 0 ? string.Join(", ", stillUnmatched) : "(none)";
                Plugin.Logger.LogInfo($"[SupplyChain] [demand] still-unmatched task outputs after union pass: [{unmatchedStr}]");
            }
            catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] [demand] still-unmatched log error: {ex}"); }

            try
            {
                string bpXCheck = bpQuantityAvailable > 0
                    ? $"bpQuantityCrossCheck: mismatches={bpQuantityMismatches}/{bpQuantityAvailable} (quantity IS the primary outputPerCraft source as of v0.11.1 — see file header)"
                    : "bpQuantityCrossCheck: n/a";
                Plugin.Logger.LogInfo(
                    $"[SupplyChain] [demand] TOTAL: stationsWithTables={stationsWithTables}/{probed} (viaWalk={stationsViaWalk}) " +
                    $"noWorkstation={noWorkstation} warehouseClassSkipped={stationsSkippedWarehouseClass} " +
                    $"blueprints={totalLocalBlueprints} (unionAdded={totalUnionAdded} unionDup={totalUnionDup}) " +
                    $"tasks={totalTasks} storageTasksSkipped={totalStorageTasks} filterTasksSkipped={totalFilterTasks} " +
                    $"matched={totalMatchedLocal} matchedViaUnion={totalMatchedUnion} matchedViaCooking={totalMatchedCooking} " +
                    $"matchedViaParts={totalMatchedParts} matchedViaTransform={totalMatchedTransform} matchedViaFarm={totalMatchedFarm} " +
                    $"unmatched={totalUnmatched} distinctInputs={allItems.Count} anyOfProtected={anyOfProtected.Count} {bpXCheck}");
            }
            catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] [demand] TOTAL log error: {ex}"); }
        }

        // ── Replace retained state wholesale (plain strings/ints only) ─────────────────────────
        int directCount = 0, derivedCount = 0;
        try
        {
            var newDirect = new Dictionary<string, int>(directQty);
            var newDerived = new Dictionary<string, int>(derivedQty);
            var newCount = new Dictionary<string, int>(wantedByCount);
            foreach (var kv in newDirect) if (kv.Value > 0) directCount++;
            foreach (var kv in newDerived) if (kv.Value > 0) derivedCount++;

            // v0.12.0 — snapshot the top DIRECT consumers per input as plain "Output xQty" labels
            // (max 6, qty-desc) for the BLOCKAGE diagnostic. Built from the local `wantedBy` map.
            const int maxLabels = 6;
            var newLabels = new Dictionary<string, List<string>>();
            foreach (var kv in wantedBy)
            {
                var entries = kv.Value;
                entries.Sort((a, b) => b.Qty.CompareTo(a.Qty));
                var labels = new List<string>();
                int shown = Math.Min(entries.Count, maxLabels);
                for (int e = 0; e < shown; e++) labels.Add($"{entries[e].Output} x{entries[e].Qty}");
                if (entries.Count > maxLabels) labels.Add($"+{entries.Count - maxLabels} more");
                newLabels[kv.Key] = labels;
            }

            // v0.15.0 (case layer) — retain the BOM edges (union recipes + transform map,
            // flattened to just input names) so AreChainRelated can walk them between rebuilds.
            var newInputs = new Dictionary<string, List<string>>();
            foreach (var kv in union)
            {
                var names = new List<string>();
                foreach (var (input, _) in kv.Value.Inputs) if (!names.Contains(input)) names.Add(input);
                newInputs[kv.Key] = names;
            }
            foreach (var kv in transformMap)
            {
                if (!newInputs.TryGetValue(kv.Key, out var names)) { names = new List<string>(); newInputs[kv.Key] = names; }
                foreach (var (input, _) in kv.Value) if (!names.Contains(input)) names.Add(input);
            }

            // v0.16.0 any-of protection — snapshot the fresh set built during Pass 2's cooking-recipe
            // Table requirement reads (see TryMatchCookingRecipe).
            var newAnyOfProtected = new HashSet<string>(anyOfProtected);

            _directQty = newDirect;
            _derivedQty = newDerived;
            _wantedByCount = newCount;
            _wantedByLabels = newLabels;
            _retainedInputs = newInputs;
            _anyOfProtected = newAnyOfProtected;
            _producedMatchKind = new Dictionary<string, string>(producedMatchKind);
        }
        catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] [demand] ReplaceState error: {ex}"); }

        _lastRebuildTime = Time.time;
        int totalMatched = totalMatchedLocal + totalMatchedUnion + totalMatchedCooking + totalMatchedParts
            + totalMatchedTransform + totalMatchedFarm;

        // v0.17.2 — DemandWatchlist rider: logs reasoning for a small configured item list on EVERY
        // finished build/refresh (verbose or not — settling the processing-station demand question
        // doesn't need an F9 press). Uses the SAME public accessors external callers use (never reads
        // the locals above directly), per the delegation's explicit ask.
        try { LogWatchlist(); }
        catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] [demand-watch] LogWatchlist error: {ex}"); }

        return new RebuildStats(allItems.Count, directCount, derivedCount, totalMatched);
    }

    // v0.17.2 — DemandWatchlist rider: one line per configured item name, read via the SAME public
    // accessors TierCaseController/BudgetPlane use (TryGetStructuralDemand, TryGetWantedByLabels) plus
    // the new GetProducedByKind. Diagnostics only — never restructures the graph itself.
    private static void LogWatchlist()
    {
        string raw = Plugin.DemandWatchlist.Value ?? "";
        if (string.IsNullOrWhiteSpace(raw)) return;

        foreach (var part in raw.Split(','))
        {
            string item = part.Trim();
            if (item.Length == 0) continue;

            try
            {
                TryGetStructuralDemand(item, out int direct, out int derived, out _);
                string wantedByStr = TryGetWantedByLabels(item, out var labels)
                    ? $"wantedBy=[{string.Join(", ", labels)}]"
                    : "wantedBy=none";
                string producedBy = GetProducedByKind(item);

                Plugin.Logger.LogInfo(
                    $"[SupplyChain] [demand-watch] '{item}': structural={direct + derived}({direct}/{derived}) {wantedByStr} producedBy={producedBy}");
            }
            catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] [demand-watch] '{item}' error: {ex}"); }
        }
    }

    // v0.11.1 fix 1 — mirrors WarehouseWatch.ParseClassList (private there, so a small local copy
    // here); comma-separated, case-sensitive, exact native class names.
    private static HashSet<string> ParseWarehouseClassSet(string raw)
    {
        var set = new HashSet<string>();
        try
        {
            if (string.IsNullOrEmpty(raw)) return set;
            foreach (var part in raw.Split(','))
            {
                var t = part.Trim();
                if (t.Length > 0) set.Add(t);
            }
        }
        catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] [demand] ParseWarehouseClassSet error: {ex}"); }
        return set;
    }

    // v0.11.1 fix 4 — parses Plugin.TransformMap ("Output<-Input[,Input2];Output2<-...") once per
    // rebuild into Output -> [(Input, qty=1), ...]. Plain strings only (project-wide static-state rule).
    private static Dictionary<string, List<(string input, int qty)>> ParseTransformMap(string raw)
    {
        var map = new Dictionary<string, List<(string input, int qty)>>();
        try
        {
            if (string.IsNullOrEmpty(raw)) return map;
            var entries = raw.Split(';');
            for (int i = 0; i < entries.Length; i++)
            {
                string entry = entries[i].Trim();
                if (entry.Length == 0) continue;

                int arrowIdx = entry.IndexOf("<-", StringComparison.Ordinal);
                if (arrowIdx < 0) continue;

                string output = entry.Substring(0, arrowIdx).Trim();
                string inputsRaw = entry.Substring(arrowIdx + 2).Trim();
                if (output.Length == 0 || inputsRaw.Length == 0) continue;

                var inputs = new List<(string input, int qty)>();
                foreach (var part in inputsRaw.Split(','))
                {
                    string inputName = part.Trim();
                    if (inputName.Length > 0) inputs.Add((inputName, 1));
                }
                if (inputs.Count > 0) map[output] = inputs;
            }
        }
        catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] [demand] ParseTransformMap error: {ex}"); }
        return map;
    }

    // v0.11.1 fix 3 — reads a BlueprintInfo-declared `quantity` off any object whose native class
    // descends from BlueprintInfo. `blueprintLike` may already be statically a BlueprintInfo
    // (CraftBlueprintInfo from the local-blueprint table build — no interop lie, that dictionary's
    // value type already IS CraftBlueprintInfo) or a base-typed ItemInfo whose native class is
    // actually a BlueprintInfo descendant (the cooking-match case — task.itemInfoQuantity.itemInfo
    // is declared ItemInfo regardless of the real native class, so this needs the project's usual
    // Il2CppObjectBase boxing + rewrap pattern to reach a derived-only property).
    private static bool TryReadQuantity(object? blueprintLike, out int qty)
    {
        qty = -1;
        try
        {
            if (blueprintLike is BlueprintInfo direct) { qty = direct.quantity; return true; }
            if (blueprintLike is Il2CppObjectBase b) { qty = new BlueprintInfo(b.Pointer).quantity; return true; }
        }
        catch { }
        return false;
    }

    // v0.11.1 fix 3 — outputPerCraft PRIMARY source is BlueprintInfo.quantity (the v0.11.0
    // cross-check proved quantity == the name-prefix batch parse on all 418 blueprints seen,
    // 0 mismatches). Falls back to the name-prefix parse when quantity throws/is absent/<1. Also
    // updates the running bpQuantityCrossCheck tally (both this and the name-prefix value, whenever
    // quantity is readable at all) so the F9 TOTAL keeps showing the pair agrees.
    private static int ResolveOutputPerCraft(object? blueprintLike, string producedName, out string source,
        ref int bpQuantityAvailable, ref int bpQuantityMismatches, out int bpQtyRaw)
    {
        int nameParseValue = ParseOutputPerCraft(producedName, out string nameParseSource);

        bpQtyRaw = -1;
        TryReadQuantity(blueprintLike, out bpQtyRaw);

        if (bpQtyRaw >= 0)
        {
            bpQuantityAvailable++;
            if (bpQtyRaw != nameParseValue) bpQuantityMismatches++;
        }

        if (bpQtyRaw >= 1)
        {
            source = "bp-quantity";
            return bpQtyRaw;
        }
        source = nameParseSource;
        return nameParseValue;
    }

    // Builds this station's LOCAL blueprint lookup (produced-item name -> recipe inputs +
    // outputPerCraft; first blueprint wins on a duplicate produced name) from a resolved table
    // list (either CraftingStation._craftingTables or a CraftInteraction hierarchy walk).
    private static Dictionary<string, BlueprintEntry> BuildLocalBlueprints(
        List<CraftInteraction> tables, string structureName, out int blueprintCount, out int dupNames,
        ref int bpQuantityAvailable, ref int bpQuantityMismatches)
    {
        var lookup = new Dictionary<string, BlueprintEntry>();
        var seenNames = new HashSet<string>();
        blueprintCount = 0;
        dupNames = 0;

        for (int t = 0; t < tables.Count; t++)
        {
            CraftInteraction? table = tables[t];
            if (table == null) continue;

            var localBlueprints = table._localBlueprints;
            if (localBlueprints == null) continue;

            foreach (var kv in localBlueprints)
            {
                blueprintCount++;
                CraftBlueprintInfo? bpInfo = kv.Value;
                if (bpInfo == null) continue;

                string itemName;
                try { itemName = Common.SafeItemName(bpInfo.GetItemInfo()); } catch { continue; }
                if (itemName == "?") continue;

                if (!seenNames.Add(itemName)) { dupNames++; continue; }

                ItemManifest? manifest = null;
                try { manifest = ItemManifest.CreateFromBlueprint(bpInfo); } catch { continue; }
                if (manifest == null) continue;

                var items = manifest.GetItems();
                int mn = items != null ? items.Count : 0;
                var inputs = new List<(string input, int qty)>();
                for (int i = 0; i < mn; i++)
                {
                    try
                    {
                        var iq = items![i];
                        inputs.Add((Common.SafeItemName(iq.itemInfo), iq.quantity));
                    }
                    catch { }
                }

                int outputPerCraft = ResolveOutputPerCraft(bpInfo, itemName, out string opcSource,
                    ref bpQuantityAvailable, ref bpQuantityMismatches, out int bpQtyRaw);

                lookup[itemName] = new BlueprintEntry
                {
                    Inputs = inputs,
                    OutputPerCraft = outputPerCraft,
                    OutputPerCraftSource = opcSource,
                    SourceStation = structureName,
                    BpQuantityRaw = bpQtyRaw,
                };
            }
        }

        return lookup;
    }

    // "10x Iron Tipped Arrow" -> 10 (leading digit run immediately followed by 'x '); default 1.
    // v0.11.1: this is now the FALLBACK source (see ResolveOutputPerCraft) — BlueprintInfo.quantity
    // is primary as of the v0.11.0 cross-check finding (0 mismatches across 418 blueprints).
    private static int ParseOutputPerCraft(string producedName, out string source)
    {
        source = "default";
        try
        {
            if (string.IsNullOrEmpty(producedName)) return 1;
            int i = 0;
            while (i < producedName.Length && char.IsDigit(producedName[i])) i++;
            if (i > 0 && i < producedName.Length && producedName[i] == 'x'
                && i + 1 < producedName.Length && producedName[i + 1] == ' ')
            {
                if (int.TryParse(producedName.Substring(0, i), out int n) && n > 0)
                {
                    source = "name-prefix";
                    return n;
                }
            }
        }
        catch { }
        return 1;
    }

    // v0.11.1 fix 5 — cooking/barbecue production tasks carry the meal as the task's ItemInfo,
    // whose NATIVE class descends from SSSGame.CookingRecipeInfo (ancestor-walked — managed casts
    // lie for this base-typed reference, project-wide gotcha). Cecil surprise (2026-07-15):
    // cookingRequirements is declared on CrockpotRecipeInfo (a nested
    // Il2CppReferenceArray<CrockpotRecipeInfo.CookingRecipeRequirement>), NOT on CookingRecipeInfo
    // itself, and not a top-level SSSGame.CookingRecipeRequirement type as DEMAND_MODEL_PLAN.md's
    // research phase had assumed. A plain (non-Crockpot) CookingRecipeInfo instance has no such
    // property — only an item whose OWN (most-derived) native class is exactly "CrockpotRecipeInfo"
    // is rewrapped; anything else in the cooking family logs one UNAVAILABLE finding (F9 only,
    // deduped per output name) and falls through to the transform map / unmatched, same as a real
    // read failure. On success, logs ONE line per DISTINCT cooking recipe (F9 only): every
    // requirement's raw `type` enum value is collected verbatim — category-acceptance data, not yet
    // interpreted.
    //
    // v0.16.0 additions: (a) a plain CookingRecipeInfo (barbecue "Cooked X", curing-rack items) has
    // no cookingRequirements at all, but DOES descend from SandSailorStudio.Inventory.BlueprintInfo
    // (same as any other craftable) — its `parts`/`cost` manifest is now tried as a fallback before
    // the item falls through to the transform map / unmatched (Cecil-confirmed 2026-07-16:
    // BlueprintInfo.parts : Il2CppReferenceArray<ItemInfoQuantity>, .cost : ItemInfoQuantity,
    // ItemInfoQuantity.{itemInfo,quantity}). The new `viaBlueprintParts` out param signals this
    // path to the caller for matchKind="parts". (b) Table/Unfiltered_Table requirements are an
    // ANY-OF acceptance list (any item in `req.tableConfig.GetItemsList()` satisfies the slot,
    // Cecil-confirmed) — user-approved semantics: those items get eviction PROTECTION only via the
    // `anyOfProtected` collector, no hard structural demand (the existing NAMED-requirement
    // `inputs.Add` below is unchanged).
    private static bool TryMatchCookingRecipe(ItemInfo item, string outputName, bool verbose, HashSet<string> loggedRecipes,
        HashSet<string> loggedTables, HashSet<string> anyOfProtected,
        ref int bpQuantityAvailable, ref int bpQuantityMismatches,
        out List<(string input, int qty)> inputs, out int outputPerCraft, out bool viaBlueprintParts)
    {
        inputs = new List<(string input, int qty)>();
        outputPerCraft = 1;
        viaBlueprintParts = false;
        try
        {
            // ItemInfo's compile-time chain doesn't reach Il2CppObjectBase (same unity-libs-stub
            // gotcha as UnityEngine.Object types) — box through `object` first, same pattern as
            // Common.NativeClassName.
            if ((object)item is not Il2CppObjectBase baseObj) return false;

            IntPtr walk;
            try { walk = IL2CPP.il2cpp_object_get_class(baseObj.Pointer); } catch { return false; }

            string ownClass = "?";
            bool isCookingFamily = false;
            bool first = true;
            int depth = 0;
            while (walk != IntPtr.Zero && depth < 12)
            {
                string? name = null;
                try { name = Marshal.PtrToStringAnsi(IL2CPP.il2cpp_class_get_name(walk)); } catch { }
                if (name == null) break;
                if (first) { ownClass = name; first = false; }
                if (name == "CookingRecipeInfo") { isCookingFamily = true; break; }
                try { walk = IL2CPP.il2cpp_class_get_parent(walk); } catch { break; }
                depth++;
            }
            if (!isCookingFamily) return false;

            outputPerCraft = ResolveOutputPerCraft(item, outputName, out _, ref bpQuantityAvailable, ref bpQuantityMismatches, out _);

            if (ownClass != "CrockpotRecipeInfo")
            {
                // v0.16.0 — not a Crockpot recipe: try the BlueprintInfo parts/cost manifest before
                // giving up (this is what brings the barbecue and curing-rack items into demand;
                // the transform-map fallback downstream still catches whatever this misses).
                try
                {
                    var bp = new SandSailorStudio.Inventory.BlueprintInfo(baseObj.Pointer);
                    var bpInputs = new List<(string input, int qty)>();

                    Il2CppReferenceArray<ItemInfoQuantity>? parts = null;
                    try { parts = bp.parts; } catch { }
                    if (parts != null)
                    {
                        int pn = parts.Length;
                        for (int p = 0; p < pn; p++)
                        {
                            try
                            {
                                var part = parts[p];
                                if (part == null) continue;
                                string partName = Common.SafeItemName(part.itemInfo);
                                int partQty = part.quantity;
                                if (!string.IsNullOrEmpty(partName) && partName != "?" && partQty > 0)
                                    bpInputs.Add((partName, partQty));
                            }
                            catch { }
                        }
                    }

                    try
                    {
                        var cost = bp.cost;
                        if (cost != null)
                        {
                            string costName = Common.SafeItemName(cost.itemInfo);
                            int costQty = cost.quantity;
                            if (!string.IsNullOrEmpty(costName) && costName != "?" && costQty > 0)
                                bpInputs.Add((costName, costQty));
                        }
                    }
                    catch { }

                    if (bpInputs.Count > 0)
                    {
                        inputs = bpInputs;
                        viaBlueprintParts = true;
                        if (verbose && loggedRecipes.Add(outputName))
                        {
                            var partsSb = new System.Text.StringBuilder();
                            foreach (var (inName, inQty) in bpInputs)
                            {
                                if (partsSb.Length > 0) partsSb.Append(", ");
                                partsSb.Append($"req '{inName}' x{inQty}");
                            }
                            Plugin.Logger.LogInfo($"[SupplyChain] [demand] cooking recipe '{outputName}' via blueprint-parts: {partsSb}");
                        }
                        return true;
                    }
                }
                catch { }

                if (verbose && loggedRecipes.Add(outputName))
                    Plugin.Logger.LogInfo($"[SupplyChain] [demand] cookingRequirements UNAVAILABLE(not CrockpotRecipeInfo, class='{ownClass}', parts=empty) for '{outputName}'");
                return false;
            }

            CrockpotRecipeInfo crock;
            try { crock = new CrockpotRecipeInfo(baseObj.Pointer); }
            catch (Exception ex)
            {
                if (verbose && loggedRecipes.Add(outputName))
                    Plugin.Logger.LogInfo($"[SupplyChain] [demand] cookingRequirements UNAVAILABLE({ex.GetType().Name}) for '{outputName}'");
                return false;
            }

            Il2CppReferenceArray<CrockpotRecipeInfo.CookingRecipeRequirement>? reqs = null;
            try { reqs = crock.cookingRequirements; }
            catch (Exception ex)
            {
                if (verbose && loggedRecipes.Add(outputName))
                    Plugin.Logger.LogInfo($"[SupplyChain] [demand] cookingRequirements UNAVAILABLE({ex.GetType().Name}) for '{outputName}'");
                return false;
            }
            if (reqs == null)
            {
                if (verbose && loggedRecipes.Add(outputName))
                    Plugin.Logger.LogInfo($"[SupplyChain] [demand] cookingRequirements UNAVAILABLE(null) for '{outputName}'");
                return false;
            }

            bool firstLog = verbose && !loggedRecipes.Contains(outputName);
            var logSb = firstLog ? new System.Text.StringBuilder() : null;

            int rn = reqs.Length;
            for (int i = 0; i < rn; i++)
            {
                try
                {
                    var req = reqs[i];
                    if (req == null) continue;

                    string acceptedName = "?";
                    try { acceptedName = Common.SafeItemName(req.acceptedInfo); } catch { }
                    int count = 0;
                    try { count = req.count; } catch { }
                    string typeStr = "?";
                    try { typeStr = req.type.ToString(); } catch { }

                    // v0.16.0 — Table/Unfiltered_Table requirements are an ANY-OF acceptance list
                    // (any item in the table satisfies this slot). User-approved semantics: eviction
                    // PROTECTION only — no hard structural demand (a single any-of item would
                    // otherwise inflate demand across the whole table). Does not affect the NAMED
                    // acceptedName path below.
                    if (typeStr == "Table" || typeStr == "Unfiltered_Table")
                    {
                        try
                        {
                            var tableConfig = req.tableConfig;
                            if (tableConfig != null)
                            {
                                string tableName = "?";
                                try { tableName = tableConfig.Name ?? "?"; } catch { }

                                var tableItems = tableConfig.GetItemsList();
                                int tin = tableItems != null ? tableItems.Count : 0;
                                var acceptedNames = new List<string>();
                                for (int ti = 0; ti < tin; ti++)
                                {
                                    try
                                    {
                                        string tableItemName = Common.SafeItemName(tableItems![ti]);
                                        if (!string.IsNullOrEmpty(tableItemName) && tableItemName != "?")
                                        {
                                            anyOfProtected.Add(tableItemName);
                                            acceptedNames.Add(tableItemName);
                                        }
                                    }
                                    catch { }
                                }

                                if (verbose && loggedTables.Add(tableName))
                                    Plugin.Logger.LogInfo($"[SupplyChain] [demand] table '{tableName}': accepts [{string.Join(", ", acceptedNames)}] (any-of protection)");
                            }
                        }
                        catch { }
                    }

                    if (logSb != null)
                    {
                        if (logSb.Length > 0) logSb.Append(", ");
                        logSb.Append($"req '{acceptedName}' x{count} type={typeStr}");
                    }

                    if (acceptedName != "?" && count > 0) inputs.Add((acceptedName, count));
                }
                catch { }
            }

            if (firstLog)
            {
                loggedRecipes.Add(outputName);
                Plugin.Logger.LogInfo($"[SupplyChain] [demand] cooking recipe '{outputName}': {logSb}");
            }

            return inputs.Count > 0;
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"[SupplyChain] [demand] TryMatchCookingRecipe '{outputName}' error: {ex}");
            return false;
        }
    }

    private static void DumpStationTasks(StationWork sw, Dictionary<string, BlueprintEntry> union,
        Dictionary<string, List<(string input, int qty)>> transformMap,
        Dictionary<string, List<WantedByEntry>> wantedBy, Dictionary<string, int> directQty, List<string> stillUnmatched,
        HashSet<string> loggedCookingRecipes, HashSet<string> loggedTables, HashSet<string> anyOfProtected,
        Dictionary<string, string> producedMatchKind, bool verbose,
        ref int totalTasks, ref int totalMatchedLocal, ref int totalMatchedUnion, ref int totalMatchedCooking,
        ref int totalMatchedParts, ref int totalMatchedTransform, ref int totalMatchedFarm,
        ref int totalUnmatched, ref int totalStorageTasks, ref int totalFilterTasks,
        ref int bpQuantityAvailable, ref int bpQuantityMismatches)
    {
        Il2CppSystem.Collections.Generic.List<WorkstationTaskData>? datas = null;
        try { datas = sw.Ws.GetWorkstationTaskDatas(); }
        catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] [demand] GetWorkstationTaskDatas '{sw.StructureName}' error: {ex}"); }
        if (datas == null) return;

        int n = datas.Count;

        int stationMatched = 0, stationMatchedUnion = 0, stationMatchedCooking = 0;
        int stationMatchedParts = 0, stationMatchedTransform = 0, stationMatchedFarm = 0;
        int stationUnmatched = 0, stationStorageTasks = 0, stationFilterTasks = 0, stationProductionTasks = 0;
        var stationUnmatchedNames = new List<string>();

        for (int i = 0; i < n; i++)
        {
            WorkstationTaskData? task = null;
            try { task = datas[i]; } catch { continue; }
            if (task == null) continue;

            // v0.11.1 fix 1 — storage-collect / hidden-filter tasks are never production orders;
            // skip + tally before any item/quota resolution so they can never pollute structural
            // demand, even on a station whose class isn't in WarehouseClassList.
            try { if (task.TryCast<ResourceStorageTaskData>() != null) { stationStorageTasks++; continue; } } catch { }
            try { if (task.TryCast<FiltersTaskData>() != null) { stationFilterTasks++; continue; } } catch { }

            stationProductionTasks++;

            ItemInfo? item = null;
            try { item = task.itemInfoQuantity?.itemInfo; } catch { }
            if (item == null) continue;

            int quota = 0;
            try
            {
                var iq = task.itemInfoQuantity;
                quota = iq != null ? iq.quantity : 0;
            }
            catch { }

            string outputName = Common.SafeItemName(item);

            // v0.16.0 — FarmingStation tasks CONSUME their item (the seed being planted); they
            // don't produce it. Task quota = structural demand for the seed itself.
            if (sw.NativeClass == "FarmingStation" || sw.NativeClass == "ForestryStation")
            {
                if (quota > 0 && !string.IsNullOrEmpty(outputName) && outputName != "?")
                {
                    if (!wantedBy.TryGetValue(outputName, out var flist))
                    {
                        flist = new List<WantedByEntry>();
                        wantedBy[outputName] = flist;
                    }
                    flist.Add(new WantedByEntry { Station = sw.StructureName, Output = $"{sw.StructureName} planting", Qty = quota });
                    directQty.TryGetValue(outputName, out int fex);
                    directQty[outputName] = fex + quota;
                    stationMatchedFarm++;
                }
                continue;
            }

            // v0.11.1 fix 4 — match order: station-local blueprints -> union -> cooking -> transform map.
            List<(string input, int qty)>? inputs = null;
            int outputPerCraft = 1;
            string matchKind = "none";

            if (sw.LocalBlueprints.TryGetValue(outputName, out var localEntry))
            {
                inputs = localEntry.Inputs;
                outputPerCraft = localEntry.OutputPerCraft > 0 ? localEntry.OutputPerCraft : 1;
                matchKind = "local";
            }
            else if (union.TryGetValue(outputName, out var unionEntry))
            {
                inputs = unionEntry.Inputs;
                outputPerCraft = unionEntry.OutputPerCraft > 0 ? unionEntry.OutputPerCraft : 1;
                matchKind = "union";
            }
            else if (TryMatchCookingRecipe(item, outputName, verbose, loggedCookingRecipes, loggedTables, anyOfProtected,
                         ref bpQuantityAvailable, ref bpQuantityMismatches, out var cookingInputs, out int cookingOutputPerCraft,
                         out bool viaBlueprintParts))
            {
                inputs = cookingInputs;
                outputPerCraft = cookingOutputPerCraft;
                matchKind = viaBlueprintParts ? "parts" : "cooking";
            }
            else if (transformMap.TryGetValue(outputName, out var transformInputs))
            {
                inputs = transformInputs;
                outputPerCraft = 1;
                matchKind = "transform";
            }

            if (inputs == null)
            {
                stationUnmatched++;
                if (stationUnmatchedNames.Count < 5) stationUnmatchedNames.Add(outputName);
                if (stillUnmatched.Count < 60 && !stillUnmatched.Contains(outputName)) stillUnmatched.Add(outputName);
                // v0.17.2 — record "none" only if no OTHER station's task already matched this same
                // produced name (last-write-wins would otherwise let an unmatched sighting clobber an
                // earlier real match — rare, but a produced name can appear at more than one station).
                if (!producedMatchKind.ContainsKey(outputName)) producedMatchKind[outputName] = "none";
                continue;
            }

            switch (matchKind)
            {
                case "local": stationMatched++; break;
                case "union": stationMatchedUnion++; break;
                case "cooking": stationMatchedCooking++; break;
                case "parts": stationMatchedParts++; break;
                case "transform": stationMatchedTransform++; break;
            }
            // v0.17.2 — a real match always overwrites (even a prior "none" from another station).
            producedMatchKind[outputName] = matchKind;

            int crafts = (int)Math.Ceiling(quota / (double)outputPerCraft);

            for (int ii = 0; ii < inputs.Count; ii++)
            {
                var (inputName, inputQty) = inputs[ii];
                int structuralQty = inputQty * crafts;
                if (structuralQty <= 0) continue;

                if (!wantedBy.TryGetValue(inputName, out var list))
                {
                    list = new List<WantedByEntry>();
                    wantedBy[inputName] = list;
                }
                list.Add(new WantedByEntry { Station = sw.StructureName, Output = outputName, Qty = structuralQty });

                directQty.TryGetValue(inputName, out int existing);
                directQty[inputName] = existing + structuralQty;
            }
        }

        totalTasks += stationProductionTasks;
        totalMatchedLocal += stationMatched;
        totalMatchedUnion += stationMatchedUnion;
        totalMatchedCooking += stationMatchedCooking;
        totalMatchedParts += stationMatchedParts;
        totalMatchedTransform += stationMatchedTransform;
        totalMatchedFarm += stationMatchedFarm;
        totalUnmatched += stationUnmatched;
        totalStorageTasks += stationStorageTasks;
        totalFilterTasks += stationFilterTasks;

        if (verbose)
        {
            string unmatchedStr = stationUnmatchedNames.Count > 0 ? $" unmatchedItems=[{string.Join(", ", stationUnmatchedNames)}]" : "";
            string walkStr = sw.ViaWalk ? "(walk)" : "";
            string storageFilterStr = "";
            if (stationStorageTasks > 0) storageFilterStr += $" storageTasks={stationStorageTasks}";
            if (stationFilterTasks > 0) storageFilterStr += $" filterTasks={stationFilterTasks}";
            Plugin.Logger.LogInfo(
                $"[SupplyChain] [demand] station '{sw.StructureName}' class='{sw.NativeClass}': tables={sw.TableCount}{walkStr} " +
                $"blueprints={sw.LocalBlueprintCount} dupNames={sw.DupNames} tasks={stationProductionTasks}{storageFilterStr} " +
                $"matched={stationMatched} matchedViaUnion={stationMatchedUnion} matchedViaCooking={stationMatchedCooking} " +
                $"matchedViaParts={stationMatchedParts} matchedViaTransform={stationMatchedTransform} matchedViaFarm={stationMatchedFarm} " +
                $"unmatched={stationUnmatched}{unmatchedStr}");
        }
    }

    // Breadth-first propagation over the UNION blueprint graph (and, as of v0.11.1, the transform
    // map — a demanded transform OUTPUT propagates to its inputs the same way a blueprint does,
    // just with an implicit outputPerCraft of 1). Each item propagates FROM itself at most once per
    // rebuild (propagatedFrom guard — also the cycle guard), using its total (direct+derived)
    // accumulated so far when its turn comes — items are processed strictly by BFS level, so every
    // level's total is fully accumulated from all shallower levels before that level propagates
    // onward (depth cap 8). NOTE: a diamond dependency (an item reachable at two different depths
    // from different parents) can still propagate using a total that's missing a LATER-arriving
    // contribution from a deeper sibling path — an accepted v1 approximation (the plan explicitly
    // scopes exact topological correctness to the future chain-capacity rollup).
    private static void PropagateTransitive(Dictionary<string, BlueprintEntry> union,
        Dictionary<string, List<(string input, int qty)>> transformMap, Dictionary<string, int> directQty,
        Dictionary<string, int> derivedQty, Dictionary<string, List<string>> derivedVia, Dictionary<string, int> wantedByCount)
    {
        var propagatedFrom = new HashSet<string>();
        var currentLevel = new List<string>(directQty.Keys);
        int depth = 0;

        while (currentLevel.Count > 0 && depth < 8)
        {
            var nextContribs = new Dictionary<string, int>();
            var nextParents = new Dictionary<string, List<string>>();

            foreach (var item in currentLevel)
            {
                if (!propagatedFrom.Add(item)) continue;

                List<(string input, int qty)>? inputs = null;
                int outputPerCraft = 1;
                if (union.TryGetValue(item, out var entry))
                {
                    inputs = entry.Inputs;
                    outputPerCraft = entry.OutputPerCraft > 0 ? entry.OutputPerCraft : 1;
                }
                else if (transformMap.TryGetValue(item, out var transformInputs))
                {
                    inputs = transformInputs;
                    outputPerCraft = 1;
                }
                if (inputs == null) continue;

                directQty.TryGetValue(item, out int d);
                derivedQty.TryGetValue(item, out int v);
                int total = d + v;
                if (total <= 0) continue;

                int crafts = (int)Math.Ceiling(total / (double)outputPerCraft);

                for (int i = 0; i < inputs.Count; i++)
                {
                    var (inputName, inputQty) = inputs[i];
                    int add = inputQty * crafts;
                    if (add <= 0) continue;

                    nextContribs.TryGetValue(inputName, out int existing);
                    nextContribs[inputName] = existing + add;

                    if (!nextParents.TryGetValue(inputName, out var parents))
                    {
                        parents = new List<string>();
                        nextParents[inputName] = parents;
                    }
                    if (parents.Count < 3 && !parents.Contains(item)) parents.Add(item);
                }
            }

            if (nextContribs.Count == 0) break;

            var nextLevel = new List<string>();
            foreach (var kv in nextContribs)
            {
                string item = kv.Key;
                derivedQty.TryGetValue(item, out int existingDerived);
                derivedQty[item] = existingDerived + kv.Value;
                nextLevel.Add(item);

                if (nextParents.TryGetValue(item, out var parents))
                {
                    if (!derivedVia.TryGetValue(item, out var list))
                    {
                        list = new List<string>();
                        derivedVia[item] = list;
                    }
                    foreach (var p in parents) if (list.Count < 3 && !list.Contains(p)) list.Add(p);
                }
            }

            currentLevel = nextLevel;
            depth++;
        }

        // Fold the (capped) derived-parent lists into wantedByCount once, after propagation settles.
        foreach (var kv in derivedVia)
        {
            wantedByCount.TryGetValue(kv.Key, out int existing);
            wantedByCount[kv.Key] = existing + kv.Value.Count;
        }
    }

    private static void LogNoTables(Dictionary<string, int> noTablesClasses)
    {
        if (noTablesClasses.Count == 0) return;

        var entries = new List<(string cls, int count)>();
        foreach (var kv in noTablesClasses) entries.Add((kv.Key, kv.Value));
        entries.Sort((a, b) =>
        {
            int c = b.count.CompareTo(a.count);
            return c != 0 ? c : string.CompareOrdinal(a.cls, b.cls);
        });

        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < entries.Count; i++)
        {
            if (sb.Length > 0) sb.Append(", ");
            sb.Append(entries[i].cls).Append(" x").Append(entries[i].count);
        }

        Plugin.Logger.LogInfo($"[SupplyChain] [demand] no craft tables: {sb}");
    }

    private static void LogRollup(HashSet<string> allItems, Dictionary<string, int> directQty, Dictionary<string, int> derivedQty,
        Dictionary<string, List<string>> derivedVia, Dictionary<string, List<WantedByEntry>> wantedBy)
    {
        var items = new List<(string name, int total, int direct, int derived)>();
        foreach (var name in allItems)
        {
            directQty.TryGetValue(name, out int d);
            derivedQty.TryGetValue(name, out int v);
            items.Add((name, d + v, d, v));
        }

        items.Sort((a, b) => b.total.CompareTo(a.total));

        const int maxLines = 60;
        const int maxWantedByEntries = 10;
        int logged = 0;

        for (int i = 0; i < items.Count; i++)
        {
            if (logged >= maxLines)
            {
                Plugin.Logger.LogInfo($"[SupplyChain] [demand] ... and {items.Count - logged} more input items");
                break;
            }

            var (name, total, direct, derived) = items[i];

            string wantedByStr = "";
            if (wantedBy.TryGetValue(name, out var entries))
            {
                entries.Sort((a, b) => b.Qty.CompareTo(a.Qty));
                var sb = new System.Text.StringBuilder();
                int shown = Math.Min(entries.Count, maxWantedByEntries);
                for (int e = 0; e < shown; e++)
                {
                    if (sb.Length > 0) sb.Append(", ");
                    sb.Append(entries[e].Station).Append('→').Append(entries[e].Output).Append(" x").Append(entries[e].Qty);
                }
                if (entries.Count > maxWantedByEntries) sb.Append($", +{entries.Count - maxWantedByEntries} more");
                wantedByStr = sb.ToString();
            }

            // v0.11.1 fix 6 — logging polish: any item with derived > 0 shows its (up to 3) immediate
            // parents, not just pure-derived (direct == 0) items — a partially-direct,
            // partially-derived item is exactly the interesting case for auditing propagation.
            string derivedViaStr = "";
            if (derived > 0 && derivedVia.TryGetValue(name, out var parents) && parents.Count > 0)
                derivedViaStr = $" derivedVia=[{string.Join(", ", parents)}]";

            Plugin.Logger.LogInfo(
                $"[SupplyChain] [demand] item '{name}': structural={total} (direct={direct} derived={derived}) " +
                $"wantedBy=[{wantedByStr}]{derivedViaStr}");
            logged++;
        }
    }

    // Manual hierarchy walk collecting EVERY CraftInteraction under root (same walker style as
    // ContainerProbe.WalkForContainers / StationWalker.FindComponent) — used for stations with no
    // CraftingStation component (e.g. Bloomstation) whose crafting tables sit elsewhere in the
    // hierarchy.
    private static void WalkForCraftInteractions(Transform node, List<CraftInteraction> results, int depth)
    {
        if (node == null || depth > 12) return;

        try
        {
            var ci = node.GetComponent<CraftInteraction>();
            if (ci != null) results.Add(ci);
        }
        catch { }

        int childCount;
        try { childCount = node.childCount; } catch { return; }
        for (int i = 0; i < childCount; i++)
        {
            Transform? child = null;
            try { child = node.GetChild(i); } catch { }
            if (child == null) continue;
            WalkForCraftInteractions(child, results, depth + 1);
        }
    }
}
