using System;
using System.Collections.Generic;
using SSSGame;
using SSSGame.Combat;
using UnityEngine;

namespace LocaleAuditMod;

// Nearby-spawner probe (v0.4.0). Bear dens and spires are NOT SSSGame.Den instances — they are
// passive PopulationSpawners that periodically spawn a creature. To let DenRespawnMod force a native
// spawn at them (timer / map-pin shift-click), it first needs to know what type they are and how to
// reach their spawner. This dumps every PopulationSpawner near the player: position + distance, what
// creature it spawns (invariant CreatureDataSheet asset name, e.g. a bear), live-creature count, and
// its gameObject / parent names. Stand at a bear den or a spire and press the spawner key; the
// nearest rows are that POI's spawner. Also logs the PopulationManager list counts for context.
internal static class SpawnerDump
{
    public static void Run()
    {
        var log = Plugin.Logger;
        log.LogInfo("========== SPAWNER DUMP BEGIN ==========");

        Vector3 playerPos = default;
        bool haveP = false;
        try
        {
            var pc = UnityEngine.Object.FindAnyObjectByType<PlayerCharacter>();
            if (pc != null) { playerPos = pc.transform.position; haveP = true; }
        }
        catch (Exception e) { log.LogWarning("[spawner] player pos failed: " + e.Message); }
        log.LogInfo("[spawner] player pos = " + (haveP ? playerPos.ToString() : "?(dumping unsorted)"));

        PopulationManager? pm = null;
        try { pm = UnityEngine.Object.FindAnyObjectByType<PopulationManager>(); } catch { }
        if (pm == null)
        {
            log.LogWarning("[spawner] no PopulationManager — not in a world?");
            log.LogInfo("========== SPAWNER DUMP END ==========");
            return;
        }

        // List-count context — if _populationSpawners doesn't hold the bear/spire spawner, these tell
        // us which other population list to look at next.
        LogListCount(log, "_populationSpawners", () => pm._populationSpawners?.Count ?? -1);
        LogListCount(log, "_biomePopulations", () => pm._biomePopulations?.Count ?? -1);
        LogListCount(log, "_freePopulations", () => pm._freePopulations?.Count ?? -1);
        LogListCount(log, "_creatureGroups", () => pm._creatureGroups?.Count ?? -1);

        Il2CppSystem.Collections.Generic.List<PopulationSpawner>? list = null;
        try { list = pm._populationSpawners; } catch (Exception e) { log.LogWarning("[spawner] _populationSpawners read failed: " + e.Message); }
        if (list == null)
        {
            log.LogWarning("[spawner] _populationSpawners null");
            log.LogInfo("========== SPAWNER DUMP END ==========");
            return;
        }

        int n = list.Count;
        var rows = new List<KeyValuePair<float, PopulationSpawner>>();
        for (int i = 0; i < n; i++)
        {
            PopulationSpawner? s = null;
            try { s = list[i]; } catch { }
            if (s == null) continue;
            float d = 0f;
            if (haveP) { try { d = Vector3.Distance(playerPos, s.transform.position); } catch { d = 999999f; } }
            rows.Add(new KeyValuePair<float, PopulationSpawner>(d, s));
        }
        rows.Sort((a, b) => a.Key.CompareTo(b.Key));

        int cap = Math.Min(rows.Count, Plugin.SpawnerDumpMax.Value);
        log.LogInfo("[spawner] total=" + n + " showing " + (haveP ? "nearest " : "first ") + cap);

        for (int i = 0; i < cap; i++)
        {
            float dist = rows[i].Key;
            var s = rows[i].Value;

            string pos = "?"; try { pos = s.transform.position.ToString(); } catch { }
            string go = "?"; try { go = s.gameObject.name; } catch { }
            string parent = "?"; try { var p = s.transform.parent; if (p != null) parent = p.gameObject.name; } catch { }
            bool isActive = false, ignoreResp = false, noAlive = false;
            int live = -1;
            try { isActive = s.isActive; } catch { }
            try { ignoreResp = s.ignoreRespawning; } catch { }
            try { noAlive = s.HasNoAliveCreatures(); } catch { }
            try { live = s.GetCreatureCount(); } catch { }

            // What it spawns: read a live creature's invariant dataSheet asset name (bear vs wight vs …).
            string crAsset = "?", crLoc = "?";
            try
            {
                var c = s.GetRandomCreature();
                if (c != null)
                {
                    var ds = c.dataSheet;
                    if (ds != null) { crAsset = ds.name ?? "?"; crLoc = ds.localizedName ?? "?"; }
                }
            }
            catch { }

            // dist is a float and pos is pre-stringified — never interpolate a Vector3 directly into a
            // BepInEx Logger call (the interpolated-string handler throws VerificationException).
            log.LogInfo($"[spawner] d={dist:F0}m go='{go}' parent='{parent}' pos={pos} active={isActive} "
                        + $"ignoreResp={ignoreResp} noAlive={noAlive} live={live} spawns.asset='{crAsset}' spawns.loc='{crLoc}'");
        }

        log.LogInfo("========== SPAWNER DUMP END ==========");
    }

    private static void LogListCount(BepInEx.Logging.ManualLogSource log, string name, Func<int> f)
    {
        int c = -1;
        try { c = f(); } catch { }
        log.LogInfo("[spawner] PopulationManager." + name + " count=" + c);
    }
}
