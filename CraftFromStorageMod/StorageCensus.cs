using System;
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
        try
        {
            Il2CppSystem.Collections.Generic.List<Structure>? structures = null;
            try { structures = settlement.GetStructures(); } catch { }
            int structureCount = 0;
            try { structureCount = structures?.Count ?? 0; } catch { }
            Plugin.Logger.LogInfo($"[CFS] Census: {structureCount} total structure(s) - walking for containers.");

            int withStorage = 0;
            int grandTotal = 0;
            if (structures != null)
            {
                foreach (var st in structures)
                {
                    if (st == null) continue;

                    ItemContainerComponent? icc = null;
                    try { icc = FindComponent<ItemContainerComponent>(st.transform); } catch { }
                    if (icc == null) continue;

                    ItemContainer? container = null;
                    try { container = icc.container; } catch { }
                    if (container == null) continue;

                    withStorage++;
                    string owner = SafeStructureName(st);
                    int capacity = 0;
                    int qty = 0;
                    string typeName = "?";
                    try { capacity = container.capacity; } catch { }
                    try { typeName = container.containerType?.name ?? "?"; } catch { }
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
                    grandTotal += qty;

                    Plugin.Logger.LogInfo($"[CFS] Census:   storage: owner='{owner}' type='{typeName}' capacity={capacity} items={qty}");
                }
            }
            Plugin.Logger.LogInfo($"[CFS] Census: {withStorage} structure(s) with a container; {grandTotal} item(s) settlement-wide.");
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
