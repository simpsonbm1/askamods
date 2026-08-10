using System;
using System.Collections.Generic;
using System.Text;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using SSSGame;
using SandSailorStudio.Inventory;
using UnityEngine;

namespace GroundItemVacuumMod;

public class VacuumTracker : MonoBehaviour
{
    private KeyCode _key = KeyCode.N;
    private string _keyRaw = "";
    private string _guiMessage = "";
    private float _guiExpiry = 0f;
    private float _autoTimer = 0f;
    private float _cfgReloadTimer = 0f;

    private sealed class Candidate
    {
        public WorldItemObject Item = null!;
        public string Name = "?";
        public string CatChain = "";
        public Vector3 Pos;
    }

    private sealed class CorpseCandidate
    {
        public Monster Creature = null!;
        public string Name = "?";
        public Vector3 Pos;
    }

    // v1.4.0: one matched data-layer record collected during a DataLayerSweep walk, kept around
    // just long enough to attempt removal after the walk completes (never cached across sweeps).
    private sealed class PendingRemoval
    {
        public WorldDataCell Cell = null!;
        public int BufferId;
        public InventoryItemInstancesBufferBase Buffer = null!;
        public uint UniqueId;
        public string Name = "?";
    }

    private struct DataLayerWalkResult
    {
        public int TilesSeen, CellsSeen, CellsWithContainer, BuffersSeen, TotalRecords;
        public int TilesSkipped, CellsSkipped, BuffersSkipped;
        public int MatchCount;
        public bool CapHit;
        public float MaxMatchDist;
        public bool HaveMatchPos;
        public Dictionary<string, int> MatchNameCounts;
        public List<PendingRemoval>? Matches;
        public int DroppedCheckExcluded;
        public int DroppedCheckUndetermined;
        public Dictionary<string, int> DroppedCheckExcludedNameCounts;
        // v1.8.0 diagnostic only (never gates removal): which concrete buffer class each record
        // came from. InventoryItemInstancesBufferBase has two subclasses -
        // InventoryItemInstancesBuffer and VegetationItemInstancesBuffer - and vegetation-rendered
        // world assets live in the latter. If the dropped-item prefab check ever mis-sorts a name,
        // these tallies say whether the buffer class separates the same records.
        public Dictionary<string, int> RecordsByBufferClass;
        public Dictionary<string, int> MatchesByBufferClass;
    }

    private void Start()
    {
        ApplyHotkey();
    }

    // Re-binds _key from config. No-op unless the raw config string changed, so it's safe to
    // call on every config reload without log spam.
    private void ApplyHotkey()
    {
        string raw = Plugin.VacuumHotkey.Value ?? "";
        if (raw == _keyRaw) return;
        _keyRaw = raw;
        if (Enum.TryParse<KeyCode>(raw, true, out var parsed))
            _key = parsed;
        else
            Plugin.Logger.LogWarning($"[Vacuum] Could not parse hotkey '{raw}'. Keeping '{_key}'.");
        Plugin.Logger.LogInfo($"[Vacuum] Hotkey bound to {_key}.");
    }

    private void Update()
    {
        // Live config pickup: BepInEx does NOT re-read an edited cfg on its own — reload it
        // periodically so DryRun/filters/radius/hotkey can be changed mid-session without a
        // relaunch (SeedScout pattern). Every other setting is already read fresh at sweep time,
        // so the hotkey is the only value that needs explicit re-application. Slowed to 30s (perf-
        // diagnostic round) — hotkey rebinding now picks up within 30s instead of 5s.
        _cfgReloadTimer += Time.deltaTime;
        if (_cfgReloadTimer >= 30f)
        {
            _cfgReloadTimer = 0f;
            try { Plugin.Cfg?.Reload(); } catch { }
            ApplyHotkey();
        }

        if (!IsTextInputFocused() && Input.GetKeyDown(_key))
        {
            try { Sweep(auto: false); }
            catch (Exception ex)
            {
                Plugin.Logger.LogError($"[Vacuum] Sweep error: {ex}");
                ShowMessage("Vacuum error - see BepInEx log.");
            }
        }

        float autoMin = Plugin.AutoVacuumMinutes.Value;
        if (autoMin > 0f)
        {
            _autoTimer += Time.deltaTime;
            if (_autoTimer >= autoMin * 60f)
            {
                _autoTimer = 0f;
                try { Sweep(auto: true); }
                catch (Exception ex) { Plugin.Logger.LogError($"[Vacuum] Auto-sweep error: {ex}"); }
            }
        }
    }

    private void Sweep(bool auto)
    {
        // v1.3.0 introduced VacuumEntireWorld as a whole-map DATA-layer walk (see DataLayerSweep
        // below) instead of merely widening the object-layer sweep's radius test - branch out and
        // return before any of the object-layer sweep below runs, so that path (including its
        // corpse handling) is untouched. v1.4.0: DataLayerSweep now performs real removal (see
        // its own header comment) instead of only scanning.
        if (Plugin.VacuumEntireWorld.Value)
        {
            try { DataLayerSweep(auto); }
            catch (Exception ex)
            {
                Plugin.Logger.LogError($"[Vacuum] whole-map sweep error: {ex}");
                if (!auto) ShowMessage("Vacuum error - see BepInEx log.");
            }
            return;
        }

        bool entireWorld = false;
        bool dryRun = Plugin.DryRun.Value;
        bool trace = Plugin.TraceEachItem.Value;

        Vector3 playerPos = Vector3.zero;
        bool havePlayer = Plugin.LocalPlayer != null && Plugin.LocalPlayer.gameObject != null;
        if (havePlayer) playerPos = Plugin.LocalPlayer!.transform.position;

        if (!entireWorld && !havePlayer)
        {
            if (!auto) ShowMessage("Vacuum: player not ready yet.");
            return;
        }

        int trackedCount;
        lock (Plugin.TrackedItems) { trackedCount = Plugin.TrackedItems.Count; }

        if (!Plugin.TrackingConfirmed && trackedCount == 0)
        {
            if (!auto) ShowMessage("Vacuum: ground-item tracking not ready yet.");
            return;
        }

        // Corpse pass is entirely gated on IncludeCorpses - snapshot stays empty when the feature
        // is off, so every corpse-related count downstream is naturally zero.
        bool includeCorpses = Plugin.IncludeCorpses.Value;
        Monster[] corpseSnap = Array.Empty<Monster>();
        if (includeCorpses)
        {
            lock (Plugin.TrackedCorpses)
            {
                corpseSnap = new Monster[Plugin.TrackedCorpses.Count];
                Plugin.TrackedCorpses.CopyTo(corpseSnap);
            }
        }

        // Build candidates (read-only). Per-step trace so a native crash pinpoints the exact item.
        var all = BuildCandidates(trace);

        Plugin.Logger.LogInfo($"[Vacuum] Sweep start ({(auto ? "auto" : "manual")}, {(dryRun ? "DRY-RUN" : "LIVE")}): tracked={trackedCount}, trace={trace}, corpses={corpseSnap.Length}.");

        // Filter.
        var targets = FilterCandidates(all, entireWorld, playerPos, out int inRangeTotal);
        var byName = new Dictionary<string, int>();
        foreach (var c in targets)
            byName[c.Name] = byName.TryGetValue(c.Name, out var n) ? n + 1 : 1;

        float radiusSqr = Plugin.Radius.Value * Plugin.Radius.Value;

        // Corpse pass. Kept in a SEPARATE list from item targets so counts stay distinct. Corpses
        // are exempt from OnlyItems/ExcludeItems/ExcludeCategories (those are debris filters) -
        // only the radius (or VacuumEntireWorld) and ExcludeCorpseNames apply. Only dead Monsters
        // are ever candidates (live monsters are skipped by the IsDead check above); Den and Pet
        // are never tracked in the first place (see Plugin.TrackedCorpses).
        var excludeCorpseNames = ParseCsv(Plugin.ExcludeCorpseNames.Value);
        var corpseTargets = new List<CorpseCandidate>();
        var corpseByName = new Dictionary<string, int>();
        int corpsesInRange = 0;

        if (includeCorpses)
        {
            for (int i = 0; i < corpseSnap.Length; i++)
            {
                var m = corpseSnap[i];
                try
                {
                    // Unity-destroyed since Spawned - prune and skip. This replaces an
                    // OnDisable-style hook; Monster only gives us Spawned/Despawned.
                    if (m == null)
                    {
                        lock (Plugin.TrackedCorpses) { Plugin.TrackedCorpses.Remove(m); }
                        continue;
                    }

                    // Live monsters are never candidates and never touched beyond this read.
                    if (!m.IsDead) continue;

                    string name = m.GetTargetName() ?? "?";
                    Vector3 pos = m.transform.position;

                    if (!entireWorld)
                    {
                        float dSqr = (pos - playerPos).sqrMagnitude;
                        if (dSqr > radiusSqr) continue;
                    }
                    corpsesInRange++;

                    if (ContainsAny(name, excludeCorpseNames))
                    {
                        if (Plugin.Diagnostics.Value)
                            Plugin.Logger.LogInfo($"[Vacuum]   corpse SKIPPED (ExcludeCorpseNames): {name}");
                        continue;
                    }

                    corpseTargets.Add(new CorpseCandidate { Creature = m, Name = name, Pos = pos });
                    corpseByName[name] = corpseByName.TryGetValue(name, out var cn) ? cn + 1 : 1;
                }
                catch (Exception ex)
                {
                    Plugin.Logger.LogError($"[Vacuum] corpse #{i} resolve error: {ex}");
                }
            }
        }

        // Report.
        if (Plugin.Diagnostics.Value)
        {
            Plugin.Logger.LogInfo($"[Vacuum] Sweep result: {all.Count} tracked items, {inRangeTotal} in range, {targets.Count} match filters.");
            foreach (var kv in byName)
                Plugin.Logger.LogInfo($"[Vacuum]   would remove x{kv.Value}: {kv.Key}");
            var catTally = new Dictionary<string, int>();
            foreach (var c in all)
                catTally[c.CatChain] = catTally.TryGetValue(c.CatChain, out var m) ? m + 1 : 1;
            foreach (var kv in catTally)
                Plugin.Logger.LogInfo($"[Vacuum]   category x{kv.Value}: {(string.IsNullOrEmpty(kv.Key) ? "<none>" : kv.Key)}");

            if (includeCorpses)
            {
                Plugin.Logger.LogInfo($"[Vacuum] Corpse sweep result: {corpseSnap.Length} tracked, {corpsesInRange} dead in range, {corpseTargets.Count} match filters.");
                foreach (var kv in corpseByName)
                    Plugin.Logger.LogInfo($"[Vacuum]   corpse x{kv.Value}: {kv.Key}");
            }
        }

        if (dryRun)
        {
            string scanMsg = includeCorpses
                ? $"SCAN: {targets.Count} item(s) + {corpseTargets.Count} corpse(s) would be vacuumed (of {inRangeTotal} items / {corpsesInRange} corpses in range). See log; set DryRun=false to remove."
                : $"SCAN: {targets.Count} item(s) would be vacuumed (of {inRangeTotal} in range). See log; set DryRun=false to remove.";
            ShowMessage(scanMsg);
            return;
        }

        if (Plugin.HostOnly.Value && !IsHost())
        {
            if (!auto) ShowMessage("Only the host can vacuum ground items.");
            return;
        }

        int removed = RemoveCandidates(targets);

        int corpseRemoved = 0;
        if (includeCorpses)
        {
            foreach (var c in corpseTargets)
            {
                try
                {
                    if (c.Creature != null)
                    {
                        c.Creature.DespawnImmediatelyIfStateAuthority();
                        corpseRemoved++;
                    }
                }
                catch (Exception ex)
                {
                    Plugin.Logger.LogError($"[Vacuum] Failed to despawn corpse '{c.Name}': {ex}");
                }
            }
        }

        if (includeCorpses)
        {
            Plugin.Logger.LogInfo($"[Vacuum] Removed {removed}/{targets.Count} ground items, {corpseRemoved}/{corpseTargets.Count} corpses.");
            if (!auto || removed > 0 || corpseRemoved > 0)
                ShowMessage($"Vacuumed {removed} ground item(s) + {corpseRemoved} corpse(s).");
        }
        else
        {
            Plugin.Logger.LogInfo($"[Vacuum] Removed {removed}/{targets.Count} ground items.");
            if (!auto || removed > 0)
                ShowMessage($"Vacuumed {removed} ground item(s).");
        }
    }

    // Shared with the object-layer sweep in Sweep(): snapshot Plugin.TrackedItems (managed
    // references maintained by OnEnable/OnDisable - we never touch the game's linked list) and
    // resolve each into a read-only Candidate. Per-step trace so a native crash pinpoints the
    // exact item.
    private List<Candidate> BuildCandidates(bool trace)
    {
        DynamicItemObject[] snap;
        lock (Plugin.TrackedItems)
        {
            snap = new DynamicItemObject[Plugin.TrackedItems.Count];
            Plugin.TrackedItems.CopyTo(snap);
        }

        var all = new List<Candidate>(snap.Length);
        for (int i = 0; i < snap.Length; i++)
        {
            var node = snap[i];
            if (node == null) continue;
            try { AddCandidate(node, i, trace, all); }
            catch (Exception ex) { Plugin.Logger.LogError($"[Vacuum] item #{i} resolve error: {ex}"); }
        }
        return all;
    }

    // Shared filter: radius (skipped when entireWorld) + OnlyItems/ExcludeItems/ExcludeCategories.
    private List<Candidate> FilterCandidates(List<Candidate> all, bool entireWorld, Vector3 playerPos, out int inRangeTotal)
    {
        var excludeCats = ParseCsv(Plugin.ExcludeCategories.Value);
        var excludeItems = ParseCsv(Plugin.ExcludeItems.Value);
        var onlyItems = ParseCsv(Plugin.OnlyItems.Value);
        float radiusSqr = Plugin.Radius.Value * Plugin.Radius.Value;

        var targets = new List<Candidate>();
        inRangeTotal = 0;
        foreach (var c in all)
        {
            if (!entireWorld)
            {
                float dSqr = (c.Pos - playerPos).sqrMagnitude;
                if (dSqr > radiusSqr) continue;
            }
            inRangeTotal++;

            if (onlyItems.Count > 0 && !ContainsAny(c.Name, onlyItems)) continue;
            if (ContainsAny(c.Name, excludeItems)) continue;
            if (ContainsAny(c.CatChain, excludeCats)) continue;

            targets.Add(c);
        }
        return targets;
    }

    // The proven object-layer removal call (confirmed in-game since v1.0.1): WorldItemObject's
    // own network-safe RemoveObjectFromWorld(). Shared by the normal radius-gated sweep and by
    // DataLayerSweep's Phase 1 (called with entireWorld candidates, radius test already skipped
    // by FilterCandidates above).
    private int RemoveCandidates(List<Candidate> targets)
    {
        int removed = 0;
        foreach (var c in targets)
        {
            try
            {
                if (c.Item != null)
                {
                    c.Item.RemoveObjectFromWorld();
                    removed++;
                }
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError($"[Vacuum] Failed to remove '{c.Name}': {ex}");
            }
        }
        return removed;
    }

    private const int DataLayerRecordCap = 250000;

    // Whole-map DATA-layer walk. Walks WorldDataManager's per-tile/per-cell inventory buffers
    // rather than the mod's own OnEnable/OnDisable object-layer tracking, so it reaches items
    // that never got a spawned GameObject. Call sequence copied from
    // GroundItemAuditMod.ItemDataDump.WalkPerCellBuffers, which ran in-game with zero exceptions.
    // Pure read: callers decide what to do with the matches (log-only scan, or DataLayerSweep's
    // removal pass / post-removal self-verification pass). Resolves nothing static - manager,
    // handler, tiles, cells and buffers are all supplied fresh by the caller for this one walk.
    private DataLayerWalkResult WalkDataLayer(
        Il2CppSystem.Collections.Generic.List<WorldTileData> tiles, int slotId,
        List<string> onlyItems, List<string> excludeItems, List<string> excludeCats,
        Vector3 playerPos, bool collectMatches,
        InventoryItemDataHandler handler, Dictionary<string, bool?> droppedItemCache)
    {
        var result = new DataLayerWalkResult
        {
            MatchNameCounts = new Dictionary<string, int>(),
            DroppedCheckExcludedNameCounts = new Dictionary<string, int>(),
            RecordsByBufferClass = new Dictionary<string, int>(),
            MatchesByBufferClass = new Dictionary<string, int>()
        };
        if (collectMatches) result.Matches = new List<PendingRemoval>();

        foreach (var tile in tiles)
        {
            if (result.CapHit) break;
            try
            {
                if (tile == null) { result.TilesSkipped++; continue; }
                result.TilesSeen++;

                var cellList = new Il2CppSystem.Collections.Generic.List<WorldDataCell>();
                tile.QueryAllCells(DataAccessMode.FETCH,
                    cellList.Cast<Il2CppSystem.Collections.Generic.ICollection<WorldDataCell>>());

                foreach (var cell in cellList)
                {
                    if (result.CapHit) break;
                    try
                    {
                        if (cell == null) { result.CellsSkipped++; continue; }
                        result.CellsSeen++;

                        CellDataContainer? container;
                        try { container = cell.GetDataContainer(slotId); }
                        catch { result.CellsSkipped++; continue; }
                        if (container == null) continue;

                        InventoryCellDataContainer? inv = null;
                        if ((object)container is Il2CppObjectBase cob)
                        {
                            var cls = IL2CPP.il2cpp_object_get_class(cob.Pointer);
                            var target = Il2CppClassPointerStore<InventoryCellDataContainer>.NativeClassPtr;
                            if (IL2CPP.il2cpp_class_is_subclass_of(cls, target, false))
                                inv = new InventoryCellDataContainer(cob.Pointer);
                        }
                        if (inv == null) continue;

                        result.CellsWithContainer++;

                        var itemBuffers = inv.itemBuffers;
                        if (itemBuffers == null) continue;

                        foreach (var kv in itemBuffers)
                        {
                            if (result.CapHit) break;
                            try
                            {
                                int bufferId = kv.Key;
                                var buffer = kv.Value;
                                if (buffer == null) { result.BuffersSkipped++; continue; }
                                result.BuffersSeen++;

                                // v1.8.0 diagnostic: concrete buffer class, resolved natively
                                // (managed casts lie for interop objects under a base declared type).
                                string bufferClass = "?";
                                try
                                {
                                    if ((object)buffer is Il2CppObjectBase bob)
                                        bufferClass = System.Runtime.InteropServices.Marshal.PtrToStringAnsi(
                                            IL2CPP.il2cpp_class_get_name(IL2CPP.il2cpp_object_get_class(bob.Pointer))) ?? "?";
                                }
                                catch { bufferClass = "?"; }

                                int size = buffer.GetSize();
                                for (int idx = 0; idx < size; idx++)
                                {
                                    if (result.TotalRecords >= DataLayerRecordCap) { result.CapHit = true; break; }

                                    Vector3 pos;
                                    string name = "?";
                                    string catChain = "";
                                    uint uniqueId = 0;
                                    ItemInfo? itemInfo = null;
                                    try
                                    {
                                        int i1 = idx;
                                        pos = buffer.GetPosAt(ref i1);
                                        int i2 = idx;
                                        var item = buffer.GetItemAt(ref i2);
                                        int i3 = idx;
                                        uniqueId = buffer.GetUniqueIdAt(ref i3);

                                        if (item != null && item.info != null && item.info.Name != null)
                                        {
                                            name = item.info.Name;
                                            catChain = BuildCategoryChain(item.info.category);
                                            itemInfo = item.info;
                                        }
                                    }
                                    catch { continue; }

                                    result.TotalRecords++;
                                    result.RecordsByBufferClass[bufferClass] =
                                        result.RecordsByBufferClass.TryGetValue(bufferClass, out var rbc) ? rbc + 1 : 1;

                                    if (name == "?") continue; // no readable name -> never a target

                                    if (onlyItems.Count > 0 && !ContainsAny(name, onlyItems)) continue;
                                    if (ContainsAny(name, excludeItems)) continue;
                                    if (ContainsAny(catChain, excludeCats)) continue;

                                    // v1.7.0: fail-closed check that this record's item spawns as a loose
                                    // ground item (its source prefab carries a DynamicItemObject component) -
                                    // the same class of thing the radius-gated sweep targets. Confirmed
                                    // in-game 2026-08-10 that WorldDataSlot.INVENTORY also holds world-placed
                                    // harvestables/creatures (Iron Deposit, Jotun Blood, Wight, ...) that the
                                    // radius sweep never sees - this test keeps whole-map removal scoped to
                                    // the same kind of record. Never removed on null/exception (undetermined).
                                    bool? isDropped = ResolveIsDroppedItem(name, itemInfo, handler, droppedItemCache);
                                    if (isDropped != true)
                                    {
                                        if (isDropped == null) result.DroppedCheckUndetermined++;
                                        else
                                        {
                                            result.DroppedCheckExcluded++;
                                            result.DroppedCheckExcludedNameCounts[name] =
                                                result.DroppedCheckExcludedNameCounts.TryGetValue(name, out var en) ? en + 1 : 1;
                                        }
                                        continue;
                                    }

                                    result.MatchCount++;
                                    result.MatchNameCounts[name] = result.MatchNameCounts.TryGetValue(name, out var n) ? n + 1 : 1;
                                    result.MatchesByBufferClass[bufferClass] =
                                        result.MatchesByBufferClass.TryGetValue(bufferClass, out var mbc) ? mbc + 1 : 1;

                                    float dx = pos.x - playerPos.x;
                                    float dz = pos.z - playerPos.z;
                                    float horizDist = Mathf.Sqrt(dx * dx + dz * dz);
                                    if (!result.HaveMatchPos || horizDist > result.MaxMatchDist) { result.MaxMatchDist = horizDist; result.HaveMatchPos = true; }

                                    if (collectMatches)
                                        result.Matches!.Add(new PendingRemoval { Cell = cell, BufferId = bufferId, Buffer = buffer, UniqueId = uniqueId, Name = name });
                                }
                            }
                            catch { result.BuffersSkipped++; continue; }
                        }
                    }
                    catch { result.CellsSkipped++; continue; }
                }
            }
            catch { result.TilesSkipped++; continue; }
        }

        return result;
    }

    // v1.7.0: does this item spawn as a loose ground item? Resolved via its source prefab's
    // DynamicItemObject component - the same component the radius-gated sweep's own-set tracking
    // is built from (see AddCandidate/DynamicItemObject at the top of this file). Cached per item
    // name for the lifetime of one Sweep() call (shared across the walk + verify passes by the
    // caller) - a world has only a few dozen distinct item names, so this turns thousands of
    // per-record prefab lookups into a handful. Fails CLOSED: any null step or exception returns
    // null (undetermined), which the caller treats as "not a match" - never removed.
    private bool? ResolveIsDroppedItem(string name, ItemInfo? itemInfo, InventoryItemDataHandler handler, Dictionary<string, bool?> cache)
    {
        if (cache.TryGetValue(name, out var cached)) return cached;

        bool? result = null;
        try
        {
            if (itemInfo != null && handler != null)
            {
                InventoryItemDescriptor? descriptor = handler.GetItemDescriptor(itemInfo);
                if (descriptor != null)
                {
                    GameObject? prefab = descriptor.GetSourceObject();
                    if (prefab != null)
                        result = prefab.GetComponent<DynamicItemObject>() != null;
                }
            }
        }
        catch { result = null; }

        cache[name] = result;
        return result;
    }

    private void LogWalkSummary(DataLayerWalkResult walk)
    {
        if (walk.CapHit)
            Plugin.Logger.LogWarning($"[Vacuum] SCAN: safety cap hit at {DataLayerRecordCap} records - the walk was stopped early. All counts below are a FLOOR, not a total.");

        Plugin.Logger.LogInfo($"[Vacuum] SCAN traversal: tiles seen={walk.TilesSeen} cells seen={walk.CellsSeen} cells with inventory container={walk.CellsWithContainer} buffers seen={walk.BuffersSeen} total records={walk.TotalRecords}.");
        Plugin.Logger.LogInfo($"[Vacuum] SCAN skipped: tiles={walk.TilesSkipped} cells={walk.CellsSkipped} buffers={walk.BuffersSkipped}.");
        foreach (var kv in walk.RecordsByBufferClass)
            Plugin.Logger.LogInfo($"[Vacuum] SCAN records by buffer class x{kv.Value}: {kv.Key}");
        foreach (var kv in walk.MatchesByBufferClass)
            Plugin.Logger.LogInfo($"[Vacuum] SCAN matches by buffer class x{kv.Value}: {kv.Key}");
        Plugin.Logger.LogInfo($"[Vacuum] SCAN dropped-item filter (DynamicItemObject prefab check): excluded {walk.DroppedCheckExcluded} record(s), undetermined/fail-closed {walk.DroppedCheckUndetermined} record(s).");
        {
            var excludedSorted = new List<KeyValuePair<string, int>>(walk.DroppedCheckExcludedNameCounts);
            excludedSorted.Sort((a, b) => b.Value.CompareTo(a.Value));
            int excludedToLog = Math.Min(40, excludedSorted.Count);
            for (int i = 0; i < excludedToLog; i++)
                Plugin.Logger.LogInfo($"[Vacuum] SCAN dropped-item filter excluded x{excludedSorted[i].Value}: {excludedSorted[i].Key}");
            Plugin.Logger.LogInfo($"[Vacuum] SCAN dropped-item filter distinct excluded names: {excludedSorted.Count}.");
        }
        Plugin.Logger.LogInfo($"[Vacuum] SCAN matched {walk.MatchCount} record(s) against OnlyItems/ExcludeItems/ExcludeCategories + dropped-item filter.");

        var sortedNames = new List<KeyValuePair<string, int>>(walk.MatchNameCounts);
        sortedNames.Sort((a, b) => b.Value.CompareTo(a.Value));
        int namesToLog = Math.Min(40, sortedNames.Count);
        for (int i = 0; i < namesToLog; i++)
            Plugin.Logger.LogInfo($"[Vacuum] SCAN match x{sortedNames[i].Value}: {sortedNames[i].Key}");
        Plugin.Logger.LogInfo($"[Vacuum] SCAN distinct matched names: {sortedNames.Count}.");

        string maxDistStr = walk.HaveMatchPos ? walk.MaxMatchDist.ToString("F0") : "n/a";
        Plugin.Logger.LogInfo($"[Vacuum] SCAN largest horizontal distance from player among matches (Y ignored): {maxDistStr}m.");
    }

    // v1.4.0 whole-map sweep (VacuumEntireWorld=true). Two phases, in order:
    //   Phase 1 - object layer: the mod's own tracked-item set, radius test skipped, removed via
    //             the proven RemoveCandidates() (WorldItemObject.RemoveObjectFromWorld(),
    //             confirmed in-game since v1.0.1). Clears anything with a spawned GameObject.
    //   Phase 2 - data layer: walk every tile/cell/buffer, collect matches, then for each
    //             re-find its live index (removal reorders buffers), fetch the WorldItemInstance,
    //             check IsRemovable, and call its own Destroy(ref bool, ref InstanceDestructionLevel).
    // Honours DryRun (full walk + would-remove counts logged, nothing removed) and HostOnly (real
    // removal only; a DryRun walk stays available to anyone). Never caches manager/handler/tiles/
    // cells/buffers/instances across sweeps - all resolved fresh in this one call.
    private void DataLayerSweep(bool auto)
    {
        bool dryRun = Plugin.DryRun.Value;
        Plugin.Logger.LogInfo($"[Vacuum] Whole-map sweep (VacuumEntireWorld=true, {(dryRun ? "DRY-RUN" : "LIVE")}): starting whole-map item-data walk.");

        var onlyItems = ParseCsv(Plugin.OnlyItems.Value);

        bool hostBlocked = !dryRun && Plugin.HostOnly.Value && !IsHost();
        bool canRemove = !dryRun && !hostBlocked;

        var excludeItems = ParseCsv(Plugin.ExcludeItems.Value);
        var excludeCats = ParseCsv(Plugin.ExcludeCategories.Value);

        Vector3 playerPos = Vector3.zero;
        bool havePlayer = Plugin.LocalPlayer != null && Plugin.LocalPlayer.gameObject != null;
        if (havePlayer) playerPos = Plugin.LocalPlayer!.transform.position;

        WorldDataManager? manager = null;
        try { manager = UnityEngine.Object.FindAnyObjectByType<WorldDataManager>(); }
        catch (Exception ex) { Plugin.Logger.LogError($"[Vacuum] SCAN: WorldDataManager lookup failed: {ex}"); }
        if (manager == null)
        {
            Plugin.Logger.LogWarning("[Vacuum] SCAN: no WorldDataManager found - not in a loaded world?");
            ShowMessage("Vacuum SCAN: world data not ready.");
            return;
        }

        InventoryItemDataHandler? handler = null;
        try { handler = manager.GetDataHandler<InventoryItemDataHandler>(WorldDataSlot.INVENTORY); }
        catch (Exception ex) { Plugin.Logger.LogError($"[Vacuum] SCAN: GetDataHandler<InventoryItemDataHandler> failed: {ex}"); }
        if (handler == null)
        {
            Plugin.Logger.LogWarning("[Vacuum] SCAN: could not resolve InventoryItemDataHandler.");
            ShowMessage("Vacuum SCAN: could not resolve item data handler.");
            return;
        }

        int slotId;
        try { slotId = handler.GetSlotId(); }
        catch (Exception ex) { Plugin.Logger.LogError($"[Vacuum] SCAN: GetSlotId() failed: {ex}"); return; }

        Il2CppSystem.Collections.Generic.List<WorldTileData>? tiles;
        try { tiles = manager.FetchAllData(); }
        catch (Exception ex) { Plugin.Logger.LogError($"[Vacuum] SCAN: FetchAllData() failed: {ex}"); return; }

        int tileCount = tiles?.Count ?? -1;
        if (tiles == null || tileCount <= 0)
        {
            Plugin.Logger.LogWarning("[Vacuum] SCAN: no tiles returned - nothing to scan.");
            ShowMessage("Vacuum SCAN: no tiles found.");
            return;
        }

        // Phase 1 - object layer (reuses the proven candidate build/filter/remove helpers).
        var objAll = BuildCandidates(trace: false);
        var objTargets = FilterCandidates(objAll, entireWorld: true, playerPos, out _);
        int phase1Matched = objTargets.Count;
        int phase1Removed = 0;
        if (canRemove)
        {
            phase1Removed = RemoveCandidates(objTargets);
            Plugin.Logger.LogInfo($"[Vacuum] Phase 1 (object layer, tracked items): removed {phase1Removed}/{phase1Matched}.");
        }
        else
        {
            string why = dryRun ? "DRY-RUN" : "HostOnly - not host";
            Plugin.Logger.LogInfo($"[Vacuum] Phase 1 (object layer, tracked items): {phase1Matched} matched, 0 removed ({why}).");
        }

        // Phase 2 - data layer: walk + collect matches (always, so a DryRun/non-host run still
        // reports full scan results), then remove when allowed. v1.7.0: droppedItemCache lives only
        // for this one Sweep() call, shared across the walk and the self-verification re-walk below
        // - never a static field, never cached across sweeps.
        var droppedItemCache = new Dictionary<string, bool?>();
        var walk = WalkDataLayer(tiles, slotId, onlyItems, excludeItems, excludeCats, playerPos, collectMatches: true, handler, droppedItemCache);
        LogWalkSummary(walk);
        Plugin.Logger.LogInfo("[Vacuum] SCAN: the corpse pass is unaffected by this mode (object-layer, radius-gated, unchanged).");

        if (onlyItems.Count == 0 && !dryRun)
            Plugin.Logger.LogWarning($"[Vacuum] LIVE: no OnlyItems allow-list is set - every non-excluded record map-wide will be removed. Matched {walk.MatchCount} record(s).");

        if (!canRemove)
        {
            string why = dryRun
                ? "DRY-RUN: no Phase 1 or Phase 2 removal performed. Set DryRun=false (and be host, if HostOnly) to remove."
                : "HostOnly is set and this client is not host: no Phase 1 or Phase 2 removal performed.";
            Plugin.Logger.LogInfo($"[Vacuum] {why}");
            if (!auto && hostBlocked) ShowMessage("Only the host can vacuum ground items.");
            else ShowMessage($"Vacuum SCAN (whole-map data): {phase1Matched} object-layer + {walk.MatchCount} data-layer matching record(s) found. Nothing removed.");
            return;
        }

        // Phase 2 removal. v1.5.0: v1.4.0's single Destroy(ref bool, ref InstanceDestructionLevel)
        // call was confirmed in-game 2026-08-10 to return without throwing yet leave the record in
        // the buffer (see docs/mods/ground-item-vacuum.md). This version tries a ladder of removal
        // routes per record, in order, re-checking with FindIndexOfUniqueId after each attempt and
        // stopping at the first route that actually removes the record. Buffer contents reorder on
        // removal, so the index is re-found immediately before each buffer-touching call rather
        // than trusting the index captured during the walk above.
        int route1Removed = 0, route2Removed = 0, route3Removed = 0;
        int route1Failed = 0, route2Failed = 0, route3Failed = 0;
        int alreadyGone = 0, notRemovable = 0, stillPresent = 0;
        var matches = walk.Matches ?? new List<PendingRemoval>();
        var invClassPtr = Il2CppClassPointerStore<InventoryItemInstance>.NativeClassPtr;

        foreach (var rec in matches)
        {
            uint uid = rec.UniqueId;
            int index = rec.Buffer.FindIndexOfUniqueId(ref uid);
            if (index < 0) { alreadyGone++; continue; }

            WorldItemInstance? instance = handler.GetInstance(rec.Cell, rec.BufferId, index, false, false);
            if (instance == null) { alreadyGone++; continue; }

            if (!instance.IsRemovable(InstanceDestructionLevel.SYSTEM)) { notRemovable++; continue; }

            // Route 1: Destroy with silent=false (v1.4.0 passed true; this tests the other value).
            try
            {
                bool flag = false;
                var level = InstanceDestructionLevel.SYSTEM;
                instance.Destroy(ref flag, ref level);
            }
            catch { route1Failed++; }

            uid = rec.UniqueId;
            if (rec.Buffer.FindIndexOfUniqueId(ref uid) < 0) { route1Removed++; continue; }

            // Route 2: handler.RemoveInstanceDataSilent(InventoryItemInstance). instance is the
            // base WorldItemInstance type - managed casts lie for interop objects obtained under a
            // base declared type, so convert by native class pointer before calling.
            bool triedRoute2 = false;
            try
            {
                if ((object)instance is Il2CppObjectBase iob)
                {
                    var cls = IL2CPP.il2cpp_object_get_class(iob.Pointer);
                    if (IL2CPP.il2cpp_class_is_subclass_of(cls, invClassPtr, false))
                    {
                        triedRoute2 = true;
                        var invInstance = new InventoryItemInstance(iob.Pointer);
                        handler.RemoveInstanceDataSilent(invInstance);
                    }
                }
            }
            catch { route2Failed++; }

            if (triedRoute2)
            {
                uid = rec.UniqueId;
                if (rec.Buffer.FindIndexOfUniqueId(ref uid) < 0) { route2Removed++; continue; }
            }

            // Route 3: raw buffer edit. Re-find the index immediately before calling - this call
            // itself reorders the buffer by swapping.
            try
            {
                uid = rec.UniqueId;
                int idxNow = rec.Buffer.FindIndexOfUniqueId(ref uid);
                if (idxNow >= 0)
                {
                    rec.Buffer.RemoveInstanceData(idxNow, out int _swapIndex);
                }
            }
            catch { route3Failed++; }

            uid = rec.UniqueId;
            if (rec.Buffer.FindIndexOfUniqueId(ref uid) < 0) { route3Removed++; continue; }

            stillPresent++;
        }

        int phase2Removed = route1Removed + route2Removed + route3Removed;
        Plugin.Logger.LogInfo($"[Vacuum] Phase 2 (data layer): matched {matches.Count}, already gone {alreadyGone}, not removable {notRemovable}.");
        Plugin.Logger.LogInfo($"[Vacuum] Phase 2 route 1 (Destroy silent=false): removed {route1Removed}, failed {route1Failed}.");
        Plugin.Logger.LogInfo($"[Vacuum] Phase 2 route 2 (RemoveInstanceDataSilent): removed {route2Removed}, failed {route2Failed}.");
        Plugin.Logger.LogInfo($"[Vacuum] Phase 2 route 3 (buffer RemoveInstanceData): removed {route3Removed}, failed {route3Failed}.");
        Plugin.Logger.LogInfo($"[Vacuum] Phase 2 still present after all routes: {stillPresent}.");

        // Self-verification: re-walk with the same filter and count what's left.
        var verify = WalkDataLayer(tiles, slotId, onlyItems, excludeItems, excludeCats, playerPos, collectMatches: false, handler, droppedItemCache);
        Plugin.Logger.LogInfo($"[Vacuum] Self-verification: matched before removal={walk.MatchCount}, removal calls succeeded={phase2Removed}, matched remaining after={verify.MatchCount}.");

        ShowMessage($"Vacuum whole-map: removed {phase1Removed} (object) + {phase2Removed} (data) of {phase1Matched + walk.MatchCount} matched. See log.");
    }

    private void AddCandidate(DynamicItemObject node, int idx, bool trace, List<Candidate> outList)
    {
        if (trace) Plugin.Logger.LogInfo($"[Vacuum] trace #{idx}: node ok");

        var itemObj = node._itemObject;
        if (itemObj == null) { if (trace) Plugin.Logger.LogInfo($"[Vacuum] trace #{idx}: _itemObject null - skip"); return; }
        if (trace) Plugin.Logger.LogInfo($"[Vacuum] trace #{idx}: _itemObject ok");

        string name = "?";
        string catChain = "";
        var item = itemObj.ItemInstance;
        if (trace) Plugin.Logger.LogInfo($"[Vacuum] trace #{idx}: ItemInstance {(item == null ? "null" : "ok")}");
        var info = item?.info;
        if (info != null)
        {
            name = info.Name ?? "?";
            if (trace) Plugin.Logger.LogInfo($"[Vacuum] trace #{idx}: name={name}");
            catChain = BuildCategoryChain(info.category);
            if (trace) Plugin.Logger.LogInfo($"[Vacuum] trace #{idx}: category='{catChain}'");
        }

        Vector3 pos = node.transform.position;
        if (trace) Plugin.Logger.LogInfo($"[Vacuum] trace #{idx}: pos ok ({pos.x:F0},{pos.z:F0})");

        outList.Add(new Candidate { Item = itemObj, Name = name, CatChain = catChain, Pos = pos });
    }

    private static string BuildCategoryChain(ItemCategoryInfo? cat)
    {
        if (cat == null) return "";
        var sb = new StringBuilder();
        int depth = 0;
        var c = cat;
        while (c != null && depth++ < 8)
        {
            string n = "";
            try { n = c.Name ?? ""; } catch { }
            if (!string.IsNullOrEmpty(n))
            {
                if (sb.Length > 0) sb.Append('/');
                sb.Append(n);
            }
            try { c = c.parent; } catch { break; }
        }
        return sb.ToString();
    }

    private bool IsHost()
    {
        try
        {
            var p = Plugin.LocalPlayer;
            if (p == null || p.NetworkObject == null || p.NetworkObject.Runner == null) return false;
            var runner = p.NetworkObject.Runner;
            return runner.IsServer || runner.IsSharedModeMasterClient;
        }
        catch { return false; }
    }

    private static List<string> ParseCsv(string raw)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(raw)) return result;
        foreach (var part in raw.Split(','))
        {
            var t = part.Trim();
            if (t.Length > 0) result.Add(t.ToLowerInvariant());
        }
        return result;
    }

    private static bool ContainsAny(string haystack, List<string> needles)
    {
        if (needles.Count == 0 || string.IsNullOrEmpty(haystack)) return false;
        string h = haystack.ToLowerInvariant();
        foreach (var n in needles)
            if (h.Contains(n)) return true;
        return false;
    }

    private void ShowMessage(string message)
    {
        _guiMessage = message;
        _guiExpiry = Time.time + 5.0f;
    }

    // Typing guard: keystrokes in the game's text fields (e.g. structure rename) also reach
    // Input.GetKeyDown, so letter-bound hotkeys fire while the player types. Skip hotkey handling
    // whenever the UI's selected object is a text input. (confirmed leak 2026-07-10)
    private static bool IsTextInputFocused()
    {
        try
        {
            var es = UnityEngine.EventSystems.EventSystem.current;
            var go = es != null ? es.currentSelectedGameObject : null;
            if (go == null) return false;
            return go.GetComponent<TMPro.TMP_InputField>() != null
                || go.GetComponent<UnityEngine.UI.InputField>() != null;
        }
        catch { return false; }
    }

    private void OnGUI()
    {
        if (Time.time < _guiExpiry && !string.IsNullOrEmpty(_guiMessage))
        {
            var shadow = new GUIStyle(GUI.skin.label)
            {
                fontSize = 24,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter
            };
            shadow.normal.textColor = Color.black;
            var text = new GUIStyle(shadow);
            text.normal.textColor = Color.cyan;

            GUI.Label(new Rect(0, 81, Screen.width, 100), _guiMessage, shadow);
            GUI.Label(new Rect(0, 80, Screen.width, 100), _guiMessage, text);
        }
    }
}
