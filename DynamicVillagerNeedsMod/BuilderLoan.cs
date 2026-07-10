using System;
using Il2CppInterop.Runtime.InteropTypes;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using SSSGame;
using SSSGame.AI;
using SSSGame.UI;   // ScheduleType

namespace DynamicVillagerNeedsMod;

// Shared "lend a villager to the builder pool without touching their UI-visible job assignment" recipe —
// the exact FULL_LOAN sequence BuilderDiag's v1.6.1 hotkey experiment proved in-game (2026-07-09):
// release the villager's home-station task agent (their home quests leave the QuestRunner; deliberately
// NOT RemoveFromTaskDatas on the home station — the task checkboxes/VillagersInCharge must survive), join
// the settlement's default Buildstation for real (SetTaskAgent + AddToTaskDatas), then paint an all-Work
// schedule so nothing else competes for the villager's dispatch. Restore reverses all three steps and
// puts the pre-lend schedule back. Used by BOTH NeedsController's OffWindowFill=Builder feature and
// BuilderDiag's hotkey diagnostic (its FULL_LOAN variant), so there is exactly ONE lend/restore
// implementation to keep correct — see CLAUDE.md's IL2CPP interop gotchas for the boxing/rewrap patterns
// used throughout (Villager/Workstation/Buildstation compile through the unity-libs stub chain, so
// `.Pointer` needs boxing via Il2CppObjectBase; Villager doesn't managed-implement ITaskAgent, so it needs
// an explicit `new ITaskAgent(pointer)` rewrap).
internal static class BuilderLoan
{
    // Everything a Restore() call needs to reverse a successful Lend(). Immutable once created.
    internal sealed class State
    {
        public ITaskAgent Agent = null!;
        public Workstation? HomeWorkstation;
        public Buildstation Buildstation = null!;
        public int[]? ScheduleSnapshot;
    }

    // Attempts the full lend sequence. Returns null (with a human-readable failReason) on ANY failure —
    // callers decide what to do next (NeedsController falls back to Leisure fill; BuilderDiag just logs
    // and aborts the experiment start).
    internal static State? Lend(Villager villager, Buildstation buildstation, Workstation? homeWorkstation, out string failReason)
    {
        failReason = "";
        try
        {
            var agent = new ITaskAgent(PtrOf(villager));
            var snapshot = SnapshotSchedule(villager);

            bool releasedHome = true; // no home workstation = nothing to release, not a failure
            if (homeWorkstation != null)
            {
                try { releasedHome = homeWorkstation.ReleaseTaskAgent(agent); }
                catch (Exception ex) { failReason = $"home ReleaseTaskAgent threw: {ex.Message}"; return null; }
            }
            if (!releasedHome)
            {
                failReason = "home ReleaseTaskAgent returned false";
                return null;
            }

            bool joined;
            try { joined = buildstation.SetTaskAgent(agent); }
            catch (Exception ex) { failReason = $"buildstation SetTaskAgent threw: {ex.Message}"; return null; }
            if (!joined)
            {
                failReason = "buildstation SetTaskAgent returned false";
                // Best-effort: the villager was already released from home — try to put them back
                // rather than leave them attached nowhere.
                if (homeWorkstation != null) { try { homeWorkstation.SetTaskAgent(agent); } catch { } }
                return null;
            }

            try { buildstation.AddToTaskDatas(villager); }
            catch (Exception ex) { failReason = $"AddToTaskDatas threw: {ex.Message}"; return null; }

            try { WriteSchedule(villager, (int)ScheduleType.Work); }
            catch (Exception ex)
            {
                // Best-effort: leave the villager joined to the buildstation — a failed schedule write
                // shouldn't strand them mid-transition, and the caller treats this whole Lend as a
                // failure anyway, so a subsequent attempt (or the normal schedule-write path) can retry.
                failReason = $"schedule write failed: {ex.Message}";
                return null;
            }

            return new State { Agent = agent, HomeWorkstation = homeWorkstation, Buildstation = buildstation, ScheduleSnapshot = snapshot };
        }
        catch (Exception ex)
        {
            failReason = ex.ToString();
            return null;
        }
    }

    // Reverses Lend(). Returns false (with a failReason) on any failure — the caller must NOT treat the
    // villager as restored in that case (a villager must never be left silently loaned).
    internal static bool Restore(Villager villager, State state, out string failReason)
    {
        failReason = "";
        try
        {
            try { state.Buildstation.RemoveFromTaskDatas(villager); }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[DynamicNeeds] BuilderLoan restore: RemoveFromTaskDatas threw: {ex.Message}"); }

            bool released;
            try { released = state.Buildstation.ReleaseTaskAgent(state.Agent); }
            catch (Exception ex) { failReason = $"buildstation ReleaseTaskAgent threw: {ex.Message}"; return false; }
            if (!released) { failReason = "buildstation ReleaseTaskAgent returned false"; return false; }

            if (state.HomeWorkstation != null)
            {
                bool homeOk;
                try { homeOk = state.HomeWorkstation.SetTaskAgent(state.Agent); }
                catch (Exception ex) { failReason = $"home SetTaskAgent threw: {ex.Message}"; return false; }
                if (!homeOk) { failReason = "home SetTaskAgent returned false"; return false; }
            }

            if (state.ScheduleSnapshot != null && state.ScheduleSnapshot.Length > 0)
            {
                try { WriteSchedule(villager, state.ScheduleSnapshot); }
                catch (Exception ex) { failReason = $"schedule restore failed: {ex.Message}"; return false; }
            }

            return true;
        }
        catch (Exception ex)
        {
            failReason = ex.ToString();
            return false;
        }
    }

    internal static int[]? SnapshotSchedule(Villager villager)
    {
        var live = villager._schedule;
        if (live == null) return null;
        var snap = new int[live.Length];
        for (int h = 0; h < live.Length; h++) snap[h] = live[h];
        return snap;
    }

    internal static void WriteSchedule(Villager villager, int uniformValue)
    {
        int hours = Villager.scheduleMaxHourCount;
        if (hours <= 0)
        {
            var existing = villager._schedule;
            hours = existing != null ? existing.Length : 24;
        }
        var arr = new Il2CppStructArray<int>(hours);
        for (int h = 0; h < hours; h++) arr[h] = uniformValue;
        WriteScheduleArray(villager, arr);
    }

    internal static void WriteSchedule(Villager villager, int[] hours)
    {
        var arr = new Il2CppStructArray<int>(hours.Length);
        for (int h = 0; h < hours.Length; h++) arr[h] = hours[h];
        WriteScheduleArray(villager, arr);
    }

    private static void WriteScheduleArray(Villager villager, Il2CppStructArray<int> arr)
    {
        long packed = villager._ScheduleToNetworkSchedule(arr);
        villager.overrideSchedule = false;
        villager.Rpc_ChangeSchedule(packed);
    }

    // Villager/Workstation/Buildstation compile through the unity-libs MonoBehaviour stub chain, so
    // `.Pointer` isn't directly accessible — box through `object` first (CLAUDE.md "managed casts lie").
    internal static IntPtr PtrOf(object o) => o is Il2CppObjectBase b ? b.Pointer : IntPtr.Zero;
}
