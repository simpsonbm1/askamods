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
        // v1.1.0: the container's own GameObject name (icc.gameObject.name), e.g. 'StorageHorns'.
        // Distinguishes bins that share one container TYPE on the same station - a bloomery's
        // StorageOre, StorageCoal and StorageBloom are all Storage_SmallItems_L1.
        internal string NodeName = "?";
        internal string StructureName = "?";
        internal int Qty;
    }

    private static readonly Dictionary<int, List<ContainerStock>> _byItemId = new();
    private static float _builtAtRealtime = -9999f;
    private static bool _everBuilt;
    private static HashSet<string>? _blacklistCache;
    private static string _blacklistRaw = "";
    // v1.2.0: node name -> the station classes it is admitted under. A null value means
    // unrestricted (admitted on any owner). See GetNodeAllowlist for the config syntax.
    private static Dictionary<string, HashSet<string>?>? _nodeAllowCache;
    private static string _nodeAllowRaw = "";

    // Called after any pull/sweep-back (design point B) so the next read re-walks instead of
    // trusting stale quantities.
    internal static void Invalidate() { _builtAtRealtime = -9999f; }

    // v1.5.2: a running correction to the DISPLAYED settlement totals, applied between snapshot
    // rebuilds. The snapshot is only rebuilt on a timer (SnapshotTtlSeconds, 5 s by default) because
    // the walk covers hundreds of containers, so after the mod moves items out of storage the cached
    // total stays stale until that timer expires. In the personal crafting menu that was visible as
    // the ingredient count refusing to drop for several crafts and then falling by the whole amount
    // at once (user, 2026-08-13: "sometimes it would go down by 10, sometimes it wouldnt go down at
    // all, and then catch up 3 clicks later to go down by 40").
    //
    // This is a DISPLAY correction only. It never affects which containers are offered as pull
    // sources: an emptied container simply yields nothing on the next move attempt, which the
    // candidate loop already handles. Cleared on every rebuild, so it can never accumulate drift.
    private static readonly Dictionary<int, int> _displayAdjust = new();

    internal static void NoteRemovedForDisplay(int itemId, int qty)
    {
        if (qty <= 0) return;
        try
        {
            _displayAdjust.TryGetValue(itemId, out int running);
            _displayAdjust[itemId] = running + qty;
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"[CFS] SettlementStock.NoteRemovedForDisplay error: {ex}");
        }
    }

    // World-leave (CraftWatcher.ClearWorldState, called from Patches/LifecyclePatches.cs
    // PlayerDespawnedPatch) - drops every held ItemContainer wrapper (project-wide gotcha: never
    // cache interop wrappers of per-world objects across sessions).
    internal static void ClearWorldState()
    {
        _byItemId.Clear();
        _displayAdjust.Clear();
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

            // Subtract anything moved out of storage since the last rebuild - see
            // NoteRemovedForDisplay for why the snapshot alone runs stale here.
            if (_displayAdjust.TryGetValue(info.id, out int removed))
            {
                qty -= removed;
                if (qty < 0) qty = 0;
            }
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

    // v1.1.0: container NODE names that are admitted as pull sources even when their container TYPE
    // is blacklisted. The blacklist is type-keyed, but the game reuses one type for both a station's
    // protected INPUT bins and its finished OUTPUT bins - a bloomery's StorageOre, StorageCoal and
    // StorageBloom are all Storage_SmallItems_L1 (census-confirmed 2026-08-11). Node names come from
    // the prefab, so they are stable across saves and repeat across instances of a building
    // (Woodcutter 1/2/3 all carry Bark/Firewood/Sticks/Thatch/FiberResin in that same census).
    // Matched case-insensitively against icc.gameObject.name.
    //
    // v1.2.0 syntax: an entry is either a bare node name, admitted on any owner, or
    // 'Node@StationClass', admitted only when the owning structure's workstation reports that
    // native class. The qualified form exists because 'Scraps' is the hunter hut's output bin AND
    // the bench bin on the Blacksmith, Metalworker, Leatherworker and Tailoring workshops
    // (census-confirmed 2026-08-11), so node name alone cannot separate them. Repeating a node with
    // different classes unions them; listing it bare anywhere makes it unrestricted.
    private static Dictionary<string, HashSet<string>?> GetNodeAllowlist()
    {
        string raw = "";
        try { raw = Plugin.SourceNodeAllowlist?.Value ?? ""; } catch { }
        if (_nodeAllowCache != null && raw == _nodeAllowRaw) return _nodeAllowCache;

        var map = new Dictionary<string, HashSet<string>?>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in raw.Split(','))
        {
            var t = part.Trim();
            if (t.Length == 0) continue;

            string node = t;
            string? stationClass = null;
            int at = t.IndexOf('@');
            if (at > 0 && at < t.Length - 1)
            {
                node = t.Substring(0, at).Trim();
                stationClass = t.Substring(at + 1).Trim();
                if (node.Length == 0 || stationClass.Length == 0) { node = t; stationClass = null; }
            }

            if (!map.TryGetValue(node, out var classes))
            {
                map[node] = stationClass == null
                    ? null
                    : new HashSet<string>(StringComparer.OrdinalIgnoreCase) { stationClass };
                continue;
            }
            // Already unrestricted stays unrestricted; a bare entry downgrades a qualified one.
            if (classes == null || stationClass == null) { map[node] = null; continue; }
            classes.Add(stationClass);
        }
        _nodeAllowRaw = raw;
        _nodeAllowCache = map;
        return map;
    }

    // Same per-structure/per-container traversal shape as StorageCensus.RunCensus's PROVEN walk
    // (GetStructures -> CollectComponents<ItemContainerComponent> -> per-container GetItems() bounded
    // by capacity), just building a queryable dictionary instead of logging.
    private static void Rebuild()
    {
        _byItemId.Clear();
        _displayAdjust.Clear();
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
            var nodeAllow = GetNodeAllowlist();

            Il2CppSystem.Collections.Generic.List<Structure>? structures = null;
            try { structures = settlement.GetStructures(); } catch { }
            if (structures == null) return;

            int containerEntries = 0;
            int skippedBlacklisted = 0;
            var seenContainerPtrs = new HashSet<IntPtr>();
            var seenContainerKeys = new HashSet<string>(StringComparer.Ordinal);
            int skippedDuplicates = 0;

            foreach (var st in structures)
            {
                if (st == null) continue;
                string structureName = StorageCensus.SafeStructureName(st);

                // v1.2.0: the owning station's class, resolved LAZILY and at most once per structure.
                // Only a station-qualified allow-list entry ('Scraps@HuntingStation') needs it, and
                // those are rare - 5 of 912 containers in the 2026-08-11 census - so the common
                // rebuild pays nothing for the extra hierarchy walk.
                string? stationClass = null;
                bool stationClassResolved = false;

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
                    string nodeName = "?";
                    try { nodeName = icc.gameObject.name ?? "?"; } catch { }
                    // v1.1.0: a blacklisted TYPE is still admitted when this specific NODE is on the
                    // output allow-list - see GetNodeAllowlist's comment for why type alone cannot
                    // separate a station's input bins from its output bins.
                    if (blacklist.Contains(typeName))
                    {
                        if (!nodeAllow.TryGetValue(nodeName, out var allowedClasses))
                        { skippedBlacklisted++; continue; }

                        // v1.2.0: a null value is unrestricted; otherwise the owning structure's
                        // station class must be one of the listed ones.
                        if (allowedClasses != null)
                        {
                            if (!stationClassResolved)
                            {
                                stationClass = StorageCensus.ResolveStationClass(st);
                                stationClassResolved = true;
                            }
                            if (stationClass == null || !allowedClasses.Contains(stationClass))
                            { skippedBlacklisted++; continue; }
                        }
                    }

                    Vector3 pos = default;
                    try { pos = icc.transform.position; } catch { }

                    // v0.14.1: same physical container is listed once per structure that reaches it
                    // (confirmed in-game 2026-07-30: identical world position + quantity under two
                    // structure names), doubling every downstream count. Dedupe by native pointer
                    // first (the boxing idiom this project uses for interop pointer reads - see
                    // CLAUDE.md's interop notes), falling back to a position+type key if the pointer
                    // can't be read. Both sets are locals scoped to this single Rebuild call, so this
                    // does not violate the project's no-caching-interop-wrappers rule.
                    IntPtr cptr = IntPtr.Zero;
                    try { if ((object)container is Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase b) cptr = b.Pointer; } catch { }
                    if (cptr != IntPtr.Zero)
                    {
                        if (!seenContainerPtrs.Add(cptr)) { skippedDuplicates++; continue; }
                    }
                    else
                    {
                        string key = pos.x.ToString("F2") + "," + pos.y.ToString("F2") + "," + pos.z.ToString("F2") + "|" + typeName;
                        if (!seenContainerKeys.Add(key)) { skippedDuplicates++; continue; }
                    }

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
                                NodeName = nodeName,
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
                    $"{containerEntries} container-entr(y/ies), {skippedBlacklisted} blacklisted container(s) skipped, " +
                    $"{skippedDuplicates} duplicate container listing(s) skipped.");
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"[CFS] SettlementStock.Rebuild error: {ex}");
        }
    }
}
