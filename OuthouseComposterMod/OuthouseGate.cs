using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime.InteropTypes;
using SandSailorStudio.Inventory;

namespace OuthouseComposterMod;

// Shared classification + outhouse-container identification, used by both the acceptance-gate
// Harmony patches (Patches/AcceptancePatches.cs) and the composter converter (ComposterDiag).
//
// v0.1.2's probe proved: every SmallItem-class raw food/seed item shares the IDENTICAL storageClass
// native pointer as the outhouse containerType's own 'SmallItem' entry (and as Compost, which the
// container DOES accept) — so ItemContainer.CanStoreItemType applies some per-item condition beyond
// storage-class asset identity that we don't need to reverse-engineer. We just override the gates
// for the outhouse's own container.
internal static class OuthouseGate
{
    private const string OuthouseContainerTypeName = "Storage_SmallItems_Outhouse";
    private const string SeedStorageClassName = "SeedItem";

    // Keyed by the container's native pointer (Il2CppObjectBase.Pointer) so repeated Harmony postfix
    // calls (fired constantly by UI/AI code on every container in the game) don't pay a string
    // compare each time. Cleared on world-leave — per-world native wrappers must never survive
    // across sessions (project-wide gotcha) — via ClearCache(), called from
    // ComposterDiag.NoteWorldLeft().
    private static readonly Dictionary<IntPtr, bool> _outhouseCache = new();

    internal static void ClearCache() => _outhouseCache.Clear();

    internal static bool IsOuthouseContainer(ItemContainer? instance)
    {
        if (instance == null) return false;

        IntPtr ptr = IntPtr.Zero;
        try { if ((object)instance is Il2CppObjectBase b) ptr = b.Pointer; } catch { }
        if (ptr == IntPtr.Zero) return false;

        if (_outhouseCache.TryGetValue(ptr, out var cached)) return cached;

        bool isOuthouse = false;
        try
        {
            var ct = instance.containerType;
            isOuthouse = ct != null && ct.name == OuthouseContainerTypeName;
        }
        catch { }

        _outhouseCache[ptr] = isOuthouse;
        return isOuthouse;
    }

    internal static bool IsSeed(ItemInfo? info)
    {
        if (info == null) return false;
        try
        {
            var sc = info.storageClass;
            return sc != null && sc.name == SeedStorageClassName;
        }
        catch { return false; }
    }

    internal static bool IsFood(ItemInfo? info)
    {
        if (info == null) return false;
        if (IsSeed(info)) return false; // seeds and food are mutually exclusive classifications
        try
        {
            var cat = info.category;
            string catName = cat != null ? (cat.Name ?? "") : "";
            if (string.IsNullOrEmpty(catName)) return false;
            foreach (var needle in ParseCsv(Plugin.FoodCategoryMatch.Value))
            {
                if (catName.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
        }
        catch { }
        return false;
    }

    internal static bool IsAcceptedInput(ItemInfo? info)
    {
        if (info == null) return false;

        // Never treat the compost item itself as an accepted input.
        try
        {
            if (string.Equals(info.Name, Plugin.CompostItemName.Value, StringComparison.OrdinalIgnoreCase))
                return false;
        }
        catch { }

        if (Plugin.AcceptFood.Value && IsFood(info)) return true;
        if (Plugin.AcceptSeeds.Value && IsSeed(info)) return true;
        return false;
    }

    // Debug-friendly item description used by the acceptance-gate fire-verification log lines —
    // these double as the category-name discovery data FoodCategoryMatch is tuned from.
    internal static string DescribeItem(ItemInfo? info)
    {
        if (info == null) return "item='?' category='?' class='?'";
        string name = "?", cat = "?", cls = "?";
        try { name = info.Name ?? "?"; } catch { }
        try { cat = info.category != null ? (info.category.Name ?? "?") : "?"; } catch { }
        try { cls = info.storageClass != null ? info.storageClass.name : "?"; } catch { }
        return $"item='{name}' category='{cat}' class='{cls}'";
    }

    private static List<string> ParseCsv(string raw)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(raw)) return result;
        foreach (var part in raw.Split(','))
        {
            var t = part.Trim();
            if (t.Length > 0) result.Add(t);
        }
        return result;
    }
}
