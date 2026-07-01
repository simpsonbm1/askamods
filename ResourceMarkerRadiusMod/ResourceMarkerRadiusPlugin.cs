using BepInEx;
using BepInEx.Configuration;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using SSSGame;
using System;
using UnityEngine;
using UnityEngine.Rendering.HighDefinition;
using Il2CppInterop.Runtime.Injection;

namespace ResourceMarkerRadiusMod
{
    [BepInPlugin("simpsonbm1.askamods.resourcemarkerradius", "Resource Marker Radius Mod", "1.1.2")]
    public class ResourceMarkerRadiusPlugin : BasePlugin
    {
        public static ConfigEntry<float> WoodRadiusMultiplier;
        public static ConfigEntry<float> StoneRadiusMultiplier;
        public static ConfigEntry<float> ForagingRadiusMultiplier;
        public static ConfigEntry<float> ForestryRadiusMultiplier;
        public static ConfigEntry<float> HuntingRadiusMultiplier;
        public static ConfigEntry<float> SettlementRadiusMultiplier;
        public static ConfigEntry<float> PatrolRadiusMultiplier;
        public static ConfigEntry<bool> EnableDiagnostics;

        public static BepInEx.Logging.ManualLogSource Logger;

        public override void Load()
        {
            Logger = base.Log;
            WoodRadiusMultiplier = Config.Bind("General", "WoodRadiusMultiplier", 2f, "Multiplier for Woodcutter markers.");
            StoneRadiusMultiplier = Config.Bind("General", "StoneRadiusMultiplier", 2f, "Multiplier for Stonecutter markers.");
            ForagingRadiusMultiplier = Config.Bind("General", "ForagingRadiusMultiplier", 2f, "Multiplier for Foraging markers.");
            ForestryRadiusMultiplier = Config.Bind("General", "ForestryRadiusMultiplier", 2f, "Multiplier for Forestry markers.");
            HuntingRadiusMultiplier = Config.Bind("General", "HuntingRadiusMultiplier", 2f, "Multiplier for Hunting markers.");
            SettlementRadiusMultiplier = Config.Bind("General", "SettlementRadiusMultiplier", 1f, "Multiplier for Settlement markers.");
            PatrolRadiusMultiplier = Config.Bind("General", "PatrolRadiusMultiplier", 1f, "Multiplier for Patrol markers.");
            EnableDiagnostics = Config.Bind("General", "EnableDiagnostics", true, "Enable logging when villagers find resources around markers, and when the map marker range is synced. (Dev default true; flip to false for release.)");

            Harmony.CreateAndPatchAll(typeof(HarvestMarkerPatch));
            Harmony.CreateAndPatchAll(typeof(OutpostStructurePatch));
            Harmony.CreateAndPatchAll(typeof(ResourceManagerPatch));
            Harmony.CreateAndPatchAll(typeof(ObjectiveMarkerContainerPatch));

            Il2CppInterop.Runtime.Injection.ClassInjector.RegisterTypeInIl2Cpp<ResourceMarkerRadiusTracker>();
            var trackerGo = new UnityEngine.GameObject("ResourceMarkerRadiusMod_Tracker");
            UnityEngine.Object.DontDestroyOnLoad(trackerGo);
            trackerGo.AddComponent<ResourceMarkerRadiusTracker>();

            Log.LogInfo($"Plugin resourcemarkerradius v1.1.2 is loaded!");
        }
    }
}
