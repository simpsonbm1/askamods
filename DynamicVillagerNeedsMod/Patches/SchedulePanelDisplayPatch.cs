using System;
using HarmonyLib;
using SSSGame;
using SSSGame.UI;

namespace DynamicVillagerNeedsMod.Patches;

// ShowIntendedScheduleInUI (v1.8.0): the villager schedule UI panel reads the villager's LIVE _schedule,
// which NeedsController temporarily collapses to a uniform activity while driving needs-based behavior
// (all-Work during a builder loan, all-Leisure during off-window fill, etc. — see NeedsController's class
// header). Without this patch, opening the panel on a villager mid-collapse showed the mod's temporary
// operating schedule instead of the player's painted intent — read by players as "my schedule got wiped",
// even though the save patch (VillagerScheduleSavePatch) and the eventual restore both correctly preserve
// it (confirmed in-game). This patch masks the panel's reads the same way, for the DURATION of each call:
// Prefix swaps the painted snapshot in via the shared ScheduleMask core (also used by the save patch — see
// ScheduleMask.cs), Postfix swaps the collapse back out. Using a per-invocation Harmony __state makes
// nesting safe automatically: if Set() internally calls Refresh(), the inner Begin() sees live == paint
// (already masked by the outer call) and returns null, so the inner Postfix is a no-op and only the
// outermost call restores.
//
// Deliberately NOT patched: UpdateSchedule() (the apply/write path) and ResetSchedule() — those are where
// the PLAYER writes a new paint, and that write must land on the real live _schedule so NeedsController's
// own universal player-edit adoption (RefreshPaintedSnapshot, 5 s cadence) picks it up.
//
// Fire-verification (IL2CPP can silently inline small/single-caller methods, killing a patch without any
// error — CLAUDE.md gotcha): each of the four patched methods logs, once per session, which one masked a
// schedule first. OnEnable is the likeliest survivor (a Unity lifecycle message; GroundItemVacuum's own
// OnEnable patch is confirmed working), so if only that line ever fires, the other three are worth
// re-checking with Cecil for inlining.
internal static class SchedulePanelMaskLog
{
    private static bool s_setFired, s_refreshFired, s_enableFired, s_hasChangesFired;

    internal static void FireOnce(ref bool flag, Villager? v, string method)
    {
        if (flag) return;
        flag = true;
        try
        {
            string name = "?";
            if (v != null) { try { name = v.GetName(); } catch { } }
            Plugin.Logger.LogInfo($"[DynamicNeeds] schedule panel mask applied for {name} (first via {method})");
        }
        catch { }
    }

    internal static void Set(Villager? v) => FireOnce(ref s_setFired, v, "Set");
    internal static void Refresh(Villager? v) => FireOnce(ref s_refreshFired, v, "Refresh");
    internal static void OnEnable(Villager? v) => FireOnce(ref s_enableFired, v, "OnEnable");
    internal static void HasUnappliedChanges(Villager? v) => FireOnce(ref s_hasChangesFired, v, "HasUnappliedChanges");

    internal static Villager? SafeVillager(VillagerSchedulePanel? p)
    {
        try { return p != null ? p._v : null; } catch { return null; }
    }
}

[HarmonyPatch(typeof(VillagerSchedulePanel), nameof(VillagerSchedulePanel.Set))]
internal static class SchedulePanelSetPatch
{
    static void Prefix(Villager v, out int[]? __state)
    {
        __state = null;
        try
        {
            if (!Plugin.ShowIntendedScheduleInUI.Value) return;
            __state = ScheduleMask.Begin(v);
            if (__state != null) SchedulePanelMaskLog.Set(v);
        }
        catch (Exception ex) { Plugin.Logger.LogError($"[DynamicNeeds] schedule panel mask (Set prefix) failed: {ex}"); }
    }

    static void Postfix(Villager v, int[]? __state)
    {
        if (__state == null) return;
        try { ScheduleMask.End(v, __state); }
        catch (Exception ex) { Plugin.Logger.LogError($"[DynamicNeeds] schedule panel mask (Set postfix) failed: {ex}"); }
    }
}

[HarmonyPatch(typeof(VillagerSchedulePanel), nameof(VillagerSchedulePanel.Refresh))]
internal static class SchedulePanelRefreshPatch
{
    static void Prefix(VillagerSchedulePanel __instance, out int[]? __state)
    {
        __state = null;
        try
        {
            if (!Plugin.ShowIntendedScheduleInUI.Value) return;
            var v = SchedulePanelMaskLog.SafeVillager(__instance);
            if (v == null) return;
            __state = ScheduleMask.Begin(v);
            if (__state != null) SchedulePanelMaskLog.Refresh(v);
        }
        catch (Exception ex) { Plugin.Logger.LogError($"[DynamicNeeds] schedule panel mask (Refresh prefix) failed: {ex}"); }
    }

    static void Postfix(VillagerSchedulePanel __instance, int[]? __state)
    {
        if (__state == null) return;
        try { ScheduleMask.End(SchedulePanelMaskLog.SafeVillager(__instance), __state); }
        catch (Exception ex) { Plugin.Logger.LogError($"[DynamicNeeds] schedule panel mask (Refresh postfix) failed: {ex}"); }
    }
}

[HarmonyPatch(typeof(VillagerSchedulePanel), nameof(VillagerSchedulePanel.OnEnable))]
internal static class SchedulePanelOnEnablePatch
{
    static void Prefix(VillagerSchedulePanel __instance, out int[]? __state)
    {
        __state = null;
        try
        {
            if (!Plugin.ShowIntendedScheduleInUI.Value) return;
            var v = SchedulePanelMaskLog.SafeVillager(__instance);
            if (v == null) return;
            __state = ScheduleMask.Begin(v);
            if (__state != null) SchedulePanelMaskLog.OnEnable(v);
        }
        catch (Exception ex) { Plugin.Logger.LogError($"[DynamicNeeds] schedule panel mask (OnEnable prefix) failed: {ex}"); }
    }

    static void Postfix(VillagerSchedulePanel __instance, int[]? __state)
    {
        if (__state == null) return;
        try { ScheduleMask.End(SchedulePanelMaskLog.SafeVillager(__instance), __state); }
        catch (Exception ex) { Plugin.Logger.LogError($"[DynamicNeeds] schedule panel mask (OnEnable postfix) failed: {ex}"); }
    }
}

// HasUnappliedChanges() returns a Boolean — the Postfix deliberately does NOT declare __result. We never
// alter the result itself; masking the live schedule for the duration of the call is what fixes what the
// method's own internal comparison (CompareSchedules against the paint panel) sees.
[HarmonyPatch(typeof(VillagerSchedulePanel), nameof(VillagerSchedulePanel.HasUnappliedChanges))]
internal static class SchedulePanelHasUnappliedChangesPatch
{
    static void Prefix(VillagerSchedulePanel __instance, out int[]? __state)
    {
        __state = null;
        try
        {
            if (!Plugin.ShowIntendedScheduleInUI.Value) return;
            var v = SchedulePanelMaskLog.SafeVillager(__instance);
            if (v == null) return;
            __state = ScheduleMask.Begin(v);
            if (__state != null) SchedulePanelMaskLog.HasUnappliedChanges(v);
        }
        catch (Exception ex) { Plugin.Logger.LogError($"[DynamicNeeds] schedule panel mask (HasUnappliedChanges prefix) failed: {ex}"); }
    }

    static void Postfix(VillagerSchedulePanel __instance, int[]? __state)
    {
        if (__state == null) return;
        try { ScheduleMask.End(SchedulePanelMaskLog.SafeVillager(__instance), __state); }
        catch (Exception ex) { Plugin.Logger.LogError($"[DynamicNeeds] schedule panel mask (HasUnappliedChanges postfix) failed: {ex}"); }
    }
}
