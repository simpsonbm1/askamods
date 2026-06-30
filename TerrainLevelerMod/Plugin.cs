using BepInEx;
using BepInEx.Configuration;
using BepInEx.Unity.IL2CPP;
using BepInEx.Logging;
using HarmonyLib;
using SSSGame;
using UnityEngine;

namespace TerrainLevelerMod
{
    [BepInPlugin(PLUGIN_GUID, PLUGIN_NAME, PLUGIN_VERSION)]
    public class Plugin : BasePlugin
    {
        public const string PLUGIN_GUID = "com.askamods.terrainleveler";
        public const string PLUGIN_NAME = "TerrainLevelerMod";
        public const string PLUGIN_VERSION = "1.1.21";

        public static ConfigEntry<float> MaxDragRange;
        public static ConfigEntry<bool> OneHitClear;
        public static ConfigEntry<bool> BypassHeightLimits;
        public static ManualLogSource ModLogger;

        public override void Load()
        {
            ModLogger = Log;
            MaxDragRange = Config.Bind("General", "MaxDragRange", 20f, "Maximum drag distance for the terraforming tool (vanilla is 10)");
            OneHitClear = Config.Bind("General", "OneHitClear", true, "If true, hitting a grid clears all tiles in it simultaneously");
            BypassHeightLimits = Config.Bind("General", "BypassHeightLimits", true, "If true, bypasses native height limits on terrain placement");

            Harmony.CreateAndPatchAll(typeof(Plugin));
            Log.LogInfo($"Plugin {PLUGIN_GUID} is loaded!");
        }

        [HarmonyPatch(typeof(DynamicDimensionsPlacementTool), nameof(DynamicDimensionsPlacementTool.Begin))]
        [HarmonyPrefix]
        public static void DynamicDimensionsPlacementTool_Begin_Prefix(DynamicDimensionsPlacementTool __instance)
        {
            __instance.maxRange = MaxDragRange.Value;
            
            if (BypassHeightLimits.Value)
            {
                __instance.maxHeightDifference = 10000f;
            }
        }

        [HarmonyPatch(typeof(DynamicDimensionsPlacementTool), nameof(DynamicDimensionsPlacementTool._ValidateFootprint))]
        [HarmonyPrefix]
        public static bool DynamicDimensionsPlacementTool_ValidateFootprint_Prefix(ref bool __result)
        {
            if (BypassHeightLimits.Value)
            {
                // Unconditionally return true. This bypasses the native physics checks entirely, 
                // preventing the array overflow crash on large grids, AND allowing placement 
                // through obstacles (like trees) so our OneHitClear can destroy them.
                // Crucially, it allows _ValidatePlacement to run natively and finish, properly clearing error strings!
                __result = true;
                return false;
            }

            return true;
        }

        [HarmonyPatch(typeof(TerraformingGrid), nameof(TerraformingGrid.CheckLevelDifference))]
        [HarmonyPrefix]
        public static bool TerraformingGrid_CheckLevelDifference_Prefix(ref bool __result)
        {
            if (BypassHeightLimits.Value)
            {
                __result = true;
                return false;
            }
            return true;
        }

        [HarmonyPatch(typeof(DynamicDimensionsPlacementTool), nameof(DynamicDimensionsPlacementTool._OnBuildRayChanged))]
        [HarmonyPrefix]
        public static void DynamicDimensionsPlacementTool_OnBuildRayChanged_Prefix(DynamicDimensionsPlacementTool __instance)
        {
            if (BypassHeightLimits.Value)
            {
                __instance.maxHeightDifference = 10000f;
            }
        }

        [HarmonyPatch(typeof(DynamicDimensionsPlacementTool), nameof(DynamicDimensionsPlacementTool._OnBuildingDimensionsChanged))]
        [HarmonyPrefix]
        public static void DynamicDimensionsPlacementTool_OnBuildingDimensionsChanged_Prefix(DynamicDimensionsPlacementTool __instance)
        {
            if (__instance.firstMarker != null && __instance._marker != null)
            {
                var start = __instance.firstMarker.transform.position;
                var end = __instance._marker.transform.position;
                
                // We only care about horizontal (X/Z) bounds to calculate the grid array size
                var start2D = new UnityEngine.Vector2(start.x, start.z);
                var end2D = new UnityEngine.Vector2(end.x, end.z);
                
                // A 20m 2D tether guarantees the grid never exceeds 441 tiles natively
                float maxDist = 20f;
                float currentDist = UnityEngine.Vector2.Distance(start2D, end2D);
                
                if (currentDist > maxDist)
                {
                    // Clamp it!
                    UnityEngine.Vector2 direction = (end2D - start2D).normalized;
                    UnityEngine.Vector2 clamped2D = start2D + (direction * maxDist);
                    
                    // We need to find the REAL ground height at the clamped position
                    // Fire a ray straight down from the sky at that X/Z coordinate
                    UnityEngine.Vector3 rayStart = new UnityEngine.Vector3(clamped2D.x, 1000f, clamped2D.y);
                    if (Physics.Raycast(rayStart, UnityEngine.Vector3.down, out UnityEngine.RaycastHit hit, 2000f, __instance.hitMask))
                    {
                        end = hit.point;
                    }
                    else
                    {
                        // Fallback
                        end = new UnityEngine.Vector3(clamped2D.x, end.y, clamped2D.y);
                    }
                }
                
                // ANTI-CRASH Y-CLAMP:
                // If the user crests a hill, the raycast drops into the valley, creating a massive vertical height difference (e.g. 50m).
                // The native TerraformingGrid mesh generator will violently crash with NaN normals if it tries to render a grid spanning a near-vertical cliff.
                // We strictly clamp the maximum vertical stretch to 15 meters to guarantee a safe slope gradient.
                float maxYDiff = 15f;
                if (System.Math.Abs(end.y - start.y) > maxYDiff)
                {
                    end.y = start.y + (System.Math.Sign(end.y - start.y) * maxYDiff);
                }
                
                // Apply the final heavily-sanitized coordinate
                __instance._marker.transform.position = end;
            }
        }

        [HarmonyPatch(typeof(DynamicDimensionTemplate), nameof(DynamicDimensionTemplate.CreatePreview))]
        [HarmonyPrefix]
        public static void DynamicDimensionTemplate_CreatePreview_Prefix(DynamicDimensionTemplate __instance)
        {
            // Capped at 512 to match the native Fusion NetworkArray limit
            __instance.maxNumberOfTiles = 512;
        }

        [HarmonyPatch(typeof(DynamicDimensionTemplate), nameof(DynamicDimensionTemplate.Create))]
        [HarmonyPrefix]
        public static void DynamicDimensionTemplate_Create_Prefix(DynamicDimensionTemplate __instance)
        {
            __instance.maxNumberOfTiles = 512;
        }

        [HarmonyPatch(typeof(TerraformingGrid), nameof(TerraformingGrid.OnDynamicBuildingDimensionsChanged))]
        [HarmonyPrefix]
        public static bool TerraformingGrid_OnDynamicBuildingDimensionsChanged_Prefix(TerraformingGrid __instance)
        {
            // If the grid exceeds 512 tiles, freeze the visual update by aborting this method.
            // This mathematically guarantees we never overflow the native 512 arrays.
            if (__instance.GetCellsCount() > 512)
            {
                return false;
            }
            return true;
        }

        [HarmonyPatch(typeof(DynamicDimensionsPlacementTool), nameof(DynamicDimensionsPlacementTool._OnSnap))]
        [HarmonyPrefix]
        public static bool DynamicDimensionsPlacementTool_OnSnap_Prefix(DynamicDimensionsPlacementTool __instance)
        {
            if (__instance._dynamicPlacement == null) return true;
            var gridSize = __instance._dynamicPlacement.gridSize;
            if ((gridSize.x * gridSize.z) > 512)
            {
                ModLogger.LogWarning("Grid exceeds maximum networked capacity (512 tiles). Placement blocked.");
                return false;
            }
            return true;
        }

        private static bool isRecursion = false;

        [HarmonyPatch(typeof(TerraformingGrid), nameof(TerraformingGrid.TryLevelTile), new[] { typeof(int) })]
        [HarmonyPrefix]
        public static bool TerraformingGrid_TryLevelTile_Prefix(TerraformingGrid __instance, int currentTileIndex)
        {
            if (!OneHitClear.Value || isRecursion) return true;

            isRecursion = true;
            int cellsCount = __instance.GetCellsCount();
            
            // Loop through all tiles and level them
            for (int i = 0; i < cellsCount; i++)
            {
                if (i != currentTileIndex)
                {
                    try
                    {
                        __instance.TryLevelTile(i);
                    }
                    catch (System.Exception e)
                    {
                        ModLogger.LogError($"Error leveling tile {i}: {e}");
                    }
                }
            }
            
            isRecursion = false;
            
            // Return true to allow the original method to run for the targeted tile, 
            // ensuring any event triggers or stamina costs tied to the successful hit still apply.
            return true;
        }
    }
}
