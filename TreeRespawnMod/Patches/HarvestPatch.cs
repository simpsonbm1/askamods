using System;
using HarmonyLib;
using SSSGame;

namespace TreeRespawnMod.Patches;

// Detect a felled tree (trunk gone, stump remains) and register it for respawn.
// Trees are BiomeItemInstance-backed; their _worldInstance stays non-null through TakeDamage
// (the stump persists), so we can read it here. Non-biome harvestables (stone clumps, loose
// Item_* nodes) are skipped — stone clumps in particular are Fusion scene objects with no
// mod-reachable respawn path (see STONE_RESPAWN_HANDOFF.md).
[HarmonyPatch(typeof(HarvestInteraction), nameof(HarvestInteraction.TakeDamage))]
internal static class HarvestPatch
{
    static void Postfix(HarvestInteraction __instance)
    {
        try
        {
            if (__instance?.harvestPieces == null) return;

            var biomeInst = __instance._worldInstance?.TryCast<BiomeItemInstance>();
            if (biomeInst == null) return; // not a tree/biome resource — skip

            string posKey = Plugin.PosKey(biomeInst.GetPosition());
            int totalPieces = __instance.harvestPieces.Count;
            int currentIdx  = __instance.GetCurrentPieceIndex();

            // Trunk felled when only the last piece (stump) remains. Check this BEFORE touching
            // WeatherSystem so a failed weather check (logged below) only fires on the actual felling
            // hit, not on every non-final swing — TakeDamage runs once per hit.
            if (totalPieces < 2 || currentIdx != totalPieces - 1 || biomeInst.Destroyed
                || Plugin.RegisteredStumps.Contains(posKey)) return;

            if (!Plugin.TryGetServerWeather(out var weather, out var reason))
            {
                Plugin.Logger.LogWarning(
                    $"[TreeRespawnMod] WeatherSystem not available ({reason}), skipping TREE respawn " +
                    $"registration at {posKey}. world={Plugin.CurrentWorldId ?? "?"}");
                return;
            }

            Plugin.RegisteredStumps.Add(posKey);
            Plugin.PendingRespawns[posKey] = weather!.NetworkedCurrentGameTime;
            // Cache the tree's WorldItemInstanceId NOW, while the instance is valid — DayTracker uses
            // it to validate the cached pointer (pooled wrappers get reused for other nodes once a
            // chunk unloads) and to re-resolve/refill the stump via the data handler while unloaded.
            try { Plugin.TreeWid[posKey] = biomeInst.GetWorldItemInstanceId(); } catch { }
            Plugin.KnownNodes[posKey] = (NodeKind.Tree, "");
            Plugin.SavePending();
            Plugin.Logger.LogInfo(
                $"[TreeRespawnMod] Tree felled at {posKey} (day={weather.GetDaysPassed()}). " +
                $"Will respawn in {Plugin.RespawnDays.Value} days if stump remains.");
        }
        catch (Exception ex)
        {
            try { Plugin.Logger.LogError($"[TreeRespawnMod] HarvestPatch: {ex}"); } catch { }
        }
    }
}

