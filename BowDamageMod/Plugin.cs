using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Unity.IL2CPP;
using BepInEx.Logging;
using HarmonyLib;

namespace BowDamageMod;

[BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
public class Plugin : BasePlugin
{
    internal static ManualLogSource Logger = null!;

    internal static ConfigEntry<float> DamageMultiplier = null!;
    internal static ConfigEntry<string> TargetWeaponNames = null!;

    public override void Load()
    {
        Logger = base.Log;

        DamageMultiplier = Config.Bind(
            section: "BowDamage",
            key: "DamageMultiplier",
            defaultValue: 3.0f,
            description: "Multiplier applied to baseDamage for matching weapons. Default 3x makes the Flimsy Shortbow viable early game.");

        TargetWeaponNames = Config.Bind(
            section: "BowDamage",
            key: "TargetWeaponNames",
            defaultValue: "Flimsy Shortbow,Wood Arrow",
            description: "Comma-separated list of item names to inspect/buff. Case-insensitive contains match.");
        // If the user's config file predates the Wood Arrow entry, patch it in at runtime
        if (!TargetWeaponNames.Value.Contains("Wood Arrow", StringComparison.OrdinalIgnoreCase))
            TargetWeaponNames.Value += ",Wood Arrow";

        var harmony = new Harmony(MyPluginInfo.PLUGIN_GUID);
        harmony.PatchAll();

        Logger.LogInfo($"BowDamageMod loaded. Multiplier={DamageMultiplier.Value}x, Targets=[{TargetWeaponNames.Value}]");
    }

    /// <summary>
    /// Returns true if the given name matches any entry in TargetWeaponNames config.
    /// </summary>
    internal static bool IsTargetWeapon(string? weaponName)
    {
        if (string.IsNullOrEmpty(weaponName)) return false;

        foreach (var entry in TargetWeaponNames.Value.Split(','))
        {
            var trimmed = entry.Trim();
            if (trimmed.Length > 0 && weaponName.Contains(trimmed, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}
