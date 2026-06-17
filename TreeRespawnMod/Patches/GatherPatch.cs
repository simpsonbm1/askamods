using System;
using HarmonyLib;
using SSSGame;
using SSSGame.Weather;

namespace TreeRespawnMod.Patches;

[HarmonyPatch(typeof(GatherInteraction), nameof(GatherInteraction.GatherItemsCharge))]
internal static class GatherPatch
{
    static void Postfix(GatherInteraction __instance, int __result)
    {
        try
        {
            if (__result <= 0) return; // Nothing was actually gathered

            var biomeInst = __instance._worldInstance?.TryCast<BiomeItemInstance>();
            if (biomeInst == null) return; // Not a biome resource — skip

            // Only register respawn once the resource is fully exhausted
            if (__instance.CheckAvailableItemCount() > 0) return;

            string posKey = Plugin.PosKey(biomeInst.GetPosition());
            if (Plugin.PendingGatherRespawns.ContainsKey(posKey)) return;

            var weather = WeatherSystem.Instance;
            if (weather == null)
            {
                Plugin.Logger.LogWarning("[TreeRespawnMod] WeatherSystem not available, skipping gather respawn registration.");
                return;
            }

            string itemName = __instance.GetGatherableItemInfo()?.Name ?? "";
            float threshold = Plugin.GetGatherThreshold(itemName);

            Plugin.PendingGatherRespawns[posKey] = (weather.NetworkedCurrentGameTime, itemName);
            Plugin.SavePending();

            if (threshold <= 0)
                Plugin.Logger.LogInfo(
                    $"[TreeRespawnMod] Gather resource \"{itemName}\" exhausted at {posKey} — respawn disabled by config.");
            else
                Plugin.Logger.LogInfo(
                    $"[TreeRespawnMod] Gather resource \"{itemName}\" exhausted at {posKey}. " +
                    $"Will respawn in {threshold} days.");
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"[TreeRespawnMod] GatherPatch: {ex}");
        }
    }
}
