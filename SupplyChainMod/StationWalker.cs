using System;
using System.Collections.Generic;
using SSSGame;
using UnityEngine;

namespace SupplyChainMod;

// One resolved settlement structure that has a Workstation component. Built fresh each resolve
// pass (StationWalker.ResolveStations) — never held across world sessions (the Workstation
// reference is a per-world native wrapper; the owner MUST drop this list on world-leave).
internal sealed class StationEntry
{
    public string PosKey = "?";
    public string StructureName = "?";
    public Workstation Ws = null!;
}

// Settlement/structure/workstation walk — READ-ONLY. Shared by TaskDump, CompositionSweep, and
// BomDump so all three see the same station set per resolve pass.
internal static class StationWalker
{
    // Settlement walk proven by OuthouseComposterMod.ResolveStructures: the raw `settlements` list
    // accessor stays null even in a loaded world (project-wide gotcha) — use the getter methods.
    internal static List<Structure> ResolveStructures()
    {
        var result = new List<Structure>();
        try
        {
            var sm = UnityEngine.Object.FindAnyObjectByType<SettlementManager>();
            if (sm == null) return result;

            var candidates = new List<Settlement>();
            try { var s = sm.GetPlayerSettlement(); if (s != null) candidates.Add(s); } catch { }
            try { var s = sm.GetCurrentSettlement(); if (s != null) candidates.Add(s); } catch { }
            try { var s = sm.worldSettlement; if (s != null) candidates.Add(s); } catch { }

            var seen = new HashSet<string>();
            foreach (var settlement in candidates)
            {
                if (settlement == null) continue;
                Il2CppSystem.Collections.Generic.List<Structure> structures;
                try { structures = settlement.GetStructures(); } catch { continue; }
                if (structures == null) continue;
                foreach (var st in structures)
                {
                    if (st == null) continue;
                    string key;
                    try { key = st.GetPosition().ToString(); } catch { continue; }
                    if (!seen.Add(key)) continue; // same building reachable via multiple settlement accessors
                    result.Add(st);
                }
            }
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"[SupplyChain] ResolveStructures error: {ex}");
        }
        return result;
    }

    // Manual hierarchy walk for a component of type T on a structure. The plural generic
    // GetComponentsInChildren<T> is missing through the interop trampoline (project-wide gotcha);
    // per-node singular GetComponent<T>() + child recursion is the proven replacement. This is
    // Unity's own native component-type matching (GetComponent<T> resolves subtypes correctly on
    // the native side) — NOT the same as the "managed cast lies" gotcha, which is about casting an
    // ALREADY-OBTAINED base-typed wrapper reference, not about Unity resolving a component by type.
    internal static T? FindComponent<T>(Transform node, int depth = 0) where T : UnityEngine.Component
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

    // Resolves the current settlement's structures down to the subset that have a Workstation
    // component, as a fresh list of StationEntry. Caller owns the list's lifetime — must be
    // dropped on world-leave (never held across world sessions).
    internal static List<StationEntry> ResolveStations()
    {
        var result = new List<StationEntry>();
        var structures = ResolveStructures();
        foreach (var s in structures)
        {
            Transform root;
            try { root = s.transform; } catch { continue; }

            Workstation? ws;
            try { ws = FindComponent<Workstation>(root); } catch { ws = null; }
            if (ws == null) continue;

            result.Add(new StationEntry
            {
                PosKey = Common.PosKey(s),
                StructureName = Common.SafeName(s),
                Ws = ws
            });
        }
        return result;
    }
}
