using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using SSSGame;
using UnityEngine;

namespace GroundItemAuditMod;

// The probe itself. Every numbered step is independently try/caught so one failure still lets the
// rest of the dump print - a probe must never go silent partway through the one run that answers
// the question. READ-ONLY: no writes, no deletes, no activation/deactivation of anything.
internal static class ItemDataDump
{
    private const int RecordCap = 250000;

    public static void Run()
    {
        var log = Plugin.Logger;
        log.LogInfo("========== GROUND ITEM AUDIT BEGIN ==========");

        // Step 1 - player position.
        Vector3 pos = default;
        bool havePos = false;
        try
        {
            var pc = UnityEngine.Object.FindAnyObjectByType<PlayerCharacter>();
            if (pc == null)
            {
                log.LogWarning("[GroundItemAudit] No PlayerCharacter found - not in a loaded world?");
                log.LogInfo("========== GROUND ITEM AUDIT END ==========");
                return;
            }
            pos = pc.transform.position;
            havePos = true;
        }
        catch (Exception ex)
        {
            log.LogWarning($"[GroundItemAudit] Player position lookup failed: {ex.Message}");
        }
        if (!havePos)
        {
            log.LogInfo("========== GROUND ITEM AUDIT END ==========");
            return;
        }

        // Step 2 - find the manager.
        WorldDataManager? manager = null;
        try
        {
            manager = UnityEngine.Object.FindAnyObjectByType<WorldDataManager>();
            if (manager == null)
            {
                log.LogWarning("[GroundItemAudit] No WorldDataManager found - not in a loaded world?");
                log.LogInfo("========== GROUND ITEM AUDIT END ==========");
                return;
            }

            int dataMapCount = -1;
            try { dataMapCount = manager._dataMap.Count; } catch { }
            log.LogInfo($"[GroundItemAudit] WorldDataManager: TileSize={manager.TileSize} "
                        + $"CellSize={manager.CellSize} CellResolution={manager.CellResolution} "
                        + $"_dataMap.Count={dataMapCount}");
        }
        catch (Exception ex)
        {
            log.LogError($"[GroundItemAudit] manager lookup/header failed: {ex}");
            log.LogInfo("========== GROUND ITEM AUDIT END ==========");
            return;
        }

        // Step 3 - activation-range config. Logged unconditionally (even zeros) to retire a
        // documented ⚠️ pending inference about which fields drive the object-layer activation
        // radius.
        try
        {
            var cfg = manager._dataConfig;
            if (cfg == null)
            {
                log.LogWarning("[GroundItemAudit] manager._dataConfig is null.");
            }
            else
            {
                log.LogInfo($"[GroundItemAudit] WorldDataConfiguration: interactionObjectsRange="
                            + $"{cfg.interactionObjectsRange} closeRangeScale={cfg.closeRangeScale} "
                            + $"nearRangeScale={cfg.nearRangeScale} farRangeScale={cfg.farRangeScale}");
            }
        }
        catch (Exception ex)
        {
            log.LogError($"[GroundItemAudit] activation-range config read failed: {ex}");
        }

        // Step 4 - resolve the inventory data handler.
        InventoryItemDataHandler? handler = null;
        try
        {
            try
            {
                var h = manager.GetDataHandler<InventoryItemDataHandler>(WorldDataSlot.INVENTORY);
                if (h != null)
                {
                    handler = h;
                    log.LogInfo("[GroundItemAudit] route GetDataHandler<InventoryItemDataHandler>: returned an object.");
                }
                else
                {
                    log.LogInfo("[GroundItemAudit] route GetDataHandler<InventoryItemDataHandler>: null.");
                }
            }
            catch (Exception ex)
            {
                log.LogInfo("[GroundItemAudit] route GetDataHandler<InventoryItemDataHandler>: threw "
                            + $"{ex.GetType().Name}: {ex.Message}");
            }

            if (handler == null)
            {
                try
                {
                    var handlers = manager._dataHandlers;
                    bool found = false;
                    if (handlers != null)
                    {
                        for (int i = 0; i < handlers.Count; i++)
                        {
                            var h = handlers[i];
                            if (h == null) continue;
                            if ((object)h is Il2CppObjectBase ob)
                            {
                                var cls = IL2CPP.il2cpp_object_get_class(ob.Pointer);
                                var target = Il2CppClassPointerStore<InventoryItemDataHandler>.NativeClassPtr;
                                if (IL2CPP.il2cpp_class_is_subclass_of(cls, target, false))
                                {
                                    handler = new InventoryItemDataHandler(ob.Pointer);
                                    found = true;
                                    break;
                                }
                            }
                        }
                    }
                    log.LogInfo($"[GroundItemAudit] route _dataHandlers walk: {(found ? "found a match" : "no match")}.");
                }
                catch (Exception ex)
                {
                    log.LogInfo($"[GroundItemAudit] route _dataHandlers walk: threw {ex.GetType().Name}: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            log.LogError($"[GroundItemAudit] handler resolution failed: {ex}");
        }

        if (handler == null)
        {
            log.LogWarning("[GroundItemAudit] Could not resolve an InventoryItemDataHandler via any route.");
        }
        else
        {
            // v0.1 comparison line - the instance list is NOT the world's item store (105 and 9
            // records seen in two in-game dumps, all already spawned, none within 60m of the
            // player). Reported once for contrast; not walked any further here.
            try
            {
                var list = handler._instanceList;
                int listCount = list?.Count ?? -1;
                log.LogInfo($"[GroundItemAudit] v0.1 handler._instanceList.Count (NOT the item "
                            + $"store) = {listCount}");
            }
            catch (Exception ex)
            {
                log.LogError($"[GroundItemAudit] _instanceList count read failed: {ex}");
            }

            // Step 5 - walk the per-cell inventory buffers, the real record store.
            try
            {
                WalkPerCellBuffers(handler, manager, pos, log);
            }
            catch (Exception ex)
            {
                log.LogError($"[GroundItemAudit] per-cell buffer walk failed: {ex}");
            }
        }

        log.LogInfo("========== GROUND ITEM AUDIT END ==========");
    }

    private static void WalkPerCellBuffers(InventoryItemDataHandler handler, WorldDataManager manager,
        Vector3 playerPos, BepInEx.Logging.ManualLogSource log)
    {
        // Step 1 of the walk - slot id.
        int slotId;
        try
        {
            slotId = handler.GetSlotId();
            log.LogInfo($"[GroundItemAudit] slotId={slotId}");
        }
        catch (Exception ex)
        {
            log.LogError($"[GroundItemAudit] GetSlotId() failed: {ex}");
            return;
        }

        // Step 2 of the walk - every tile via FetchAllData (183 tiles, no _dataMap.Count change,
        // in the v0.1 run).
        Il2CppSystem.Collections.Generic.List<WorldTileData>? tiles;
        try
        {
            tiles = manager.FetchAllData();
        }
        catch (Exception ex)
        {
            log.LogError($"[GroundItemAudit] FetchAllData() failed: {ex}");
            return;
        }

        int tileCount = tiles?.Count ?? -1;
        log.LogInfo($"[GroundItemAudit] FetchAllData tile count={tileCount}");
        if (tiles == null || tileCount <= 0)
        {
            log.LogWarning("[GroundItemAudit] No tiles returned - nothing to walk.");
            return;
        }

        int tilesSeen = 0;
        int cellsSeen = 0;
        int cellsWithContainer = 0;
        int buffersSeen = 0;
        int totalRecords = 0;

        int tilesSkipped = 0;
        int cellsSkipped = 0;
        int buffersSkipped = 0;

        bool bufferIterationLogged = false;

        string[] bandLabels = { "0-30", "30-60", "60-120", "120-256", "256-512", "512+" };
        float[] bandUpperBounds = { 30f, 60f, 120f, 256f, 512f, float.MaxValue };
        int[] bandTotal = new int[bandLabels.Length];

        float minX = float.MaxValue, maxX = float.MinValue;
        float minZ = float.MaxValue, maxZ = float.MinValue;
        float maxDist = float.MinValue;
        bool haveAnyPos = false;

        var nameCounts = new Dictionary<string, int>();

        bool capHit = false;

        foreach (var tile in tiles)
        {
            if (capHit) break;
            try
            {
                if (tile == null) { tilesSkipped++; continue; }
                tilesSeen++;

                var cellList = new Il2CppSystem.Collections.Generic.List<WorldDataCell>();
                tile.QueryAllCells(DataAccessMode.FETCH,
                    cellList.Cast<Il2CppSystem.Collections.Generic.ICollection<WorldDataCell>>());

                foreach (var cell in cellList)
                {
                    if (capHit) break;
                    try
                    {
                        if (cell == null) { cellsSkipped++; continue; }
                        cellsSeen++;

                        CellDataContainer? container = null;
                        try
                        {
                            container = cell.GetDataContainer(slotId);
                        }
                        catch
                        {
                            cellsSkipped++;
                            continue;
                        }
                        if (container == null) continue;

                        InventoryCellDataContainer? inv = null;
                        if ((object)container is Il2CppObjectBase cob)
                        {
                            var cls = IL2CPP.il2cpp_object_get_class(cob.Pointer);
                            var target = Il2CppClassPointerStore<InventoryCellDataContainer>.NativeClassPtr;
                            if (IL2CPP.il2cpp_class_is_subclass_of(cls, target, false))
                            {
                                inv = new InventoryCellDataContainer(cob.Pointer);
                            }
                        }
                        if (inv == null) continue;

                        cellsWithContainer++;

                        var itemBuffers = inv.itemBuffers;
                        if (itemBuffers == null) continue;

                        foreach (var buffer in itemBuffers.Values)
                        {
                            if (capHit) break;
                            try
                            {
                                if (!bufferIterationLogged)
                                {
                                    log.LogInfo("[GroundItemAudit] itemBuffers.Values foreach: succeeded.");
                                    bufferIterationLogged = true;
                                }

                                if (buffer == null) { buffersSkipped++; continue; }
                                buffersSeen++;

                                int size = buffer.GetSize();
                                for (int idx = 0; idx < size; idx++)
                                {
                                    if (totalRecords >= RecordCap)
                                    {
                                        capHit = true;
                                        break;
                                    }

                                    int i = idx;
                                    Vector3 p;
                                    string name = "?";
                                    try
                                    {
                                        p = buffer.GetPosAt(ref i);
                                        int i2 = idx;
                                        var item = buffer.GetItemAt(ref i2);
                                        try
                                        {
                                            if (item != null && item.info != null
                                                && item.info.Name != null)
                                            {
                                                name = item.info.Name;
                                            }
                                        }
                                        catch { name = "?"; }
                                    }
                                    catch
                                    {
                                        continue;
                                    }

                                    totalRecords++;

                                    float dx = p.x - playerPos.x;
                                    float dz = p.z - playerPos.z;
                                    float horizDist = Mathf.Sqrt(dx * dx + dz * dz);

                                    for (int b = 0; b < bandUpperBounds.Length; b++)
                                    {
                                        if (horizDist < bandUpperBounds[b])
                                        {
                                            bandTotal[b]++;
                                            break;
                                        }
                                    }

                                    if (p.x < minX) minX = p.x;
                                    if (p.x > maxX) maxX = p.x;
                                    if (p.z < minZ) minZ = p.z;
                                    if (p.z > maxZ) maxZ = p.z;
                                    if (horizDist > maxDist) maxDist = horizDist;
                                    haveAnyPos = true;

                                    nameCounts.TryGetValue(name, out int c);
                                    nameCounts[name] = c + 1;
                                }
                            }
                            catch
                            {
                                buffersSkipped++;
                                continue;
                            }
                        }
                    }
                    catch
                    {
                        cellsSkipped++;
                        continue;
                    }
                }
            }
            catch
            {
                tilesSkipped++;
                continue;
            }
        }

        if (capHit)
        {
            log.LogWarning($"[GroundItemAudit] SAFETY CAP HIT at {RecordCap} records - the walk "
                            + "was stopped early. All counts below are a FLOOR, not a total.");
        }

        log.LogInfo($"[GroundItemAudit] traversal: tiles seen={tilesSeen} cells seen={cellsSeen} "
                    + $"cells with inventory container={cellsWithContainer} buffers seen={buffersSeen} "
                    + $"total records={totalRecords}");
        log.LogInfo($"[GroundItemAudit] skipped: tiles={tilesSkipped} cells={cellsSkipped} "
                    + $"buffers={buffersSkipped}");

        for (int b = 0; b < bandLabels.Length; b++)
        {
            log.LogInfo($"[GroundItemAudit] band {bandLabels[b]}m: count={bandTotal[b]}");
        }

        if (haveAnyPos)
        {
            log.LogInfo($"[GroundItemAudit] position bounds: minX={minX} maxX={maxX} minZ={minZ} maxZ={maxZ}");
            log.LogInfo($"[GroundItemAudit] largest horizontal distance from player: {maxDist}");
        }
        else
        {
            log.LogInfo("[GroundItemAudit] no readable positions found - bounds/max distance unavailable.");
            maxDist = 0f;
        }

        int maxNames = Plugin.MaxNamesLogged.Value;
        var sortedNames = new List<KeyValuePair<string, int>>(nameCounts);
        sortedNames.Sort((a, b) => b.Value.CompareTo(a.Value));
        int namesToLog = Math.Min(maxNames, sortedNames.Count);
        for (int i = 0; i < namesToLog; i++)
        {
            log.LogInfo($"[GroundItemAudit] name '{sortedNames[i].Key}': count={sortedNames[i].Value}");
        }
        log.LogInfo($"[GroundItemAudit] distinct names total: {sortedNames.Count}");

        int listCountForHeadline = -1;
        try { listCountForHeadline = handler._instanceList?.Count ?? -1; } catch { }

        log.LogInfo($"[GroundItemAudit] HEADLINE: total records={totalRecords} "
                    + $"_instanceList.Count={listCountForHeadline} "
                    + $"largest horizontal distance from player={maxDist}");
    }
}
