using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using SandSailorStudio.Inventory;
using SSSGame;
using UnityEngine;

namespace CraftFromStorageMod;

// v0.2.0 delta watcher (idea-17 Phase 0 blocker): _OnCraftingSuccess PRE/POST snapshots proved
// CraftInteraction.ItemInventory is NOT the consumption site (confirmed in-game 2026-07-20,
// architecture.md "Player crafting pipeline"). Cecil cannot settle where consumption actually
// happens - interop method bodies are trampolines. This watcher samples EVERY candidate
// ItemCollection repeatedly across the whole craft and logs only what changed and when, so a
// single glance names the consuming collection and its timing. READ-ONLY: every method here only
// LOGS - none writes inventory, transfers items, or mutates a game object.
//
// Registered as an injected MonoBehaviour the same way StorageCensus already is. All static state
// (armed/slots/stopwatch) is manipulated from Harmony patch callbacks (GatePatches.cs) via the
// Arm()/Mark() entry points; the actual per-frame polling happens in this component's Update().
public class CraftWatcher : MonoBehaviour
{
    private const int MaxDiffEntries = 12;
    private const float DefaultWindowSeconds = 20f;
    private const float DefaultPollIntervalSeconds = 0.1f;

    // One tracked ItemCollection. Holds the wrapper only while armed - project-wide gotcha:
    // never cache interop wrappers of per-world objects across world sessions. ClearWorldState()
    // (called from PlayerDespawnedPatch on world-leave) nulls this out via _slots.Clear().
    private sealed class WatchSlot
    {
        internal readonly string Name;
        internal readonly ItemCollection Collection;
        internal Dictionary<string, int> LastSnapshot;
        internal int LastTotal;
        internal readonly int StartTotal;
        internal long? FirstChangeAtMs;

        internal WatchSlot(string name, ItemCollection collection, Dictionary<string, int> snapshot, int total)
        {
            Name = name;
            Collection = collection;
            LastSnapshot = snapshot;
            LastTotal = total;
            StartTotal = total;
            FirstChangeAtMs = null;
        }
    }

    private static readonly List<WatchSlot> _slots = new();
    private static readonly List<string> _unresolved = new();
    private static bool _armed;
    private static readonly Stopwatch _stopwatch = new();
    private static float _pollAccumulator;

    // v0.3.0 Phase 1 (design point E): on-screen abort message, same OnGUI/shadow-label pattern as
    // GroundItemVacuumMod.VacuumTracker.ShowMessage/OnGUI. Static (not instance) since the rest of
    // this class's state is already all-static and manipulated from Harmony patch callbacks
    // (GatePatches.cs) via static entry points - OnGUI itself must stay an instance method (Unity
    // lifecycle callback) but only ever reads these static fields.
    private static string _guiMessage = "";
    private static float _guiExpiry;

    internal static void ShowMessage(string message)
    {
        _guiMessage = message;
        _guiExpiry = Time.time + 5.0f;
    }

    private void OnGUI()
    {
        if (Time.time >= _guiExpiry || string.IsNullOrEmpty(_guiMessage)) return;

        var shadow = new GUIStyle(GUI.skin.label)
        {
            fontSize = 24,
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.MiddleCenter
        };
        shadow.normal.textColor = Color.black;
        var text = new GUIStyle(shadow);
        text.normal.textColor = new Color(1f, 0.55f, 0.15f); // orange - warning tone, distinct from GroundItemVacuum's cyan

        GUI.Label(new Rect(0, 81, Screen.width, 100), _guiMessage, shadow);
        GUI.Label(new Rect(0, 80, Screen.width, 100), _guiMessage, text);
    }

    private void Update()
    {
        // Gate-call rollup flush is independent of the watcher's armed state - it just needs a
        // per-frame tick, and this component is the only one guaranteed to be alive whenever
        // GatePatches.cs is active. See GatePatches.cs GateRollup.
        try { Patches.GateRollup.Tick(); }
        catch (Exception ex) { Plugin.Logger.LogError($"[CFS] GateRollup.Tick error: {ex}"); }

        // v0.8.0: same reasoning - the villager-side Point A log rate limiter (Change 3, in
        // CraftTransfer.cs) and the fetch-quest priority-suppression rollup (Change 2,
        // FetchQuestSuppression.cs) both need a per-frame idle-flush tick.
        try { VillagerAvailabilityRollup.Tick(); }
        catch (Exception ex) { Plugin.Logger.LogError($"[CFS] VillagerAvailabilityRollup.Tick error: {ex}"); }
        try { FetchQuestSuppression.Tick(); }
        catch (Exception ex) { Plugin.Logger.LogError($"[CFS] FetchQuestSuppression.Tick error: {ex}"); }

        if (!_armed) return;

        long elapsedMs;
        try { elapsedMs = _stopwatch.ElapsedMilliseconds; } catch { elapsedMs = 0; }

        float windowSeconds = DefaultWindowSeconds;
        try { windowSeconds = Plugin.WatchWindowSeconds?.Value ?? DefaultWindowSeconds; } catch { }
        if (windowSeconds <= 0f) windowSeconds = DefaultWindowSeconds;

        if (elapsedMs >= (long)(windowSeconds * 1000f))
        {
            EmitSummary(windowSeconds);
            _armed = false;
            return;
        }

        float pollInterval = DefaultPollIntervalSeconds;
        try { pollInterval = Plugin.PollIntervalSeconds?.Value ?? DefaultPollIntervalSeconds; } catch { }
        if (pollInterval <= 0f) pollInterval = DefaultPollIntervalSeconds;

        _pollAccumulator += Time.deltaTime;
        if (_pollAccumulator < pollInterval) return;
        _pollAccumulator = 0f;

        PollOnce(elapsedMs);
    }

    // Called from the three BeginCraftingSequence prefixes (GatePatches.cs). Resolves every
    // candidate slot fresh and takes baselines. Re-arming while already armed (a second craft
    // starting before the first's window expired) is allowed - re-baseline from scratch.
    internal static void Arm(CraftInteraction interaction, InteractionSession session, string typeName)
    {
        try
        {
            bool enabled = true;
            try { enabled = Plugin.EnableCraftWatcher?.Value ?? true; } catch { }
            if (!enabled) return;

            bool reArm = _armed;
            BuildSlots(interaction, session);

            _stopwatch.Restart();
            _pollAccumulator = 0f;
            _armed = true;

            string sessionClass = Plugin.NativeClassName(session);
            string agentClass = "null";
            try { agentClass = Plugin.NativeClassName(session?.Agent); } catch { }
            bool useAgentInv = false;
            try { useAgentInv = interaction.useAgentInventory; } catch { }

            var resolvedNames = new List<string>();
            foreach (var slot in _slots) resolvedNames.Add(slot.Name);
            string resolvedList = string.Join(",", resolvedNames);
            string unresolvedList = string.Join(",", _unresolved);

            string verb = reArm ? "RE-ARM" : "ARM";
            Plugin.Logger.LogInfo($"[CFS] WATCH {verb} [{typeName}] session={sessionClass} agent={agentClass} " +
                $"useAgentInventory={useAgentInv} slots=[{resolvedList}] unresolved=[{unresolvedList}]");
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"[CFS] CraftWatcher.Arm error: {ex}");
        }
    }

    // Takes an immediate delta reading and logs it tagged with `label`, regardless of the normal
    // poll cadence. No-op (silently) when not armed.
    internal static void Mark(string label)
    {
        if (!_armed) return;
        try
        {
            long elapsedMs = _stopwatch.ElapsedMilliseconds;
            bool any = false;
            foreach (var slot in _slots)
            {
                try
                {
                    var (newDict, newTotal) = Snapshot(slot.Collection);
                    if (newTotal == slot.LastTotal && DictEquals(slot.LastSnapshot, newDict)) continue;
                    any = true;

                    string diff = BuildDiffString(slot.LastSnapshot, newDict);
                    Plugin.Logger.LogInfo($"[CFS] WATCH MARK '{label}' +{elapsedMs}ms [{slot.Name}] total {slot.LastTotal}->{newTotal} | {diff}");

                    if (slot.FirstChangeAtMs == null) slot.FirstChangeAtMs = elapsedMs;
                    slot.LastSnapshot = newDict;
                    slot.LastTotal = newTotal;
                }
                catch (Exception ex)
                {
                    Plugin.Logger.LogError($"[CFS] CraftWatcher mark error [{slot.Name}]: {ex}");
                }
            }
            if (!any)
                Plugin.Logger.LogInfo($"[CFS] WATCH MARK '{label}' +{elapsedMs}ms (no changes)");
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"[CFS] CraftWatcher.Mark error: {ex}");
        }
    }

    // World-leave: never cache interop wrappers of per-world objects across sessions (project-wide
    // gotcha) - reading a stale ItemCollection wrapper is an unrecoverable native crash. Called
    // from PlayerDespawnedPatch (Patches/LifecyclePatches.cs), right where LocalPlayer is cleared.
    internal static void ClearWorldState()
    {
        try
        {
            bool hadState = _armed || _slots.Count > 0;
            _armed = false;
            _slots.Clear();
            _unresolved.Clear();
            _stopwatch.Reset();
            _pollAccumulator = 0f;
            if (hadState)
                Plugin.Logger.LogInfo("[CFS] CraftWatcher: world-leave - held wrappers cleared, disarmed.");
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"[CFS] CraftWatcher.ClearWorldState error: {ex}");
        }

        // v0.3.0 Phase 1 (design point F): the transfer ledger and settlement snapshot also hold
        // per-world ItemContainer/ItemInfo wrappers - never cache interop wrappers of per-world
        // objects across world sessions (project-wide gotcha). Routed through this one existing
        // world-leave call site (PlayerDespawnedPatch already calls CraftWatcher.ClearWorldState())
        // rather than adding a second call in Patches/LifecyclePatches.cs.
        try { CraftTransfer.ClearWorldState(); }
        catch (Exception ex) { Plugin.Logger.LogError($"[CFS] CraftTransfer.ClearWorldState error: {ex}"); }
        try { SettlementStock.ClearWorldState(); }
        catch (Exception ex) { Plugin.Logger.LogError($"[CFS] SettlementStock.ClearWorldState error: {ex}"); }
        // v0.4.0: the settlement-stock requirement-UI feature holds a per-world CraftMenu reference -
        // never cache interop wrappers of per-world objects across world sessions (project-wide gotcha).
        try { CraftUiState.ClearWorldState(); }
        catch (Exception ex) { Plugin.Logger.LogError($"[CFS] CraftUiState.ClearWorldState error: {ex}"); }
        // v0.4.3: drops the per-panel resolved-label cache (interop wrappers of per-world UI objects)
        // and re-arms the one-shot diagnostics so a second world load logs its own evidence.
        try { CraftUiAvailability.ClearWorldState(); }
        catch (Exception ex) { Plugin.Logger.LogError($"[CFS] CraftUiAvailability.ClearWorldState error: {ex}"); }
        // v0.5.0: the requirement-UI poller (CraftUiPoller.cs) holds no interop wrappers of its own,
        // but re-arms its one-shot "panels found" diagnostic so a second world load logs its own
        // evidence (same reasoning as CraftUiAvailability.ClearWorldState above).
        try { CraftUiPoller.ClearWorldState(); }
        catch (Exception ex) { Plugin.Logger.LogError($"[CFS] CraftUiPoller.ClearWorldState error: {ex}"); }
        // v0.6.0: the villager-fetch diagnostic spike (VillagerFetchTrace.cs) holds no interop
        // wrappers of its own (state is keyed by GameObject NAME strings, not cached wrappers), but
        // per-cycle counters are still cleared on world-leave so a stale cycle from a previous world
        // can never produce a misleading DIRECT/TOURED verdict after a reload.
        try { VillagerFetchTrace.ClearWorldState(); }
        catch (Exception ex) { Plugin.Logger.LogError($"[CFS] VillagerFetchTrace.ClearWorldState error: {ex}"); }

        // Don't let a stale abort banner survive into the next world.
        _guiMessage = "";
        _guiExpiry = 0f;
    }

    private static void PollOnce(long elapsedMs)
    {
        foreach (var slot in _slots)
        {
            try
            {
                var (newDict, newTotal) = Snapshot(slot.Collection);
                if (newTotal == slot.LastTotal && DictEquals(slot.LastSnapshot, newDict)) continue;

                string diff = BuildDiffString(slot.LastSnapshot, newDict);
                Plugin.Logger.LogInfo($"[CFS] WATCH +{elapsedMs}ms [{slot.Name}] total {slot.LastTotal}->{newTotal} | {diff}");

                if (slot.FirstChangeAtMs == null) slot.FirstChangeAtMs = elapsedMs;
                slot.LastSnapshot = newDict;
                slot.LastTotal = newTotal;
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError($"[CFS] CraftWatcher poll error [{slot.Name}]: {ex}");
            }
        }
    }

    private static void EmitSummary(float windowSeconds)
    {
        try
        {
            int changedCount = 0;
            var lines = new List<string>();
            foreach (var slot in _slots)
            {
                if (slot.FirstChangeAtMs.HasValue)
                {
                    changedCount++;
                    int netDelta = slot.LastTotal - slot.StartTotal;
                    string sign = netDelta >= 0 ? "+" : "";
                    lines.Add($"[CFS] WATCH SUMMARY:   [{slot.Name}] firstChangeAt=+{slot.FirstChangeAtMs.Value}ms " +
                        $"netTotal {slot.StartTotal}->{slot.LastTotal} netDelta={sign}{netDelta}");
                }
                else
                {
                    lines.Add($"[CFS] WATCH SUMMARY:   [{slot.Name}] NO CHANGE");
                }
            }
            Plugin.Logger.LogInfo($"[CFS] WATCH SUMMARY: window={windowSeconds:0.##}s slots_changed={changedCount}");
            foreach (var line in lines) Plugin.Logger.LogInfo(line);
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"[CFS] CraftWatcher.EmitSummary error: {ex}");
        }
    }

    // ---- Slot resolution (arm-time only) ----

    private static void BuildSlots(CraftInteraction interaction, InteractionSession session)
    {
        _slots.Clear();
        _unresolved.Clear();

        // AGENT - session.Agent.GetInventory().
        ItemCollection? agentInv = null;
        try { agentInv = session?.Agent?.GetInventory(); } catch { }
        AddSlot("AGENT", agentInv);

        // STATION_ItemInventory - interaction.ItemInventory.
        ItemCollection? stationItemInv = null;
        try { stationItemInv = interaction?.ItemInventory; } catch { }
        AddSlot("STATION_ItemInventory", stationItemInv);

        // STATION_inventory - interaction.inventory -> GetItemCollection() (fall back to .items).
        ItemCollection? stationInv = null;
        try
        {
            InventoryComponent? invComp = interaction?.inventory;
            if (invComp != null)
            {
                try { stationInv = invComp.GetItemCollection(); } catch { }
                if (stationInv == null) { try { stationInv = invComp.items; } catch { } }
            }
        }
        catch { }
        AddSlot("STATION_inventory", stationInv);

        // STATION_blueprintInv - interaction.blueprintInventory -> same accessor.
        ItemCollection? blueprintInv = null;
        try
        {
            InventoryComponent? invComp = interaction?.blueprintInventory;
            if (invComp != null)
            {
                try { blueprintInv = invComp.GetItemCollection(); } catch { }
                if (blueprintInv == null) { try { blueprintInv = invComp.items; } catch { } }
            }
        }
        catch { }
        AddSlot("STATION_blueprintInv", blueprintInv);

        // WORKSTATION - nearest Workstation walking the interaction's transform then its
        // ancestors (NOT descendants), then GetInventory().
        ItemCollection? workstationInv = null;
        try
        {
            Transform? t = null;
            try { t = interaction?.transform; } catch { }
            Workstation? ws = FindWorkstationSelfAndAncestors(t);
            if (ws != null) { try { workstationInv = ws.GetInventory(); } catch { } }
        }
        catch { }
        AddSlot("WORKSTATION", workstationInv);

        // CRAFTINGAGENT - nearest CraftingAgent from session.Agent.GetTransform() (self, then
        // ancestors, then a recursive child walk), then ItemInventory.
        ItemCollection? craftingAgentInv = null;
        try
        {
            Transform? at = null;
            try { at = session?.Agent?.GetTransform(); } catch { }
            CraftingAgent? ca = FindCraftingAgent(at);
            if (ca != null) { try { craftingAgentInv = ca.ItemInventory; } catch { } }
        }
        catch { }
        AddSlot("CRAFTINGAGENT", craftingAgentInv);
    }

    private static void AddSlot(string name, ItemCollection? coll)
    {
        if (coll == null) { _unresolved.Add(name); return; }
        var (dict, total) = Snapshot(coll);
        _slots.Add(new WatchSlot(name, coll, dict, total));
    }

    // Full snapshot (no cap - this is a diff source, completeness matters; the diff OUTPUT is
    // what's capped). Same GetItemInfos()+GetItemQuantity() loop as the proven GateLog reader.
    private static (Dictionary<string, int> dict, int total) Snapshot(ItemCollection? coll)
    {
        var dict = new Dictionary<string, int>();
        int total = 0;
        if (coll == null) return (dict, total);
        try { total = coll.GetTotalItemsQuantity(); } catch { }
        try
        {
            var infos = coll.GetItemInfos();
            if (infos != null)
            {
                foreach (var info in infos)
                {
                    if (info == null) continue;
                    string name = "?";
                    try { name = info.Name ?? "?"; } catch { }
                    int qty = 0;
                    try { qty = coll.GetItemQuantity(info); } catch { }
                    dict[name] = qty;
                }
            }
        }
        catch { }
        return (dict, total);
    }

    private static bool DictEquals(Dictionary<string, int> a, Dictionary<string, int> b)
    {
        if (a.Count != b.Count) return false;
        foreach (var kv in a)
        {
            if (!b.TryGetValue(kv.Key, out var v) || v != kv.Value) return false;
        }
        return true;
    }

    // Capped at MaxDiffEntries changed items, sorted for determinism.
    private static string BuildDiffString(Dictionary<string, int> oldDict, Dictionary<string, int> newDict)
    {
        var keys = new List<string>(oldDict.Keys);
        foreach (var k in newDict.Keys) { if (!oldDict.ContainsKey(k)) keys.Add(k); }
        keys.Sort(StringComparer.Ordinal);

        var sb = new StringBuilder();
        int count = 0;
        foreach (var key in keys)
        {
            int oldQty = oldDict.TryGetValue(key, out var oq) ? oq : 0;
            int newQty = newDict.TryGetValue(key, out var nq) ? nq : 0;
            if (oldQty == newQty) continue;
            if (count >= MaxDiffEntries) { sb.Append(", ..."); break; }
            if (sb.Length > 0) sb.Append(", ");
            sb.Append(key).Append(' ').Append(oldQty).Append("->").Append(newQty);
            count++;
        }
        return sb.ToString();
    }

    // Self + ancestors only (no descendant walk) - the interaction node normally sits at or above
    // the station's Workstation component, never below it.
    private static Workstation? FindWorkstationSelfAndAncestors(Transform? t, int maxDepth = 12)
    {
        int depth = 0;
        while (t != null && depth <= maxDepth)
        {
            Workstation? w = null;
            try { w = t.GetComponent<Workstation>(); } catch { }
            if (w != null) return w;
            try { t = t.parent; } catch { t = null; }
            depth++;
        }
        return null;
    }

    // Self + ancestors first (cheap, common case), then a recursive descendant walk from the
    // original transform as a fallback. The plural generic GetComponentsInChildren<T>() is
    // missing through the interop trampoline (project-wide gotcha) - per-node singular
    // GetComponent<T>() + child recursion is the proven replacement.
    private static CraftingAgent? FindCraftingAgent(Transform? start, int maxDepth = 12)
    {
        if (start == null) return null;

        Transform? t = start;
        int depth = 0;
        while (t != null && depth <= maxDepth)
        {
            CraftingAgent? ca = null;
            try { ca = t.GetComponent<CraftingAgent>(); } catch { }
            if (ca != null) return ca;
            try { t = t.parent; } catch { t = null; }
            depth++;
        }

        return FindComponentRecursive<CraftingAgent>(start, 0, maxDepth);
    }

    private static T? FindComponentRecursive<T>(Transform? node, int depth, int maxDepth) where T : UnityEngine.Component
    {
        if (node == null || depth > maxDepth) return null;

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
            var found = FindComponentRecursive<T>(child, depth + 1, maxDepth);
            if (found != null) return found;
        }
        return null;
    }
}
