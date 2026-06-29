using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using SSSGame;
using SSSGame.Weather;

namespace TreeRespawnMod;

public class DayTracker : MonoBehaviour
{
    private int _worldCheck;
    private static DateTime _lastOverdueDiagLog = DateTime.MinValue;

    void Update()
    {
        // Resolve which world we're in (StorageManager.ActiveSessionID) so we use the right per-world
        // save file. Poll every frame until known; afterwards poll occasionally to catch a world
        // switch (loading a different save without restarting the game).
        if (Plugin.CurrentWorldId == null)
        {
            Plugin.PollWorldId();
            if (Plugin.CurrentWorldId == null) return;
        }
        else if (++_worldCheck >= 60)
        {
            _worldCheck = 0;
            Plugin.PollWorldId();
        }

        if (Plugin.PendingRespawns.Count == 0 && Plugin.PendingGatherRespawns.Count == 0) return;

        var ws = WeatherSystem.Instance;
        if (ws == null || ws.Runner == null || !ws.Runner.IsServer) return;

        float dayLength = ws.dayLength;
        float threshold = Plugin.RespawnDays.Value;

        bool diag = Plugin.EnableDiagnostics.Value;
        List<string>? overdueTreeNotLoaded = diag ? new List<string>() : null;
        List<string>? overdueGatherNotLoaded = diag ? new List<string>() : null;

        var toRemove = new List<string>();
        foreach (var kvp in Plugin.PendingRespawns)
        {
            string posKey = kvp.Key;
            float gameTimeFelled = kvp.Value;

            // Look up the live instance by position — safe after scene reloads and restarts.
            if (!Plugin.ActiveInstances.TryGetValue(posKey, out var inst))
            {
                if (overdueTreeNotLoaded != null)
                {
                    float elapsed = ws.GetTimeDifferenceFromCurrentGameTimeInSeconds(gameTimeFelled) / dayLength;
                    if (elapsed >= threshold) overdueTreeNotLoaded.Add($"{posKey}({elapsed:F1}d)");
                }
                continue;
            }

            if (inst.Destroyed)
            {
                // Stump was harvested — cancel the respawn
                toRemove.Add(posKey);
                Plugin.RegisteredStumps.Remove(posKey);
                Plugin.Logger.LogInfo(
                    $"[TreeRespawnMod] Stump harvested — cancelled respawn at {posKey}");
                continue;
            }

            float elapsedDays =
                ws.GetTimeDifferenceFromCurrentGameTimeInSeconds(gameTimeFelled) / dayLength;

            if (elapsedDays >= threshold)
            {
                toRemove.Add(posKey);
                Plugin.RegisteredStumps.Remove(posKey);
                try
                {
                    inst.Replenish();
                    Plugin.Logger.LogInfo(
                        $"[TreeRespawnMod] Tree at {posKey} respawned ({elapsedDays:F2} days elapsed).");
                }
                catch (Exception ex)
                {
                    Plugin.Logger.LogError(
                        $"[TreeRespawnMod] Replenish failed at {posKey}: {ex}");
                }
            }
        }

        // Gather resources (reeds, etc.) — no stump-cancel condition, just time-based.
        var toRemoveGather = new List<string>();
        foreach (var kvp in Plugin.PendingGatherRespawns)
        {
            string posKey = kvp.Key;
            var (gameTimeExhausted, itemName) = kvp.Value;

            float gatherThreshold = Plugin.GetGatherThreshold(itemName);
            if (gatherThreshold <= 0)
            {
                // Respawn disabled for this resource type — drop it from the queue.
                toRemoveGather.Add(posKey);
                continue;
            }

            if (!Plugin.ActiveInstances.TryGetValue(posKey, out var inst))
            {
                if (overdueGatherNotLoaded != null)
                {
                    float elapsed = ws.GetTimeDifferenceFromCurrentGameTimeInSeconds(gameTimeExhausted) / dayLength;
                    if (elapsed >= gatherThreshold) overdueGatherNotLoaded.Add($"{posKey}({elapsed:F1}d,{itemName})");
                }
                continue;
            }

            float elapsedDays =
                ws.GetTimeDifferenceFromCurrentGameTimeInSeconds(gameTimeExhausted) / dayLength;

            if (elapsedDays >= gatherThreshold)
            {
                toRemoveGather.Add(posKey);
                try
                {
                    inst.Replenish();
                    Plugin.Logger.LogInfo(
                        $"[TreeRespawnMod] Gather resource \"{itemName}\" at {posKey} respawned ({elapsedDays:F2} days elapsed).");
                }
                catch (Exception ex)
                {
                    Plugin.Logger.LogError(
                        $"[TreeRespawnMod] Replenish failed at {posKey}: {ex}");
                }
            }
        }

        // Throttled summary (Issue D): entries past their threshold that can't respawn because their
        // node isn't streamed in right now. Combined tree+gather under one throttle so a noisy one
        // can't crowd out the other.
        if (diag && (overdueTreeNotLoaded!.Count > 0 || overdueGatherNotLoaded!.Count > 0)
            && (DateTime.UtcNow - _lastOverdueDiagLog).TotalSeconds >= 5)
        {
            _lastOverdueDiagLog = DateTime.UtcNow;
            if (overdueTreeNotLoaded.Count > 0)
                Plugin.Logger.LogInfo(
                    $"[TreeRespawnMod] [diag] overdue-but-not-loaded (tree): {overdueTreeNotLoaded.Count} — {string.Join(", ", overdueTreeNotLoaded.Take(5))}");
            if (overdueGatherNotLoaded!.Count > 0)
                Plugin.Logger.LogInfo(
                    $"[TreeRespawnMod] [diag] overdue-but-not-loaded (gather): {overdueGatherNotLoaded.Count} — {string.Join(", ", overdueGatherNotLoaded.Take(5))}");
        }

        bool anyChanges = toRemove.Count > 0 || toRemoveGather.Count > 0;

        foreach (string key in toRemove)
            Plugin.PendingRespawns.Remove(key);
        foreach (string key in toRemoveGather)
            Plugin.PendingGatherRespawns.Remove(key);

        if (anyChanges)
            Plugin.SavePending();
    }
}
