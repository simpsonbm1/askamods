using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using BepInEx.Configuration;
using SandSailorStudio.Inventory;
using SSSGame;
using SSSGame.AI;
using SSSGame.AI.FSM;

namespace CraftFromStorageMod;

// v0.8.0 (idea-17 Phase 2b): shared logic for Patches/FetchQuestPatches.cs. The v0.7.0/v0.7.1
// villager availability widening (Point A, CraftTransfer.TryReportAvailable) did NOT stop the
// villager's supply walk - confirmed in-game 2026-07-21 (gate widened 5619 times, the test villager
// still took 4 supply trips at 20-45s each, modPulls=0 on every cycle, villager Point C/D paths never
// fired). Conclusion: CheckOwnedRequirements does not schedule the villager fetch quest - the walk is
// decided by the fetch quest's own GetPriority score against competing quests. This file suppresses
// that score whenever the mod can already cover the quest's own needed-supplies manifest from
// settlement storage, so the AI scheduler (hopefully) skips the fetch quest and the villager crafts
// immediately - the just-in-time pull already built in v0.7.0 (Point C,
// CraftTransfer.HandleBeginCraftingSequence) then supplies her at craft time, exactly as it already
// does for the player.
//
// It is NOT known whether a lower GetPriority value actually prevents quest selection in this game's
// AI scheduler, or what scale "priority" runs on - that is exactly what the rate-limited PRIORITY
// OBSERVE logging below (Change 2) is for. Whatever the in-game verdict, the suppression itself fails
// safe: it only ever fires when settlement storage covers the ENTIRE needed-supplies manifest (see
// HandleGetPriority below), so a villager who genuinely needs something the mod cannot supply is
// always left free to go fetch it.
internal static class FetchQuestSuppression
{
    // ---- one-time PATCH ALIVE marking (own HashSet, own tag - deliberately NOT
    // VillagerFetchTrace.MarkAlive, which hardcodes the "[CFS-P2]" tag that file's own header
    // reserves for Patches/VillagerFetchPatches.cs's READ-ONLY spike output specifically. This
    // feature mutates __result, so it gets its own "[CFS-FQ]" tag to stay greppable on its own and
    // not be confused with the untouched read-only spike.) ----
    private static readonly HashSet<string> _aliveLogged = new();

    internal static void MarkAlive(string target)
    {
        try
        {
            if (_aliveLogged.Add(target))
                Plugin.Logger.LogInfo($"[CFS] [CFS-FQ] PATCH ALIVE {target}");
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"[CFS] [CFS-FQ] FetchQuestSuppression.MarkAlive error: {ex}");
        }
    }

    // ---- Change 2: rate-limited vanilla-priority learning (GateRollup pattern, Patches/
    // GatePatches.cs - first N raw lines, then a periodic idle-flush rollup of min/max/count).
    // Ticked from CraftWatcher.Update() via FetchQuestSuppression.Tick() below, matching exactly how
    // GateRollup.Tick() is already wired in from the same Update(). ----
    private static class FetchPriorityRollup
    {
        private const long FlushIdleMs = 1500;
        private const int MaxRawLogs = 20;

        private static int _totalCalls;
        private static int _rawLogged;
        private static float _minPriority = float.MaxValue;
        private static float _maxPriority = float.MinValue;
        private static int _suppressedCount;
        private static readonly Dictionary<string, int> _perQuestTypeCounts = new();
        private static long _lastCallMs = -1;
        private static readonly Stopwatch _sw = new();

        // Returns true if THIS observation should also get its own raw log line (first MaxRawLogs
        // per idle-flush burst - matches GateRollup's own per-burst, not lifetime, cap).
        internal static bool Record(string questType, float vanillaPriority, bool suppressed)
        {
            try
            {
                if (!_sw.IsRunning) _sw.Restart();
                _lastCallMs = _sw.ElapsedMilliseconds;
                _totalCalls++;
                if (vanillaPriority < _minPriority) _minPriority = vanillaPriority;
                if (vanillaPriority > _maxPriority) _maxPriority = vanillaPriority;
                if (suppressed) _suppressedCount++;
                _perQuestTypeCounts.TryGetValue(questType, out int c);
                _perQuestTypeCounts[questType] = c + 1;

                if (_rawLogged < MaxRawLogs) { _rawLogged++; return true; }
                return false;
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError($"[CFS] [CFS-FQ] FetchPriorityRollup.Record error: {ex}");
                return false;
            }
        }

        // Called every frame from CraftWatcher.Update(), same wiring as GatePatches.cs
        // GateRollup.Tick(). Flushes once FlushIdleMs pass with no further GetPriority calls.
        internal static void Tick()
        {
            if (_totalCalls == 0) return;
            if (!_sw.IsRunning) return;
            if (_sw.ElapsedMilliseconds - _lastCallMs < FlushIdleMs) return;
            Flush();
        }

        private static void Flush()
        {
            try
            {
                string breakdown = string.Join(", ", _perQuestTypeCounts.Select(kv => $"{kv.Key}={kv.Value}"));
                Plugin.Logger.LogInfo($"[CFS] [CFS-FQ] PRIORITY rollup: {_totalCalls} call(s) min={_minPriority:F1} " +
                    $"max={_maxPriority:F1} suppressed={_suppressedCount} types=[{breakdown}]");
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError($"[CFS] [CFS-FQ] FetchPriorityRollup.Flush error: {ex}");
            }
            finally
            {
                _totalCalls = 0;
                _rawLogged = 0;
                _minPriority = float.MaxValue;
                _maxPriority = float.MinValue;
                _suppressedCount = 0;
                _perQuestTypeCounts.Clear();
                _sw.Reset();
                _lastCallMs = -1;
            }
        }
    }

    // Called every frame from CraftWatcher.Update() alongside GateRollup.Tick() and
    // VillagerAvailabilityRollup.Tick().
    internal static void Tick()
    {
        try { FetchPriorityRollup.Tick(); }
        catch (Exception ex) { Plugin.Logger.LogError($"[CFS] [CFS-FQ] FetchQuestSuppression.Tick error: {ex}"); }
    }

    // ---- shared postfix body for both HarmonyPatch classes in Patches/FetchQuestPatches.cs.
    // `instance` is declared CrafterFetchQuest here - CrafterSpecificFetchQuest IS-A CrafterFetchQuest
    // (an ordinary safe upcast of an already-correctly-typed Harmony __instance parameter, NOT the
    // "managed casts lie for a base-DECLARED type" gotcha, which only bites DOWNcasts of objects
    // materialized under a compile-time base type). ----
    internal static void HandleGetPriority(CrafterFetchQuest instance, string questTypeName, QuestData questData, ref float __result)
    {
        MarkAlive($"{questTypeName}.GetPriority");

        try
        {
            if (!SafeGetBool(Plugin.EnableForVillagers, true)) return;
            if (!CraftTransfer.IsHostOrSolo()) return;

            float vanillaPriority = __result;
            bool suppressed = false;
            float suppressedTo = 0f;

            // Step 2: CALL GetNeededSuppliesManifest(), never patch it - ItemManifest return type is
            // the inventory-adjacent family this project treats as a patch-crash risk; calling is safe.
            ItemManifest? manifest = null;
            try { manifest = instance.GetNeededSuppliesManifest(); }
            catch (Exception ex)
            {
                Plugin.Logger.LogError($"[CFS] [CFS-FQ] {questTypeName}.GetNeededSuppliesManifest() error: {ex}");
            }

            var pairs = CraftTransfer.EnumerateManifest(manifest);
            if (pairs.Count > 0)
            {
                bool covered = true;
                foreach (var (info, qty) in pairs)
                {
                    int avail = SettlementStock.GetAvailableQuantity(info);
                    if (avail < qty) { covered = false; break; }
                }

                if (covered)
                {
                    suppressedTo = Plugin.FetchQuestSuppressedPriority.Value;
                    __result = suppressedTo;
                    suppressed = true;
                }
            }
            // pairs.Count == 0 (manifest null, empty, or nothing with positive quantity): __result is
            // left completely untouched, per design - nothing for the mod to claim credit for.

            // Change 2: rate-limited observation line. Suppression fields ride along on the SAME
            // rate-limited line (rather than an unbounded second line) - GetPriority is called at
            // least as often as CheckOwnedRequirements (which alone produced 5619 lines in one
            // session, the exact spam Change 3 elsewhere in this build fixes), so a second unbounded
            // "log every suppression" line would reintroduce the identical problem. The periodic
            // PRIORITY rollup still reports the aggregate suppressed=N count beyond the raw-line cap,
            // so no suppression activity is silently lost, just not each individually logged forever.
            bool logRaw = FetchPriorityRollup.Record(questTypeName, vanillaPriority, suppressed);
            if (logRaw)
            {
                string villagerName = ResolveVillagerName(questData);
                string stationName = ResolveStationName(instance);
                Plugin.Logger.LogInfo($"[CFS] [CFS-FQ] PRIORITY OBSERVE type={questTypeName} villager={villagerName} " +
                    $"station={stationName} vanillaPriority={vanillaPriority:F2} suppressed={suppressed}" +
                    (suppressed ? $" suppressedTo={suppressedTo:F1}" : ""));
            }
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"[CFS] [CFS-FQ] FetchQuestSuppression.HandleGetPriority error: {ex}");
        }
    }

    // Villager identity: questData arrives base-typed as QuestData (managed casts lie for interop
    // objects materialized under a base declared type - project-wide gotcha), so this identifies the
    // REAL native class first and only rewraps when it actually matches - the same pattern
    // Patches/VillagerFetchPatches.cs's own LogQuestDataEnrichment already uses for the identical
    // CrafterFetchQuestData type. That method is private to its own file (which this change must not
    // touch), so this is a separate but equivalent implementation, not a copy-modify of the protected
    // file.
    //
    // Deliberately uses VillagerFetchTrace.SafeVillagerNameFromVillager(Villager?) rather than either
    // helper literally named in the design spec (CraftTransfer.SafeVillagerName(IInteractionAgent?) /
    // VillagerFetchTrace.SafeVillagerName(IFSMBehaviourController?)): neither accepts what's actually
    // available at THIS call site. GetPriority(QuestData) exposes no IInteractionAgent or
    // IFSMBehaviourController - only, after the rewrap below, a Villager. SafeVillagerNameFromVillager
    // reads the identical gameObject.name off the Villager component that the other two helpers read
    // off their own source objects, so its output still correlates with the [CFS-P2] CYCLE SUMMARY
    // villager=<name> field whenever it's the same GameObject (the expected case, per
    // VillagerFetchTrace.cs's own documented assumption). Falls back to "?" on any failure, per spec.
    private static string ResolveVillagerName(QuestData? questData)
    {
        try
        {
            if (questData == null) return "?";
            string nativeClass = Plugin.NativeClassName(questData);
            if (nativeClass != "CrafterFetchQuestData") return "?";

            IntPtr ptr = VillagerFetchTrace.SafePointer(questData);
            if (ptr == IntPtr.Zero) return "?";

            var cfqd = new CrafterFetchQuest.CrafterFetchQuestData(ptr);
            Villager? villager = null;
            try { villager = cfqd.GetVillager(); } catch { }
            return VillagerFetchTrace.SafeVillagerNameFromVillager(villager);
        }
        catch { return "?"; }
    }

    private static string ResolveStationName(CrafterFetchQuest instance)
    {
        try
        {
            var station = instance.craftingStation;
            if (station == null) return "?";
            try
            {
                string? n = station.GetName();
                if (!string.IsNullOrEmpty(n)) return n!;
            }
            catch { }
            return Plugin.NativeClassName(station);
        }
        catch { return "?"; }
    }

    private static bool SafeGetBool(ConfigEntry<bool>? entry, bool fallback)
    {
        try { return entry?.Value ?? fallback; } catch { return fallback; }
    }
}
