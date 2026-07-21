using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using Il2CppInterop.Runtime.InteropTypes;
using SSSGame;
using SSSGame.AI.FSM;
using UnityEngine;

namespace CraftFromStorageMod;

// Phase 2a (idea-17) villager-fetch spike (v0.6.0): shared read-only tracker/state for
// Patches/VillagerFetchPatches.cs. Answers ONE question from the log alone - does a villager
// crafter walk straight to its station (verdict=DIRECT) or take a supply-run tour through
// FSM_FetchCraftingSupplies first (verdict=TOURED)? Every method here only counts and logs -
// nothing writes inventory, flips a __result, or mutates a villager/quest/storage object.
//
// Per-villager state is keyed by the villager's own GameObject NAME (fsmBehaviour.gameObject.name
// from the FSM patches, villager.gameObject.name from the whitelist patch - the SAME GameObject in
// the expected case, so both readings key into the SAME bucket; project-wide gotcha: key by a
// stable identity, never by a per-chunk-restarting UniqueId). This is a plain string key, not a
// cached interop wrapper, so it carries none of the "never cache interop wrappers of per-world
// objects" risk - but the counters are still dropped on world-leave (ClearWorldState) so a stale
// cycle from a previous world session can never produce a misleading verdict after a reload.
internal static class VillagerFetchTrace
{
    private sealed class CycleState
    {
        internal int FetchEnters;
        internal int StorageProbes;
        internal int Allowed;
        internal int Denied;
        internal readonly HashSet<IntPtr> DistinctSitePointers = new();
        internal readonly HashSet<IntPtr> LoggedSitePointers = new();
        internal float CycleStartTime;

        // v0.7.1: closes the loop with CraftTransfer.cs's [CFS-V] pull lines - how many times did
        // THIS villager's mod-pull actually move something during this cycle, and how many items
        // total. Reset alongside every other counter in FormatAndResetCycle (and wholesale on
        // world-leave via ClearWorldState's _cycles.Clear()).
        internal int ModPulls;
        internal int ModItemsPulled;
    }

    private static readonly Dictionary<string, CycleState> _cycles = new();

    // Fire-verification is process-lifetime, NOT per-world - a patch that has fired once is proven
    // alive for the rest of this game process, so this set is deliberately NOT cleared on
    // world-leave (unlike _cycles below).
    private static readonly HashSet<string> _aliveLogged = new();

    internal static void MarkAlive(string target)
    {
        try
        {
            if (_aliveLogged.Add(target))
                Plugin.Logger.LogInfo($"[CFS-P2] PATCH ALIVE {target}");
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"[CFS-P2] VillagerFetchTrace.MarkAlive error: {ex}");
        }
    }

    // World-leave (wired from CraftWatcher.ClearWorldState, matching every other per-mod tracker in
    // this project). Only the per-cycle counters are dropped - _aliveLogged deliberately survives.
    internal static void ClearWorldState()
    {
        try
        {
            bool hadState = _cycles.Count > 0;
            _cycles.Clear();
            if (hadState)
                Plugin.Logger.LogInfo("[CFS-P2] VillagerFetchTrace: world-leave - cycle counters cleared.");
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"[CFS-P2] VillagerFetchTrace.ClearWorldState error: {ex}");
        }
    }

    internal static string SafeVillagerName(IFSMBehaviourController? fsmBehaviour)
    {
        try
        {
            var go = fsmBehaviour?.gameObject;
            if (go != null) return go.name;
        }
        catch { }
        return "?";
    }

    internal static string SafeVillagerNameFromVillager(Villager? villager)
    {
        try
        {
            var go = villager?.gameObject;
            if (go != null) return go.name;
        }
        catch { }
        return "?";
    }

    // Project-wide boxing pattern (CLAUDE.md gotcha: managed as/is casts lie for interop objects
    // materialized under a base declared type, but (object)x is Il2CppObjectBase b works at runtime
    // regardless, since the compile-time base-chain gap is unity-libs-stub-only). Gives the real
    // native pointer for identity/hex-logging purposes without gambling on a derived-type cast.
    internal static IntPtr SafePointer(object? obj)
    {
        try
        {
            if (obj is Il2CppObjectBase b) return b.Pointer;
        }
        catch { }
        return IntPtr.Zero;
    }

    internal static string SafeHex(object? obj)
    {
        IntPtr p = SafePointer(obj);
        return p == IntPtr.Zero ? "?" : p.ToString("X");
    }

    internal static bool SafeGetBool(ConfigEntry<bool>? entry, bool fallback)
    {
        try { return entry?.Value ?? fallback; } catch { return fallback; }
    }

    internal static int SafeGetInt(ConfigEntry<int>? entry, int fallback)
    {
        try { return entry?.Value ?? fallback; } catch { return fallback; }
    }

    internal static void RecordFetchEnter(string villagerName)
    {
        try { GetOrCreate(villagerName).FetchEnters++; }
        catch (Exception ex) { Plugin.Logger.LogError($"[CFS-P2] VillagerFetchTrace.RecordFetchEnter error: {ex}"); }
    }

    // Always counts (StorageProbes/Allowed/Denied/DistinctSitePointers) regardless of the return
    // value - the return value ONLY gates whether the CALLER should emit a per-probe log line, so
    // the CYCLE SUMMARY totals stay accurate even after the per-site log cap is hit. A site whose
    // pointer didn't resolve (sitePtr == IntPtr.Zero) can't be deduped/rate-limited by identity, so
    // it is always surfaced - rare in practice, since rss is a live interop parameter.
    internal static bool RecordStorageProbe(string villagerName, IntPtr sitePtr, bool allowed, int maxLogsPerCycle)
    {
        try
        {
            var s = GetOrCreate(villagerName);
            s.StorageProbes++;
            if (allowed) s.Allowed++; else s.Denied++;

            if (sitePtr == IntPtr.Zero) return true;

            s.DistinctSitePointers.Add(sitePtr);
            if (s.LoggedSitePointers.Contains(sitePtr)) return false; // already logged this exact site this cycle
            if (s.LoggedSitePointers.Count >= maxLogsPerCycle) return false; // rate limit hit (maxLogsPerCycle<=0 => never log details, still counts)
            s.LoggedSitePointers.Add(sitePtr);
            return true;
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"[CFS-P2] VillagerFetchTrace.RecordStorageProbe error: {ex}");
            return false;
        }
    }

    // v0.7.1: called from CraftTransfer.HandleBeginCraftingSequence when a VILLAGER pull actually
    // moved something (never for the player path, never for a no-op pull - the caller gates that).
    // Closes the loop between CraftTransfer.cs's [CFS-V] per-pull lines and this spike's own CYCLE
    // SUMMARY line, so grepping one villager's name tells the whole story in one filtered view.
    // '?'-safe by design: an unresolved villager name is a deliberate no-op (never attributed into
    // some OTHER real villager's bucket, and never given its own "?" bucket either) - GetOrCreate is
    // only reached once villagerName has already passed the resolved check below.
    internal static void NoteModPull(string villagerName, int itemsPulled)
    {
        try
        {
            if (string.IsNullOrEmpty(villagerName) || villagerName == "?") return;
            if (itemsPulled <= 0) return;
            var s = GetOrCreate(villagerName);
            s.ModPulls++;
            s.ModItemsPulled += itemsPulled;
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"[CFS-P2] VillagerFetchTrace.NoteModPull error: {ex}");
        }
    }

    // Called from FSM_UseCraftingStation.OnStateEnter (target 3) - builds the exact CYCLE SUMMARY
    // line the tester greps for, then resets this villager's counters for the next cycle.
    internal static string FormatAndResetCycle(string villagerName)
    {
        try
        {
            var s = GetOrCreate(villagerName);

            string verdict = s.FetchEnters == 0 ? "DIRECT" : "TOURED";
            float elapsed = Mathf.Max(0f, Time.time - s.CycleStartTime);

            // v0.7.1: modPulls/modItemsPulled appended at the end - baseline fields (fetchEnters
            // through elapsed) are unchanged in content, order, and formatting from v0.6.0.
            string line = $"[CFS-P2] CYCLE SUMMARY villager={villagerName} verdict={verdict} fetchEnters={s.FetchEnters} " +
                $"storageProbes={s.StorageProbes} allowed={s.Allowed} denied={s.Denied} " +
                $"distinctSites={s.DistinctSitePointers.Count} elapsed={elapsed:F1} " +
                $"modPulls={s.ModPulls} modItemsPulled={s.ModItemsPulled}";

            s.FetchEnters = 0;
            s.StorageProbes = 0;
            s.Allowed = 0;
            s.Denied = 0;
            s.DistinctSitePointers.Clear();
            s.LoggedSitePointers.Clear();
            s.CycleStartTime = Time.time;
            s.ModPulls = 0;
            s.ModItemsPulled = 0;

            return line;
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"[CFS-P2] VillagerFetchTrace.FormatAndResetCycle error: {ex}");
            return $"[CFS-P2] CYCLE SUMMARY villager={villagerName} verdict=ERROR";
        }
    }

    private static CycleState GetOrCreate(string villagerName)
    {
        if (!_cycles.TryGetValue(villagerName, out var s))
        {
            s = new CycleState { CycleStartTime = Time.time };
            _cycles[villagerName] = s;
        }
        return s;
    }
}
