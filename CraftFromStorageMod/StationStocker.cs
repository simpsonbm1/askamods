using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using SandSailorStudio.Inventory;
using SSSGame;
using SSSGame.AI;
using SSSGame.AI.FSM;

namespace CraftFromStorageMod;

// v0.9.0 (idea-17 Phase 2c) - the inverted approach to villager crafting from settlement storage.
// v0.8.0 tried to make the AI scheduler skip FSM_FetchCraftingSupplies entirely (FetchQuestSuppression.
// cs) so the villager would craft immediately instead of walking off first. Confirmed in-game
// 2026-07-27 that this stalled crafting entirely: 704 of 708 cycles logged verdict=DIRECT
// fetchEnters=0 (the walk WAS suppressed), but all 708 cycles logged modPulls=0 and no crafting-
// success marker ever appeared in a 5m49s run. Root cause: the mod's existing just-in-time pull hangs
// off BeginCraftingSequence (Point C, CraftTransfer.HandleBeginCraftingSequence), which the AI
// scheduler never reaches for a villager standing at an EMPTY station - suppressing the walk removed
// the only thing that would ever have stocked it.
//
// v0.9.0 instead lets the fetch quest be chosen as vanilla intends, then intercepts it the MOMENT it
// starts (FSM_FetchCraftingSupplies.OnStateEnter) and teleports the materials she was about to fetch
// from settlement storage directly into the crafting station's inventory - so the walk becomes
// unnecessary through vanilla's own scheduling path rather than by fighting it. The v0.8.0 lever is
// retired behind Plugin.SuppressFetchQuestPriority (default false), not deleted - see Plugin.cs Load()
// and FetchQuestSuppression.cs for that history.
//
// All log lines here carry the "[CFS-SS]" tag (on top of the mod-wide "[CFS]" tag), distinct from both
// the v0.6.0 read-only "[CFS-P2]" spike and the v0.7.x/v0.8.0 "[CFS-V]"/"[CFS-FQ]" tags, so this
// feature's output greps cleanly on its own.
internal static class StationStocker
{
    // Own HashSet, own tag - deliberately NOT VillagerFetchTrace.MarkAlive (hardcodes "[CFS-P2]",
    // reserved for Patches/VillagerFetchPatches.cs's READ-ONLY spike) and NOT FetchQuestSuppression.
    // MarkAlive (hardcodes "[CFS-FQ]", reserved for the v0.8.0 retired lever).
    private static readonly HashSet<string> _aliveLogged = new();

    // v0.9.2: cached parse of Plugin.SkipBlueprintClasses, same shape as SettlementStock's
    // _blacklistCache/_blacklistRaw pair - rebuilt only when the raw config string actually changes.
    private static HashSet<string>? _skipClassCache;
    private static string _skipClassRaw = "";

    // v0.9.2: rate limiters for the two new diagnostic lines this feature adds. Both are plain
    // string-keyed counters (never interop wrappers - project-wide gotcha), cleared on world-leave
    // via ClearWorldState below.
    private static readonly Dictionary<string, int> _unresolvedBpClassLogged = new(); // keyed by failReason
    private static readonly Dictionary<string, int> _skipLogged = new(); // keyed by "bpClass|station"
    // v0.10.0 originally added a _stationSkipLogged rate limiter here for a station-class SKIP line.
    // v0.10.1 (defect fix): retired - the physical-transform gate below now calls
    // CraftTransfer.IsSkippedStation(CraftingStation) directly, which owns its own rate-limited
    // "[CFS] [CFS-SS] stationProbe" diagnostic (see CraftTransfer.cs) covering the SAME matched/
    // not-matched distinction this line used to report, plus the unresolved/null cases it never could.

    // v0.12.0 (TRANSFORM_STATION_PLAN.md "The fix"): rate limiter for the new routeDecision
    // diagnostic below, keyed by station name - same idiom as _stationProbeLogged in CraftTransfer.cs.
    private static readonly Dictionary<string, int> _routeLogged = new();

    // v0.13.0: rate limiter for the per-item DENIED ITEM diagnostic below, keyed by
    // "itemName|station" - same idiom as _skipLogged above.
    private static readonly Dictionary<string, int> _deniedItemLogged = new();

    internal static void MarkAlive(string target)
    {
        try
        {
            if (_aliveLogged.Add(target))
                Plugin.Logger.LogInfo($"[CFS] [CFS-SS] PATCH ALIVE {target}");
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"[CFS] [CFS-SS] StationStocker.MarkAlive error: {ex}");
        }
    }

    // v0.9.2: world-leave reset for this file's own rate-limiter dictionaries (StationStocker holds
    // no interop wrappers of its own - string/int only - but a stale run's counts shouldn't survive
    // into the next world session). Wired from CraftWatcher.ClearWorldState alongside every other
    // per-mod tracker's own reset.
    internal static void ClearWorldState()
    {
        try
        {
            _unresolvedBpClassLogged.Clear();
            _skipLogged.Clear();
            _routeLogged.Clear();
            _deniedItemLogged.Clear();
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"[CFS] [CFS-SS] StationStocker.ClearWorldState error: {ex}");
        }
    }

    private static HashSet<string> GetSkipClasses()
    {
        string raw = "";
        try { raw = Plugin.SkipBlueprintClasses?.Value ?? ""; } catch { }
        if (_skipClassCache != null && raw == _skipClassRaw) return _skipClassCache;

        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in raw.Split(','))
        {
            var t = part.Trim();
            if (t.Length > 0) set.Add(t);
        }
        _skipClassRaw = raw;
        _skipClassCache = set;
        return set;
    }

    // v0.9.2: resolves the native class name of the blueprint a villager's crafting fetch quest is
    // actually fetching FOR, via the CrafterSpecificFetchQuest -> CraftingProject -> CraftingQuest ->
    // BlueprintInfo chain (Cecil-confirmed 2026-07-28, _explore/cecil_cfs_v092*.ps1/_out.txt). Every
    // step wrapped in its own try/catch. FAIL-OPEN is mandatory here: an empty return must mean
    // "stock exactly as v0.9.1 did" - a null link in the chain must never silently disable working
    // behavior, so the caller treats "" as "not gated" rather than "blocked".
    //
    // v0.9.3: a plain CrafterFetchQuest (not the ...Specific... subclass) has no craftingProject link
    // at all, and the v0.9.2 in-game run showed that's HALF of all fetch quests (447/899 STOCKED lines
    // logged bpClass=? with reason=notSpecific:CrafterFetchQuest) - leaving the gate blind to them.
    // The fallback below goes through the STATION instead of the quest: a station only ever gets
    // gated when every one of its own craftingProjects agrees on the same blueprint class, which keeps
    // the fail-open guarantee - an ambiguous or unreadable station stocks exactly as it did before.
    private static string ResolveBlueprintClass(CrafterFetchQuest quest, CraftingStation station, out string failReason)
    {
        failReason = "";
        try
        {
            // Step 1: identify the REAL native class before rewrapping - managed as/is casts lie for
            // interop objects materialized under a base declared type (project-wide gotcha).
            string questClass = Plugin.NativeClassName(quest);
            if (questClass != "CrafterSpecificFetchQuest")
            {
                return ResolveBlueprintClassViaStation(station, out failReason);
            }

            // Step 2.
            IntPtr ptr = VillagerFetchTrace.SafePointer(quest);
            if (ptr == IntPtr.Zero) { failReason = "nullPtr"; return ""; }

            // Step 3: rewrap by pointer (same pattern as the CrafterFetchQuestData rewrap above).
            CrafterSpecificFetchQuest specific;
            try { specific = new CrafterSpecificFetchQuest(ptr); }
            catch (Exception ex) { failReason = "rewrapError:" + ex.GetType().Name; return ""; }

            // Step 4.
            CraftingStation.CraftingProject? project = null;
            try { project = specific.craftingProject; } catch { }
            if (project == null) { failReason = "nullProject"; return ""; }

            // Step 5.
            CraftingQuest? craftingQuest = null;
            try { craftingQuest = project.craftingQuest; } catch { }
            if (craftingQuest == null) { failReason = "nullQuest"; return ""; }

            // Step 6.
            BlueprintInfo? bpInfo = null;
            try { bpInfo = craftingQuest.BlueprintInfo; } catch { }
            if (bpInfo == null) { failReason = "nullBpInfo"; return ""; }

            // Step 7.
            return Plugin.NativeClassName(bpInfo);
        }
        catch (Exception ex)
        {
            failReason = "exception:" + ex.GetType().Name;
            return "";
        }
    }

    // v0.9.3: fallback path for a plain CrafterFetchQuest, which carries no craftingProject link -
    // walks the STATION's own craftingProjects list instead and reads each element's
    // craftingQuest.BlueprintInfo chain (same shape as the specific-quest path above, just rooted at
    // the station rather than the quest). Attribution rule: a plain CrafterFetchQuest gives no way to
    // know WHICH of the station's projects it belongs to, so this only ever returns a class when every
    // resolved project agrees - otherwise it returns "" (ambiguous/unresolved), which the caller
    // treats as "not gated", preserving fail-open.
    private static string ResolveBlueprintClassViaStation(CraftingStation station, out string failReason)
    {
        failReason = "";
        try
        {
            Il2CppSystem.Collections.Generic.List<CraftingStation.CraftingProject>? projects = null;
            try { projects = station.craftingProjects; } catch { }
            if (projects == null || projects.Count == 0) { failReason = "fallbackNoProjects"; return ""; }

            string? agreed = null;
            bool ambiguous = false;
            int resolvedCount = 0;
            int distinctCount = 0;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < projects.Count; i++)
            {
                CraftingStation.CraftingProject? project = null;
                try { project = projects[i]; } catch { }
                if (project == null) continue;

                CraftingQuest? craftingQuest = null;
                try { craftingQuest = project.craftingQuest; } catch { }
                if (craftingQuest == null) continue;

                BlueprintInfo? bpInfo = null;
                try { bpInfo = craftingQuest.BlueprintInfo; } catch { }
                if (bpInfo == null) continue;

                string cls = Plugin.NativeClassName(bpInfo);
                resolvedCount++;
                if (seen.Add(cls)) distinctCount++;
                if (agreed == null) agreed = cls;
                else if (!string.Equals(agreed, cls, StringComparison.OrdinalIgnoreCase)) ambiguous = true;
            }

            if (resolvedCount == 0) { failReason = "fallbackUnresolved"; return ""; }
            if (ambiguous) { failReason = "fallbackAmbiguous:" + distinctCount; return ""; }
            return agreed!;
        }
        catch (Exception ex)
        {
            failReason = "exception:" + ex.GetType().Name;
            return "";
        }
    }

    // v0.13.0: per-ITEM denial set for the station-fallback path (TRANSFORM_STATION_PLAN.md follow-up
    // defect fix). ResolveBlueprintClassViaStation's whole-station agree/ambiguous test denies the
    // ENTIRE fetch the moment a workshop's shared quest can't resolve to one blueprint class - which
    // wrongly starves an ordinary table sharing a building with a transform unit whenever their
    // projects disagree (confirmed in-game 2026-07-30: routeDecision station=Workshop House 2
    // route=station-fallback detail=fallbackAmbiguous:3 outcome=skip). This walks the SAME
    // craftingProjects list but keeps ordinary-project and transform-project item names separate, so
    // only items wanted EXCLUSIVELY by a transform project get denied - an item any ordinary project
    // also wants is never withheld. Every interop read is its own try/catch; the outer catch fails
    // open (empty set = deny nothing), matching this mod's fail-open rule throughout.
    private static HashSet<string> BuildDeniedItemNames(CraftingStation station, out int ordinaryProjects, out int transformProjects)
    {
        ordinaryProjects = 0;
        transformProjects = 0;
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            Il2CppSystem.Collections.Generic.List<CraftingStation.CraftingProject>? projects = null;
            try { projects = station.craftingProjects; } catch { }
            if (projects == null || projects.Count == 0) return result;

            var transformNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var ordinaryNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var skipClasses = GetSkipClasses();

            for (int i = 0; i < projects.Count; i++)
            {
                CraftingStation.CraftingProject? project = null;
                try { project = projects[i]; } catch { }
                if (project == null) continue;

                CraftingQuest? craftingQuest = null;
                try { craftingQuest = project.craftingQuest; } catch { }
                if (craftingQuest == null) continue;

                BlueprintInfo? bpInfo = null;
                try { bpInfo = craftingQuest.BlueprintInfo; } catch { }
                if (bpInfo == null) continue;
                string bpClass = Plugin.NativeClassName(bpInfo);

                ItemManifest? manifest = null;
                try { manifest = craftingQuest.TotalPartsManifest; } catch { }
                if (manifest == null)
                {
                    try { manifest = craftingQuest._partsManifest; } catch { }
                }
                if (manifest == null) continue;

                var pairs = CraftTransfer.EnumerateManifest(manifest);

                bool isTransform = skipClasses.Contains(bpClass);
                if (isTransform)
                {
                    transformProjects++;
                    foreach (var (info, _) in pairs) transformNames.Add(SafeName(info));
                }
                else
                {
                    ordinaryProjects++;
                    foreach (var (info, _) in pairs) ordinaryNames.Add(SafeName(info));
                }
            }

            transformNames.ExceptWith(ordinaryNames);
            return transformNames;
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"[CFS] [CFS-SS] BuildDeniedItemNames error: {ex}");
            // Reset both counts so a partially-built classification can never be reported as a
            // usable one. A nonzero count alongside an empty denied set would send the caller
            // down the per-item route with nothing to deny, silently stocking transform units
            // while toggle 2 is off. Zeroing here routes that case to the whole-building test.
            ordinaryProjects = 0;
            transformProjects = 0;
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    // Same shape CraftTransfer.SafeName uses (that one is private to its own file, so this is a
    // duplicate rather than a shared helper).
    private static string SafeName(ItemInfo? info)
    {
        if (info == null) return "?";
        try { return info.Name ?? "?"; } catch { return "?"; }
    }

    // v0.14.0: builds the "one craft's worth" wanted list for a single fetch, replacing
    // quest.GetNeededSuppliesManifest()'s AGGREGATE manifest (every queued craft at the station) as
    // HandleFetchStateEnter's primary quantity source. Confirmed in-game 2026-07-30 that stocking the
    // aggregate at one workshop moved 105 items in one shot, filled the bin to hard capacity, and then
    // refused the ONE part (Large Iron Axe Head) the active recipe actually needed - villagers looped
    // at the table while the part sat in warehouses. The routing/denial logic in HandleFetchStateEnter
    // (recipe/per-item/station-fallback, deniedItems) is untouched by this method; only the QUANTITY
    // fed into the shortfall loop changes. Returns null (with planSource explaining why) whenever no
    // plan could be built at all - the caller falls back to the aggregate manifest exactly as before,
    // preserving this mod's fail-open rule.
    private static List<(ItemInfo info, int qty)>? BuildOneCraftPlan(
        CrafterFetchQuest quest, CraftingStation station, out string planSource)
    {
        planSource = "none";
        try
        {
            // Branch A: the quest is project-specific - same CrafterSpecificFetchQuest rewrap chain
            // ResolveBlueprintClass uses above (Step 1-5), just reading .Blueprint (parts) instead of
            // .BlueprintInfo (classification only) at the end. Any null link along the way falls
            // through to Branch B below rather than returning - unlike ResolveBlueprintClass, an
            // unresolved project-specific quest still has the station-wide fallback available here.
            string questClass = Plugin.NativeClassName(quest);
            if (questClass == "CrafterSpecificFetchQuest")
            {
                IntPtr ptr = VillagerFetchTrace.SafePointer(quest);
                if (ptr != IntPtr.Zero)
                {
                    CrafterSpecificFetchQuest? specific = null;
                    try { specific = new CrafterSpecificFetchQuest(ptr); } catch { }

                    CraftingStation.CraftingProject? project = null;
                    if (specific != null) { try { project = specific.craftingProject; } catch { } }

                    CraftingQuest? craftingQuest = null;
                    if (project != null) { try { craftingQuest = project.craftingQuest; } catch { } }

                    Blueprint? bp = null;
                    if (craftingQuest != null) { try { bp = craftingQuest.Blueprint; } catch { } }

                    if (bp != null)
                    {
                        ItemManifest? m = new ItemManifest();
                        try { bp.FillPartsManifest(m); } catch { m = null; }

                        if (m != null)
                        {
                            var recipePairs = CraftTransfer.EnumerateManifest(m);
                            if (recipePairs.Count > 0)
                            {
                                planSource = "recipe";
                                return recipePairs;
                            }
                        }
                    }
                }
            }

            // Branch B: walk the station's own projects, merging one craft's worth from each included
            // project. Same classify-by-BlueprintInfo test HandleFetchStateEnter's routing block uses
            // above (GetSkipClasses / StockTransformStationMaterials).
            Il2CppSystem.Collections.Generic.List<CraftingStation.CraftingProject>? projects = null;
            try { projects = station.craftingProjects; } catch { }
            if (projects == null || projects.Count == 0) { planSource = "none"; return null; }

            var skipClasses = GetSkipClasses();
            bool stockTransform = SafeGetBool(Plugin.StockTransformStationMaterials, false);
            var merged = new Dictionary<string, (ItemInfo info, int qty)>(StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < projects.Count; i++)
            {
                CraftingStation.CraftingProject? project = null;
                try { project = projects[i]; } catch { }
                if (project == null) continue;

                CraftingQuest? craftingQuest = null;
                try { craftingQuest = project.craftingQuest; } catch { }
                if (craftingQuest == null) continue;

                BlueprintInfo? bpInfo = null;
                try { bpInfo = craftingQuest.BlueprintInfo; } catch { }
                if (bpInfo == null) continue;
                string bpClass = Plugin.NativeClassName(bpInfo);

                // Skip-listed (transform-family) project: include only if toggle 2 is on. Any other
                // class: always include.
                if (skipClasses.Contains(bpClass) && !stockTransform) continue;

                Blueprint? bp = null;
                try { bp = craftingQuest.Blueprint; } catch { }
                if (bp == null) continue;

                ItemManifest? m = new ItemManifest();
                try { bp.FillPartsManifest(m); } catch { m = null; }
                if (m == null) continue;

                var projectPairs = CraftTransfer.EnumerateManifest(m);
                foreach (var (info, qty) in projectPairs)
                {
                    string key = SafeName(info);
                    if (merged.TryGetValue(key, out var existing))
                        merged[key] = (existing.info, existing.qty + qty);
                    else
                        merged[key] = (info, qty);
                }
            }

            if (merged.Count > 0)
            {
                planSource = "projects";
                return new List<(ItemInfo info, int qty)>(merged.Values);
            }

            planSource = "none";
            return null;
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"[CFS] [CFS-SS] BuildOneCraftPlan error: {ex}");
            planSource = "error";
            return null;
        }
    }

    // v0.9.3: diagnostic-only - returns station.CraftingType.ToString() so one run's worth of STOCKED
    // lines reveals the enum's actual values, since a station-kind test may later replace the
    // blueprint-class gate entirely (see the file header + Plugin.cs SkipBlueprintClasses comment).
    // Nothing branches on this; "?" on any failure.
    private static string ResolveStationType(CraftingStation station)
    {
        try { return station.CraftingType.ToString(); }
        catch { return "?"; }
    }

    // Called from Patches/StationStockPatches.cs FetchCraftingSuppliesStockPatch.Postfix. Wrapped in
    // try/catch by the caller as well as here (belt-and-suspenders) - a throw in villager-AI-adjacent
    // code must never break the FSM state transition that called it.
    internal static void HandleFetchStateEnter(FSM_FetchCraftingSupplies instance, IFSMBehaviourController fsmBehaviour)
    {
        try
        {
            MarkAlive("FSM_FetchCraftingSupplies.OnStateEnter (stocker)");

            // Gate: both master switches must be on, and this client must hold write authority over
            // settlement-shared state (same host gate CraftTransfer/FetchQuestSuppression already use).
            if (!SafeGetBool(Plugin.EnableForVillagers, true)) return;
            if (!SafeGetBool(Plugin.StockStationOnFetch, true)) return;
            if (!CraftTransfer.IsHostOrSolo()) return;

            // Step 3: GetQuestData is inherited from FSM_QuestAction (Patches/VillagerFetchPatches.cs
            // line 165 already calls it this exact way).
            QuestData? qd = null;
            try { qd = instance.GetQuestData(fsmBehaviour); } catch { }
            if (qd == null) return;

            // Step 4: identify the REAL native class before rewrapping - managed as/is casts lie for
            // interop objects materialized under a base declared type (project-wide gotcha). Mirrors
            // LogQuestDataEnrichment in Patches/VillagerFetchPatches.cs.
            string nativeClass = Plugin.NativeClassName(qd);
            if (nativeClass != "CrafterFetchQuestData") return;

            IntPtr ptr = VillagerFetchTrace.SafePointer(qd);
            if (ptr == IntPtr.Zero) return;

            CrafterFetchQuest.CrafterFetchQuestData cfqd;
            try { cfqd = new CrafterFetchQuest.CrafterFetchQuestData(ptr); }
            catch (Exception ex) { Plugin.Logger.LogError($"[CFS] [CFS-SS] CrafterFetchQuestData rewrap error: {ex}"); return; }

            // Step 5.
            CrafterFetchQuest? quest = null;
            try { quest = cfqd.Quest; } catch (Exception ex) { Plugin.Logger.LogError($"[CFS] [CFS-SS] get_Quest error: {ex}"); }
            if (quest == null) return;

            // Step 6.
            CraftingStation? station = null;
            try { station = quest.craftingStation; } catch (Exception ex) { Plugin.Logger.LogError($"[CFS] [CFS-SS] get_craftingStation error: {ex}"); }
            if (station == null) return;

            // v0.9.2: resolved early (moved up from below Step 10) so both the SKIP gate and the
            // eventual STOCKED line can reuse the same villager/station name strings.
            string villagerName = VillagerFetchTrace.SafeVillagerName(fsmBehaviour);
            string stationName = ResolveStationName(station);
            string stationObjName = ResolveStationObjectName(station); // v0.9.1

            // v0.12.0 (TRANSFORM_STATION_PLAN.md "The fix" - defect fix): the recipe-family gate now
            // runs BEFORE the station-level gate, not after. The recipe gate is per-UNIT by nature - a
            // forging recipe means the anvil, an ordinary craft recipe means the table - while
            // CraftTransfer.IsSkippedStation(CraftingStation) answers per BUILDING. One station object
            // can own several work surfaces at once (confirmed in-game 2026-07-30: one station's own
            // all=[CraftInteraction@descendant; AnvilInteraction@descendant; CarpenterInteraction@
            // descendant; CarpenterInteraction@descendant]), so running the building-level test first
            // denied toggle 1 to a villager working an ordinary table just because an add-on shares the
            // same building. bpClass is resolved ONCE here (moved up, resolution logic unchanged) and
            // reused by both this decision and Part 2's STOCKED line below.
            //
            // The station test still earns its place as a fallback rather than being deleted: 170 of
            // 336 stock attempts in the 2026-07-29 run could not resolve a recipe family at all, and
            // those need SOME answer - the whole-building test is the only one left once the per-unit
            // signal is unavailable.
            string bpClass = ResolveBlueprintClass(quest, station, out string bpFailReason);
            if (bpFailReason.Length > 0)
            {
                _unresolvedBpClassLogged.TryGetValue(bpFailReason, out int loggedCount);
                if (loggedCount < 5)
                {
                    _unresolvedBpClassLogged[bpFailReason] = loggedCount + 1;
                    Plugin.Logger.LogInfo($"[CFS] [CFS-SS] bpClass UNRESOLVED reason={bpFailReason} (stocking anyway - fail-open).");
                }
            }

            bool proceed;
            string route;
            string routeDetail;
            HashSet<string> deniedItems = new(StringComparer.OrdinalIgnoreCase);

            if (bpClass.Length > 0)
            {
                route = "recipe";
                routeDetail = bpClass;

                if (GetSkipClasses().Contains(bpClass))
                {
                    // Resolved to a transform family (e.g. ForgingBlueprintInfo) - this recipe means
                    // the add-on unit, not the bin. Toggle 2 decides, same as it always has for the
                    // station-level gate below.
                    if (SafeGetBool(Plugin.StockTransformStationMaterials, false))
                    {
                        Plugin.Logger.LogInfo($"[CFS] [CFS-SS] StockTransformStationMaterials=true - proceeding " +
                            $"to stock physical-transform station villager={villagerName} station={stationName}.");
                        proceed = true;
                    }
                    else
                    {
                        string skipKey = bpClass + "|" + stationName;
                        _skipLogged.TryGetValue(skipKey, out int skipCount);
                        if (skipCount < 10)
                        {
                            _skipLogged[skipKey] = skipCount + 1;
                            Plugin.Logger.LogInfo($"[CFS] [CFS-SS] SKIP blueprintClass={bpClass} villager={villagerName} station={stationName}");
                        }
                        proceed = false;
                    }
                }
                else
                {
                    // Resolved to an ordinary family - toggle 1's table work. Proceed regardless of
                    // what else the building contains; the station test is not consulted at all. This
                    // is the actual fix: a resolvable ordinary recipe must never be denied because of
                    // what else the building holds.
                    proceed = true;
                }
            }
            else
            {
                // Family could not be resolved for the shared quest as a whole - v0.13.0 decides per
                // ITEM instead of per building whenever the station's own projects can be read at all
                // (BuildDeniedItemNames), which is what actually fixes the ordinary-table starvation:
                // a workshop's shared fetch quest no longer denies materials wanted by BOTH an ordinary
                // project and a transform project just because the two disagree on blueprint class.
                if (SafeGetBool(Plugin.StockTransformStationMaterials, false))
                {
                    proceed = true;
                    route = "station-fallback";
                    routeDetail = bpFailReason.Length > 0 ? bpFailReason : "unresolved";
                    Plugin.Logger.LogInfo($"[CFS] [CFS-SS] StockTransformStationMaterials=true - proceeding " +
                        $"to stock physical-transform station villager={villagerName} station={stationName}.");
                }
                else
                {
                    var denied = BuildDeniedItemNames(station, out int ordinaryProjects, out int transformProjects);
                    if (ordinaryProjects + transformProjects > 0)
                    {
                        proceed = true;
                        route = "per-item";
                        deniedItems = denied;
                        routeDetail = $"ordinary={ordinaryProjects} transform={transformProjects} denied={deniedItems.Count}";
                    }
                    else
                    {
                        // Nothing at the station could be read - fall back to today's whole-building
                        // behavior exactly, which is correct: it's the only signal left once the
                        // per-item one is unavailable. The stationProbe line
                        // CraftTransfer.IsSkippedStation(CraftingStation) emits is only reached here.
                        route = "station-fallback";
                        routeDetail = bpFailReason.Length > 0 ? bpFailReason : "unresolved";
                        proceed = !CraftTransfer.IsSkippedStation(station);
                    }
                }
            }

            if (Plugin.TransferDiagnostics.Value)
            {
                _routeLogged.TryGetValue(stationName, out int routeCount);
                if (routeCount < 5)
                {
                    _routeLogged[stationName] = routeCount + 1;
                    Plugin.Logger.LogInfo($"[CFS] [CFS-SS] routeDecision station={stationName} route={route} " +
                        $"detail={routeDetail} outcome={(proceed ? "proceed" : "skip")}");
                }
            }

            if (!proceed) return;

            // Step 7: CALL GetNeededSuppliesManifest(), never patch it - ItemManifest return type is
            // the inventory-family patch-crash risk this project avoids (FetchQuestSuppression.cs line
            // 161 already calls it the same way).
            ItemManifest? manifest = null;
            try { manifest = quest.GetNeededSuppliesManifest(); }
            catch (Exception ex) { Plugin.Logger.LogError($"[CFS] [CFS-SS] GetNeededSuppliesManifest error: {ex}"); return; }

            // Step 8: the AGGREGATE manifest (every queued craft at the station) - kept as the fallback
            // quantity source and as the aggQty diagnostic below; no longer the primary source fed into
            // Step 10's shortfall loop (see BuildOneCraftPlan's v0.14.0 header comment).
            var aggregatePairs = CraftTransfer.EnumerateManifest(manifest);
            if (aggregatePairs.Count == 0) return;

            // v0.14.0: one craft's worth per project, replacing the aggregate as the DEFAULT quantity
            // fed into the shortfall loop below (confirmed in-game 2026-07-30 that the aggregate
            // over-stocks a bin to hard capacity and then starves the one part the active recipe
            // needs). Falls back to the aggregate whenever no plan could be built at all.
            var plan = BuildOneCraftPlan(quest, station, out string planSource);
            List<(ItemInfo info, int qty)> pairs;
            if (plan != null && plan.Count > 0)
            {
                // v0.14.1: cap the plan by the game's own fetch request (aggregatePairs) - confirmed
                // in-game 2026-07-30 that an uncapped plan delivered up to 80 of an item when the
                // fetch manifest asked for 1 (plan=projects planQty=80 aggQty=1). The plan must never
                // deliver an item the fetch manifest didn't request, nor more than one craft's worth.
                var aggByName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                foreach (var (info, qty) in aggregatePairs)
                {
                    string name = SafeName(info);
                    aggByName.TryGetValue(name, out int existingQty);
                    aggByName[name] = existingQty + qty;
                }

                var filteredPlan = new List<(ItemInfo info, int qty)>();
                foreach (var (info, qty) in plan)
                {
                    string name = SafeName(info);
                    if (!aggByName.TryGetValue(name, out int cap)) continue;
                    int cappedQty = Math.Min(qty, cap);
                    if (cappedQty <= 0) continue;
                    filteredPlan.Add((info, cappedQty));
                }

                if (filteredPlan.Count > 0)
                {
                    pairs = filteredPlan;
                }
                else
                {
                    // The game asked only for things the plan doesn't cover - vanilla's request is
                    // then the safer guide. The deniedItems filter in the Step 10 loop below still
                    // guards the hardwood items on this path.
                    pairs = aggregatePairs;
                    planSource = "aggregate-nooverlap";
                }
            }
            else
            {
                pairs = aggregatePairs;
                planSource = "aggregate";
            }

            int planQty = 0; foreach (var (_, qty) in pairs) planQty += qty;
            int aggQty = 0; foreach (var (_, qty) in aggregatePairs) aggQty += qty;

            // Step 9.
            ItemCollection? stationInv = null;
            try { stationInv = station.GetInventory(); } catch (Exception ex) { Plugin.Logger.LogError($"[CFS] [CFS-SS] GetInventory error: {ex}"); }
            if (stationInv == null) return;

            // Step 10: shortfall against the STATION inventory only (not the villager's own inventory -
            // she hasn't picked anything up yet, this fires the moment the fetch quest STARTS).
            // v0.13.0: items denied by the per-item route (deniedItems, populated only on the
            // per-item route above) are skipped here rather than folded into the shortfall - they are
            // wanted exclusively by a transform project and toggle 2 is off.
            var shortfall = new List<(ItemInfo info, int missing)>();
            int deniedSkipped = 0;
            foreach (var (info, qty) in pairs)
            {
                string itemName = SafeName(info);
                if (deniedItems.Contains(itemName))
                {
                    deniedSkipped++;
                    if (Plugin.TransferDiagnostics.Value)
                    {
                        string deniedKey = itemName + "|" + stationName;
                        _deniedItemLogged.TryGetValue(deniedKey, out int deniedCount);
                        if (deniedCount < 5)
                        {
                            _deniedItemLogged[deniedKey] = deniedCount + 1;
                            Plugin.Logger.LogInfo($"[CFS] [CFS-SS] DENIED ITEM '{itemName}' station={stationName} " +
                                $"villager={villagerName} - wanted only by a transform project and toggle 2 is off.");
                        }
                    }
                    continue;
                }

                int have = 0;
                try { have = stationInv.GetItemQuantity(info); } catch { }
                int missing = qty - have;
                if (missing > 0) shortfall.Add((info, missing));
            }
            if (shortfall.Count == 0) return;

            // Step 11.
            var (itemsMoved, qtyMoved, stillShort) = CraftTransfer.StockStation(shortfall, stationInv, villagerName, stationName);

            // Step 12: one unconditional summary line - this is the run's success metric, not behind
            // TransferDiagnostics. v0.9.2 appends bpClass so a run's worth of STOCKED lines tells us,
            // in one grep, whether ordinary crafting-table recipes report CraftBlueprintInfo or
            // WorkshopBlueprintInfo (currently unknown) - "?" when the resolution chain came up empty.
            // v0.9.3 appends stationType (purely diagnostic, see ResolveStationType's own comment).
            Plugin.Logger.LogInfo($"[CFS] [CFS-SS] STOCKED villager={villagerName} station={stationName} " +
                $"stationObj={stationObjName} wanted={pairs.Count} short={shortfall.Count} itemsMoved={itemsMoved} " +
                $"qtyMoved={qtyMoved} stillShort={stillShort} bpClass={(bpClass.Length > 0 ? bpClass : "?")} " +
                $"stationType={ResolveStationType(station)} denied={deniedSkipped} " +
                $"plan={planSource} planQty={planQty} aggQty={aggQty}");
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"[CFS] [CFS-SS] StationStocker.HandleFetchStateEnter error: {ex}");
        }
    }

    // Same shape FetchQuestSuppression.ResolveStationName already uses.
    private static string ResolveStationName(CraftingStation station)
    {
        try
        {
            string? n = station.GetName();
            if (!string.IsNullOrEmpty(n)) return n!;
        }
        catch { }
        return Plugin.NativeClassName(station);
    }

    // v0.9.1: the station's own GameObject name, independent of ResolveStationName's building-level
    // display name (station.GetName(), which returns the containing building's name - e.g. "Workshop
    // House 4" - for every station inside it). Added so a specific workstation (a carpenter's table,
    // say) can be told apart from the building it sits in. CraftingStation derives from MonoBehaviour
    // through its base chain, so .gameObject is available directly; other code in this project reads
    // GameObject.name off a component the same way (Patches/VillagerFetchPatches.cs,
    // IsWhitelistedByStoragePatch's site-name block).
    private static string ResolveStationObjectName(CraftingStation station)
    {
        try { return station.gameObject.name; }
        catch { return "?"; }
    }

    // Same defensive config-read helper shape FetchQuestSuppression.SafeGetBool uses.
    private static bool SafeGetBool(ConfigEntry<bool>? entry, bool fallback)
    {
        try { return entry?.Value ?? fallback; } catch { return fallback; }
    }
}
