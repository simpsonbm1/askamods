using System;
using HarmonyLib;
using SSSGame;
using UnityEngine;

namespace SeedScoutMod;

// Hostiles (animal dens, enemy camps) stream in per-tile like lakes — capture their world position as
// they spawn so scoring can penalize caves that sit next to danger. A Den IS a Creature, so its TYPE
// (wulfar den / graveyard / spire …) and difficulty live on its CreatureDataSheet — we log those to
// learn what distinguishes the den variants, and stash the best label + threat on the HostileHit.
[HarmonyPatch(typeof(Den), "Start")]
internal static class DenStartPatch
{
    static void Postfix(Den __instance) => HostileCapture.RecordDen(__instance);
}

[HarmonyPatch(typeof(HostileVikingSettlement), "Awake")]
internal static class HostileSettlementAwakePatch
{
    static void Postfix(HostileVikingSettlement __instance) => HostileCapture.RecordSettlement(__instance);
}

internal static class HostileCapture
{
    internal static void RecordDen(Den den)
    {
        try
        {
            if (den == null) return;
            var p = den.transform.position;
            var pos = new Vector2(p.x, p.z);

            foreach (var h in Plugin.Hostiles)
                if (h.Kind == "Den" && (h.Pos - pos).sqrMagnitude < 16f) return; // dedupe ~4m

            // A Den's identity/difficulty come from the Creature base + its CreatureDataSheet.
            string name = Safe(() => den.GetName());
            string target = Safe(() => den.GetTargetName());
            string go = Safe(() => den.gameObject.name);
            string loc = "?", faction = "?";
            float threat = 0f;
            try
            {
                var ds = den.dataSheet;
                if (ds != null) { loc = ds.localizedName ?? "?"; faction = ds.faction.ToString(); threat = ds.baseThreatScore; }
            }
            catch { }
            int subs = 0; try { subs = den.subCreatures != null ? den.subCreatures.Count : 0; } catch { }

            // GetName is the cleanest label (e.g. "Wulfar Den", "Draugar den", "Skeleton Den Cluster").
            string label = Pick(name, target, loc, go, "Den");

            // Tier/size signal lives in what the den SPAWNS: alphaSpawner -> populations -> config
            // (asset name often encodes the tier) + max population size.
            string spawnInfo = SpawnerInfo(den);

            Plugin.Hostiles.Add(new HostileHit(pos, "Den", label, threat));
            Plugin.LogInfo($"SeedScout: Den at ({pos.x:0},{pos.y:0}) name='{name}' " +
                                  $"go='{go}' loc='{loc}' faction={faction} threat={threat:0.#} subs={subs}{spawnInfo}  " +
                                  $"hostiles={Plugin.Hostiles.Count}");
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"SeedScout: den capture err: {e.Message}"); }
    }

    internal static void RecordSettlement(HostileVikingSettlement s)
    {
        try
        {
            if (s == null) return;
            var p = s.transform.position;
            var pos = new Vector2(p.x, p.z);

            foreach (var h in Plugin.Hostiles)
                if (h.Kind == "EnemyCamp" && (h.Pos - pos).sqrMagnitude < 16f) return;

            string go = Safe(() => s.gameObject.name);
            Plugin.Hostiles.Add(new HostileHit(pos, "EnemyCamp", "EnemyCamp", 0f));
            Plugin.LogInfo($"SeedScout: EnemyCamp at ({pos.x:0},{pos.y:0}) go='{go}'  hostiles={Plugin.Hostiles.Count}");
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"SeedScout: settlement capture err: {e.Message}"); }
    }

    // Introspect what a den spawns: each SpawnPopulation's config asset name (likely encodes tier) and
    // its max population size. Runtime _populations may be empty at Start; if so, fall back to the
    // serialized 'creatures' config array length so we at least see how many population types exist.
    private static string SpawnerInfo(Den den)
    {
        try
        {
            var sp = den.alphaSpawner;
            if (sp == null) return " spawner=null";

            var pops = sp._populations;
            int pn = pops != null ? pops.Count : 0;
            if (pn == 0)
            {
                int cfgCount = 0;
                try { var cr = sp.creatures; cfgCount = cr != null ? cr.Length : 0; } catch { }
                return $" spawner[pops=0 cfgs={cfgCount}]";
            }

            var sb = new System.Text.StringBuilder(" spawner[");
            for (int i = 0; i < pn && i < 4; i++)
            {
                var pop = pops![i];
                var cfg = pop != null ? pop.config : null;
                string cfgName = "?"; int maxPop = 0; string popInfo = "?";
                if (cfg != null)
                {
                    try { cfgName = cfg.name; } catch { }
                    try { maxPop = cfg.maxPopulationSize; } catch { }
                    try { popInfo = cfg.populationInfo != null ? cfg.populationInfo.name : "?"; } catch { }
                }
                if (i > 0) sb.Append(' ');
                sb.Append($"{cfgName}(info={popInfo} max={maxPop})");
            }
            sb.Append(']');
            return sb.ToString();
        }
        catch (Exception e) { return $" spawner=<err {e.Message}>"; }
    }

    private static string Safe(Func<string> f) { try { var s = f(); return string.IsNullOrEmpty(s) ? "?" : s; } catch { return "?"; } }

    // First non-"?" / non-empty candidate.
    private static string Pick(params string[] candidates)
    {
        foreach (var c in candidates)
            if (!string.IsNullOrEmpty(c) && c != "?") return c;
        return "Den";
    }
}
