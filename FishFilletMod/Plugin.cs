using System.Linq;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Unity.IL2CPP;
using BepInEx.Logging;
using HarmonyLib;
using Il2CppInterop.Runtime.Injection;
using SSSGame.UI;
using UnityEngine;

namespace FishFilletMod;

[BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
public class Plugin : BasePlugin
{
    internal static ManualLogSource Logger = null!;
    internal static ConfigFile? Cfg;
    internal static ConfigEntry<bool> EnableFillet = null!;
    internal static ConfigEntry<string> ToolCategoriesRaw = null!;
    internal static ConfigEntry<string> ModifierKeyRaw = null!;
    internal static ConfigEntry<string> FilletKeyRaw = null!;
    internal static string[] ToolCategories = new[] { "Knives" };

    public override void Load()
    {
        Logger = base.Log;
        Cfg = Config;

        EnableFillet = Config.Bind(
            "Fillet", "EnableFilletInInventory", true,
            "Allow filleting fish (and other knife-harvestable food) directly in the inventory using the game's own harvest, instead of dropping + skinning. Consumes the fish and grants its normal fillet yield (meat/blubber).");

        ToolCategoriesRaw = Config.Bind(
            "Fillet", "HarvestToolCategories", "Knives",
            "Comma-separated, case-insensitive item-category names whose harvest tool requirement is unlocked in inventory. Default 'Knives' targets fish/food filleting. Only items that are ALREADY harvestable (CanBeHarvested) AND require one of these tool categories get unlocked; bare-hand items are unaffected.");

        ModifierKeyRaw = Config.Bind(
            "Fillet", "FilletModifierKey", "Shift",
            "Modifier key held while triggering a fillet. 'Shift', 'Ctrl' or 'Alt' accept either the left or right key; 'None' needs no modifier; any other value is a Unity KeyCode name (e.g. LeftAlt, CapsLock). Change this if another mod claims the same gesture.");

        FilletKeyRaw = Config.Bind(
            "Fillet", "FilletKey", "Mouse1",
            "Key that fillets the item under the mouse. ANY Unity KeyCode name works: mouse buttons (Mouse0 = left, Mouse1 = right, Mouse2 = middle, Mouse3/Mouse4 = side buttons) or keyboard keys (F, X, Delete...). 'Left'/'Right'/'Middle' are accepted as aliases for Mouse0/1/2. Mouse0/1/2 fire on click and suppress the game's own click action; every other key fires on press while hovering the item, leaving clicks untouched. Note that Mouse0 with the Shift modifier re-enters the game's own inventory-harvest gesture and brings back its cosmetic 'can't be harvested' toast.");

        RefreshToolCategories();

        var harmony = new Harmony(MyPluginInfo.PLUGIN_GUID);
        harmony.PatchAll();
        ArmHoverTracking(harmony);

        ClassInjector.RegisterTypeInIl2Cpp<FilletTracker>();
        var go = new GameObject("FishFilletMod_Tracker");
        UnityEngine.Object.DontDestroyOnLoad(go);
        go.AddComponent<FilletTracker>();

        Logger.LogInfo($"FishFilletMod v{MyPluginInfo.PLUGIN_VERSION} loaded ({Binding.Describe()} filleting). EnableFillet={EnableFillet.Value}");
    }

    internal static void RefreshToolCategories()
    {
        ToolCategories = (ToolCategoriesRaw.Value ?? "")
            .Split(',')
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .ToArray();
    }

    // Resolved by name at runtime so a game build without this handler simply leaves the hover path
    // disarmed (mouse0/1/2 bindings keep working) instead of failing the load. `_OnHighlighted` is
    // the ItemHighlightBehaviour.OnHighlighted subscriber — see the Hover class for the chain.
    private static void ArmHoverTracking(Harmony harmony)
    {
        try
        {
            var highlighted = AccessTools.Method(typeof(ItemThumbnailPanel), "_OnHighlighted", new[] { typeof(bool) });
            if (highlighted == null)
            {
                Logger.LogWarning("[FishFillet] Hover tracking unavailable (ItemThumbnailPanel._OnHighlighted not found) — only Mouse0/Mouse1/Mouse2 bindings will work.");
                return;
            }
            harmony.Patch(highlighted, postfix: new HarmonyMethod(typeof(Hover), nameof(Hover.HighlightPostfix)));
            Hover.Armed = true;
            Logger.LogInfo("[FishFillet] Hover tracking armed on ItemThumbnailPanel._OnHighlighted (keyboard / side-button bindings available).");
        }
        catch (System.Exception e)
        {
            Logger.LogWarning($"[FishFillet] Hover tracking failed to arm: {e.Message} — only Mouse0/Mouse1/Mouse2 bindings will work.");
        }
    }
}
