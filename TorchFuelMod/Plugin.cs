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
    internal static ConfigEntry<bool> LogBurnableBuildings = null!;

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

        KeepAllLightSources = Config.Bind(
            section: "TorchFuel",
            key: "KeepAllLightSources",
            defaultValue: false,
            description: "If true, keep EVERY light-emitting fire fueled regardless of name — including fires built into buildings (e.g. the tavern campfire) and braziers that the name list above misses. Catches anything with a light outlet; leaves cooking stations/forges/kilns (no light outlet) alone. Note: cave WALL SCONCES are a separate equipment system and are NOT covered by this.");

        LogAllFireStructures = Config.Bind(
            section: "TorchFuel",
            key: "LogAllFireStructures",
            defaultValue: false,
            description: "Diagnostic: log every fire structure and light outlet as it loads (owner DefaultName/StructureName + GameObject name), so you can find the exact name of a specific fire to add to TargetStructureNames. Leave off for normal play.");

        LogBurnableBuildings = Config.Bind(
            section: "TorchFuel",
            key: "LogBurnableBuildings",
            defaultValue: false,
            description: "Diagnostic: log every smithing/coal building (bloomery kiln, bellows, forge, charcoal station) as it loads — its in-game display name AND which fuel mechanism it uses (FireStructure fuel volume vs. coal-item _fuelVAttr). Use this to find which building you want to keep fueled and what lever applies. Leave off for normal play.");

        ClassInjector.RegisterTypeInIl2Cpp<TorchFuelTracker>();
        var go = new GameObject("TorchFuelMod_Tracker");
        UnityEngine.Object.DontDestroyOnLoad(go);
        go.AddComponent<TorchFuelTracker>();

        var harmony = new Harmony(MyPluginInfo.PLUGIN_GUID);
        harmony.PatchAll();

        Logger.LogInfo($"TorchFuelMod v{MyPluginInfo.PLUGIN_VERSION} loaded. Targets=[{TargetStructureNames.Value}], CheckIntervalSeconds={CheckIntervalSeconds.Value}, LogBurnableBuildings={LogBurnableBuildings.Value}");
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

    // Diagnostic helper: emit one "[burn-diag]" line tying a smithing/coal building's runtime type to its
    // in-game display name (owner Structure) and a per-mechanism detail string. Owner-name reads are each
    // wrapped because some name fields can be unset mid-spawn.
    internal static void LogBurnableBuilding(string typeName, Structure? owner, string detail)
    {
        if (!LogBurnableBuildings.Value) return;

        string? def = null, name = null, getName = null;
        try { def = owner != null ? owner.DefaultName : null; } catch { }
        try { name = owner != null ? owner.StructureName : null; } catch { }
        try { getName = owner != null ? owner.GetName() : null; } catch { }

        Logger.LogInfo($"[TorchFuelMod][burn-diag] {typeName} owner default='{def}' name='{name}' getName='{getName}' | {detail}");
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
