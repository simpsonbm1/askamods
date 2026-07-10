using System;
using System.Collections.Generic;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes;
using SSSGame;

namespace DynamicVillagerNeedsMod.Patches;

// PreserveScheduleInSaves (v1.7.0): DVN only restores a villager's real painted schedule at app-quit
// (NeedsController.RestoreAll). Quitting to the main menu (or any autosave) while a needs-driven collapse
// write (all-Work/all-Sleep/all-Leisure, or the OffWindowFill=Builder loan's all-Work write) is still live
// permanently bakes that collapse over the player's painted schedule into the save file — confirmed bug
// (destroyed the user's staggered cook schedules). Fix: for the duration of the native Serialize call
// (the thing that actually copies _schedule into the save's DataObject), swap _schedule's CONTENTS to the
// painted snapshot NeedsController already tracks (the exact same source RestoreAll uses — see
// NeedsController.GetPaintedSnapshotForSave / _scheduleHours), then swap the displaced (collapsed) values
// straight back in Postfix — so from every OTHER caller's point of view _schedule never changed for the
// rest of this frame, and NeedsController's own applied-packed idempotency tracking stays valid.
//
// v1.8.0: the actual guarded swap now lives in the shared ScheduleMask core (also used by
// SchedulePanelDisplayPatch for the schedule UI). This patch still keeps its OWN per-native-pointer
// dictionary (rather than a Harmony __state) because the key must be computed BEFORE calling
// ScheduleMask.Begin() — if PtrOf ever failed we must not have mutated _schedule with no way to restore
// it, so the pointer is resolved first and Begin() is only invoked once we know we can record the
// restore target.
[HarmonyPatch(typeof(Villager), nameof(Villager.Serialize))]
internal static class VillagerScheduleSavePatch
{
    // Displaced (collapsed) values, per villager, between Prefix and Postfix — keyed by native pointer
    // rather than a single static slot in case multiple villagers serialize in one pass (a whole
    // settlement saves in one sweep).
    private static readonly Dictionary<IntPtr, int[]> s_displaced = new();

    private static int s_maskedCount;   // session counter, logged at debug level from Postfix
    private static bool s_fireVerified; // one-time "it actually fired and masked something" confirmation
    private static bool s_errorLogged;  // one-time failure note so a broken patch doesn't spam every save

    static void Prefix(Villager __instance)
    {
        if (!Plugin.PreserveScheduleInSaves.Value) return;
        try
        {
            if (__instance == null) return;
            IntPtr key = PtrOf(__instance);
            if (key == IntPtr.Zero) return;

            var displaced = ScheduleMask.Begin(__instance);
            if (displaced == null) return;

            s_displaced[key] = displaced;

            s_maskedCount++;
            if (!s_fireVerified)
            {
                s_fireVerified = true;
                string name = "?";
                try { name = __instance.GetName(); } catch { }
                Plugin.Logger.LogInfo($"[DynamicNeeds] schedule mask applied for {name} during save");
            }
        }
        catch (Exception ex)
        {
            if (!s_errorLogged)
            {
                s_errorLogged = true;
                Plugin.Logger.LogError($"[DynamicNeeds] schedule save-mask prefix failed (restoring nothing): {ex}");
            }
        }
    }

    static void Postfix(Villager __instance)
    {
        if (!Plugin.PreserveScheduleInSaves.Value) return;
        try
        {
            if (__instance == null) return;
            IntPtr key = PtrOf(__instance);
            if (key == IntPtr.Zero || !s_displaced.TryGetValue(key, out var displaced)) return;
            s_displaced.Remove(key);

            ScheduleMask.End(__instance, displaced);

            if (Plugin.DebugLogging.Value)
                Plugin.Logger.LogInfo($"[DynamicNeeds] schedule mask session count={s_maskedCount}");
        }
        catch (Exception ex)
        {
            if (!s_errorLogged)
            {
                s_errorLogged = true;
                Plugin.Logger.LogError($"[DynamicNeeds] schedule save-mask postfix failed: {ex}");
            }
        }
    }

    // Villager compiles through the unity-libs MonoBehaviour stub chain (CLAUDE.md "managed casts lie")
    // so `.Pointer` isn't directly accessible — box through `object` first.
    private static IntPtr PtrOf(object o) => o is Il2CppObjectBase b ? b.Pointer : IntPtr.Zero;
}
