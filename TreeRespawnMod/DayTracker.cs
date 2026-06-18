using System;
using System.Collections.Generic;
using Fusion;
using UnityEngine;
using SSSGame;
using SSSGame.Weather;

namespace TreeRespawnMod;

public class DayTracker : MonoBehaviour
{
    void Update()
    {
        if (Plugin.PendingRespawns.Count == 0 && Plugin.PendingGatherRespawns.Count == 0
            && Plugin.PendingMiningRespawns.Count == 0) return;

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

        // Mining / stone nodes — determine which backing type we have and attempt respawn.
        var toRemoveMining = new List<string>();
        foreach (var kvp in Plugin.PendingMiningRespawns)
        {
            string posKey = kvp.Key;
            var (gameTimeDepleted, nodeName) = kvp.Value;

            float miningThreshold = Plugin.GetMiningThreshold(nodeName);
            if (miningThreshold <= 0) { toRemoveMining.Add(posKey); continue; }

            float elapsedDays =
                ws.GetTimeDifferenceFromCurrentGameTimeInSeconds(gameTimeDepleted) / dayLength;
            if (elapsedDays < miningThreshold) continue;

            // Stone clumps — identified by presence in CachedStoneData (populated at world load
            // by NetworkHittableSpawnedPatch while the NetworkObject GUID is still valid).
            if (Plugin.CachedStoneData.ContainsKey(posKey) || Plugin.ActiveStoneInstances.ContainsKey(posKey))
            {
                if (!Plugin.CachedStoneData.TryGetValue(posKey, out var stoneData) || !stoneData.guid.IsValid)
                {
                    Plugin.Logger.LogWarning(
                        $"[TreeRespawnMod] Stone \"{nodeName}\" at {posKey}: no GUID cached — giving up.");
                    toRemoveMining.Add(posKey);
                    continue;
                }

                var runner = Plugin.GameNetworkSpawner?._runner;
                if (runner == null || !runner.IsRunning) continue; // retry next tick

                if (!runner.IsServer)
                {
                    toRemoveMining.Add(posKey); // server handles it
                    continue;
                }

                try
                {
                    runner.Spawn(stoneData.guid,
                        new Il2CppSystem.Nullable<Vector3>(Plugin.PosKeyToVector3(posKey)),
                        new Il2CppSystem.Nullable<Quaternion>(stoneData.rotation),
                        null, null, null, false);
                    Plugin.Logger.LogInfo(
                        $"[TreeRespawnMod] Stone \"{nodeName}\" at {posKey} respawned " +
                        $"({elapsedDays:F2} days, guid={stoneData.guid}).");
                    Plugin.CachedStoneData.Remove(posKey);
                    Plugin.ActiveStoneInstances.Remove(posKey);
                    toRemoveMining.Add(posKey);
                }
                catch (Exception ex)
                {
                    Plugin.Logger.LogError($"[TreeRespawnMod] Stone spawn failed at {posKey}: {ex}");
                    toRemoveMining.Add(posKey);
                }
                continue;
            }

            // WorldItemInstance-backed nodes (e.g. Large Stone dropped on ground).
            if (Plugin.ActiveMiningInstances.TryGetValue(posKey, out var miningInst))
            {
                try
                {
                    ushort flagDestroyed = WorldItemInstance.c_flagDestroyed;
                    miningInst._contextFlags = (ushort)(miningInst._contextFlags & ~flagDestroyed);
                    miningInst._OnActivateInstance();
                    Plugin.Logger.LogInfo(
                        $"[TreeRespawnMod] Mining node \"{nodeName}\" at {posKey} respawn attempted " +
                        $"({elapsedDays:F2} days). Destroyed={miningInst.Destroyed}, Active={miningInst.Active}");
                }
                catch (Exception ex)
                {
                    Plugin.Logger.LogError(
                        $"[TreeRespawnMod] Mining respawn failed at {posKey}: {ex}");
                }
                toRemoveMining.Add(posKey);
            }
        }

        bool anyChanges = toRemove.Count > 0 || toRemoveGather.Count > 0 || toRemoveMining.Count > 0;

        foreach (string key in toRemove)
            Plugin.PendingRespawns.Remove(key);
        foreach (string key in toRemoveGather)
            Plugin.PendingGatherRespawns.Remove(key);
        foreach (string key in toRemoveMining)
            Plugin.PendingMiningRespawns.Remove(key);

        if (anyChanges)
            Plugin.SavePending();
    }
}
