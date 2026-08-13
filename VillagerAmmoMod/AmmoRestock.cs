using System;
using System.Collections.Generic;
using System.Text;
using SandSailorStudio.Inventory;

namespace VillagerAmmoMod;

// v1.1.0 (RestockFromStorage): moves arrows from settlement storage into a villager's quiver
// container. Precise per-slot removal, mirroring CraftFromStorageMod/CraftTransfer.cs's
// RemoveFromContainer - ItemContainer has no bulk RemoveItems(ItemInfo,int) overload, only the
// Item-instance-based RemoveItem(item, count, ItemEventContext.Default).
//
// v1.1.1: this method does NOT invalidate SettlementStock's snapshot - it decrements the
// candidate's Qty in place instead (see the loop below) so later candidates in the SAME pass stay
// accurate without a full re-walk. The CALLER (AmmoTracker.RunRestockPassCore) owns invalidation,
// once per whole pass, not once per villager served.
internal static class AmmoRestock
{
    internal static int TryRestock(ItemContainer quiver, ItemInfo info, int want, out string sourceSummary)
    {
        int remaining = want;
        var sources = new List<(string label, int qty)>();

        try
        {
            var candidates = SettlementStock.GetCandidates(info);
            foreach (var candidate in candidates)
            {
                if (remaining <= 0) break;
                if (candidate?.Container == null) continue;

                int take = Math.Min(remaining, candidate.Qty);
                if (take <= 0) continue;

                int removed = 0;
                try { removed = RemoveFromContainer(candidate.Container, info, take); }
                catch (Exception ex) { Plugin.Logger.LogError($"[VillagerAmmo] AmmoRestock RemoveFromContainer error: {ex}"); }
                if (removed <= 0) continue;

                int added = 0;
                try { added = quiver.AddItems(info, removed); }
                catch (Exception ex) { Plugin.Logger.LogError($"[VillagerAmmo] AmmoRestock AddItems error: {ex}"); }

                if (added < removed)
                {
                    int giveBack = removed - added;
                    try { candidate.Container.AddItems(info, giveBack); }
                    catch (Exception ex) { Plugin.Logger.LogError($"[VillagerAmmo] AmmoRestock give-back error: {ex}"); }
                }

                if (added <= 0) continue;

                remaining -= added;
                sources.Add((candidate.StructureName, added));

                // v1.1.1: decrement the snapshot in place instead of invalidating it. GetCandidates
                // returns a new List holding references to the SAME ContainerStock objects the
                // snapshot owns, so this keeps later candidates in this pass accurate without a full
                // re-walk. Decrement by `added`, not `removed` - the give-back above already returned
                // the difference to this container.
                candidate.Qty -= added;
                if (candidate.Qty < 0) candidate.Qty = 0;
            }
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"[VillagerAmmo] AmmoRestock.TryRestock error: {ex}");
        }

        int moved = want - remaining;

        sourceSummary = BuildSourceSummary(sources);
        return moved;
    }

    private static string BuildSourceSummary(List<(string label, int qty)> sources)
    {
        if (sources.Count == 0) return "(none)";
        var sb = new StringBuilder();
        int shown = Math.Min(3, sources.Count);
        for (int i = 0; i < shown; i++)
        {
            if (i > 0) sb.Append(", ");
            sb.Append(sources[i].label).Append(" x").Append(sources[i].qty);
        }
        if (sources.Count > 3) sb.Append(", ...");
        return sb.ToString();
    }

    // Precise per-container removal (CraftFromStorageMod/CraftTransfer.cs RemoveFromContainer,
    // reproduced here) - ItemContainer has no bulk RemoveItems(ItemInfo,int) overload, only Item-
    // instance-based RemoveItem - so walk this container's own slots (bounded by capacity, same
    // pattern SettlementStock uses), matching by ItemInfo.id (never managed ==/is on interop
    // objects - project-wide gotcha), removing from each matching stack until satisfied.
    private static int RemoveFromContainer(ItemContainer container, ItemInfo info, int qty)
    {
        int remaining = qty;
        try
        {
            int targetId = info.id;
            int capacity = 0;
            try { capacity = container.capacity; } catch { }
            int bound = capacity > 0 ? capacity : 64;

            var items = container.GetItems();
            var matching = new List<Item>();
            for (int slot = 0; slot < bound; slot++)
            {
                Item? it = null;
                try { it = items != null ? items[slot] : null; } catch { break; }
                if (it == null) continue;
                try { if (it.info == null || it.info.id != targetId) continue; } catch { continue; }
                matching.Add(it);
            }

            foreach (var item in matching)
            {
                if (remaining <= 0) break;
                int itemCount = 0;
                try { itemCount = item.count; } catch { }
                if (itemCount <= 0) continue;
                int take = Math.Min(remaining, itemCount);

                bool ok = false;
                try { ok = container.RemoveItem(item, take, ItemEventContext.Default); }
                catch (Exception ex) { Plugin.Logger.LogError($"[VillagerAmmo] RemoveFromContainer RemoveItem error: {ex}"); }
                if (ok) remaining -= take;
            }
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"[VillagerAmmo] RemoveFromContainer error: {ex}");
        }
        return qty - remaining;
    }
}
