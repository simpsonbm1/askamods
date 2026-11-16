using System;
using System.Collections.Generic;
using SandSailorStudio.Inventory;
using SSSGame;
using UnityEngine;

namespace ItemCountAuditMod;

// v0.3.0: the MANIFEST walk. The corrupt experiment (2026-09-09, this machine) showed that after the
// game evicts a below-zero stack from its container, the manifest error keeps firing while the
// container walk reports nothing. So a negative lives in an ItemManifest (the game's ItemInfo->int
// tally) held by a building or the settlement, not in any Item. Cpp2IL field census of persistent
// holders (diffable-cs, SSSGame): Settlement._searchManifest / _settlementResourcesManifestCache /
// _availabilityManifest; FarmingStation._storageLayoutManifest; FarmingOutlet.SeedsComplainingManifest;
// every IResourceStorageSite.GetItemManifest() (FarmingStation, ResourceStorage, AnimalPen,
// Bloomstation, CookingStation, CraftingStation, DiningStation, Kennel, Marketplace, DismantlePile).
// Quest promise manifests (per-villager, transient) are NOT walked here.
internal static class ManifestScan
{
    internal readonly struct BadEntry
    {
        public readonly string Where; public readonly ItemManifest Manifest; public readonly ItemInfo Info; public readonly int Quantity;
        public BadEntry(string where, ItemManifest m, ItemInfo info, int q) { Where = where; Manifest = m; Info = info; Quantity = q; }
    }

    internal static void Scan(List<BadEntry>? collect, ref int bad, ref int manifestsWalked, ref int entriesWalked)
    {
        Settlement? settlement = ItemCountScan.ResolveSettlementPublic(out _);
        if (settlement == null) return;

        // Settlement-level tallies (private fields are exposed by the interop wrapper).
        Check("settlement._searchManifest", Safe(() => settlement._searchManifest), collect, ref bad, ref manifestsWalked, ref entriesWalked);
        Check("settlement._settlementResourcesManifestCache", Safe(() => settlement._settlementResourcesManifestCache), collect, ref bad, ref manifestsWalked, ref entriesWalked);
        Check("settlement._availabilityManifest", Safe(() => settlement._availabilityManifest), collect, ref bad, ref manifestsWalked, ref entriesWalked);

        Il2CppSystem.Collections.Generic.List<Structure>? structures = null;
        try { structures = settlement.GetStructures(); } catch { }
        if (structures == null) return;

        foreach (var st in structures)
        {
            if (st == null) continue;
            string owner = "?";
            try { owner = st.name ?? "?"; } catch { }
            Transform? t = null;
            try { t = st.transform; } catch { }
            if (t == null) continue;

            // Farms: the storage-layout manifest tracks what the seed/harvest storages hold.
            var farms = new List<FarmingStation>();
            try { ItemCountScan.CollectComponentsPublic(t, farms, 0); } catch { }
            foreach (var farm in farms)
            {
                if (farm == null) continue;
                Check($"'{owner}' FarmingStation._storageLayoutManifest", Safe(() => farm._storageLayoutManifest), collect, ref bad, ref manifestsWalked, ref entriesWalked);
                Check($"'{owner}' FarmingStation.GetItemManifest()", Safe(() => farm.GetItemManifest()), collect, ref bad, ref manifestsWalked, ref entriesWalked);
            }
            var outlets = new List<FarmingOutlet>();
            try { ItemCountScan.CollectComponentsPublic(t, outlets, 0); } catch { }
            foreach (var o in outlets)
            {
                if (o == null) continue;
                Check($"'{owner}' FarmingOutlet.SeedsComplainingManifest", Safe(() => o.SeedsComplainingManifest), collect, ref bad, ref manifestsWalked, ref entriesWalked);
            }

            // Every other storage site: GetItemManifest() is IResourceStorageSite's, dispatched natively.
            var stores = new List<ResourceStorage>();
            try { ItemCountScan.CollectComponentsPublic(t, stores, 0); } catch { }
            foreach (var rs in stores)
            {
                if (rs == null) continue;
                Check($"'{owner}' ResourceStorage.GetItemManifest()", Safe(() => rs.GetItemManifest()), collect, ref bad, ref manifestsWalked, ref entriesWalked);
            }
            // Workstation subclasses that are storage sites. Base-typed casts lie through interop, so
            // each is probed by its exact component type.
            CheckSite<CookingStation>(t, owner, collect, ref bad, ref manifestsWalked, ref entriesWalked);
            CheckSite<CraftingStation>(t, owner, collect, ref bad, ref manifestsWalked, ref entriesWalked);
            CheckSite<DiningStation>(t, owner, collect, ref bad, ref manifestsWalked, ref entriesWalked);
            CheckSite<Bloomstation>(t, owner, collect, ref bad, ref manifestsWalked, ref entriesWalked);
            CheckSite<AnimalPen>(t, owner, collect, ref bad, ref manifestsWalked, ref entriesWalked);
            CheckSite<Kennel>(t, owner, collect, ref bad, ref manifestsWalked, ref entriesWalked);
        }
    }

    private static void CheckSite<T>(Transform t, string owner, List<BadEntry>? collect, ref int bad, ref int walked, ref int entries) where T : Component
    {
        var list = new List<T>();
        try { ItemCountScan.CollectComponentsPublic(t, list, 0); } catch { }
        foreach (var c in list)
        {
            if (c == null) continue;
            ItemManifest? m = null;
            try
            {
                switch (c)
                {
                    case CookingStation x: m = x.GetItemManifest(); break;
                    case CraftingStation x: m = x.GetItemManifest(); break;
                    case DiningStation x: m = x.GetItemManifest(); break;
                    case Bloomstation x: m = x.GetItemManifest(); break;
                    case AnimalPen x: m = x.GetItemManifest(); break;
                    case Kennel x: m = x.GetItemManifest(); break;
                }
            }
            catch { }
            Check($"'{owner}' {typeof(T).Name}.GetItemManifest()", m, collect, ref bad, ref walked, ref entries);
        }
    }

    private static ItemManifest? Safe(Func<ItemManifest?> f) { try { return f(); } catch { return null; } }

    private static void Check(string where, ItemManifest? m, List<BadEntry>? collect, ref int bad, ref int walked, ref int entries)
    {
        if (m == null) return;
        walked++;
        Il2CppSystem.Collections.Generic.List<ItemInfoQuantity>? items = null;
        try { items = m.GetItems(); } catch { }
        if (items == null) return;
        int n = 0;
        try { n = items.Count; } catch { }
        for (int i = 0; i < n; i++)
        {
            ItemInfoQuantity? iq = null;
            try { iq = items[i]; } catch { break; }
            if (iq == null) continue;
            entries++;
            ItemInfo? info = null; int q = 0;
            try { info = iq.itemInfo; q = iq.quantity; } catch { continue; }
            string name = "?";
            try { name = info?.name ?? "?"; } catch { }
            if (q < 0)
            {
                bad++;
                Plugin.Logger.LogWarning($"[ItemCountAudit] BAD MANIFEST: {where} item='{name}' quantity={q}");
                if (collect != null && info != null) collect.Add(new BadEntry(where, m, info, q));
            }
            else if (Plugin.LogEveryItem.Value)
            {
                Plugin.Logger.LogInfo($"[ItemCountAudit]   manifest {where} item='{name}' quantity={q}");
            }
        }
    }

    // Repair a manifest entry with the manifest's own RemoveAll(ItemInfo), which deletes the key.
    internal static void Repair(List<BadEntry> bad)
    {
        foreach (var e in bad)
        {
            string name = "?";
            try { name = e.Info.name ?? "?"; } catch { }
            try
            {
                e.Manifest.RemoveAll(e.Info);
                int after = int.MinValue;
                try { after = e.Manifest.GetQuantity(e.Info); } catch { }
                Plugin.Logger.LogWarning($"[ItemCountAudit] REPAIR manifest: {e.Where} item='{name}' was {e.Quantity}; RemoveAll done, GetQuantity now {after}.");
            }
            catch (Exception ex) { Plugin.Logger.LogError($"[ItemCountAudit] REPAIR manifest {e.Where} '{name}': {ex}"); }
        }
    }
}
