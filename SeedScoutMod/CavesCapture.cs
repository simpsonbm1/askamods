using HarmonyLib;
using SandSailorStudio.WorldGen;
using SSSGame;
using UnityEngine;

namespace SeedScoutMod;

// Accumulate every cave instance the game registers (value-copy position + bounds, deduped
// by tile position). RegisterCaves may fire per-tile as the world streams, so this grows
// over a session.
[HarmonyPatch(typeof(CavesManager), "RegisterCaves")]
internal static class RegisterCavesPatch
{
    static void Postfix(AreaInstancelist caveInstances)
    {
        if (caveInstances == null) return;
        int added = 0;
        int n = caveInstances.Count;
        for (int i = 0; i < n; i++)
        {
            var a = caveInstances[i];
            if (a == null) continue;

            Vector2Int pos = default;
            Vector4 bounds = default;
            try { pos = a.position; } catch { }
            try { bounds = a.GetBounds(); } catch { }

            bool dup = false;
            foreach (var c in Plugin.RegisteredCaves)
                if (c.Pos == pos) { dup = true; break; }
            if (!dup) { Plugin.RegisteredCaves.Add(new CaveHit(pos, bounds)); added++; }
        }
        if (added > 0)
            Plugin.Logger.LogInfo($"SeedScout: RegisterCaves +{added} (total {Plugin.RegisteredCaves.Count})");
    }
}
