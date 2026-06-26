using System;
using HarmonyLib;
using SSSGame.AI;

namespace TreeRespawnMod.Patches;

// Optional diagnostic (gated by config TreeRespawn/EnableDiagnostics, default off). When a
// GatherAndHarvest worker (woodcutter/forager/etc.) goes idle, this tells us WHY: "NoResourcesFound"
// = it searched and found no allowed target (e.g. firewood prioritized while stumps are protected — a
// benign work-priority issue, NOT a mod softlock; confirmed in-game 2026-06-26), vs silence = it's
// stuck in some other (likely vanilla) routine unrelated to this mod. Kept in for future debugging.
internal static class WorkerIdleDiag
{
    static DateTime _last = DateTime.MinValue;

    internal static void Log(string what)
    {
        try
        {
            if (!Plugin.EnableDiagnostics.Value) return;
            var now = DateTime.UtcNow;
            if ((now - _last).TotalSeconds < 1.5) return; // light throttle so it can't flood the log
            _last = now;
            Plugin.Logger.LogInfo($"[TreeRespawnMod] DIAG worker idle complaint: {what}");
        }
        catch { }
    }
}

[HarmonyPatch(typeof(GatherAndHarvestQuest.GatherAndHarvestData),
    nameof(GatherAndHarvestQuest.GatherAndHarvestData.ComplainNoResourcesFound))]
internal static class ComplainNoResourcesDiag
{
    static void Postfix(bool value)
    {
        if (value) WorkerIdleDiag.Log("NoResourcesFound (searched, found no allowed target → starved, not broken)");
    }
}

[HarmonyPatch(typeof(GatherAndHarvestQuest.GatherAndHarvestData),
    nameof(GatherAndHarvestQuest.GatherAndHarvestData.ComplainNoGatherTask))]
internal static class ComplainNoTaskDiag
{
    static void Postfix(bool value)
    {
        if (value) WorkerIdleDiag.Log("NoGatherTask (no task assigned)");
    }
}
