using System;
using System.Collections.Generic;
using UnityEngine;
using SSSGame.Weather;

namespace TreeRespawnMod;

public class DayTracker : MonoBehaviour
{
    void Update()
    {
        if (Plugin.PendingRespawns.Count == 0 && Plugin.PendingGatherRespawns.Count == 0) return;

        var ws = WeatherSystem.Instance;
        if (ws == null) return;

        float dayLength = ws.dayLength;
        float threshold = Plugin.RespawnDays.Value;

        var toRemove = new List<string>();
        foreach (var kvp in Plugin.PendingRespawns)
        {
            string posKey = kvp.Key;
            float gameTimeFelled = kvp.Value;

            // Look up the live instance by position — safe after scene reloads and restarts.
            if (!Plugin.ActiveInstances.TryGetValue(posKey, out var inst)) continue;

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

            if (!Plugin.ActiveInstances.TryGetValue(posKey, out var inst)) continue;

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

        bool anyChanges = toRemove.Count > 0 || toRemoveGather.Count > 0;

        foreach (string key in toRemove)
            Plugin.PendingRespawns.Remove(key);
        foreach (string key in toRemoveGather)
            Plugin.PendingGatherRespawns.Remove(key);

        if (anyChanges)
            Plugin.SavePending();
    }
}
