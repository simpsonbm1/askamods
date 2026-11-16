using System;
using System.Collections.Generic;
using SandSailorStudio.Inventory;
using SSSGame;
using UnityEngine;

namespace ItemCountAuditMod;

// Ctrl+F3 REPAIR (the handout build, v0.4.0): for every stack with count <= 0 found by the audit
// walk, call the game's own ItemContainer.RemoveItem(item, item.count). Cpp2IL of RemoveItem: it
// subtracts the quantity (a negative minus itself = 0), then takes the count==0 branch that removes
// the item from the container list, unregisters it, and fires the removal events. Confirmed in-game
// 2026-09-10: `RemoveItem(item, -1) returned True; count readback=0`, error spam stopped.
// The Shift+F3 CORRUPT chord that drove a stack to -1 for that experiment was REMOVED before the
// build was handed to anyone (user ruling 2026-09-10); the experiment is recorded in
// docs/mods/item-count-audit.md.
// Taking the stack out of the container by hand also clears it (same eviction path, confirmed
// in-game 2026-09-10), so repair is a convenience, not the only route.
internal static class Experiment
{
    internal static void Repair()
    {
        Plugin.Logger.LogWarning("[ItemCountAudit] REPAIR requested (Ctrl+F3).");
        var bad = new List<(string where, Item item)>();
        ItemCountScan.CollectBad(bad);
        Plugin.Logger.LogWarning($"[ItemCountAudit] REPAIR: {bad.Count} stack(s) with count<=0 to hand to ItemContainer.RemoveItem.");
        var seen = new HashSet<long>();
        foreach (var (where, it) in bad)
        {
            // A container under a nested structure is reached from both parents' transforms, so the
            // same Item can be listed twice. Repair each native object once.
            long ptr = 0;
            try { ptr = ((Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase)(object)it).Pointer.ToInt64(); } catch { }
            if (ptr != 0 && !seen.Add(ptr)) continue;
            int count = 0; string itemName = "?";
            try { count = it.count; } catch { }
            try { itemName = it.info?.name ?? "?"; } catch { }
            ItemContainer? container = null;
            try { container = it.Container; } catch { }
            if (container == null)
            {
                Plugin.Logger.LogWarning($"[ItemCountAudit] REPAIR skip: {where} item='{itemName}' count={count} has no Container.");
                continue;
            }
            bool ok = false;
            try { ok = container.RemoveItem(it, count); }
            catch (Exception ex) { Plugin.Logger.LogError($"[ItemCountAudit] REPAIR RemoveItem threw for {where} '{itemName}': {ex}"); continue; }
            int readback = int.MinValue;
            try { readback = it.count; } catch { }
            Plugin.Logger.LogWarning($"[ItemCountAudit] REPAIR: {where} item='{itemName}' count was {count}; "
                                     + $"RemoveItem(item, {count}) returned {ok}; count readback={readback}.");
        }
        var badManifests = new List<ManifestScan.BadEntry>();
        int bm = 0, mw = 0, me = 0;
        try { ManifestScan.Scan(badManifests, ref bm, ref mw, ref me); }
        catch (Exception ex) { Plugin.Logger.LogError($"[ItemCountAudit] REPAIR manifest scan: {ex}"); }
        Plugin.Logger.LogWarning($"[ItemCountAudit] REPAIR: {badManifests.Count} negative manifest entr(ies) to RemoveAll.");
        ManifestScan.Repair(badManifests);
        ItemCountScan.Run("post-repair");
    }
}
