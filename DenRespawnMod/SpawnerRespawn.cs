using System;
using System.Collections.Generic;
using SSSGame;
using SSSGame.Combat;
using SandSailorStudio.WorldGen;
using SandSailorStudio.Streaming;
using UnityEngine;

namespace DenRespawnMod;

// Parallel force-respawn path (v1.4.0) for bear dens / wight spires, which are NOT SSSGame.Den
// instances — they are standalone SSSGame.Combat.PopulationSpawner entries in
// PopulationManager._populationSpawners (confirmed in-game 2026-07-21, see
// LocaleAuditMod/SpawnerDump.cs). They have no durable "defeated" flag, only current occupancy
// (HasNoAliveCreatures/GetCreatureCount). Mirrors DenMapRevive's shape (manual click entry point +
// a pending remote-load queue + a day-tick timer) but owns entirely separate transient state —
// this file never touches DenTracker's own _pendingRefreshes / _anchors.
internal static class SpawnerRespawn
{
    private const float PendingTimeoutSeconds = 30f;

    private static readonly List<GameObject> _anchors = new();
    private static int _anchorCounter;

    private struct Pending
    {
        public Vector3 Pos;
        public float RequestedAt;
    }

    private static readonly List<Pending> _pending = new();

    // Day-rule timer bookkeeping — spawner name (as configured) -> last in-game day it fired.
    private static readonly Dictionary<string, int> _ruleLastFiredDay = new(StringComparer.OrdinalIgnoreCase);

    private static bool _fireVerified;

    // Strips a trailing "(Clone)", trims, lowercases. Shared by NameMatches (whitelist prefix
    // match) and ForceRuleMatches (single rule-name prefix match).
    private static string NormalizeSpawnerName(string goName)
    {
        if (string.IsNullOrEmpty(goName)) return "";
        string n = goName.Trim();
        const string cloneSuffix = "(Clone)";
        if (n.EndsWith(cloneSuffix, StringComparison.OrdinalIgnoreCase))
            n = n.Substring(0, n.Length - cloneSuffix.Length).Trim();
        return n.ToLowerInvariant();
    }

    // True if the stripped/trimmed spawner name StartsWith any configured SpawnerNames prefix.
    private static bool NameMatches(string goName)
    {
        try
        {
            string n = NormalizeSpawnerName(goName);
            if (n.Length == 0) return false;

            foreach (var prefix in Plugin.SpawnerNameList)
            {
                if (prefix.Length > 0 && n.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"[DenRespawn] SpawnerRespawn.NameMatches error: {ex}");
        }
        return false;
    }

    private static PopulationManager? GetPM()
    {
        try { return UnityEngine.Object.FindAnyObjectByType<PopulationManager>(); }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"[DenRespawn] SpawnerRespawn.GetPM error: {ex}");
            return null;
        }
    }

    // Same Runner.IsServer/IsSharedModeMasterClient host gate as DenTracker.EnqueueRemoteRefresh.
    // Returns false (no spam) if LocalPlayer/NetworkObject/Runner isn't available yet.
    private static bool IsHost()
    {
        try
        {
            var player = Plugin.LocalPlayer;
            if (player == null) return false;
            if (player.NetworkObject == null || player.NetworkObject.Runner == null) return false;
            return player.NetworkObject.Runner.IsServer || player.NetworkObject.Runner.IsSharedModeMasterClient;
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"[DenRespawn] SpawnerRespawn.IsHost error: {ex}");
            return false;
        }
    }

    // Scans PM._populationSpawners for whitelist matches within `radius` of `pos`, and
    // force-respawns each (SetActiveSpawner + RespawnAllPopulations, each in its own try/catch so
    // one bad spawner never aborts the scan). When onlyIfEmpty is true, only currently-empty
    // spawners are forced; when false (manual click — explicit user intent), all name/radius
    // matches are forced regardless of occupancy. Returns how many were forced.
    private static int ForceMatchesNear(Vector3 pos, float radius, bool onlyIfEmpty)
    {
        int forced = 0;
        int candidates = 0;
        float nearestXZ = float.MaxValue;
        bool nearestEmpty = false;
        string nearestName = "?";
        try
        {
            var pm = GetPM();
            if (pm == null) return 0;

            var list = pm._populationSpawners;
            if (list == null) return 0;

            int n = list.Count;
            for (int i = 0; i < n; i++)
            {
                PopulationSpawner? s = null;
                try { s = list[i]; } catch { }
                if (s == null) continue;

                string goName = "?";
                try { goName = s.gameObject.name; } catch { continue; }
                if (!NameMatches(goName)) continue;

                candidates++;

                // Horizontal (XZ-plane) distance — map pins resolve at Y=0 while spawners sit at
                // terrain height, so 3D distance was rejecting valid same-column matches
                // (confirmed in-game 2026-07-21: pin Y=0, bear den spawner Y≈44 -> 3D dist 44m >
                // 40m radius despite identical X/Z).
                float dist;
                try
                {
                    float dx = pos.x - s.transform.position.x;
                    float dz = pos.z - s.transform.position.z;
                    dist = (float)Math.Sqrt(dx * dx + dz * dz);
                }
                catch { continue; }

                bool empty = false;
                try { empty = s.HasNoAliveCreatures(); } catch { }

                if (dist < nearestXZ)
                {
                    nearestXZ = dist;
                    nearestEmpty = empty;
                    nearestName = goName;
                }

                if (dist > radius) continue;
                if (onlyIfEmpty && !empty) continue;

                if (ForceOne(s, goName)) forced++;
            }
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"[DenRespawn] SpawnerRespawn.ForceMatchesNear error: {ex}");
        }

        if (forced > 0)
        {
            Plugin.Logger.LogInfo($"[DenRespawn] SpawnerRespawn forced {forced} spawner(s) near target position (radius={radius:F0}m).");
        }
        else if (Plugin.DenDiagnostics.Value)
        {
            string posStr = pos.ToString();
            Plugin.Logger.LogInfo($"[DenRespawn] SpawnerRespawn: forced 0 near {posStr} (radius={radius:F0}m) — {candidates} name-matched candidate(s); nearest XZ={nearestXZ:F0}m empty={nearestEmpty} name='{nearestName}'");
        }

        return forced;
    }

    // Scans the WHOLE loaded set (no radius — the day timer is map-wide over loaded tiles) for
    // spawners matching this one rule name that are currently empty, and force-respawns each.
    private static int ForceRuleMatches(string ruleName)
    {
        int forced = 0;
        try
        {
            var pm = GetPM();
            if (pm == null) return 0;

            var list = pm._populationSpawners;
            if (list == null) return 0;

            string ruleNorm = NormalizeSpawnerName(ruleName);
            if (ruleNorm.Length == 0) return 0;

            int n = list.Count;
            for (int i = 0; i < n; i++)
            {
                PopulationSpawner? s = null;
                try { s = list[i]; } catch { }
                if (s == null) continue;

                string goName = "?";
                try { goName = s.gameObject.name; } catch { continue; }

                string stripped = NormalizeSpawnerName(goName);
                if (stripped.Length == 0 || !stripped.StartsWith(ruleNorm, StringComparison.OrdinalIgnoreCase)) continue;

                bool empty = false;
                try { empty = s.HasNoAliveCreatures(); } catch { }
                if (!empty) continue;

                if (ForceOne(s, goName)) forced++;
            }
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"[DenRespawn] SpawnerRespawn.ForceRuleMatches error for rule '{ruleName}': {ex}");
        }

        Plugin.Logger.LogInfo($"[DenRespawn] SpawnerRespawn timer rule '{ruleName}' forced {forced} spawner(s).");
        return forced;
    }

    // Instant-spawn-first force: directly calls SpawnPopulationFree on each of the spawner's
    // configured populations to top them up to their max size right now, rather than only
    // scheduling a respawn via RespawnAllPopulations (which the engine defers while the player
    // is in range — the original bug this rewrite fixes). Falls back to the scheduling path
    // only if the instant loop spawned nothing at all, so a click is never a total no-op. Each
    // interop call/read guarded in its own try/catch. Fires the one-time FIRE-VERIFY line the
    // first time this reaches the summary log. Always returns true (this is the "force").
    private static bool ForceOne(PopulationSpawner s, string goName)
    {
        int instantTotal = 0;

        try
        {
            var pops = s._populations;
            if (pops != null && pops.Count > 0)
            {
                int n = pops.Count;
                for (int i = 0; i < n; i++)
                {
                    PopulationSpawner.SpawnPopulation? pop = null;
                    try { pop = pops[i]; } catch { }
                    if (pop == null) continue;

                    int have = 0;
                    try { have = pop.creatures?.Count ?? 0; } catch { }

                    int want = 0;
                    try { want = pop.MaxPopulationSize; } catch { }
                    if (want <= 0)
                    {
                        try { want = pop.size; } catch { }
                    }
                    if (want <= 0)
                    {
                        try { want = pop.config?.maxPopulationSize ?? 0; } catch { }
                    }
                    if (want <= 0) want = 1;

                    int toSpawn = want - have;
                    if (toSpawn < 0) toSpawn = 0;
                    if (toSpawn > 10) toSpawn = 10;
                    if (toSpawn <= 0) continue;

                    bool hasSpace = false;
                    try { hasSpace = pop.HasSpaceToSpawn(); } catch { }

                    int got = 0;
                    try
                    {
                        var spawned = s.SpawnPopulationFree(pop, toSpawn, null);
                        got = spawned?.Count ?? 0;
                    }
                    catch (Exception ex)
                    {
                        Plugin.Logger.LogError($"[DenRespawn] SpawnerRespawn: SpawnPopulationFree error on '{goName}': {ex}");
                    }

                    instantTotal += got;

                    if (Plugin.DenDiagnostics.Value)
                    {
                        Plugin.Logger.LogInfo($"[DenRespawn][spawnforce] '{goName}' pop: have={have} want={want} toSpawn={toSpawn} hasSpace={hasSpace} -> SpawnPopulationFree returned={got}");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"[DenRespawn] SpawnerRespawn: instant-spawn loop error on '{goName}': {ex}");
        }

        if (instantTotal == 0)
        {
            try { s.SetActiveSpawner(true, true); }
            catch (Exception ex)
            {
                Plugin.Logger.LogError($"[DenRespawn] SpawnerRespawn: SetActiveSpawner error on '{goName}': {ex}");
            }

            try { s.RespawnAllPopulations(false); }
            catch (Exception ex)
            {
                Plugin.Logger.LogError($"[DenRespawn] SpawnerRespawn: RespawnAllPopulations error on '{goName}': {ex}");
            }

            if (Plugin.DenDiagnostics.Value)
            {
                Plugin.Logger.LogInfo($"[DenRespawn][spawnforce] '{goName}' instant=0 -> fell back to SetActiveSpawner+RespawnAllPopulations (will spawn when player leaves)");
            }
        }

        if (instantTotal > 0)
        {
            Plugin.Logger.LogInfo($"[DenRespawn] SpawnerRespawn '{goName}': instant-spawned {instantTotal} creature(s)");
        }
        else
        {
            Plugin.Logger.LogInfo($"[DenRespawn] SpawnerRespawn '{goName}': scheduled respawn (instant path spawned 0)");
        }

        if (!_fireVerified)
        {
            _fireVerified = true;
            Plugin.Logger.LogInfo("[DenRespawn] FIRE-VERIFY: SpawnerRespawn forced its first spawner");
        }

        return true;
    }

    // Manual Shift+click entry point. Returns true if it CLAIMS the click (caller then skips the
    // Den path), false if it declines and the Den path should run instead.
    internal static bool TryForce(Vector3 pos)
    {
        try
        {
            if (!Plugin.SpawnerRespawnEnable.Value) return false;
            if (DenRegistry.FindNearest(pos, Plugin.MapPinMatchRadius.Value) != null) return false;
        }
        catch (Exception ex)
        {
            // Failure before claiming — decline and let the Den path run rather than swallow the click.
            Plugin.Logger.LogError($"[DenRespawn] SpawnerRespawn.TryForce pre-claim error: {ex}");
            return false;
        }

        // From here on we CLAIM the click — any further internal error still returns true so we
        // never fall through into the Den path mid-failure.
        try
        {
            if (!IsHost())
            {
                DenTracker.Instance?.Notify("Only the host can respawn this!");
                return true;
            }

            int forced = ForceMatchesNear(pos, Plugin.SpawnerMatchRadius.Value, onlyIfEmpty: false);
            if (forced > 0)
            {
                DenTracker.Instance?.Notify($"Repopulating ({forced})...");
                return true;
            }

            // Nothing loaded near the pin — force-stream the tile (mirrors
            // DenTracker.EnqueueRemoteRefresh's remote-load path).
            var streaming = Plugin.StreamingManager;
            if (streaming == null)
            {
                DenTracker.Instance?.Notify("World streaming not ready");
                Plugin.Logger.LogWarning("[DenRespawn] SpawnerRespawn.TryForce: no WorldStreamingManager captured.");
                return true;
            }

            float tileSize = 128f;
            try
            {
                var cfg = WorldConfiguration.GetActive();
                if (cfg != null && cfg.tileSize > 0) tileSize = cfg.tileSize;
            }
            catch { }

            // WorldTileId lives in SandSailorStudio.Streaming, not .WorldGen (confirmed by
            // build — DenTracker.cs's bare reference resolves via its own `using
            // SandSailorStudio.Streaming;`, which this file also carries above).
            WorldTileId tileId;
            try { tileId = WorldTileId.GetLowest(pos.x, pos.z, tileSize); }
            catch (Exception ex)
            {
                Plugin.Logger.LogError($"[DenRespawn] SpawnerRespawn.TryForce: WorldTileId.GetLowest failed: {ex}");
                DenTracker.Instance?.Notify("Spawner respawn failed (see log)");
                return true;
            }

            var anchor = new GameObject($"DenRespawn_SpawnerAnchor_{_anchorCounter}");
            anchor.transform.position = pos;
            UnityEngine.Object.DontDestroyOnLoad(anchor);
            _anchors.Add(anchor);

            int requestId = 772000 + _anchorCounter;
            _anchorCounter++;

            try { streaming.RequestLoadWorldTile(requestId, anchor.transform, tileId); }
            catch (Exception ex)
            {
                Plugin.Logger.LogError($"[DenRespawn] SpawnerRespawn.TryForce: RequestLoadWorldTile failed: {ex}");
            }

            _pending.Add(new Pending { Pos = pos, RequestedAt = Time.time });

            string tileStr = tileId.ToString();
            Plugin.Logger.LogInfo($"[DenRespawn] SpawnerRespawn requested remote tile load tile={tileStr} reqId={requestId}");

            DenTracker.Instance?.Notify("Reaching that spire/den...");
            return true;
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"[DenRespawn] SpawnerRespawn.TryForce error: {ex}");
            return true;
        }
    }

    // Pending-request scan — called from DenTracker's own tick. Resolves or times out each queued
    // remote-force request.
    internal static void ServicePending()
    {
        try
        {
            if (_pending.Count == 0) return;

            for (int i = _pending.Count - 1; i >= 0; i--)
            {
                var entry = _pending[i];
                int forced = ForceMatchesNear(entry.Pos, Plugin.SpawnerMatchRadius.Value, onlyIfEmpty: false);

                if (forced > 0)
                {
                    DenTracker.Instance?.Notify($"Repopulated ({forced})!");
                    _pending.RemoveAt(i);
                }
                else if (Time.time - entry.RequestedAt > PendingTimeoutSeconds)
                {
                    Plugin.Logger.LogWarning("[DenRespawn] SpawnerRespawn: pending remote force timed out (tile never produced a matching empty spawner).");
                    DenTracker.Instance?.Notify("Nothing to repopulate there");
                    _pending.RemoveAt(i);
                }
            }
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"[DenRespawn] SpawnerRespawn.ServicePending error: {ex}");
        }
    }

    // Day-rule tick — called once per detected in-game day change (from DenTracker, alongside
    // TickAutoRespawnRules). Baseline-then-fire per rule name: the first day a rule is observed
    // just establishes _ruleLastFiredDay so it doesn't fire immediately on load.
    internal static void TickTimer()
    {
        try
        {
            if (!Plugin.SpawnerRespawnEnable.Value || !IsHost() || Plugin.SpawnerRules.Count == 0) return;

            foreach (var kv in Plugin.SpawnerRules)
            {
                string name = kv.Key;
                int days = kv.Value;

                if (!_ruleLastFiredDay.TryGetValue(name, out int lastFired))
                {
                    _ruleLastFiredDay[name] = DayCounter.CurrentDay;
                    continue;
                }

                if (DayCounter.CurrentDay - lastFired >= days)
                {
                    _ruleLastFiredDay[name] = DayCounter.CurrentDay;
                    Plugin.Logger.LogInfo($"[DenRespawn] SpawnerRespawn rule '{name}:{days}' firing (last fired day {lastFired}, now day {DayCounter.CurrentDay}).");
                    ForceRuleMatches(name);
                }
            }
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"[DenRespawn] SpawnerRespawn.TickTimer error: {ex}");
        }
    }

    // Mirrors DenTracker.ClearTransientState — called from Plugin.NoteWorldLeft. Drops all
    // per-world transient state: pending remote-force queue, force-load anchor GameObjects
    // (destroyed, not just forgotten), and the day-rule timer baselines.
    internal static void ClearTransientState()
    {
        try
        {
            _pending.Clear();

            foreach (var go in _anchors)
                if (go != null) UnityEngine.Object.Destroy(go);
            _anchors.Clear();

            _ruleLastFiredDay.Clear();
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"[DenRespawn] SpawnerRespawn.ClearTransientState error: {ex}");
        }
    }
}
