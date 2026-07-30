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

            // v0.10.0 (TRANSFORM_STATION_PLAN.md Change 1): physical-transform station gate - an item
            // placed on the station and struck/dyed rather than drawn from a bin (forge/sawhorses/dye
            // bench by default), so stocking the station inventory does nothing useful. Checked BEFORE
            // the blueprint-class gate below, since it's the cheaper test and doesn't need the
            // quest/project/blueprint chain walk.
            //
            // v0.10.1 (defect fix): v0.10.0 resolved the interaction INLINE here via a single
            // station.gameObject.GetComponent<CraftInteraction>() call and never found anything -
            // confirmed in-game 2026-07-29 (20x "SKIP blueprintClass=ForgingBlueprintInfo" from the
            // gate below, ZERO "SKIP stationClass=" lines, against a carpenter station where
            // AnvilInteraction is the easiest possible match). The resolution now lives in
            // CraftTransfer.IsSkippedStation(CraftingStation) itself, which walks self, every ancestor
            // and every descendant instead of a single GetComponent call, and logs its own rate-
            // limited "[CFS] [CFS-SS] stationProbe" outcome for every station it's asked about (see
            // that method) - this call site no longer needs its own resolution or its own SKIP line.
            //
            // v0.11.0 (TRANSFORM_STATION_PLAN.md Change 2): this early return is now conditional on
            // Plugin.StockTransformStationMaterials (default false). The user LIKES the stocker keeping
            // a transform station's own rack topped up (confirmed in-game 2026-07-29: a carpentry rack
            // holding one hardwood log and one hardwood long stick at a time refilled without the
            // carpenter walking to a warehouse) and wants it kept, but behind a toggle defaulted OFF -
            // so they can verify the toggle actually does something by switching it on. Scoped to the
            // STOCKER ONLY: CraftTransfer.TryReportAvailable and HandleBeginCraftingSequence never
            // consult this toggle and must keep standing aside unconditionally (see the comments at
            // those two sites).
            if (CraftTransfer.IsSkippedStation(station))
            {
                if (!SafeGetBool(Plugin.StockTransformStationMaterials, false)) return;

                Plugin.Logger.LogInfo($"[CFS] [CFS-SS] StockTransformStationMaterials=true - proceeding " +
                    $"to stock physical-transform station villager={villagerName} station={stationName}.");
            }

            // v0.9.2: blueprint-class gate - metalworker/forge and other auxiliary-station recipes
            // (dyeing, painting, study) don't craft from a station bin, so stocking the station
            // inventory for them does nothing useful. bpClass is resolved ONCE here and reused by
            // Part 2's STOCKED line below regardless of which branch this takes.
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
            if (bpClass.Length > 0 && GetSkipClasses().Contains(bpClass))
            {
                string skipKey = bpClass + "|" + stationName;
                _skipLogged.TryGetValue(skipKey, out int skipCount);
                if (skipCount < 10)
                {
                    _skipLogged[skipKey] = skipCount + 1;
                    Plugin.Logger.LogInfo($"[CFS] [CFS-SS] SKIP blueprintClass={bpClass} villager={villagerName} station={stationName}");
                }
                return;
            }

            // Step 7: CALL GetNeededSuppliesManifest(), never patch it - ItemManifest return type is
            // the inventory-family patch-crash risk this project avoids (FetchQuestSuppression.cs line
            // 161 already calls it the same way).
            ItemManifest? manifest = null;
            try { manifest = quest.GetNeededSuppliesManifest(); }
            catch (Exception ex) { Plugin.Logger.LogError($"[CFS] [CFS-SS] GetNeededSuppliesManifest error: {ex}"); return; }

            // Step 8.
            var pairs = CraftTransfer.EnumerateManifest(manifest);
            if (pairs.Count == 0) return;

            // Step 9.
            ItemCollection? stationInv = null;
            try { stationInv = station.GetInventory(); } catch (Exception ex) { Plugin.Logger.LogError($"[CFS] [CFS-SS] GetInventory error: {ex}"); }
            if (stationInv == null) return;

            // Step 10: shortfall against the STATION inventory only (not the villager's own inventory -
            // she hasn't picked anything up yet, this fires the moment the fetch quest STARTS).
            var shortfall = new List<(ItemInfo info, int missing)>();
            foreach (var (info, qty) in pairs)
            {
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
                $"stationType={ResolveStationType(station)}");
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
