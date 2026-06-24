using System;
using HarmonyLib;
using SSSGame;
using UnityEngine;

namespace SeedScoutMod;

// Hostiles (animal dens, enemy camps) stream in per-tile like lakes — capture their world
// position as they spawn so scoring can penalize caves that sit next to danger.
[HarmonyPatch(typeof(Den), "Start")]
internal static class DenStartPatch
{
    static void Postfix(Den __instance) => HostileCapture.Record(__instance, "Den");
}

[HarmonyPatch(typeof(HostileVikingSettlement), "Awake")]
internal static class HostileSettlementAwakePatch
{
    static void Postfix(HostileVikingSettlement __instance) => HostileCapture.Record(__instance, "EnemyCamp");
}

internal static class HostileCapture
{
    internal static void Record(Component c, string kind)
    {
        try
        {
            if (c == null) return;
            var p = c.transform.position;
            var pos = new Vector2(p.x, p.z);
            foreach (var h in Plugin.Hostiles)
                if (h.Kind == kind && (h.Pos - pos).sqrMagnitude < 16f) return; // dedupe ~4m
            Plugin.Hostiles.Add(new HostileHit(pos, kind));
            Plugin.Logger.LogInfo($"SeedScout: {kind} at ({pos.x:0},{pos.y:0})  hostiles={Plugin.Hostiles.Count}");
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"SeedScout: hostile capture err: {e.Message}"); }
    }
}
