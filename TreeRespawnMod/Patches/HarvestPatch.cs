using System;
using HarmonyLib;
using SSSGame;
using SSSGame.Weather;

namespace TreeRespawnMod.Patches;

[HarmonyPatch(typeof(HarvestInteraction), nameof(HarvestInteraction.TakeDamage))]
internal static class HarvestPatch
{
    static void Postfix(HarvestInteraction __instance)
    {
        try
        {
            if (__instance?.harvestPieces == null) return;

            int totalPieces = __instance.harvestPieces.Count;
            if (totalPieces < 2) return;  // Single-piece resources have no separate stump

            int currentIdx = __instance.GetCurrentPieceIndex();
            if (currentIdx < totalPieces - 1) return;  // Still working through trunk pieces

            // Only biome vegetation (trees etc.) will cast to BiomeItemInstance
            var biomeInst = __instance._worldInstance?.TryCast<BiomeItemInstance>();
            if (biomeInst == null)
            {
                Plugin.Logger.LogDebug(
                    $"[TreeRespawnMod] Stump reached but _worldInstance is not a BiomeItemInstance " +
                    $"(type={__instance._worldInstance?.GetType()?.Name ?? "null"}) — skipping.");
                return;
            }

            string posKey = Plugin.PosKey(biomeInst.GetPosition());
            if (Plugin.RegisteredStumps.Contains(posKey)) return;

            var weather = WeatherSystem.Instance;
            if (weather == null)
            {
                Plugin.Logger.LogWarning("[TreeRespawnMod] WeatherSystem not available, skipping respawn registration.");
                return;
            }

            Plugin.RegisteredStumps.Add(posKey);
            Plugin.PendingRespawns[posKey] = weather.NetworkedCurrentGameTime;
            Plugin.SavePending();

            Plugin.Logger.LogInfo(
                $"[TreeRespawnMod] Tree felled at {posKey} (day={weather.GetDaysPassed()}). " +
                $"Will respawn in {Plugin.RespawnDays.Value} days if stump remains.");
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"[TreeRespawnMod] HarvestPatch: {ex}");
        }
    }
}
