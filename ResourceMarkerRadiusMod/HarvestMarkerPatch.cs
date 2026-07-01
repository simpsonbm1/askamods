using HarmonyLib;
using SSSGame;
using System.Collections.Generic;
using UnityEngine;
using Il2CppInterop.Runtime;

namespace ResourceMarkerRadiusMod
{
    [HarmonyPatch(typeof(HarvestMarker))]
    public static class HarvestMarkerPatch
    {
        public static List<HarvestMarker> AllMarkers = new List<HarvestMarker>();

        [HarmonyPatch(nameof(HarvestMarker.Awake))]
        [HarmonyPrefix]
        public static void Awake_Prefix(HarvestMarker __instance)
        {
            if (__instance == null) return;

            float multiplier = 1.0f;

            if ((__instance.AreaType & HarvestMarkerAreaType.WOOD) != 0)
                multiplier = ResourceMarkerRadiusPlugin.WoodRadiusMultiplier.Value;
            else if ((__instance.AreaType & HarvestMarkerAreaType.STONE) != 0)
                multiplier = ResourceMarkerRadiusPlugin.StoneRadiusMultiplier.Value;
            else if ((__instance.AreaType & HarvestMarkerAreaType.FORAGING) != 0)
                multiplier = ResourceMarkerRadiusPlugin.ForagingRadiusMultiplier.Value;
            else if ((__instance.AreaType & HarvestMarkerAreaType.FORESTRY) != 0)
                multiplier = ResourceMarkerRadiusPlugin.ForestryRadiusMultiplier.Value;
            else if ((__instance.AreaType & HarvestMarkerAreaType.HUNTING) != 0)
                multiplier = ResourceMarkerRadiusPlugin.HuntingRadiusMultiplier.Value;
            else if ((__instance.AreaType & HarvestMarkerAreaType.SETTLEMENT) != 0)
                multiplier = ResourceMarkerRadiusPlugin.SettlementRadiusMultiplier.Value;
            else if ((__instance.AreaType & HarvestMarkerAreaType.PATROL) != 0)
                multiplier = ResourceMarkerRadiusPlugin.PatrolRadiusMultiplier.Value;

            if (multiplier != 1.0f)
            {
                __instance.radius *= multiplier;
            }
        }



        [HarmonyPatch(nameof(HarvestMarker.Awake))]
        [HarmonyPostfix]
        public static void Awake_Postfix(HarvestMarker __instance)
        {
            if (__instance != null && !AllMarkers.Contains(__instance))
            {
                AllMarkers.Add(__instance);
            }
        }

        [HarmonyPatch(nameof(HarvestMarker.ShowRadiusAbsolute))]
        [HarmonyPrefix]
        public static void ShowRadiusAbsolute_Prefix(HarvestMarker __instance, ref float radius)
        {
            if (__instance != null)
            {
                radius = __instance.radius;
            }
        }

        [HarmonyPatch(nameof(HarvestMarker.ShowRadiusRoutine))]
        [HarmonyPrefix]
        public static void ShowRadiusRoutine_Prefix(HarvestMarker __instance, ref float radius)
        {
            if (__instance != null)
            {
                radius = __instance.radius;
            }
        }

        [HarmonyPatch("OnDestroy")]
        [HarmonyPostfix]
        public static void OnDestroy_Postfix(HarvestMarker __instance)
        {
            if (__instance != null)
            {
                AllMarkers.Remove(__instance);
            }
        }
    }

    // The map/compass radius ring is drawn from StructureObjectiveMarker.range (inherited from
    // WorldObjectiveMarker). The previous approach reached the marker via
    // StructureObjectiveMarker.OwnerStructure.GetComponent<OutpostStructure>() — fragile, because the
    // marker's OwnerStructure is not necessarily the OutpostStructure, so GetComponent could return
    // null and the write silently no-op'd. It also never hooked OutpostStructure._RefreshRanges, the
    // native method that re-syncs ranges (and resets `range` back to the base OutpostRange after load).
    //
    // OutpostStructure directly exposes BOTH markers (objectiveMarker + harvestMarker), so we drive
    // the sync from there: copy the live (already-multiplied) harvestMarker.radius into
    // objectiveMarker.range so the map ring always matches the in-world ring. We sync immediately on
    // the native resync points, and a polling tracker (ResourceMarkerRadiusTracker) re-asserts it as a
    // backstop against any resetter we haven't enumerated and against the map icon being created later.
    [HarmonyPatch(typeof(OutpostStructure))]
    public static class OutpostStructurePatch
    {
        public static readonly List<OutpostStructure> TrackedOutposts = new List<OutpostStructure>();

        // Awake fires for EVERY OutpostStructure, including ones reconstructed when loading a save —
        // unlike Activate/_RefreshRanges, which only fire on fresh placement or data change. Without
        // this, pre-existing outposts on a loaded save were never tracked and never synced (the cause
        // of the empty diagnostic log). We only register here; the markers may not be wired yet, so the
        // polling tracker performs the actual sync once they are.
        [HarmonyPatch("Awake")]
        [HarmonyPostfix]
        public static void Awake_Postfix(OutpostStructure __instance)
        {
            Track(__instance);
        }

        [HarmonyPatch("_RefreshRanges")]
        [HarmonyPostfix]
        public static void RefreshRanges_Postfix(OutpostStructure __instance)
        {
            Track(__instance);
            SyncObjectiveRange(__instance);
        }

        [HarmonyPatch(nameof(OutpostStructure.Activate))]
        [HarmonyPostfix]
        public static void Activate_Postfix(OutpostStructure __instance)
        {
            Track(__instance);
            SyncObjectiveRange(__instance);
        }

        private static void Track(OutpostStructure outpost)
        {
            if (outpost != null && !TrackedOutposts.Contains(outpost))
                TrackedOutposts.Add(outpost);
        }

        // Copy the multiplied in-world radius onto the map marker's range. Returns true if it changed
        // something (used by the tracker to decide whether to log). Safe to call every frame.
        public static bool SyncObjectiveRange(OutpostStructure outpost)
        {
            if (outpost == null) return false;

            var marker = outpost.objectiveMarker;
            var harvest = outpost.harvestMarker;
            if (marker == null || harvest == null) return false;

            float desired = harvest.radius;
            if (Mathf.Approximately(marker.range, desired)) return false;

            if (ResourceMarkerRadiusPlugin.EnableDiagnostics.Value)
            {
                ResourceMarkerRadiusPlugin.Logger.LogInfo(
                    $"[Diagnostic] Syncing map marker range {marker.range:F1} -> {desired:F1} " +
                    $"for {harvest.AreaType} outpost '{outpost.StructureName}'.");
            }

            marker.range = desired;
            marker.Refresh();
            return true;
        }
    }
}
