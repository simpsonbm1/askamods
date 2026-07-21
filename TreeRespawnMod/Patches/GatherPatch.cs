using System;
using HarmonyLib;
using SSSGame;
using UnityEngine;

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

            if (!Plugin.TryGetServerWeather(out var weather, out var reason))
            {
                Plugin.Logger.LogWarning(
                    $"[TreeRespawnMod] WeatherSystem not available ({reason}), skipping gather respawn " +
                    $"registration at {posKey}. world={Plugin.CurrentWorldId ?? "?"}");
                return;
            }

            // Locale-proof (v1.7.1): key the per-item respawn override on the INVARIANT asset name
            // ("Item_Food_BiomeMushroomGrey") rather than the localized display name ("Graue Pilze"),
            // so a config override like "Mushroom"->0.1 resolves in every language. The override keys
            // are substrings of the English-derived asset names, so English behaviour is unchanged;
            // in non-English the display name matched nothing and every node fell back to the default
            // rate. Falls back to the display name if the asset name is unavailable. (Gather-respawn
            // diagnostic log lines therefore show the asset name, which is locale-stable.)
            var ggi = __instance.GetGatherableItemInfo();
            string itemName = "";
            try { itemName = ggi?.name ?? ""; } catch { }
            if (string.IsNullOrEmpty(itemName)) { try { itemName = ggi?.Name ?? ""; } catch { } }
            float threshold = Plugin.GetGatherThreshold(itemName);

            Plugin.PendingGatherRespawns[posKey] = (weather!.NetworkedCurrentGameTime, itemName);
            Plugin.SavePending();

            // Cache the node's WorldItemInstanceId NOW, while the instance is valid — the
            // deactivated-replenish path (v1.2.9) needs it to re-resolve the instance after the chunk
            // unloads (by then the cached ActiveInstances pointer is stale: deregistered, UniqueId=0).
            try { Plugin.GatherWid[posKey] = biomeInst.GetWorldItemInstanceId(); } catch { }
            Plugin.KnownNodes[posKey] = (NodeKind.Gather, itemName);

            // v1.2.7 probe (EnableDiagnostics-gated, purely additive — wrapped so a throw here can't
            // disturb the registration above): capture, AT HARVEST TIME, how far the player was from
            // this node and the node's own liveness. This forks the open question in
            // TREERESPAWN_HANDOFF.md — can a villager exhaust a node whose tile isn't streamed in near
            // the host? A registration logged with a large playerDist and/or inActiveInst=False /
            // instActive=False would say YES (the depletion happens away from the host), which means a
            // liveness guard alone can't give hands-off respawn. Compare instActive here against the
            // gather-respawn snapshot's Active for the same posKey to learn what Active actually tracks.
            if (Plugin.EnableDiagnostics.Value)
            {
                try
                {
                    string playerInfo = "playerDist=?";
                    if (Plugin.TryGetPlayerPos(out var pp))
                    {
                        var np = biomeInst.GetPosition();
                        float dx = np.x - pp.x, dz = np.z - pp.z;
                        playerInfo = $"playerDist={Mathf.Sqrt(dx * dx + dz * dz):F0}m";
                    }

                    bool inAI = Plugin.ActiveInstances.ContainsKey(posKey);
                    bool instActive = false, instDestroyed = false;
                    try { instActive = biomeInst.Active; } catch { }
                    try { instDestroyed = biomeInst.Destroyed; } catch { }
                    string interactGo = "?";
                    try { interactGo = __instance.gameObject != null && __instance.gameObject.activeInHierarchy ? "active" : "inactive"; } catch { }

                    Plugin.Logger.LogInfo(
                        $"[TreeRespawnMod] [diag] gather-reg \"{itemName}\" at {posKey}: {playerInfo} " +
                        $"inActiveInst={inAI} instActive={instActive} destroyed={instDestroyed} interactGO={interactGo} " +
                        $"| {Plugin.ProbeBuffer(biomeInst)}");
                }
                catch { }
            }

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

