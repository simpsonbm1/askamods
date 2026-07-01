using HarmonyLib;
using SSSGame;
using UnityEngine;

namespace ResourceMarkerRadiusMod
{
    [HarmonyPatch(typeof(GatherInteraction), nameof(GatherInteraction.GatherItemsCharge))]
    public static class ResourceManagerPatch
    {
        private static System.Collections.Generic.HashSet<int> _loggedInstances = new();

        [HarmonyPostfix]
        public static void Postfix(GatherInteraction __instance)
        {
            if (!ResourceMarkerRadiusPlugin.EnableDiagnostics.Value) return;

            int instanceId = __instance.gameObject.GetInstanceID();
            if (_loggedInstances.Contains(instanceId)) return;
            _loggedInstances.Add(instanceId);

            Vector3 resourcePos = __instance.transform.position;
            HarvestMarker closestMarker = null;
            float minDist = float.MaxValue;

            foreach (var marker in HarvestMarkerPatch.AllMarkers)
            {
                if (marker == null || !marker.isActiveAndEnabled) continue;

                float dist = Vector3.Distance(resourcePos, marker.transform.position);
                if (dist < minDist)
                {
                    minDist = dist;
                    closestMarker = marker;
                }
            }

            if (closestMarker != null)
            {
                string itemName = __instance.name;
                var biomeInst = __instance.GetComponentInParent<BiomeItemInstance>();
                if (biomeInst != null && biomeInst.Descriptor != null)
                {
                    itemName = biomeInst.Descriptor.GetFormalName();
                }

                BepInEx.Logging.Logger.CreateLogSource("ResourceMarkerRadiusMod").LogInfo(
                    $"[Diagnostic] A gather occurred on '{itemName}' at distance {minDist:F1}m from closest {closestMarker.AreaType} marker. (Marker's effective radius: {closestMarker.radius:F1}m)."
                );
            }
        }
    }
}
