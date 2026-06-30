using System;
using HarmonyLib;
using SSSGame;
using SSSGame.Weather;

namespace TreeRespawnMod.Patches;

// By patching WorldItemInstance._OnDataChanged rather than HarvestInteraction._OnWorldInstanceDataChanged,
// we avoid the Unity "Handle is not initialized" crash caused by patching a MonoBehaviour lifecycle method
// during its native initialization. WorldItemInstance is a pure Il2Cpp object, so patching it is safe.
[HarmonyPatch(typeof(WorldItemInstance), nameof(WorldItemInstance._OnDataChanged))]
internal static class DataSyncPatch
{
    // _OnDataChanged fires for every networked data change on every world item, so a weather-unavailable
    // warning here is throttled — unlike GatherPatch/HarvestPatch's local checks (one warning per actual
    // harvest/exhaustion), this would otherwise spam once per tick across the whole loaded world.
    private static DateTime _lastWeatherWarn = DateTime.MinValue;

    static void Postfix(WorldItemInstance __instance)
    {
        try
        {
            var biomeInst = __instance.TryCast<BiomeItemInstance>();
            if (biomeInst == null || biomeInst.gameObject == null) return;

            var harvest = biomeInst.gameObject.GetComponent<HarvestInteraction>();
            var gather = harvest == null ? biomeInst.gameObject.GetComponent<GatherInteraction>() : null;
            if (harvest == null && gather == null) return; // not a tree/gather node — nothing to check

            if (!Plugin.TryGetServerWeather(out var weather, out var reason))
            {
                if ((DateTime.UtcNow - _lastWeatherWarn).TotalSeconds >= 5)
                {
                    _lastWeatherWarn = DateTime.UtcNow;
                    Plugin.Logger.LogWarning(
                        $"[TreeRespawnMod] WeatherSystem not available ({reason}), skipping data-sync " +
                        $"respawn check. world={Plugin.CurrentWorldId ?? "?"}");
                }
                return;
            }

            if (harvest != null)
                CheckHarvest(biomeInst, harvest, weather!);
            else
                CheckGather(biomeInst, gather!, weather!);
        }
        catch (Exception ex)
        {
            try { Plugin.Logger.LogError($"[TreeRespawnMod] DataSyncPatch: {ex}"); } catch { }
        }
    }

    static void CheckHarvest(BiomeItemInstance biomeInst, HarvestInteraction harvest, WeatherSystem weather)
    {
        if (harvest.harvestPieces == null) return;
        string posKey = Plugin.PosKey(biomeInst.GetPosition());
        int totalPieces = harvest.harvestPieces.Count;
        int currentIdx  = harvest.GetCurrentPieceIndex();

        if (totalPieces >= 2 && currentIdx == totalPieces - 1 && !biomeInst.Destroyed
            && !Plugin.RegisteredStumps.Contains(posKey))
        {
            Plugin.RegisteredStumps.Add(posKey);
            Plugin.PendingRespawns[posKey] = weather.NetworkedCurrentGameTime;
            Plugin.SavePending();
            Plugin.Logger.LogInfo(
                $"[TreeRespawnMod] Tree felled (data sync) at {posKey} (day={weather.GetDaysPassed()}). " +
                $"Will respawn in {Plugin.RespawnDays.Value} days if stump remains.");
        }
    }

    static void CheckGather(BiomeItemInstance biomeInst, GatherInteraction gather, WeatherSystem weather)
    {
        if (gather.CheckAvailableItemCount() > 0) return;

        string posKey = Plugin.PosKey(biomeInst.GetPosition());
        if (Plugin.PendingGatherRespawns.ContainsKey(posKey)) return;

        string itemName = gather.GetGatherableItemInfo()?.Name ?? "";
        float threshold = Plugin.GetGatherThreshold(itemName);

        Plugin.PendingGatherRespawns[posKey] = (weather.NetworkedCurrentGameTime, itemName);
        Plugin.SavePending();

        if (threshold <= 0)
            Plugin.Logger.LogInfo(
                $"[TreeRespawnMod] Gather resource \"{itemName}\" exhausted (data sync) at {posKey} — respawn disabled by config.");
        else
            Plugin.Logger.LogInfo(
                $"[TreeRespawnMod] Gather resource \"{itemName}\" exhausted (data sync) at {posKey}. " +
                $"Will respawn in {threshold} days.");
    }
}
