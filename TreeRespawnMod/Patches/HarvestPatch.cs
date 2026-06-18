using System;
using Fusion;
using HarmonyLib;
using SSSGame;
using SSSGame.Weather;
using UnityEngine;

namespace TreeRespawnMod.Patches;

// Capture the stone clump's NetworkObject + GUID at interaction-start time, while the stone is
// still alive and Fusion-spawned. HarvestInteraction.Use() is patchable (unlike Fusion's own
// NetworkObject/SimulationBehaviour methods, which HarmonyX can't intercept in this IL2CPP build).
// By TakeDamage time (post-despawn) everything Fusion-related is null, so we must grab it earlier.
[HarmonyPatch(typeof(HarvestInteraction), nameof(HarvestInteraction.Use))]
internal static class HarvestUsePatch
{
    static void Prefix(HarvestInteraction __instance)
    {
        try
        {
            if (__instance._worldInstance != null) return; // trees / gather — handled elsewhere

            var hittable = __instance._hittable;
            var netObj = hittable?.Object;
            if (netObj == null)
            {
                Plugin.Logger.LogInfo("[TreeRespawnMod] Use(): stone alive but hittable.Object NULL.");
                return;
            }

            var guid = netObj.NetworkGuid;
            string posKey = Plugin.PosKey(__instance.transform.position);
            string name = netObj.gameObject.name;

            Plugin.Logger.LogInfo(
                $"[TreeRespawnMod] Use() stone alive: name=\"{name}\" valid={netObj.IsValid} " +
                $"sceneObj={netObj.IsSceneObject} guid={guid} pos={posKey}");

            if (guid.IsValid)
                Plugin.CachedStoneData[posKey] = (guid, netObj.transform.rotation, name);
        }
        catch (Exception ex)
        {
            try { Plugin.Logger.LogError($"[TreeRespawnMod] HarvestUsePatch: {ex}"); } catch { }
        }
    }
}

[HarmonyPatch(typeof(HarvestInteraction), nameof(HarvestInteraction.TakeDamage))]
internal static class HarvestPatch
{
    private static int _stoneHits = 0;

    static void Postfix(HarvestInteraction __instance)
    {
        try
        {
            if (__instance?.harvestPieces == null) return;

            var biomeInst = __instance._worldInstance?.TryCast<BiomeItemInstance>();

            // ── Biome items (trees, vegetation) ──────────────────────────────────
            if (biomeInst != null)
            {
                string posKey = Plugin.PosKey(biomeInst.GetPosition());
                var weather = WeatherSystem.Instance;
                if (weather == null) return;

                int totalPieces = __instance.harvestPieces.Count;
                int currentIdx  = __instance.GetCurrentPieceIndex();

                if (totalPieces >= 2 && currentIdx == totalPieces - 1 && !biomeInst.Destroyed
                    && !Plugin.RegisteredStumps.Contains(posKey))
                {
                    Plugin.RegisteredStumps.Add(posKey);
                    Plugin.PendingRespawns[posKey] = weather.NetworkedCurrentGameTime;
                    Plugin.SavePending();
                    Plugin.Logger.LogInfo(
                        $"[TreeRespawnMod] Tree felled at {posKey} (day={weather.GetDaysPassed()}). " +
                        $"Will respawn in {Plugin.RespawnDays.Value} days if stump remains.");
                }
                return;
            }

            // ── WorldItemInstance (e.g. Large Stone dropped on ground) ────────────
            var worldInst = __instance._worldInstance;
            if (worldInst != null)
            {
                string miningPosKey = Plugin.PosKey(worldInst.GetPosition());
                Plugin.ActiveMiningInstances[miningPosKey] = worldInst;
                if (Plugin.PendingMiningRespawns.ContainsKey(miningPosKey)) return;

                if (__instance.GetTotalRemainingHitPoints() > 0) return;

                var miningWeather = WeatherSystem.Instance;
                if (miningWeather == null) return;

                string nodeName = worldInst.Descriptor?.GetFormalName() ?? "unknown";
                float threshold = Plugin.GetMiningThreshold(nodeName);
                Plugin.PendingMiningRespawns[miningPosKey] = (miningWeather.NetworkedCurrentGameTime, nodeName);
                Plugin.SavePending();

                Plugin.Logger.LogInfo(threshold <= 0
                    ? $"[TreeRespawnMod] Mining node \"{nodeName}\" depleted at {miningPosKey} — respawn disabled."
                    : $"[TreeRespawnMod] Mining node \"{nodeName}\" depleted at {miningPosKey}. Will respawn in {threshold} days.");
                return;
            }

            // ── Stone clumps: no WorldItemInstance ──
            var go = __instance.gameObject;
            if (go == null) return;

            Vector3 stonePos = __instance.transform.position;
            string stonePosKey = Plugin.PosKey(stonePos);
            float hp         = __instance.GetTotalRemainingHitPoints();
            uint  vegeData   = __instance._currentVegeData;
            int   pieceCount = __instance.harvestPieces.Count;
            var   netObjRef  = __instance.NetworkObject;
            bool  isValid    = netObjRef?.IsValid ?? false;

            // Diagnostic log fires regardless of pending state — must be before the early exit.
            if (_stoneHits < 20)
            {
                _stoneHits++;
                var hittableRef = __instance._hittable;
                string parentName = __instance.transform.parent?.gameObject?.name ?? "NO_PARENT";

                string hittableObjInfo = "hittable=NULL";
                if (hittableRef != null)
                {
                    var hno = hittableRef.Object;
                    if (hno == null)
                        hittableObjInfo = "hittable.Object=NULL";
                    else
                        hittableObjInfo =
                            $"hittable.Object: valid={hno.IsValid} sceneObj={hno.IsSceneObject} " +
                            $"guid={hno.NetworkGuid} name=\"{hno.gameObject.name}\"";
                }

                Plugin.Logger.LogInfo(
                    $"[TreeRespawnMod] Stone hit #{_stoneHits}: goName=\"{__instance.gameObject.name}\" " +
                    $"parent=\"{parentName}\" " +
                    $"netObj={(netObjRef == null ? "NULL" : $"valid={netObjRef.IsValid} guid={netObjRef.NetworkGuid}")} " +
                    $"{hittableObjInfo} " +
                    $"HP={hp:F1} vegeData={vegeData}");
            }

            Plugin.ActiveStoneInstances[stonePosKey] = __instance;
            if (Plugin.PendingMiningRespawns.ContainsKey(stonePosKey)) return;

            string stoneNodeName = Plugin.CachedStoneData.TryGetValue(stonePosKey, out var cached) && !string.IsNullOrEmpty(cached.name)
                ? cached.name
                : __instance.gameObject.name;

            bool depleted = hp <= 0 || (pieceCount == 1 && (vegeData & 1u) != 0) || !isValid;
            if (!depleted) return;

            var weather2 = WeatherSystem.Instance;
            if (weather2 == null) return;

            float threshold2 = Plugin.GetMiningThreshold(stoneNodeName);
            Plugin.PendingMiningRespawns[stonePosKey] = (weather2.NetworkedCurrentGameTime, stoneNodeName);
            Plugin.SavePending();

            var cachedGuid = Plugin.CachedStoneData.TryGetValue(stonePosKey, out var cd) ? cd.guid : default;
            Plugin.Logger.LogInfo(threshold2 <= 0
                ? $"[TreeRespawnMod] Stone \"{stoneNodeName}\" depleted at {stonePosKey} — respawn disabled (guid={cachedGuid})."
                : $"[TreeRespawnMod] Stone \"{stoneNodeName}\" depleted at {stonePosKey}. Will respawn in {threshold2} days (guid={cachedGuid}).");
        }
        catch (Exception ex)
        {
            try { Plugin.Logger.LogError($"[TreeRespawnMod] HarvestPatch: {ex}"); } catch { }
        }
    }
}

[HarmonyPatch(typeof(SSSGame.Combat.HarvestDamageReceiver), nameof(SSSGame.Combat.HarvestDamageReceiver.TakeDamage))]
internal static class HarvestDamageReceiverPatch
{
    private static bool _fired = false;

    static void Postfix(SSSGame.Combat.HarvestDamageReceiver __instance)
    {
        try
        {
            if (!_fired)
            {
                _fired = true;
                Plugin.Logger.LogInfo(
                    $"[TreeRespawnMod] HarvestDamageReceiver.TakeDamage fired! " +
                    $"hostObject={__instance.hostObject?.name ?? "null"}, " +
                    $"host type={__instance._hostDamageReceiver?.GetType()?.Name ?? "null"}");
            }
        }
        catch (Exception ex)
        {
            try { Plugin.Logger.LogError($"[TreeRespawnMod] HarvestDamageReceiverPatch: {ex}"); } catch { }
        }
    }
}
