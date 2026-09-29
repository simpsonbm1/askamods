using System;
using System.Collections.Generic;
using SandSailorStudio.Inventory;
using SSSGame;
using UnityEngine;

namespace ItemCountAuditMod;

// The read-only scan. Two populations:
//   1. Settlement storage - the CraftFromStorageMod StorageCensus walk (in-game-proven):
//      Settlement.GetStructures() -> per-structure CollectComponents<ItemContainerComponent> ->
//      container.GetItems() indexer bounded by capacity, break on throw.
//   2. Villager inventories - every Villager reachable from a VillagerSurvival captured by the
//      Spawned postfix (the DynamicVillagerNeedsMod pattern); Villager.GetInventory() is an
//      ItemCollection, ItemCollection.GetContainers() gives its ItemContainers, then the same slot
//      walk as above. Consumption happens FROM the villager's own inventory (Cpp2IL:
//      SatisfyObjectiveQuestData.TryToConsume reads Character.get_Inventory), so a double-consume
//      would leave the bad count HERE, not in settlement storage.
// Never touches Settlement.QuerySettlementResources() - it hangs the game (architecture.md).
internal static class ItemCountScan
{
    private static int _runs;
    // Native pointers of stacks already reported in the current audit. A container under a nested
    // structure (barbecue under a fire pit) is reached from both parents' transforms and would be
    // listed twice otherwise.
    private static readonly HashSet<long> _reported = new();

    // "38 m, ahead-left of you": distance plus a bearing relative to where the camera is looking,
    // which is the only frame the player can act on without knowing the world's compass axes.
    internal static string Locate(Vector3 target)
    {
        try
        {
            var cam = Camera.main;
            if (cam == null) return $"at world ({target.x:F0}, {target.z:F0})";
            Vector3 here = cam.transform.position;
            Vector3 d = target - here; d.y = 0f;
            float dist = d.magnitude;
            if (dist < 1f) return "right where you stand";
            Vector3 fwd = cam.transform.forward; fwd.y = 0f;
            if (fwd.sqrMagnitude < 0.0001f) return $"{dist:F0} m from you";
            fwd.Normalize(); d /= dist;
            float ang = Vector3.SignedAngle(fwd, d, Vector3.up);   // -180..180, +ve = to the right
            string where;
            float a = Mathf.Abs(ang);
            if (a <= 22.5f) where = "straight ahead";
            else if (a <= 67.5f) where = ang > 0 ? "ahead-right" : "ahead-left";
            else if (a <= 112.5f) where = ang > 0 ? "to your right" : "to your left";
            else if (a <= 157.5f) where = ang > 0 ? "behind-right" : "behind-left";
            else where = "behind you";
            return $"{dist:F0} m {where}";
        }
        catch { return $"at world ({target.x:F0}, {target.z:F0})"; }
    }

    internal static void Run(string reason)
    {
        _runs++;
        _reported.Clear();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Plugin.Logger.LogInfo($"[ItemCountAudit] === audit #{_runs} start (reason: {reason}) ===");

        int bad = 0;
        int totalItems = 0;
        int totalContainers = 0;

        try { ScanSettlement(ref bad, ref totalItems, ref totalContainers); }
        catch (Exception ex) { Plugin.Logger.LogError($"[ItemCountAudit] settlement scan error: {ex}"); }

        try { ScanVillagers(ref bad, ref totalItems, ref totalContainers); }
        catch (Exception ex) { Plugin.Logger.LogError($"[ItemCountAudit] villager scan error: {ex}"); }

        int badManifest = 0, manifests = 0, entries = 0;
        try { ManifestScan.Scan(null, ref badManifest, ref manifests, ref entries); }
        catch (Exception ex) { Plugin.Logger.LogError($"[ItemCountAudit] manifest scan error: {ex}"); }

        sw.Stop();
        if (bad == 0 && badManifest == 0)
            Plugin.Logger.LogInfo("[ItemCountAudit] RESULT: no negative stacks anywhere in settlement storage or villager inventories.");
        else
            Plugin.Logger.LogWarning($"[ItemCountAudit] RESULT: {bad} negative stack(s) listed above" + (badManifest > 0 ? $" plus {badManifest} negative tally entr(ies)" : "") + ".");
        Plugin.Logger.LogInfo($"[ItemCountAudit] === audit #{_runs} end: {bad} item(s) with count<=0, "
                              + $"{totalItems} item slot(s) in {totalContainers} container(s); "
                              + $"{badManifest} negative manifest entr(ies) in {entries} entries across {manifests} manifests; "
                              + $"{sw.ElapsedMilliseconds} ms ===");
    }

    // ---------------- settlement storage ----------------

    private static void ScanSettlement(ref int bad, ref int totalItems, ref int totalContainers)
    {
        Settlement? settlement = ResolveSettlement(out string via);
        if (settlement == null)
        {
            Plugin.Logger.LogInfo("[ItemCountAudit] no settlement resolved (GetPlayerSettlement/GetCurrentSettlement/worldSettlement all null).");
            return;
        }

        Il2CppSystem.Collections.Generic.List<Structure>? structures = null;
        try { structures = settlement.GetStructures(); } catch { }
        int structureCount = 0;
        try { structureCount = structures?.Count ?? 0; } catch { }
        Plugin.Logger.LogInfo($"[ItemCountAudit] settlement via {via}: {structureCount} structure(s).");
        if (structures == null) return;

        foreach (var st in structures)
        {
            if (st == null) continue;
            string owner = SafeStructureName(st);

            var containers = new List<ItemContainerComponent>();
            try { CollectComponents<ItemContainerComponent>(st.transform, containers, 0); } catch { }

            foreach (var icc in containers)
            {
                if (icc == null) continue;
                ItemContainer? container = null;
                try { container = icc.container; } catch { }
                if (container == null) continue;
                string node = "?";
                try { node = icc.gameObject.name ?? "?"; } catch { }
                Vector3 pos = Vector3.zero; bool havePos = false;
                try { pos = icc.transform.position; havePos = true; } catch { }
                if (!havePos) { try { pos = st.transform.position; havePos = true; } catch { } }
                ScanContainer($"structure '{owner}' node '{node}'", havePos ? pos : (Vector3?)null, container, ref bad, ref totalItems, ref totalContainers);
            }
        }
    }

    // ---------------- villager inventories ----------------

    private static void ScanVillagers(ref int bad, ref int totalItems, ref int totalContainers)
    {
        var survivals = VillagerRegistry.Tracked;
        int n = survivals.Count;
        int scanned = 0;
        for (int i = n - 1; i >= 0; i--)
        {
            VillagerSurvival? surv = survivals[i];
            if (surv == null) { survivals.RemoveAt(i); continue; }

            Villager? villager = null;
            try { villager = surv.GetVillager(); } catch { }
            if (villager == null) continue;

            string name = "?";
            try { name = villager.name ?? "?"; } catch { }
            Vector3 vpos = Vector3.zero; bool haveVpos = false;
            try { vpos = villager.transform.position; haveVpos = true; } catch { }

            ItemCollection? inv = null;
            try { inv = villager.GetInventory(); } catch { }
            if (inv == null)
            {
                Plugin.Logger.LogInfo($"[ItemCountAudit] villager '{name}': GetInventory() returned null.");
                continue;
            }
            scanned++;

            // ItemCollection.GetContainers() returns an IReadOnlyList stub that exposes only an
            // indexer through the compile-time reference, and indexing past the end of an IL2CPP
            // list is not guaranteed to raise a managed exception (CraftFromStorageMod census
            // note). GetAllItems() returns a real List<Item> with Count, so walk that instead.
            Il2CppSystem.Collections.Generic.List<Item>? all = null;
            try { all = inv.GetAllItems(); } catch { }
            if (all == null)
            {
                Plugin.Logger.LogInfo($"[ItemCountAudit] villager '{name}': GetAllItems() returned null.");
                continue;
            }
            int cnt = 0;
            try { cnt = all.Count; } catch { }
            totalContainers++;
            for (int k = 0; k < cnt; k++)
            {
                Item? it = null;
                try { it = all[k]; } catch { break; }
                if (it == null) continue;
                totalItems++;
                int count = int.MinValue;
                string itemName = "?";
                string typeName = "?";
                try { count = it.count; } catch { }
                try { itemName = it.info?.name ?? "?"; } catch { }
                try { typeName = it.Container?.containerType?.name ?? "?"; } catch { }
                if (count <= 0)
                {
                    long ptr = 0;
                    try { ptr = ((Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase)(object)it).Pointer.ToInt64(); } catch { }
                    if (ptr != 0 && !_reported.Add(ptr)) continue;
                    bad++;
                    string loc = haveVpos ? Locate(vpos) : "location unknown";
                    Plugin.Logger.LogWarning($"[ItemCountAudit] NEGATIVE STACK: {itemName} x{count} carried by villager '{name}', {loc} "
                                             + $"(container '{typeName}')");
                }
                else if (Plugin.LogEveryItem.Value)
                {
                    Plugin.Logger.LogInfo($"[ItemCountAudit]   villager '{name}' container='{typeName}' item='{itemName}' count={count}");
                }
            }
        }
        Plugin.Logger.LogInfo($"[ItemCountAudit] villagers: {scanned} inventory(ies) scanned of {n} tracked.");
    }

    // ---------------- shared slot walk ----------------

    private static void ScanContainer(string where, Vector3? pos, ItemContainer container, ref int bad, ref int totalItems, ref int totalContainers)
    {
        totalContainers++;
        int capacity = 0;
        string typeName = "?";
        try { capacity = container.capacity; } catch { }
        try { typeName = container.containerType?.name ?? "?"; } catch { }

        Il2CppSystem.Collections.Generic.IReadOnlyList<Item>? items = null;
        try { items = container.GetItems(); } catch { }
        if (items == null) return;

        int bound = capacity > 0 ? capacity : 64;
        for (int slot = 0; slot < bound; slot++)
        {
            Item? it = null;
            try { it = items[slot]; } catch { break; }
            if (it == null) continue;
            totalItems++;

            int count = int.MinValue;
            string itemName = "?";
            try { count = it.count; } catch { }
            try { itemName = it.info?.name ?? "?"; } catch { }

            if (count <= 0)
            {
                long ptr = 0;
                try { ptr = ((Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase)(object)it).Pointer.ToInt64(); } catch { }
                if (ptr != 0 && !_reported.Add(ptr)) continue;
                bad++;
                string loc = pos.HasValue ? Locate(pos.Value) : "location unknown";
                Plugin.Logger.LogWarning($"[ItemCountAudit] NEGATIVE STACK: {itemName} x{count} in a storage {loc} "
                                         + $"({where}, container '{typeName}', slot {slot}). Open that storage and take the item out, or press Ctrl+F3.");
            }
            else if (Plugin.LogEveryItem.Value)
            {
                Plugin.Logger.LogInfo($"[ItemCountAudit]   {where} type='{typeName}' slot={slot} item='{itemName}' count={count}");
            }
        }
    }

    // ---------------- experiment support (Experiment.cs) ----------------

    internal static Settlement? ResolveSettlementPublic(out string via) => ResolveSettlement(out via);
    internal static void CollectComponentsPublic<T>(Transform? node, List<T> results, int depth) where T : Component
        => CollectComponents<T>(node, results, depth);

    // Same two walks as Run(), but returns the Item wrappers with count<=0 instead of logging.
    internal static void CollectBad(List<(string where, Item item)> bad)
    {
        try
        {
            Settlement? settlement = ResolveSettlement(out _);
            Il2CppSystem.Collections.Generic.List<Structure>? structures = null;
            try { structures = settlement?.GetStructures(); } catch { }
            if (structures != null)
            {
                foreach (var st in structures)
                {
                    if (st == null) continue;
                    string owner = SafeStructureName(st);
                    var comps = new List<ItemContainerComponent>();
                    try { CollectComponents<ItemContainerComponent>(st.transform, comps, 0); } catch { }
                    foreach (var icc in comps)
                    {
                        ItemContainer? container = null;
                        try { container = icc?.container; } catch { }
                        if (container == null) continue;
                        int capacity = 0;
                        try { capacity = container.capacity; } catch { }
                        Il2CppSystem.Collections.Generic.IReadOnlyList<Item>? items = null;
                        try { items = container.GetItems(); } catch { }
                        if (items == null) continue;
                        int bound = capacity > 0 ? capacity : 64;
                        for (int slot = 0; slot < bound; slot++)
                        {
                            Item? it = null;
                            try { it = items[slot]; } catch { break; }
                            if (it == null) continue;
                            int count = 1;
                            try { count = it.count; } catch { continue; }
                            if (count <= 0) bad.Add(($"structure '{owner}' slot {slot}", it));
                        }
                    }
                }
            }
        }
        catch (Exception ex) { Plugin.Logger.LogError($"[ItemCountAudit] CollectBad settlement: {ex}"); }

        try
        {
            foreach (var surv in VillagerRegistry.Tracked)
            {
                if (surv == null) continue;
                Villager? villager = null;
                try { villager = surv.GetVillager(); } catch { }
                if (villager == null) continue;
                string name = "?";
                try { name = villager.name ?? "?"; } catch { }
                Il2CppSystem.Collections.Generic.List<Item>? all = null;
                try { all = villager.GetInventory()?.GetAllItems(); } catch { }
                if (all == null) continue;
                int cnt = 0;
                try { cnt = all.Count; } catch { }
                for (int k = 0; k < cnt; k++)
                {
                    Item? it = null;
                    try { it = all[k]; } catch { break; }
                    if (it == null) continue;
                    int count = 1;
                    try { count = it.count; } catch { continue; }
                    if (count <= 0) bad.Add(($"villager '{name}'", it));
                }
            }
        }
        catch (Exception ex) { Plugin.Logger.LogError($"[ItemCountAudit] CollectBad villagers: {ex}"); }
    }

    // ---------------- helpers (StorageCensus.cs shapes) ----------------

    // Never SettlementManager.settlements - stays null in a loaded world (project-wide gotcha).
    private static Settlement? ResolveSettlement(out string via)
    {
        via = "none";
        SettlementManager? sm = null;
        try { sm = UnityEngine.Object.FindAnyObjectByType<SettlementManager>(); } catch { }
        if (sm == null) return null;
        try { var s = sm.GetPlayerSettlement(); if (s != null) { via = "GetPlayerSettlement"; return s; } } catch { }
        try { var s = sm.GetCurrentSettlement(); if (s != null) { via = "GetCurrentSettlement"; return s; } } catch { }
        try { var s = sm.worldSettlement; if (s != null) { via = "worldSettlement"; return s; } } catch { }
        return null;
    }

    private static string SafeStructureName(Structure st)
    {
        try { return st.name ?? "?"; } catch { return "?"; }
    }

    // Plural GetComponentsInChildren<T> is missing through the interop trampoline (project-wide
    // gotcha); singular per-node GetComponent<T> + child recursion is the proven replacement.
    private static void CollectComponents<T>(Transform? node, List<T> results, int depth) where T : Component
    {
        if (node == null || depth > 12) return;
        try
        {
            var c = node.GetComponent<T>();
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
            CollectComponents<T>(child, results, depth + 1);
        }
    }
}
