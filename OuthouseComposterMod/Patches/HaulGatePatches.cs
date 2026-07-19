using System;
using System.Collections.Generic;
using HarmonyLib;
using SandSailorStudio.Inventory;
using SSSGame;

namespace OuthouseComposterMod.Patches;

// Warehouse haul-gate ("outhouse contents invisible to haulers, except Compost"), added v1.1.0.
// SSSGame.ResourceStorage.CanCreateStorageTaskForItemInfo is the single bool gate the warehouse
// uses to decide whether to create a "haul to me" task for a given (StorageSupply, ItemInfo) pair.
// Without this patch, warehouse workers haul food/seeds back OUT of the outhouse before the
// composter converts them (Nexus user report). We suppress task creation for outhouse-owned
// supplies unless the item is the compost item itself (compost still needs to be hauled out to
// farmers). Cecil-confirmed 2026-07-19: ResourceStorage has exactly ONE implementation of this
// virtual method in Assembly-CSharp, param names storageSupply/info.
[HarmonyPatch(typeof(ResourceStorage), nameof(ResourceStorage.CanCreateStorageTaskForItemInfo))]
internal static class CanCreateStorageTaskPatch
{
    private static bool _aliveLogged;
    private static readonly HashSet<string> _loggedItems = new();

    static bool Prefix(StorageSupply storageSupply, ItemInfo info, ref bool __result)
    {
        try
        {
            if (!_aliveLogged)
            {
                _aliveLogged = true;
                Plugin.Logger.LogInfo("[OuthouseComposter][haulgate] CanCreateStorageTaskForItemInfo patch alive.");
            }

            if (!Plugin.ProtectOuthouseContents.Value) return true;
            if (!OuthouseGate.IsOuthouseSupply(storageSupply)) return true;

            bool isCompost = false;
            try { isCompost = info != null && string.Equals(info.Name, Plugin.CompostItemName.Value, StringComparison.OrdinalIgnoreCase); }
            catch { }
            if (isCompost) return true;

            __result = false;

            if (Plugin.LogHaulGate.Value)
            {
                string owner = SafeOwnerName(storageSupply);
                string name = SafeName(info);
                if (_loggedItems.Add(owner + "|" + name))
                    Plugin.Logger.LogInfo($"[OuthouseComposter][haulgate] suppressed haul task from outhouse: owner='{owner}' {OuthouseGate.DescribeItem(info)}");
            }

            return false;
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"[OuthouseComposter] CanCreateStorageTaskPatch error: {ex}");
            return true;
        }
    }

    private static string SafeName(ItemInfo? info)
    {
        if (info == null) return "?";
        try { return info.Name ?? "?"; } catch { return "?"; }
    }

    // v1.2.0 logging rider: resolves the owning structure's display name for the suppression log
    // line (SafeStructureName widened to internal in OuthouseGate.cs for this reuse).
    private static string SafeOwnerName(StorageSupply? supply)
    {
        if (supply == null) return "?";
        try
        {
            var owner = supply.OwnerStructure;
            if (owner != null) return OuthouseGate.SafeStructureName(owner);
        }
        catch { }
        return "?";
    }
}
