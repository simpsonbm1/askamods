using System;
using System.Collections.Generic;
using System.Linq;
using SandSailorStudio.Inventory;
using SSSGame;
using UnityEngine;

namespace VillagerAmmoMod;

// v1.1.0 (RestockFromStorage): settlement-wide container snapshot, cached with a short TTL, used as
// the SOURCE of arrows when RestockFromStorage is on. Ported from CraftFromStorageMod's
// SettlementStock/StorageCensus pair rather than reinvented - that walk is in-game proven (417
// structures, 651 containers, 44,670 items) and already carries the two corrections this needs:
//
//  - The plural generic GetComponentsInChildren<T> is missing through the interop trampoline
//    (project-wide gotcha), so containers are collected by a per-node singular GetComponent<T>()
//    recursion.
//  - The same physical container is listed once per structure that reaches it, which doubles every
//    count downstream (confirmed in-game 2026-07-30 in CraftFromStorageMod). Dedupe by native
//    pointer, falling back to a position+type key.
//
// Deliberately NOT ported: CraftFromStorageMod's station-qualified node allow-list. That exists to
// separate a crafting station's protected INPUT bins from its OUTPUT bins, a distinction with no
// meaning for arrows sitting in a warehouse.
//
// ItemContainer wrappers are held only for the lifetime of one snapshot (short TTL, dropped on
// world-leave via ClearWorldState) - project-wide gotcha: never cache interop wrappers of per-world
// objects across world SESSIONS.
internal static class SettlementStock
{
    internal sealed class ContainerStock
    {
        internal ItemContainer Container = null!;
        internal Vector3 WorldPos;
        internal string TypeName = "?";
        internal string StructureName = "?";
        internal ItemInfo Info = null!;
        internal int Qty;
    }

    private static readonly Dictionary<int, List<ContainerStock>> _byItemId = new();
    private static float _builtAtRealtime = -9999f;
    private static bool _everBuilt;
    private static HashSet<string>? _blacklistCache;
    private static string _blacklistRaw = "";

    // Called after a restock move so the next read re-walks instead of trusting stale quantities.
    internal static void Invalidate() { _builtAtRealtime = -9999f; }

    // World-leave (PlayerDespawnedPatch) - drops every held ItemContainer wrapper.
    internal static void ClearWorldState()
    {
        _byItemId.Clear();
        _builtAtRealtime = -9999f;
        _everBuilt = false;
    }

    // Pull candidates for one item, largest stockpile first (minimizes the number of distinct source
    // containers a single top-up touches).
    internal static List<ContainerStock> GetCandidates(ItemInfo info)
    {
        EnsureFresh();
        try
        {
            if (_byItemId.TryGetValue(info.id, out var list))
                return list.Where(c => c.Qty > 0).OrderByDescending(c => c.Qty).ToList();
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"[VillagerAmmo] SettlementStock.GetCandidates error: {ex}");
        }
        return new List<ContainerStock>();
    }

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
            Plugin.Logger.LogError($"[VillagerAmmo] SettlementStock.GetAvailableQuantity error: {ex}");
        }
        return 0;
    }

    // v1.1.0: the empty-quiver case. A villager whose quiver has been empty since world load offers
    // no ItemInfo to match against storage, and the InfoCache has never seen that container either,
    // so the mod has to CHOOSE an arrow. Resolution order:
    //
    //  1. RestockArrowPreference, a comma-separated item-name list in priority order - the first
    //     name settlement storage actually holds wins.
    //  2. Failing that, the largest settlement stock whose category chain contains
    //     ArrowCategoryMatch (the same matcher the stuck-arrow cull already uses).
    //
    // Step 2 exists because item display names are localized: a German player's preference list
    // never matches, and without the category fallback the feature would silently do nothing for
    // them. Category matching is not proven locale-invariant either, which is why the preference
    // list leads rather than follows.
    internal static ItemInfo? ResolveArrowInfo(string preferenceRaw, string categoryMatch)
    {
        EnsureFresh();
        try
        {
            foreach (var token in ParseList(preferenceRaw))
            {
                ItemInfo? best = null;
                int bestQty = 0;
                foreach (var kv in _byItemId)
                {
                    foreach (var cs in kv.Value)
                    {
                        string name = "";
                        try { name = cs.Info?.Name ?? ""; } catch { }
                        if (!string.Equals(name, token, StringComparison.OrdinalIgnoreCase)) continue;
                        if (cs.Qty > bestQty) { bestQty = cs.Qty; best = cs.Info; }
                    }
                }
                if (best != null) return best;
            }

            if (!string.IsNullOrWhiteSpace(categoryMatch))
            {
                ItemInfo? best = null;
                int bestQty = 0;
                foreach (var kv in _byItemId)
                {
                    int total = 0;
                    ItemInfo? info = null;
                    foreach (var cs in kv.Value) { total += cs.Qty; info ??= cs.Info; }
                    if (info == null || total <= bestQty) continue;
                    string chain = "";
                    try { chain = AmmoTracker.CategoryChainOf(info.category); } catch { }
                    if (chain.IndexOf(categoryMatch, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    bestQty = total;
                    best = info;
                }
                return best;
            }
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"[VillagerAmmo] SettlementStock.ResolveArrowInfo error: {ex}");
        }
        return null;
    }

    internal static string[] ParseList(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return Array.Empty<string>();
        var parts = raw.Split(',');
        var list = new List<string>(parts.Length);
        foreach (var p in parts)
        {
            var t = p.Trim();
            if (t.Length > 0) list.Add(t);
        }
        return list.ToArray();
    }

    private static void EnsureFresh()
    {
        float ttl = 10f;
        try { ttl = Plugin.RestockSnapshotTtlSeconds?.Value ?? 10f; } catch { }
        if (ttl <= 0f) ttl = 10f;
        if (_everBuilt && (Time.realtimeSinceStartup - _builtAtRealtime) < ttl) return;
        Rebuild();
    }

    private static HashSet<string> GetBlacklist()
    {
        string raw = "";
        try { raw = Plugin.RestockBlacklistContainerTypes?.Value ?? ""; } catch { }
        if (_blacklistCache != null && raw == _blacklistRaw) return _blacklistCache;

        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in ParseList(raw)) set.Add(t);
        _blacklistRaw = raw;
        _blacklistCache = set;
        return set;
    }

    private static void Rebuild()
    {
        _byItemId.Clear();
        _everBuilt = true;
        _builtAtRealtime = Time.realtimeSinceStartup;

        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            Settlement? settlement = ResolveSettlement();
            if (settlement == null)
            {
                if (Plugin.EnableDiagnostics.Value)
                    Plugin.Logger.LogInfo("[VillagerAmmo] SettlementStock.Rebuild: no settlement resolved - snapshot empty.");
                return;
            }

            var blacklist = GetBlacklist();

            Il2CppSystem.Collections.Generic.List<Structure>? structures = null;
            try { structures = settlement.GetStructures(); } catch { }
            if (structures == null) return;

            int containerEntries = 0;
            int skippedBlacklisted = 0;
            int skippedDuplicates = 0;
            var seenContainerPtrs = new HashSet<IntPtr>();
            var seenContainerKeys = new HashSet<string>(StringComparer.Ordinal);

            foreach (var st in structures)
            {
                if (st == null) continue;
                string structureName = SafeStructureName(st);

                var containers = new List<ItemContainerComponent>();
                try { CollectComponents(st.transform, containers, 0); } catch { }

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
                                Info = infoById[kv.Key],
                                Qty = kv.Value
                            });
                            containerEntries++;
                        }
                    }
                    catch (Exception ex)
                    {
                        Plugin.Logger.LogError($"[VillagerAmmo] SettlementStock.Rebuild container walk error: {ex}");
                    }
                }
            }

            if (Plugin.EnableDiagnostics.Value)
                Plugin.Logger.LogInfo($"[VillagerAmmo] SettlementStock rebuilt: {_byItemId.Count} distinct item type(s), " +
                    $"{containerEntries} container-entr(y/ies), {skippedBlacklisted} blacklisted, {skippedDuplicates} duplicate listing(s) skipped.");
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"[VillagerAmmo] SettlementStock.Rebuild error: {ex}");
        }
        finally
        {
            sw.Stop();
            double ms = sw.Elapsed.TotalMilliseconds;
            if (ms > 20.0)
                Plugin.Logger.LogInfo($"[Perf][VillagerAmmo] settlement snapshot rebuild took {ms:F1} ms");
        }
    }

    // Never use SettlementManager.settlements - that list stays null even in a loaded world
    // (project-wide gotcha). Try the getter methods in order; first non-null wins.
    private static Settlement? ResolveSettlement()
    {
        SettlementManager? sm = null;
        try { sm = UnityEngine.Object.FindAnyObjectByType<SettlementManager>(); } catch { }
        if (sm == null) return null;

        try { var s = sm.GetPlayerSettlement(); if (s != null) return s; } catch { }
        try { var s = sm.GetCurrentSettlement(); if (s != null) return s; } catch { }
        try { var s = sm.worldSettlement; if (s != null) return s; } catch { }
        return null;
    }

    // Per-node singular GetComponent<T>() + child recursion; the plural generic
    // GetComponentsInChildren<T> is missing through the interop trampoline (project-wide gotcha).
    private static void CollectComponents(Transform? node, List<ItemContainerComponent> results, int depth)
    {
        if (node == null || depth > 12) return;

        try
        {
            var c = node.GetComponent<ItemContainerComponent>();
            if (c != null) results.Add(c);
        }
        catch { }

        int childCount;
        try { childCount = node.childCount; } catch { return; }
        for (int i = 0; i < childCount; i++)
        {
            Transform? child = null;
            try { child = node.GetChild(i); } catch { }
            if (child == null) continue;
            CollectComponents(child, results, depth + 1);
        }
    }

    private static string SafeStructureName(Structure? s)
    {
        if (s == null) return "?";
        try { var n = s.StructureName; if (!string.IsNullOrEmpty(n)) return n; } catch { }
        try { var n = s.DefaultName; if (!string.IsNullOrEmpty(n)) return n; } catch { }
        try { return s.gameObject.name ?? "?"; } catch { return "?"; }
    }
}
