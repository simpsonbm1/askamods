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

    // Populated by FireStructurePatch as matching structures (torches) spawn/load in.
    internal static readonly List<FireStructure> TrackedFireStructures = new();

    public override void Load()
    {
        Logger = base.Log;

        TargetStructureNames = Config.Bind(
            section: "TorchFuel",
            key: "TargetStructureNames",
            defaultValue: "Torch",
            description: "Comma-separated list of structure names to keep perpetually fueled. Case-insensitive contains match (e.g. matches 'Standing Torch', 'Wall Torch').");

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

        ClassInjector.RegisterTypeInIl2Cpp<TorchFuelTracker>();
        var go = new GameObject("TorchFuelMod_Tracker");
        UnityEngine.Object.DontDestroyOnLoad(go);
        go.AddComponent<TorchFuelTracker>();

        var harmony = new Harmony(MyPluginInfo.PLUGIN_GUID);
        harmony.PatchAll();

        Logger.LogInfo($"TorchFuelMod loaded. Targets=[{TargetStructureNames.Value}], CheckIntervalSeconds={CheckIntervalSeconds.Value}");
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
