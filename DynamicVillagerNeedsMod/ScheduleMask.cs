using System;
using SSSGame;

namespace DynamicVillagerNeedsMod;

// Shared masking core (v1.8.0) used by every Harmony patch that needs a villager's LIVE _schedule to read
// back as the player's INTENDED (painted) schedule instead of one of NeedsController's temporary needs-
// driven collapse writes (all-Work during a builder loan, all-Leisure during off-window fill, etc.).
// Originally inline in Patches.VillagerScheduleSavePatch (the save-path mask); factored out here so
// Patches.SchedulePanelDisplayPatch (the schedule UI panel) can reuse the exact same guard.
//
// Begin() swaps the painted snapshot into villager._schedule and hands back the displaced (collapsed)
// values; End() writes them back. Guard logic mirrors NeedsController.GetPaintedSnapshotForSave: masking
// only ever fires when the live array is bit-for-bit the mod's own last write (livePacked ==
// _appliedPacked[surv]) AND differs from the stored paint — so player edits and vanilla (non-DVN-
// controlled) schedules are never touched. Callers use per-invocation state (a Harmony `__state`, or a
// local variable) rather than shared statics, which makes nested calls safe automatically: if an outer
// call already masked the schedule, an inner call's Begin() sees live == paint (already masked) and
// returns null, so the inner End() is a no-op and only the outermost call restores.
internal static class ScheduleMask
{
    private static bool s_beginErrorLogged;
    private static bool s_endErrorLogged;

    // Returns the displaced (pre-mask) per-hour values if masking was applied — the caller must pass them
    // to End() once its read/serialize is done — or null if nothing was masked (nothing to restore).
    internal static int[]? Begin(Villager v)
    {
        try
        {
            if (v == null) return null;
            var controller = NeedsController.Instance;
            if (controller == null) return null;

            VillagerSurvival? surv = null;
            try { surv = v.GetSurvival(); } catch { }
            if (surv == null) return null;
            try { if (!surv._hasAuthority) return null; } catch { }

            var live = v._schedule;
            if (live == null) return null;

            var liveManaged = new int[live.Length];
            for (int h = 0; h < live.Length; h++) liveManaged[h] = live[h];

            long livePacked;
            try { livePacked = v._ScheduleToNetworkSchedule(live); } catch { return null; }

            var snap = controller.GetPaintedSnapshotForSave(surv, liveManaged, livePacked);
            if (snap == null) return null; // no snapshot, an unadopted player edit is live, or nothing to mask

            int n = Math.Min(live.Length, snap.Length);
            for (int h = 0; h < n; h++) live[h] = snap[h];

            return liveManaged;
        }
        catch (Exception ex)
        {
            if (!s_beginErrorLogged)
            {
                s_beginErrorLogged = true;
                Plugin.Logger.LogError($"[DynamicNeeds] schedule mask begin failed (masking nothing): {ex}");
            }
            return null;
        }
    }

    // Writes the displaced values back. No-op when `displaced` is null (Begin() found nothing to mask).
    internal static void End(Villager? v, int[]? displaced)
    {
        if (displaced == null) return;
        try
        {
            if (v == null) return;
            var live = v._schedule;
            if (live == null) return;

            int n = Math.Min(live.Length, displaced.Length);
            for (int h = 0; h < n; h++) live[h] = displaced[h];
        }
        catch (Exception ex)
        {
            if (!s_endErrorLogged)
            {
                s_endErrorLogged = true;
                Plugin.Logger.LogError($"[DynamicNeeds] schedule mask end failed (restoring nothing): {ex}");
            }
        }
    }
}
