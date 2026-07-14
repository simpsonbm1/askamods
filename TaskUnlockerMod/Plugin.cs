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
        public static ConfigEntry<bool> UnlockResourceJournalEntries = null!;
        public static ConfigEntry<bool> UnlockSeedJournalEntries = null!;
        public static ConfigEntry<bool> UnlockWearableJournalEntries = null!;
        public static ConfigEntry<bool> UnlockWeaponJournalEntries = null!;
        public static ConfigEntry<bool> ResetJournalForDisabledCategories = null!;
        public static ConfigEntry<bool> DiagnosticsLogItemUnlocks = null!;
        public static ConfigEntry<bool> DiagnosticsLogPassTimings = null!;

        public static SandSailorStudio.Inventory.ItemInfoDatabase ItemDb;

        public override void Load()
        {
            Log = base.Log;

            UnlockCookingRecipes = Config.Bind("General", "UnlockCookingRecipes", true, "Unlock all cooking recipes from the start.");
            UnlockFish = Config.Bind("General", "UnlockFish", true, "Unlock all fish types from the start.");
            UnlockResourceJournalEntries = Config.Bind("General", "UnlockResourceJournalEntries", true,
                "Add the journal/discovery entry for every resource, food and consumable item at world load, " +
                "so item-gated tasks (tavern, harbor loading, storage, ...) show up without having to pick each item up first.");
            UnlockSeedJournalEntries = Config.Bind("General", "UnlockSeedJournalEntries", true,
                "Add the journal/discovery entry for every seed and sapling at world load, revealing farming/planting tasks.");
            UnlockWearableJournalEntries = Config.Bind("General", "UnlockWearableJournalEntries", false,
                "Add the journal/discovery entry for every wearable (armor, clothing) at world load. " +
                "WARNING: this reveals crafting tasks at workshops for higher-tier gear whose station attachments aren't built yet, " +
                "which is why it defaults to false.");
            UnlockWeaponJournalEntries = Config.Bind("General", "UnlockWeaponJournalEntries", false,
                "Add the journal/discovery entry for every weapon, tool, ammo and fishing-gear item at world load. " +
                "Same workshop caveat as UnlockWearableJournalEntries — defaults to false.");
            ResetJournalForDisabledCategories = Config.Bind("General", "ResetJournalForDisabledCategories", false,
                "!!! WARNING — THIS ERASES JOURNAL PROGRESS. READ BEFORE ENABLING !!!\n" +
                "While enabled, EVERY world load RE-HIDES the journal entries of ALL items in categories whose Unlock toggle above is false — " +
                "INCLUDING entries you discovered legitimately by picking items up. Their tasks disappear from buildings until you pick the item up again.\n" +
                "This is a one-shot repair tool to undo a category you previously unlocked with this mod: " +
                "enable it, load the world once, then IMMEDIATELY set it back to false.");
            DiagnosticsLogItemUnlocks = Config.Bind("Diagnostics", "DiagnosticsLogItemUnlocks", false, "Log every item that gets unlocked. Disable to reduce log spam.");
            DiagnosticsLogPassTimings = Config.Bind("Diagnostics", "DiagnosticsLogPassTimings", false,
                "Log the duration and work counts of each unlock/marking pass, to verify the mod goes idle after the initial unlock.");

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
