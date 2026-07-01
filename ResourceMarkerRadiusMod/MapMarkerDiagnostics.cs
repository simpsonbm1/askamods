using System;
using HarmonyLib;
using SSSGame;
using UnityEngine;

namespace ResourceMarkerRadiusMod
{
    // THE map-ring fix + instrumentation.
    //
    // Confirmed from runtime logs: the map/compass radius ring is sized directly from the
    // WorldObjectiveMarker passed to ObjectiveMarkerContainer.AddMarker (ring sizeDelta == 2 * range),
    // read live at AddMarker time. That marker's `range` is INDEPENDENT of the gathering `radius` we
    // multiply on HarvestMarker — which is why the villagers' search radius grew but the map ring did
    // not. Patching OutpostStructure.objectiveMarker never helped because (a) our sync never even ran
    // and (b) the map uses this WorldObjectiveMarker, not necessarily that objectiveMarker instance.
    //
    // So we intercept at the exact sizing point: a PREFIX on AddMarker resolves the marker's owning
    // OutpostStructure (via WorldObjectiveMarker.structure) and copies the live, already-multiplied
    // harvestMarker.radius into __0.range BEFORE the native code reads it to size the ring. AddMarker
    // fires every time the map/compass rebuilds its icons, so this is self-correcting.
    [HarmonyPatch(typeof(ObjectiveMarkerContainer))]
    public static class ObjectiveMarkerContainerPatch
    {
        [HarmonyPatch(nameof(ObjectiveMarkerContainer.AddMarker))]
        [HarmonyPrefix]
        public static void AddMarker_Prefix(WorldObjectiveMarker __0)
        {
            if (__0 == null) return;

            try
            {
                var harvest = ResolveHarvestMarker(__0, out string how, out string diag);
                bool diagOn = ResourceMarkerRadiusPlugin.EnableDiagnostics.Value;

                if (harvest == null)
                {
                    if (diagOn)
                        ResourceMarkerRadiusPlugin.Logger.LogInfo($"[MapFix] AddMarker resolve FAIL ({diag})");
                    return;
                }

                float radius = harvest.radius;
                if (radius <= 0f) return;

                if (diagOn)
                {
                    ResourceMarkerRadiusPlugin.Logger.LogInfo(
                        $"[MapFix] AddMarker resolve OK via={how} area={harvest.AreaType} " +
                        $"radius={radius:F1} oldRange={__0.range:F1}");
                }

                if (!Mathf.Approximately(__0.range, radius))
                    __0.range = radius;
            }
            catch (Exception ex)
            {
                ResourceMarkerRadiusPlugin.Logger.LogError($"[MapFix] AddMarker_Prefix failed: {ex}");
            }
        }

        // v1.1.1 resolved via WorldObjectiveMarker.structure.GetComponent<OutpostStructure>() and got
        // NULL every time (resolve OK = 0 in the log). The OutpostStructure is almost certainly a
        // PARENT of the objective-marker GameObject, not a sibling — hence GetComponent (same object)
        // misses it. This resolver tries self/parent/child, then falls back to matching the nearest
        // tracked HarvestMarker by world position. `diag` explains why a lookup failed so the next
        // session can see exactly where the chain breaks without another build.
        private static HarvestMarker ResolveHarvestMarker(WorldObjectiveMarker marker, out string how, out string diag)
        {
            how = "none";
            diag = "";

            var structure = marker.structure;
            if (structure == null)
            {
                diag = "structure=null";
            }
            else
            {
                OutpostStructure outpost = structure.GetComponent<OutpostStructure>();
                if (outpost == null) outpost = structure.GetComponentInParent<OutpostStructure>();
                if (outpost == null) outpost = structure.GetComponentInChildren<OutpostStructure>();

                if (outpost == null)
                    diag = $"struct='{structure.name}' but no OutpostStructure self/parent/child";
                else if (outpost.harvestMarker == null)
                    diag = "outpost.harvestMarker=null";
                else
                {
                    how = "structure";
                    return outpost.harvestMarker;
                }
            }

            // Position fallback: match nearest tracked HarvestMarker (co-located with its structure).
            var pos = marker.transform.position;
            HarvestMarker best = null;
            float bestSqr = 25f; // 5m radius
            foreach (var hm in HarvestMarkerPatch.AllMarkers)
            {
                if (hm == null) continue;
                float d = (hm.transform.position - pos).sqrMagnitude;
                if (d < bestSqr)
                {
                    bestSqr = d;
                    best = hm;
                }
            }

            if (best != null)
            {
                how = $"pos{Mathf.Sqrt(bestSqr):F1}m";
                return best;
            }

            if (diag.Length == 0) diag = "no HarvestMarker within 5m";
            return null;
        }

        // Postfix confirms the ring the game actually built from the (now corrected) range.
        [HarmonyPatch(nameof(ObjectiveMarkerContainer.AddMarker))]
        [HarmonyPostfix]
        public static void AddMarker_Postfix(WorldObjectiveMarker __0, CompassObjectiveMarker __result)
        {
            if (!ResourceMarkerRadiusPlugin.EnableDiagnostics.Value) return;
            if (__result == null) return;

            var rt = __result._rangeTransform;
            if (rt == null) return; // markers without a range ring (characters/waypoints/etc.)

            ResourceMarkerRadiusPlugin.Logger.LogInfo(
                $"[MapFix] AddMarker built ring range={( __0 != null ? __0.range : -1f):F1} " +
                $"sizeDelta={rt.sizeDelta} localScale={rt.localScale}");
        }
    }
}
