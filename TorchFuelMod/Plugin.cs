using System;
using System.Collections.Generic;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Unity.IL2CPP;
using BepInEx.Logging;
using HarmonyLib;
using Il2CppInterop.Runtime.Injection;
using SSSGame;
using UnityEngine;

namespace TorchFuelMod;

[BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
public class Plugin : BasePlugin
{
    internal static ManualLogSource Logger = null!;
    internal static ConfigEntry<string> TargetStructureNames = null!;
    internal static ConfigEntry<float> CheckIntervalSeconds = null!;
    internal static ConfigEntry<bool> PreventRainExtinguish = null!;
    internal static ConfigEntry<bool> AutoRelightAfterRain = null!;
    internal static ConfigEntry<bool> KeepAllLightSources = null!;
    internal static ConfigEntry<bool> LogAllFireStructures = null!;

    // Populated by FireStructurePatch as matching structures (torches) spawn/load in.
    internal static readonly List<FireStructure> TrackedFireStructures = new();

    public override void Load()
    {
        Logger = base.Log;

        TargetStructureNames = Config.Bind(
            section: "TorchFuel",
            key: "TargetStructureNames",
            defaultValue: "Torch",
            description: "Comma-separated list of structure names to keep perpetually fueled. Case-insensitive substring match (e.g. 'Torch' matches 'Flimsy Torch', 'Standing Torch'). Known fire buildings you can add (confirmed in-game): smelter='Bloomery' (also matches 'Improved Bloomery'); forge='Metalworker' (also 'Improved Metalworker'); charcoal='Coal Maker'; plus base fires 'Campfire', 'Small Fireplace', 'Cooking Hut'. (There is NO 'Furnace' in this game.)");

        CheckIntervalSeconds = Config.Bind(
            section: "TorchFuel",
            key: "CheckIntervalSeconds",
            defaultValue: 5.0f,
            description: "How often (in real seconds) to top off fuel on matching structures.");

        PreventRainExtinguish = Config.Bind(
            section: "TorchFuel",
            key: "PreventRainExtinguish",
            defaultValue: false,
            description: "If true, matched torches ignore weather entirely — rain and wind cannot extinguish them.");

        AutoRelightAfterRain = Config.Bind(
            section: "TorchFuel",
            key: "AutoRelightAfterRain",
            defaultValue: false,
            description: "If true, any matched torch extinguished during rain is automatically re-lit when the rain stops. Has no effect if PreventRainExtinguish is also true.");

        KeepAllLightSources = Config.Bind(
            section: "TorchFuel",
            key: "KeepAllLightSources",
            defaultValue: false,
            description: "If true, keep EVERY light-emitting fire fueled regardless of name — including fires built into buildings (e.g. the tavern campfire) and braziers that the name list above misses. Catches anything with a light outlet; leaves cooking stations/forges/kilns (no light outlet) alone. Note: cave WALL SCONCES are a separate equipment system and are NOT covered by this.");

        LogAllFireStructures = Config.Bind(
            section: "TorchFuel",
            key: "LogAllFireStructures",
            defaultValue: false,
            description: "Diagnostic: log every fire structure and light outlet as it loads (owner DefaultName/StructureName + GameObject name), so you can find the exact name of a specific fire to add to TargetStructureNames. Leave off for normal play. NOTE: forge/bellows(bloomery)/charcoal-station fires all carry a FireStructure, so this flag also reveals those smithing/coal buildings by their in-game name.");

        ClassInjector.RegisterTypeInIl2Cpp<TorchFuelTracker>();
        var go = new GameObject("TorchFuelMod_Tracker");
        UnityEngine.Object.DontDestroyOnLoad(go);
        go.AddComponent<TorchFuelTracker>();

        var harmony = new Harmony(MyPluginInfo.PLUGIN_GUID);
        harmony.PatchAll();

        Logger.LogInfo($"TorchFuelMod v{MyPluginInfo.PLUGIN_VERSION} loaded. Targets=[{TargetStructureNames.Value}], CheckIntervalSeconds={CheckIntervalSeconds.Value}, LogAllFireStructures={LogAllFireStructures.Value}");
    }

    // Single entry point for both patches (name-matched torches and light-outlet fires) so
    // dedupe + logging live in one place. No-op for nulls/dupes.
    internal static void TrackFireStructure(FireStructure? fire, string label)
    {
        if (fire == null) return;
        if (TrackedFireStructures.Contains(fire)) return;
        TrackedFireStructures.Add(fire);
        Logger.LogInfo($"[TorchFuelMod] Tracking {label} for infinite fuel.");
    }

    internal static bool IsTargetStructure(string? structureName)
    {
        if (string.IsNullOrEmpty(structureName)) return false;

        foreach (var entry in TargetStructureNames.Value.Split(','))
        {
            var trimmed = entry.Trim();
            if (trimmed.Length > 0 && structureName.Contains(trimmed, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}
