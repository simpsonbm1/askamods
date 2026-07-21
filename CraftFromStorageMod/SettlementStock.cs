using System;
using System.Collections.Generic;
using System.Linq;
using SandSailorStudio.Inventory;
using SSSGame;
using UnityEngine;

namespace CraftFromStorageMod;

// Phase 1 (idea-17) settlement-wide container stock snapshot, cached with a short TTL. Feeds both
// the availability check (Patches/GatePatches.cs CheckOwnedRequirementsPatch -> CraftTransfer.
// TryReportAvailable) and the actual pull (CraftTransfer.PullShortfall).
//
// Reuses StorageCensus's proven settlement walk (ResolveSettlement / CollectComponents<T> /
// SafeStructureName, widened to internal there) instead of re-implementing traversal - "Reuse its
// machinery... Do not rewrite what already works." Ground truth from that walk (confirmed in-game
// 2026-07-20): 417 structures, 651 containers, 44,670 items.
//
// Exclusion is a container-TYPE-NAME blacklist (Plugin.BlacklistContainerTypes), NOT StorageCensus's
// own structural EquipPoint probe - that probe tagged 0 of 651 containers in-game, so it is a
// confirmed dead-end as an exclusion mechanism here.
//
// Containers are kept by WORLD POSITION (never display name - names repeat across buildings), held
// as live ItemContainer wrappers only for the lifetime of one snapshot (short TTL, invalidated after
// every pull/sweep-back, and dropped entirely on world-leave via ClearWorldState) - project-wide
// gotcha: never cache interop wrappers of per-world objects across world SESSIONS. Holding them for
// a few seconds within the SAME session mirrors CraftWatcher's own WatchSlot pattern.
internal static class SettlementStock
{
    internal sealed class ContainerStock
    {
        internal ItemContainer Container = null!;
        internal Vector3 WorldPos;
        internal string TypeName = "?";
        internal string StructureName = "?";
        internal int Qty;
    }

    private static readonly Dictionary<int, List<ContainerStock>> _byItemId = new();
    private static float _builtAtRealtime = -9999f;
    private static bool _everBuilt;
    private static HashSet<string>? _blacklistCache;
    private static string _blacklistRaw = "";

    // Called after any pull/sweep-back (design point B) so the next read re-walks instead of
    // trusting stale quantities.
    internal static void Invalidate() { _builtAtRealtime = -9999f; }

    // World-leave (CraftWatcher.ClearWorldState, called from Patches/LifecyclePatches.cs
    // PlayerDespawnedPatch) - drops every held ItemContainer wrapper (project-wide gotcha: never
    // cache interop wrappers of per-world objects across sessions).
    internal static void ClearWorldState()
    {
        _byItemId.Clear();
        _builtAtRealtime = -9999f;
        _everBuilt = false;
    }

    // Read-only total across every non-blacklisted container (point A availability check - no
    // mutation, no ledger).
    internal static int GetAvailableQuantity(ItemInfo info)
    {
        EnsureFresh();
        try
        {
            if (_byItemId.TryGetValue(info.id, out var list))
                return list.Sum(c => c.Qty);
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"[CFS] SettlementStock.GetAvailableQuantity error: {ex}");
        }
        return 0;
    }

    // v0.4.0 (idea-17 UI follow-up): READ-ONLY accessor for the availability-UI patches
    // (CraftUiAvailability.cs). Unlike GetAvailableQuantity() above, this NEVER calls EnsureFresh()/
    // Rebuild() - _UpdateAvailablility()/_UpdateAvailabilityStatus() can fire on every container item
    // add/remove for every visible panel, and the settlement walk (651 containers in-game) must never
    // run from a UI postfix. Returns false only when no snapshot has EVER been built yet (caller skips
    // the row); a stale-but-existing snapshot is returned as-is rather than triggering a rebuild.
    internal static bool TryGetCachedQuantity(ItemInfo info, out int qty)
    {
        qty = 0;
        if (!_everBuilt) return false;
        try
        {
            if (_byItemId.TryGetValue(info.id, out var list))
                qty = list.Sum(c => c.Qty);
            return true;
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"[CFS] SettlementStock.TryGetCachedQuantity error: {ex}");
            return false;
        }
    }

    // Pull candidates, largest stockpile first (minimizes the number of distinct source containers
    // touched - and therefore the ledger size / sweep-back complexity - per craft).
    internal static List<ContainerStock> GetCandidates(ItemInfo info)
    {
        EnsureFresh();
        try
        {
            if (_byItemId.TryGetValue(info.id, out var list))
                return list.OrderByDescending(c => c.Qty).ToList();
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"[CFS] SettlementStock.GetCandidates error: {ex}");
        }
        return new List<ContainerStock>();
    }

    private static void EnsureFresh()
    {
        float ttl = 5f;
        try { ttl = Plugin.SnapshotTtlSeconds?.Value ?? 5f; } catch { }
        if (ttl <= 0f) ttl = 5f;
        if (_everBuilt && (Time.realtimeSinceStartup - _builtAtRealtime) < ttl) return;
        Rebuild();
    }

    private static HashSet<string> GetBlacklist()
    {
        string raw = "";
        try { raw = Plugin.BlacklistContainerTypes?.Value ?? ""; } catch { }
        if (_blacklistCache != null && raw == _blacklistRaw) return _blacklistCache;

        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in raw.Split(','))
        {
            var t = part.Trim();
            if (t.Length > 0) set.Add(t);
        }
        _blacklistRaw = raw;
        _blacklistCache = set;
        return set;
    }

    // Same per-structure/per-container traversal shape as StorageCensus.RunCensus's PROVEN walk
    // (GetStructures -> CollectComponents<ItemContainerComponent> -> per-container GetItems() bounded
    // by capacity), just building a queryable dictionary instead of logging.
    private static void Rebuild()
    {
        _byItemId.Clear();
        _everBuilt = true;
        _builtAtRealtime = Time.realtimeSinceStartup;

        try
        {
            Settlement? settlement = StorageCensus.ResolveSettlement(out _);
            if (settlement == null)
            {
                if (Plugin.TransferDiagnostics.Value)
                    Plugin.Logger.LogInfo("[CFS] SettlementStock.Rebuild: no settlement resolved - snapshot empty.");
                return;
            }

            var blacklist = GetBlacklist();

            Il2CppSystem.Collections.Generic.List<Structure>? structures = null;
            try { structures = settlement.GetStructures(); } catch { }
            if (structures == null) return;

            int containerEntries = 0;
            int skippedBlacklisted = 0;

            foreach (var st in structures)
            {
                if (st == null) continue;
                string structureName = StorageCensus.SafeStructureName(st);

                var containers = new List<ItemContainerComponent>();
                try { StorageCensus.CollectComponents(st.transform, containers, 0); } catch { }

                foreach (var icc in containers)
                {
                    if (icc == null) continue;
                    ItemContainer? container = null;
                    try { container = icc.container; } catch { }
                    if (container == null) continue;

                    string typeName = "?";
                    try { typeName = container.containerType?.name ?? "?"; } catch { }
                    if (blacklist.Contains(typeName)) { skippedBlacklisted++; continue; }

                    Vector3 pos = default;
                    try { pos = icc.transform.position; } catch { }

                    // Per-item running totals for THIS container (a container can hold several
                    // stacks of the same item across different slots) - same bounded-indexer walk
                    // StorageCensus/OuthouseComposterMod already use (container.GetItems() only
                    // exposes an indexer through the compile-time reference).
                    try
                    {
                        int capacity = 0;
                        try { capacity = container.capacity; } catch { }
                        int bound = capacity > 0 ? capacity : 64;

                        var items = container.GetItems();
                        var perItem = new Dictionary<int, int>();
                        var infoById = new Dictionary<int, ItemInfo>();
                        for (int slot = 0; slot < bound; slot++)
                        {
                            Item? it = null;
                            try { it = items != null ? items[slot] : null; } catch { break; }
                            if (it == null) continue;
                            ItemInfo? info = null; int cnt = 0;
                            try { info = it.info; cnt = it.count; } catch { }
                            if (info == null || cnt <= 0) continue;
                            int id;
                            try { id = info.id; } catch { continue; }
                            perItem.TryGetValue(id, out int running);
                            perItem[id] = running + cnt;
                            infoById[id] = info;
                        }

                        foreach (var kv in perItem)
                        {
                            if (!_byItemId.TryGetValue(kv.Key, out var list))
                            {
                                list = new List<ContainerStock>();
                                _byItemId[kv.Key] = list;
                            }
                            list.Add(new ContainerStock
                            {
                                Container = container,
                                WorldPos = pos,
                                TypeName = typeName,
                                StructureName = structureName,
                                Qty = kv.Value
                            });
                            containerEntries++;
                        }
                    }
                    catch (Exception ex)
                    {
                        Plugin.Logger.LogError($"[CFS] SettlementStock.Rebuild container walk error: {ex}");
                    }
                }
            }

            if (Plugin.TransferDiagnostics.Value)
                Plugin.Logger.LogInfo($"[CFS] SettlementStock rebuilt: {_byItemId.Count} distinct item type(s), " +
                    $"{containerEntries} container-entr(y/ies), {skippedBlacklisted} blacklisted container(s) skipped.");
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"[CFS] SettlementStock.Rebuild error: {ex}");
        }
    }
}
