using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Unity.IL2CPP;
using BepInEx.Logging;
using HarmonyLib;
using Il2CppInterop.Runtime.Injection;
using SSSGame;
using UnityEngine;

namespace MineRefreshMod;

[BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
public class Plugin : BasePlugin
{
    internal static ManualLogSource Logger = null!;
    internal static ConfigEntry<string> TriggerHotkey = null!;
    internal static ConfigEntry<float> SafetyRadius = null!;
    internal static ConfigEntry<bool> TriggerOnlyNearEntrance = null!;
    internal static ConfigEntry<float> MaxEntranceDistance = null!;
    internal static ConfigEntry<bool> RespawnItems = null!;

    internal static readonly System.Collections.Generic.List<Character> ActiveCharacters = new();
    internal static CavesManager? GameCavesManager;
    internal static PlayerCharacter? LocalPlayer;

    public override void Load()
    {
        Logger = base.Log;

        TriggerHotkey = Config.Bind(
            section: "General",
            key: "TriggerHotkey",
            defaultValue: "u",
            description: "The hotkey (lowercase letter or Unity KeyCode name) pressed to trigger the mine refresh. Pressed by the host player while standing near a mine entrance.");

        SafetyRadius = Config.Bind(
            section: "General",
            key: "SafetyRadius",
            defaultValue: 25.0f,
            description: "Safety proximity scan radius (in meters). If any player or worker is within this distance of any hallway/node in the mine, the refresh will be blocked to prevent trapping them.");

        TriggerOnlyNearEntrance = Config.Bind(
            section: "General",
            key: "TriggerOnlyNearEntrance",
            defaultValue: true,
            description: "If true, the player must be standing near a mine entrance to trigger the refresh.");

        MaxEntranceDistance = Config.Bind(
            section: "General",
            key: "MaxEntranceDistance",
            defaultValue: 20.0f,
            description: "Maximum distance (in meters) from a mine entrance the player can be to trigger the refresh. Only applies if TriggerOnlyNearEntrance is true.");

        RespawnItems = Config.Bind(
            section: "General",
            key: "RespawnItems",
            defaultValue: true,
            description: "If true, also run the mine's native item spawners to regenerate loose resources (iron deposits, mushrooms, chests) inside the mine.");

        // Register our custom Mono class into IL2CPP
        ClassInjector.RegisterTypeInIl2Cpp<MineRefreshTracker>();
        
        // Spawn our persistent manager GameObject
        var go = new GameObject("MineRefreshMod_Tracker");
        UnityEngine.Object.DontDestroyOnLoad(go);
        go.AddComponent<MineRefreshTracker>();

        // Apply Harmony patches
        var harmony = new Harmony(MyPluginInfo.PLUGIN_GUID);
        harmony.PatchAll();

        Logger.LogInfo($"MineRefreshMod v{MyPluginInfo.PLUGIN_VERSION} loaded successfully. Hotkey='{TriggerHotkey.Value}', SafetyRadius={SafetyRadius.Value}m, TriggerOnlyNearEntrance={TriggerOnlyNearEntrance.Value}");
    }
}
