using System;
using HarmonyLib;
using SSSGame;
using SSSGame.AI;

namespace SupplyChainMod.Patches;

// WidgetWatcher fire-verification (Phase 0, read-only) — postfixes SettlementIssueTrackerWidget's
// own push methods so the polling-based delta detection (WidgetWatcher.Poll) has an independent
// confirmation that these events actually fired and when. All four params are safe types
// (Workstation, HarvestMarkerComplaint — neither is in the inventory-family crash list), so these
// are always patched (not gated by PatchVillagerComplaints — that gate is for ComplaintPatches
// only). Always try/caught; a station's display name resolution failing never blocks the log line.
internal static class WidgetPatchLog
{
    internal static void Log(string what, Workstation? ws, string extra = "")
    {
        try
        {
            string structName = "?";
            try { structName = Common.SafeName(ws?.GetStructure()); } catch { }
            string ts = DateTime.UtcNow.ToString("HH:mm:ss.fff");
            Plugin.Logger.LogInfo($"[SupplyChain] [widget-event] {what} station='{structName}'{extra} t={ts}");
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"[SupplyChain] WidgetPatchLog error: {ex}");
        }
    }
}

[HarmonyPatch(typeof(SettlementIssueTrackerWidget), nameof(SettlementIssueTrackerWidget.AddStorageFullComplaint))]
internal static class AddStorageFullComplaintPatch
{
    static void Postfix(Workstation workstation)
    {
        WidgetPatchLog.Log("AddStorageFullComplaint", workstation);

        // v0.4.0 Phase 2a — feed WarehouseWatch's storage-full registry. Own try/catch so a
        // registry failure never blocks the fire-verification log line above. Extract strings
        // immediately — never store the Workstation/Structure reference (project-wide gotcha).
        try
        {
            var st = workstation?.GetStructure();
            WarehouseWatch.NoteStorageFull(Common.PosKey(st), Common.SafeName(st));
        }
        catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] WarehouseWatch.NoteStorageFull error: {ex}"); }
    }
}

[HarmonyPatch(typeof(SettlementIssueTrackerWidget), nameof(SettlementIssueTrackerWidget.RemoveStorageFullComplaint))]
internal static class RemoveStorageFullComplaintPatch
{
    static void Postfix(Workstation workstation)
    {
        WidgetPatchLog.Log("RemoveStorageFullComplaint", workstation);

        try
        {
            var st = workstation?.GetStructure();
            WarehouseWatch.NoteStorageFullCleared(Common.PosKey(st));
        }
        catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] WarehouseWatch.NoteStorageFullCleared error: {ex}"); }
    }
}

[HarmonyPatch(typeof(SettlementIssueTrackerWidget), nameof(SettlementIssueTrackerWidget.AddMarkerComplaint))]
internal static class AddMarkerComplaintPatch
{
    static void Postfix(Workstation workstation, HarvestMarkerComplaint complaint)
    {
        string itemName = "?";
        try { itemName = Common.SafeItemName(complaint?.markerInfo); } catch { }
        WidgetPatchLog.Log("AddMarkerComplaint", workstation, $" marker='{itemName}'");
    }
}

// Note: the game's own method name has the typo "Complain" (not "Complaint") — this is the exact
// native method name, not a mistake here.
[HarmonyPatch(typeof(SettlementIssueTrackerWidget), nameof(SettlementIssueTrackerWidget.AddMineExhaustedComplain))]
internal static class AddMineExhaustedComplainPatch
{
    static void Postfix(Workstation workstation) => WidgetPatchLog.Log("AddMineExhaustedComplain", workstation);
}
