using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime.InteropTypes;
using SandSailorStudio.Inventory;
using SSSGame;
using UnityEngine;

namespace CraftFromStorageMod;

// Phase 0 (idea-17) settlement storage census: proves the Phase 1 READ half (can we see settlement-
// wide resources + per-storage breakdowns at all). Hotkey-driven, entirely read-only - never writes
// inventory. Registered as an injected MonoBehaviour the same way other mods in this repo register
// their tracker components (GroundItemVacuumMod/VacuumTracker.cs, OuthouseComposterMod/ComposterDiag.cs).
public class StorageCensus : MonoBehaviour
{
    private KeyCode _key = KeyCode.F12;
    private string _keyRaw = "";
    private float _cfgReloadTimer;

    private void Start()
    {
        ApplyHotkey();
    }

    // Re-binds _key from config. No-op unless the raw config string changed (SeedScout/Vacuum pattern).
    private void ApplyHotkey()
    {
        string raw = Plugin.CensusHotkey.Value ?? "";
        if (raw == _keyRaw) return;
        _keyRaw = raw;
        if (Enum.TryParse<KeyCode>(raw, true, out var parsed))
            _key = parsed;
        else
            Plugin.Logger.LogWarning($"[CFS] Could not parse CensusHotkey '{raw}'. Keeping '{_key}'.");
        Plugin.Logger.LogInfo($"[CFS] Census hotkey bound to {_key}.");
    }

    private void Update()
    {
        // Live config pickup so CensusHotkey can be changed mid-session without a relaunch.
        _cfgReloadTimer += Time.deltaTime;
        if (_cfgReloadTimer >= 30f)
        {
            _cfgReloadTimer = 0f;
            try { Plugin.Cfg?.Reload(); } catch { }
            ApplyHotkey();
        }

        if (!IsTextInputFocused() && Input.GetKeyDown(_key))
        {
            try { RunCensus(); }
            catch (Exception ex) { Plugin.Logger.LogError($"[CFS] Census error: {ex}"); }
        }
    }

    private void RunCensus()
    {
        if (Plugin.LocalPlayer == null)
        {
            Plugin.Logger.LogInfo("[CFS] Census: local player not ready yet - skipping.");
            return;
        }

        Plugin.Logger.LogInfo("[CFS] === Settlement storage census start ===");

        Settlement? settlement = ResolveSettlement(out string via);
        if (settlement == null)
        {
            Plugin.Logger.LogInfo("[CFS] Census: no settlement resolved via GetPlayerSettlement/GetCurrentSettlement/worldSettlement.");
            Plugin.Logger.LogInfo("[CFS] === Settlement storage census end ===");
            return;
        }
        Plugin.Logger.LogInfo($"[CFS] Census: settlement resolved via {via}.");

        // -- Settlement.QuerySettlementResources() - HANGS THE GAME. Default OFF. --
        // v0.1.1 froze the main thread here (confirmed in-game 2026-07-20: Windows logged AppHangB1,
        // NOT an access violation - no crash dump, no managed exception; the log ends on the
        // "settlement resolved" line immediately above). A managed try/catch cannot rescue a hang.
        // This primitive was only ever SIGNATURE-confirmed (interop dump 2026-06-21), never called
        // in-game by any mod in this repo. The per-structure walk below is the proven replacement.
        // Left behind an opt-in flag purely to discriminate WHICH call hangs, if we ever care: the
        // three markers below bracket QuerySettlementResources() and GetTotalQuantity() separately,
        // so the last surviving log line names the exact call that froze.
        if (Plugin.CensusTryQuerySettlementResources.Value)
        {
            Plugin.Logger.LogWarning("[CFS] Census: about to call QuerySettlementResources() - THIS HUNG THE GAME in v0.1.1.");
            try
            {
                ItemManifest? resources = settlement.QuerySettlementResources();
                Plugin.Logger.LogInfo("[CFS] Census: QuerySettlementResources() RETURNED - about to call GetTotalQuantity().");
                int total = resources != null ? resources.GetTotalQuantity() : -1;
                Plugin.Logger.LogInfo($"[CFS] Census: GetTotalQuantity() RETURNED = {total}.");
            }
            catch (Exception ex) { Plugin.Logger.LogError($"[CFS] Census: QuerySettlementResources error: {ex}"); }
        }
        else
        {
            Plugin.Logger.LogInfo("[CFS] Census: QuerySettlementResources SKIPPED (hangs the game - see CensusTryQuerySettlementResources).");
        }

        // -- Per-structure storage census via GetStructures() - the PROVEN enumeration. --
        // GetStructures() is confirmed in-game by four mods in this repo (SupplyChainMod StationWalker,
        // OuthouseComposterMod, TreeRespawnMod StructureQuery/WellRefill). Deliberately NOT using
        // Settlement.GetStorageSites(): its IReadOnlyList stub exposes only an indexer, forcing a
        // scan-past-the-end-until-it-throws pattern - and indexing past the end of an IL2CPP list is
        // not guaranteed to raise a *managed* exception, which is exactly the class of move that just
        // hung the game. Per-structure singular GetComponent walks are the in-game-proven primitive
        // (per-station inventory reads measured at 0.2-5.1 ms, full 31-station pass 6-20 ms).
        //
        // v0.2.0 census v2: the v0.1.2 walk took only the FIRST ItemContainerComponent per structure,
        // which provably missed real crafting-station storage (Workshop House 2 etc. reported a
        // decorative CharacterFlask instead - architecture.md "Settlement storage census ground
        // truth"). CollectComponents<T> now enumerates ALL containers per structure, each tagged with
        // a structural equip-slot probe so equipment-managed containers (armor racks, character
        // flasks) are identified and excluded from the grand total instead of name-blacklisted.
        try
        {
            Il2CppSystem.Collections.Generic.List<Structure>? structures = null;
            try { structures = settlement.GetStructures(); } catch { }
            int structureCount = 0;
            try { structureCount = structures?.Count ?? 0; } catch { }
            Plugin.Logger.LogInfo($"[CFS] Census: {structureCount} total structure(s) - walking for containers.");

            int containerCount = 0;
            int equipManagedCount = 0;
            int stationCount = 0;
            int grandTotal = 0;
            if (structures != null)
            {
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

                        containerCount++;
                        int capacity = 0;
                        int qty = 0;
                        string typeName = "?";
                        string nodeName = "?";
                        try { capacity = container.capacity; } catch { }
                        try { typeName = container.containerType?.name ?? "?"; } catch { }
                        try { nodeName = icc.gameObject.name ?? "?"; } catch { }
                        // Proven slot-iteration pattern (OuthouseComposterMod/ComposterDiag.cs CountPool,
                        // confirmed in-game): ItemContainer.GetItems() exposes only an indexer through the
                        // compile-time reference, so iterate bounded by capacity and break on throw.
                        try
                        {
                            var items = container.GetItems();
                            int bound = capacity > 0 ? capacity : 64;
                            for (int slot = 0; slot < bound; slot++)
                            {
                                Item? it = null;
                                try { it = items != null ? items[slot] : null; } catch { break; }
                                if (it == null) continue;
                                try { qty += it.count; } catch { }
                            }
                        }
                        catch { }

                        string equipStatus = "n/a";
                        if (Plugin.IncludeEquipmentProbe.Value)
                        {
                            try { equipStatus = ProbeEquipStatus(icc.transform, container); }
                            catch { equipStatus = "no"; }
                        }

                        bool excluded = equipStatus == "yes-equipPoint" || equipStatus == "yes-managerAncestor";
                        if (excluded) equipManagedCount++;
                        else grandTotal += qty;

                        Plugin.Logger.LogInfo($"[CFS] Census:   storage: owner='{owner}' node='{nodeName}' type='{typeName}' " +
                            $"capacity={capacity} items={qty} equip={equipStatus}");
                    }

                    if (Plugin.IncludeWorkstationStock.Value)
                    {
                        Workstation? ws = null;
                        try { ws = FindComponent<Workstation>(st.transform); } catch { }
                        if (ws != null)
                        {
                            stationCount++;
                            string wsClass = Plugin.NativeClassName(ws);
                            string wsInvTotal = "?";
                            string neededCount = "?";
                            try
                            {
                                var inv = ws.GetInventory();
                                if (inv != null) wsInvTotal = inv.GetTotalItemsQuantity().ToString();
                            }
                            catch { }
                            try
                            {
                                var needed = ws.GetItemsNeededFromSettlement();
                                if (needed != null) neededCount = needed.Count.ToString();
                            }
                            catch { }

                            Plugin.Logger.LogInfo($"[CFS] Census:   station: owner='{owner}' type='{wsClass}' " +
                                $"inventoryTotal={wsInvTotal} neededFromSettlement={neededCount}");
                        }
                    }
                }
            }
            Plugin.Logger.LogInfo($"[CFS] Census: {structureCount} structures, {containerCount} containers " +
                $"({equipManagedCount} equipment-managed, skipped from storage totals), {stationCount} workstations; " +
                $"{grandTotal} item(s) settlement-wide (equipment containers EXCLUDED from this total).");
        }
        catch (Exception ex) { Plugin.Logger.LogError($"[CFS] Census: structure walk error: {ex}"); }

        Plugin.Logger.LogInfo("[CFS] === Settlement storage census end ===");
    }

    // Never use SettlementManager.settlements - that list stays null even in a loaded world
    // (project-wide gotcha). Try the getter methods in order; first non-null wins.
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

    // Manual hierarchy walk for a component of type T. The plural generic GetComponentsInChildren<T>
    // is missing through the interop trampoline (project-wide gotcha); per-node singular
    // GetComponent<T>() + child recursion is the proven replacement (SupplyChainMod StationWalker).
    private static T? FindComponent<T>(Transform? node, int depth = 0) where T : UnityEngine.Component
    {
        if (node == null || depth > 12) return null;

        try
        {
            var c = node.GetComponent<T>();
            if (c != null) return c;
        }
        catch { }

        int childCount;
        try { childCount = node.childCount; } catch { return null; }
        for (int i = 0; i < childCount; i++)
        {
            Transform? child = null;
            try { child = node.GetChild(i); } catch { }
            if (child == null) continue;
            var found = FindComponent<T>(child, depth + 1);
            if (found != null) return found;
        }
        return null;
    }

    // v0.2.0: collect-ALL variant of FindComponent<T> below - the v0.1.2 walk stopped at the FIRST
    // match per structure, which missed real crafting-station storage sitting behind a decorative
    // or character-slot container earlier in hierarchy order. Same singular-GetComponent + child-
    // recursion shape (plural GetComponentsInChildren<T> is missing through the interop trampoline -
    // project-wide gotcha), just appending instead of returning early.
    private static void CollectComponents<T>(Transform? node, List<T> results, int depth) where T : UnityEngine.Component
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

    // Structural equip-slot probe (architecture.md "Equipment-slot containers are a first-class
    // mechanism - prefer a STRUCTURAL exclusion rule over a name blacklist"). EquipPoint is not a
    // MonoBehaviour (base Il2CppSystem.Object) so it can't be found via GetComponent - reach it
    // through EquipmentManager (which IS a MonoBehaviour) instead: walk UP from the container's
    // node looking for an EquipmentManager, then compare each of its equipPoints' ItemContainer
    // against this container BY POINTER (managed equality lies for interop objects - project-wide
    // gotcha). Exact match -> "yes-equipPoint"; a manager exists nearby but no container match ->
    // "yes-managerAncestor" (still equipment-adjacent, still excluded from storage totals); no
    // manager anywhere above -> "no".
    private static string ProbeEquipStatus(Transform? node, ItemContainer container)
    {
        Transform? t = node;
        int depth = 0;
        while (t != null && depth <= 12)
        {
            EquipmentManager? mgr = null;
            try { mgr = t.GetComponent<EquipmentManager>(); } catch { }
            if (mgr != null)
            {
                try
                {
                    long containerPtr = ((Il2CppObjectBase)(object)container).Pointer.ToInt64();
                    var points = mgr.equipPoints;
                    if (points != null)
                    {
                        foreach (var ep in points)
                        {
                            if (ep == null) continue;
                            ItemContainer? epc = null;
                            try { epc = ep.ItemContainer; } catch { }
                            if (epc == null) continue;
                            try
                            {
                                long epPtr = ((Il2CppObjectBase)(object)epc).Pointer.ToInt64();
                                if (epPtr == containerPtr) return "yes-equipPoint";
                            }
                            catch { }
                        }
                    }
                }
                catch { }
                return "yes-managerAncestor";
            }
            try { t = t.parent; } catch { t = null; }
            depth++;
        }
        return "no";
    }

    private static string SafeStructureName(Structure? s)
    {
        if (s == null) return "?";
        try { var n = s.StructureName; if (!string.IsNullOrEmpty(n)) return n; } catch { }
        try { var n = s.DefaultName; if (!string.IsNullOrEmpty(n)) return n; } catch { }
        try { return s.gameObject.name ?? "?"; } catch { return "?"; }
    }

    // Typing guard: keystrokes in the game's text fields (e.g. structure rename) also reach
    // Input.GetKeyDown, so a hotkey fires while the player types (project-wide convention -
    // GroundItemVacuumMod/VacuumTracker.cs, OuthouseComposterMod/ComposterDiag.cs). ASKA's UI is
    // TextMeshPro-based, so the TMP_InputField check is the one that actually matters here; the
    // csproj carries the Unity.TextMeshPro reference for it.)
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
}
