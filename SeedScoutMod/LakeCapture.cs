using System;
using HarmonyLib;
using SSSGame;
using UnityEngine;

namespace SeedScoutMod;

// Lakes aren't map-level areas — they spawn per-tile as the world streams. Capture each
// BiomeLakeStampMask as its biome spawns near the player (value-copy world position, deduped).
[HarmonyPatch(typeof(BiomeLakeStampMask), "_OnSpawnBiome")]
internal static class LakeSpawnPatch
{
    static void Postfix(BiomeLakeStampMask __instance)
    {
        try
        {
            Vector3 p = __instance.transform.position;
            var pos = new Vector2(p.x, p.z);

            foreach (var l in Plugin.Lakes)
                if ((l.Pos - pos).sqrMagnitude < 4f) return; // dedupe within ~2m

            float water = 0f;
            try { water = __instance.GetWaterLevel(); } catch { }

            Plugin.Lakes.Add(new LakeHit(pos, water));
            Plugin.Logger.LogInfo($"SeedScout: lake spawned at ({pos.x:0},{pos.y:0})  total {Plugin.Lakes.Count}");
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"SeedScout: lake capture err: {e.Message}"); }
    }
}
