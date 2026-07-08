using System.Linq;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Unity.IL2CPP;
using BepInEx.Logging;
using HarmonyLib;

namespace FishFilletMod;

[BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
public class Plugin : BasePlugin
{
    internal static ManualLogSource Logger = null!;
    internal static ConfigEntry<bool> EnableFillet = null!;
    internal static ConfigEntry<string> ToolCategoriesRaw = null!;
    internal static string[] ToolCategories = new[] { "Knives" };

    public override void Load()
    {
        Logger = base.Log;

        EnableFillet = Config.Bind(
            "Fillet", "EnableFilletInInventory", true,
            "Allow filleting fish (and other knife-harvestable food) directly in the inventory using the game's own harvest, instead of dropping + skinning. Consumes the fish and grants its normal fillet yield (meat/blubber).");

        ToolCategoriesRaw = Config.Bind(
            "Fillet", "HarvestToolCategories", "Knives",
            "Comma-separated, case-insensitive item-category names whose harvest tool requirement is unlocked in inventory. Default 'Knives' targets fish/food filleting. Only items that are ALREADY harvestable (CanBeHarvested) AND require one of these tool categories get unlocked; bare-hand items are unaffected.");

        ToolCategories = ToolCategoriesRaw.Value
            .Split(',')
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .ToArray();

        var harmony = new Harmony(MyPluginInfo.PLUGIN_GUID);
        harmony.PatchAll();

        Logger.LogInfo($"FishFilletMod v{MyPluginInfo.PLUGIN_VERSION} loaded (Shift+Right-click filleting). EnableFillet={EnableFillet.Value}");
    }
}
