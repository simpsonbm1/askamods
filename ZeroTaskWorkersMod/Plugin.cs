using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using Il2CppInterop.Runtime.Injection;
using UnityEngine;

namespace ZeroTaskWorkersMod
{
    [BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
    public class Plugin : BasePlugin
    {
        internal static new ManualLogSource Log = null!;

        public static ConfigEntry<bool> LogTaskEvents = null!;
        public static ConfigEntry<string> BuildingNameList = null!;
        public static ConfigEntry<bool> ApplyToAllBuildings = null!;
        public static ConfigEntry<float> LoadGraceSeconds = null!;

        public override void Load()
        {
            Log = base.Log;

            LogTaskEvents = Config.Bind("Diagnostics", "LogTaskEvents", false, "Log all workstation task-data events (hire, canadd-gate, remove, deserialize). Very chatty — enable only for troubleshooting.");

            BuildingNameList = Config.Bind("General", "BuildingNameList", "", "Only used when ApplyToAllBuildings is false: comma-separated, case-insensitive substrings matched against a structure's display name; a new worker assigned to a matching building inherits ZERO tasks.");
            ApplyToAllBuildings = Config.Bind("General", "ApplyToAllBuildings", true, "Blocks task inheritance on ALL buildings (build sites / boat yards always exempt, so unassigned villagers keep building). Set false to limit the effect to the buildings named in BuildingNameList.");
            LoadGraceSeconds = Config.Bind("General", "LoadGraceSeconds", 10f, "Suppress blocking for this many seconds after any task-data deserialize (protects saved assignments at world load / structure upgrade).");

            // Register our custom MonoBehaviour
            ClassInjector.RegisterTypeInIl2Cpp<ZeroTaskTracker>();

            // Apply Harmony patches
            Harmony.CreateAndPatchAll(typeof(Plugin).Assembly);

            // Create invisible GameObject to hold the tracker
            var go = new GameObject("ZeroTaskTracker");
            GameObject.DontDestroyOnLoad(go);
            go.AddComponent<ZeroTaskTracker>();

            Log.LogInfo($"Plugin {MyPluginInfo.PLUGIN_GUID} v{MyPluginInfo.PLUGIN_VERSION} loaded");
        }
    }
}
