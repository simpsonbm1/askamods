using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using Il2CppInterop.Runtime.Injection;
using UnityEngine;

namespace TaskUnlockerMod
{
    [BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
    public class Plugin : BasePlugin
    {
        internal static new ManualLogSource Log = null!;
        
        public static ConfigEntry<bool> UnlockCookingRecipes = null!;
        public static ConfigEntry<bool> UnlockFish = null!;
        public static ConfigEntry<bool> DiagnosticsLogItemUnlocks = null!;

        public static SandSailorStudio.Inventory.ItemInfoDatabase ItemDb;

        public override void Load()
        {
            Log = base.Log;

            UnlockCookingRecipes = Config.Bind("General", "UnlockCookingRecipes", true, "Unlock all cooking recipes from the start.");
            UnlockFish = Config.Bind("General", "UnlockFish", true, "Unlock all fish types from the start.");
            DiagnosticsLogItemUnlocks = Config.Bind("Diagnostics", "DiagnosticsLogItemUnlocks", false, "Log every item that gets unlocked. Disable to reduce log spam.");

            // Register our custom MonoBehaviour
            ClassInjector.RegisterTypeInIl2Cpp<TaskUnlockTracker>();

            // Apply Harmony patches
            Harmony.CreateAndPatchAll(typeof(Plugin).Assembly);

            // Create invisible GameObject to hold the tracker
            var go = new GameObject("TaskUnlockTracker");
            GameObject.DontDestroyOnLoad(go);
            go.AddComponent<TaskUnlockTracker>();

            Log.LogInfo($"Plugin {MyPluginInfo.PLUGIN_GUID} is loaded!");
        }
    }

    [HarmonyPatch(typeof(SandSailorStudio.Inventory.ItemInfoDatabase), nameof(SandSailorStudio.Inventory.ItemInfoDatabase.Initialize))]
    public static class ItemInfoDatabasePatch
    {
        public static void Postfix(SandSailorStudio.Inventory.ItemInfoDatabase __instance)
        {
            Plugin.ItemDb = __instance;
        }
    }
}
