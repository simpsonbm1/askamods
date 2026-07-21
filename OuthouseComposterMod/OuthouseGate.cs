using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime.InteropTypes;
using SandSailorStudio.Inventory;
using SSSGame;
using UnityEngine;

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

    // Keyed by the StorageSupply's native pointer, same rationale as _outhouseCache above — used by
    // the v1.1.0 warehouse haul-gate patch (Patches/HaulGatePatches.cs) to identify outhouse-owned
    // supplies. Cleared on world-leave via ClearCache().
    private static readonly Dictionary<IntPtr, bool> _outhouseSupplyCache = new();

    // Keyed by the ResourceOutlet's / IResourceStorageSite's native pointer, same rationale as
    // above — used by the v1.2.0 villager eat-gate patches (Patches/EatGatePatches.cs) to identify
    // outhouse-owned consume-quest whitelist targets. Cleared on world-leave via ClearCache().
    private static readonly Dictionary<IntPtr, bool> _outhouseOutletCache = new();
    private static readonly Dictionary<IntPtr, bool> _outhouseSiteCache = new();

    internal static void ClearCache()
    {
        _outhouseCache.Clear();
        _outhouseSupplyCache.Clear();
        _outhouseOutletCache.Clear();
        _outhouseSiteCache.Clear();
    }

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

    // v1.1.0 warehouse haul-gate: identifies whether a StorageSupply (the (structure, item) task
    // dispatcher SSSGame.ResourceStorage gates haul-task creation through) belongs to the outhouse,
    // by walking StorageSupply.OwnerStructure and matching its name the same way ComposterDiag's
    // structure census does. Cached by native pointer — never survives world reload (ClearCache()).
    internal static bool IsOuthouseSupply(StorageSupply? supply)
    {
        if (supply == null) return false;

        IntPtr ptr = IntPtr.Zero;
        try { if ((object)supply is Il2CppObjectBase b) ptr = b.Pointer; } catch { }
        if (ptr == IntPtr.Zero) return false;

        if (_outhouseSupplyCache.TryGetValue(ptr, out var cached)) return cached;

        bool isOuthouse = false;
        try
        {
            var owner = supply.OwnerStructure;
            if (owner != null) isOuthouse = StructureNameMatches(owner);
        }
        catch { }

        _outhouseSupplyCache[ptr] = isOuthouse;
        return isOuthouse;
    }

    // v1.2.0 villager eat-gate: identifies whether a ResourceOutlet (the SatisfyObjectiveQuestData.
    // IsWhitelistedByStorage(ResourceOutlet) target) belongs to the outhouse, via
    // ResourceOutlet.Structure. Cached by native pointer — never survives world reload (ClearCache()).
    internal static bool IsOuthouseOutlet(ResourceOutlet? outlet)
    {
        if (outlet == null) return false;

        IntPtr ptr = IntPtr.Zero;
        try { if ((object)outlet is Il2CppObjectBase b) ptr = b.Pointer; } catch { }
        if (ptr == IntPtr.Zero) return false;

        if (_outhouseOutletCache.TryGetValue(ptr, out var cached)) return cached;

        bool isOuthouse = false;
        try
        {
            var structure = outlet.Structure;
            if (structure != null) isOuthouse = StructureNameMatches(structure);
        }
        catch { }

        _outhouseOutletCache[ptr] = isOuthouse;
        return isOuthouse;
    }

    // v1.2.0 villager eat-gate: identifies whether an IResourceStorageSite (the
    // IsWhitelistedByStorage(IResourceStorageSite) / IsWhitelistedByAny(IResourceStorageSite)
    // target) belongs to the outhouse. IResourceStorageSite carries no direct Structure reference —
    // resolve via GetTransform() + a manual parent walk (project-wide gotcha: plural/
    // GetComponentInParent variants are unreliable through the interop trampoline; per-node singular
    // GetComponent<T>() is the proven pattern). Cached by native pointer — never survives world
    // reload (ClearCache()).
    internal static bool IsOuthouseSite(IResourceStorageSite? rss)
    {
        if (rss == null) return false;

        IntPtr ptr = IntPtr.Zero;
        try { if ((object)rss is Il2CppObjectBase b) ptr = b.Pointer; } catch { }
        if (ptr == IntPtr.Zero) return false;

        if (_outhouseSiteCache.TryGetValue(ptr, out var cached)) return cached;

        bool isOuthouse = false;
        try
        {
            var structure = ResolveOwningStructure(rss);
            if (structure != null) isOuthouse = StructureNameMatches(structure);
        }
        catch { }

        _outhouseSiteCache[ptr] = isOuthouse;
        return isOuthouse;
    }

    // Manual parent walk from a storage site's Transform up to the first ancestor (or self)
    // carrying a Structure component. Bounded at 30 levels as a safety net against an unexpectedly
    // deep/cyclic hierarchy. Also used by the eat-gate patches to resolve an owner display name for
    // logging.
    internal static Structure? ResolveOwningStructure(IResourceStorageSite? rss)
    {
        if (rss == null) return null;
        try
        {
            Transform? t = rss.GetTransform();
            int depth = 0;
            while (t != null && depth < 30)
            {
                Structure? s = null;
                try { s = t.GetComponent<Structure>(); } catch { }
                if (s != null) return s;
                try { t = t.parent; } catch { t = null; }
                depth++;
            }
        }
        catch { }
        return null;
    }

    // Shared by IsOuthouseSupply/IsOuthouseOutlet/IsOuthouseSite: case-insensitive substring match
    // of Plugin.StructureNameMatch against the structure's display name OR its DefaultName.
    private static bool StructureNameMatches(Structure s)
        => StructureMatchesFilter(s, Plugin.StructureNameMatch.Value);

    // Locale-proof structure identity (v1.3.0). The outhouse building is matched by the
    // StructureNameMatch token ("Outhouse"). Display name (GetName/StructureName) AND DefaultName both
    // resolve through LocalizationManager, so in a non-English game they read e.g. "Plumpsklo" and the
    // English token matched nothing — the converter resolved 0 outhouse containers and never composted
    // (the non-English "accepts food but never converts" report). The Unity template asset name
    // (Template.name = "Item_Structures_OuthouseL1") and the gameObject name ("OuthouseL1(Clone)") are
    // dev-authored, English-derived and never translate. Matching all four keeps the English token
    // working in every language. Verified additive against the v0.3.0 locale audit: in English the
    // outhouse's default/display name already contains "Outhouse", so English behaviour is unchanged.
    // Shared by the acceptance/haul/eat gates (here) and the converter's container resolver
    // (ComposterDiag.StructureMatches routes to this).
    internal static bool StructureMatchesFilter(Structure s, string filter)
    {
        if (string.IsNullOrEmpty(filter)) return false;
        try { var n = SafeStructureName(s); if (n.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0) return true; } catch { }
        try { var d = SafeStructureDefaultName(s); if (d.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0) return true; } catch { }
        try { var t = s.Template; var a = t != null ? t.name : null; if (!string.IsNullOrEmpty(a) && a.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0) return true; } catch { }
        try { var go = s.gameObject; var g = go != null ? go.name : null; if (!string.IsNullOrEmpty(g) && g.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0) return true; } catch { }
        return false;
    }

    // DefaultName is the pristine type name (survives player renames); StructureName/GetName() is
    // the display name. Mirrors ComposterDiag's SafeName/SafeDefaultName (private to that class).
    // Internal (not private) since v1.2.0's haul-gate/eat-gate logging riders reuse it to resolve an
    // owning structure's display name for their log lines.
    internal static string SafeStructureName(Structure s)
    {
        try { var n = s.GetName(); if (!string.IsNullOrEmpty(n)) return n; } catch { }
        try { var n = s.StructureName; if (!string.IsNullOrEmpty(n)) return n; } catch { }
        return "?";
    }

    private static string SafeStructureDefaultName(Structure s)
    {
        try { return s.DefaultName ?? "?"; } catch { return "?"; }
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
            if (cat == null) return false;
            foreach (var needle in ParseCsv(Plugin.FoodCategoryMatch.Value))
            {
                if (CatNameOrAssetContains(cat, needle)) return true;
            }
        }
        catch { }
        return false;
    }

    // --- Locale-proof identity matching (v1.3.0) --------------------------------------------------
    // Display names (.Name) resolve through LocalizationManager, so an English config token only
    // matches when the game runs in English — the root cause of the non-English "only accepts seeds /
    // never makes compost" reports. The Unity asset name (.name, lowercase) is the dev-authored,
    // English-derived id ("Categ_Resources_Food", "Item_Junk_Compost") and never translates. Matching
    // BOTH keeps the English default token working in every language. Verified ADDITIVE against the
    // v0.3.0 locale audit (1121 items): in English no category/item has an asset-name match without an
    // existing display-name match, so English behaviour is unchanged. See docs/architecture.md.
    internal static bool CatNameOrAssetContains(ItemCategoryInfo? cat, string token)
    {
        if (cat == null || string.IsNullOrEmpty(token)) return false;
        try { var n = cat.Name; if (!string.IsNullOrEmpty(n) && n.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0) return true; } catch { }
        try { var a = cat.name; if (!string.IsNullOrEmpty(a) && a.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0) return true; } catch { }
        return false;
    }

    // Identity of a SPECIFIC named item (the produced Compost). The display name stays an EXACT match
    // (English behaviour unchanged); the asset name is a substring test because the asset id carries a
    // prefix ("Item_Junk_Compost" contains the token "Compost").
    internal static bool ItemIsNamed(ItemInfo? info, string token)
    {
        if (info == null || string.IsNullOrEmpty(token)) return false;
        try { if (string.Equals(info.Name, token, StringComparison.OrdinalIgnoreCase)) return true; } catch { }
        try { var a = info.name; if (!string.IsNullOrEmpty(a) && a.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0) return true; } catch { }
        return false;
    }

    internal static bool IsAcceptedInput(ItemInfo? info)
    {
        if (info == null) return false;

        // Never treat the compost item itself as an accepted input.
        try
        {
            if (ItemIsNamed(info, Plugin.CompostItemName.Value))
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
