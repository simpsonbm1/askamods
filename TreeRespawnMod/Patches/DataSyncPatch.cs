using System;
using HarmonyLib;
using SSSGame;

namespace TreeRespawnMod.Patches;

// The host-side network catch: fires whenever a world instance's networked data changes, INCLUDING
// changes replicated from a co-op client's actions — which never run GatherItemsCharge/TakeDamage on
// the host, so the direct patches can't see them.
//
// By patching WorldItemInstance._OnDataChanged rather than HarvestInteraction._OnWorldInstanceDataChanged,
// we avoid the Unity "Handle is not initialized" crash caused by patching a MonoBehaviour lifecycle method
// during its native initialization. WorldItemInstance is a pure Il2Cpp object, so patching it is safe.
//
// v1.3.0 rework — the old version required biomeInst.gameObject + GetComponent<GatherInteraction>() to
// classify the node, which structurally missed most gather nodes: the interaction component often does
// NOT sit on the instance's own GameObject (v1.2.14 finding: even GetComponent<HarvestInteraction> came
// back null for trees), and a node the host hasn't instantiated has no GameObject at all. Result in
// co-op testing (2026-07-01): client tree chops registered, client gathers never did. Classification is
// now data-first: Plugin.KnownNodes (recorded at SetWorldInstance bind time — the authoritative
// interaction↔instance mapping) with a component search (self + children) as fallback, and the
// registration conditions read pure instance data (Registration.cs) so no GameObject is ever required.
[HarmonyPatch(typeof(WorldItemInstance), nameof(WorldItemInstance._OnDataChanged))]
internal static class DataSyncPatch
{
    // Per-window counters, summarized every 5s when EnableDiagnostics is on. This is what tells a
    // co-op test session WHERE client harvests are dying (never fires? not classified? not exhausted?).
    private static int _cFired, _cNotBiome, _cHealthy, _cErr, _cKnown, _cComp, _cUnknown, _cRegTree, _cRegGather;
    private static DateTime _lastSummary = DateTime.MinValue;

    static void Postfix(WorldItemInstance __instance)
    {
        try
        {
            _cFired++;
            var biomeInst = __instance.TryCast<BiomeItemInstance>();
            if (biomeInst == null) { _cNotBiome++; DiagSummary(); return; }

            // Hot-path gate (v1.3.1): _OnDataChanged fires tens of thousands of times per 5s window
            // during streaming (measured ~105k/5s in-game 2026-07-02) and almost all of it is healthy
            // instances we don't care about. One native IsExhausted() read filters those BEFORE any
            // position read, PosKey string formatting, or dictionary work. Both registration
            // conditions imply IsExhausted anyway (a stump reads true — v1.1.1 gates; an empty gather
            // node likewise — it's the depleted flag the AI resource search keys on).
            bool exhausted = false;
            try { exhausted = biomeInst.IsExhausted(); } catch { }
            if (!exhausted) { _cHealthy++; DiagSummary(); return; }

            string posKey;
            try { posKey = Plugin.PosKey(biomeInst.GetPosition()); }
            catch { _cErr++; DiagSummary(); return; }

            NodeKind kind; string itemName;
            if (Plugin.KnownNodes.TryGetValue(posKey, out var known))
            {
                kind = known.kind; itemName = known.itemName; _cKnown++;
                if (kind == NodeKind.Other) { DiagSummary(); return; } // confirmed not-respawnable
            }
            else
            {
                var c = ClassifyByComponents(biomeInst, out kind, out itemName);
                if (c == Classify.Found)
                {
                    Plugin.KnownNodes[posKey] = (kind, itemName); // cache — skip the hierarchy walk next fire
                    _cComp++;
                }
                else if (c == Classify.NotRespawnable)
                {
                    // GameObject exists but holds no tree/gather interaction — cache the negative so
                    // this instance's frequent data changes stop re-walking its hierarchy. If a real
                    // interaction binds later, its SetWorldInstance postfix overwrites this entry.
                    Plugin.KnownNodes[posKey] = (NodeKind.Other, "");
                    _cUnknown++; DiagSummary(); return;
                }
                else
                {
                    // No GameObject to inspect (node not instantiated host-side) and never bound this
                    // session — can't classify yet; don't cache. If it's a depleted node, the
                    // SetWorldInstance catch-up registers it the moment the host streams it in.
                    _cUnknown++; DiagSummary(); return;
                }
            }

            if (kind == NodeKind.Tree)
            {
                if (Registration.TryRegisterTree(biomeInst, posKey, "data sync")) _cRegTree++;
            }
            else
            {
                if (string.IsNullOrEmpty(itemName))
                    try { itemName = biomeInst.Descriptor?.itemInfo?.Name ?? ""; } catch { }
                if (Registration.TryRegisterGather(biomeInst, posKey, itemName, "data sync")) _cRegGather++;
            }
            DiagSummary();
        }
        catch (Exception ex)
        {
            try { Plugin.Logger.LogError($"[TreeRespawnMod] DataSyncPatch: {ex}"); } catch { }
        }
    }

    private enum Classify { NoGameObject, NotRespawnable, Found }

    // Component-based classification fallback for nodes not yet in KnownNodes. Searches the instance's
    // own GameObject AND its children (includeInactive — an exhausted gather node's visuals/interaction
    // can be deactivated, which is one reason the old plain GetComponent missed them).
    private static Classify ClassifyByComponents(BiomeItemInstance bi, out NodeKind kind, out string itemName)
    {
        kind = NodeKind.Other; itemName = "";
        try
        {
            var go = bi.gameObject;
            if (go == null) return Classify.NoGameObject;

            var h = go.GetComponent<HarvestInteraction>();
            if (h == null) h = go.GetComponentInChildren<HarvestInteraction>(true);
            if (h != null)
            {
                var pieces = h.harvestPieces;
                if (pieces == null || pieces.Count < 2) return Classify.NotRespawnable; // single-piece — no stump
                kind = NodeKind.Tree;
                return Classify.Found;
            }

            var g = go.GetComponent<GatherInteraction>();
            if (g == null) g = go.GetComponentInChildren<GatherInteraction>(true);
            if (g != null)
            {
                try { itemName = g.GetGatherableItemInfo()?.Name ?? ""; } catch { }
                kind = NodeKind.Gather;
                return Classify.Found;
            }
            return Classify.NotRespawnable;
        }
        catch { return Classify.NoGameObject; }
    }

    private static void DiagSummary()
    {
        if (!Plugin.EnableDiagnostics.Value) return;
        if ((DateTime.UtcNow - _lastSummary).TotalSeconds < 5) return;
        _lastSummary = DateTime.UtcNow;
        Plugin.Logger.LogInfo(
            $"[TreeRespawnMod] [diag] datasync(5s): fired={_cFired} notBiome={_cNotBiome} healthy={_cHealthy} " +
            $"classified known={_cKnown}/comp={_cComp} unknown={_cUnknown} " +
            $"registered tree={_cRegTree} gather={_cRegGather} err={_cErr}");
        _cFired = _cNotBiome = _cHealthy = _cErr = _cKnown = _cComp = _cUnknown = _cRegTree = _cRegGather = 0;
    }
}
