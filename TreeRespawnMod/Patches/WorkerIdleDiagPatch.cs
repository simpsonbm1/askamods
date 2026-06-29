using System;
using System.Text;
using HarmonyLib;
using SandSailorStudio.Inventory;
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
    // finderManifest is WHAT the worker was searching for — naming it here is what lets us tell a real
    // "nothing exists nearby" starve apart from "fixated on an unavailable higher-priority resource
    // while something else gatherable sits right there" (see TREERESPAWN_HANDOFF.md Issue C).
    static void Postfix(GatherAndHarvestQuest.GatherAndHarvestData __instance, bool value, ItemManifest finderManifest)
    {
        if (!value || !Plugin.EnableDiagnostics.Value) return;
        try
        {
            string who = __instance?.GetVillager()?.GetName() ?? "?";
            string wanted = DescribeManifest(finderManifest);
            WorkerIdleDiag.Log($"NoResourcesFound: '{who}' wants {wanted} (searched, found no allowed target → starved, not broken)");
        }
        catch { }
    }

    private static string DescribeManifest(ItemManifest manifest)
    {
        if (manifest == null) return "(no manifest)";
        try
        {
            var items = manifest.GetItems();
            if (items == null || items.Count == 0) return "(empty manifest)";
            var sb = new StringBuilder();
            int shown = 0;
            for (int i = 0; i < items.Count; i++)
            {
                if (shown >= 5) { sb.Append(", ..."); break; }
                if (shown > 0) sb.Append(", ");
                var iq = items[i];
                sb.Append(iq.itemInfo != null ? iq.itemInfo.Name : "?").Append('x').Append(iq.quantity);
                shown++;
            }
            return sb.ToString();
        }
        catch { return "(unreadable)"; }
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
