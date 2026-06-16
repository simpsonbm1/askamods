using BepInEx;
using BepInEx.Configuration;
using BepInEx.Unity.IL2CPP;
using BepInEx.Logging;
using HarmonyLib;
using Il2CppInterop.Runtime.Injection;
using SSSGame;
using UnityEngine;

namespace HealthRegenMod;

[BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
public class Plugin : BasePlugin
{
    internal static ManualLogSource Logger = null!;
    internal static ConfigEntry<float> RegenPerSecond = null!;
    internal static ConfigEntry<float> OutOfCombatSeconds = null!;

    // Set by PlayerCharacterPatch when the locally-controlled avatar spawns; cleared on despawn.
    internal static PlayerCharacter? LocalPlayer;

    public override void Load()
    {
        Logger = base.Log;

        RegenPerSecond = Config.Bind(
            section: "HealthRegen",
            key: "RegenPerSecond",
            defaultValue: 1.0f,
            description: "HP restored per second while out of combat.");

        OutOfCombatSeconds = Config.Bind(
            section: "HealthRegen",
            key: "OutOfCombatSeconds",
            defaultValue: 10.0f,
            description: "Seconds since last taking damage before regen starts.");

        ClassInjector.RegisterTypeInIl2Cpp<RegenTracker>();
        var go = new GameObject("HealthRegenMod_RegenTracker");
        Object.DontDestroyOnLoad(go);
        go.AddComponent<RegenTracker>();

        var harmony = new Harmony(MyPluginInfo.PLUGIN_GUID);
        harmony.PatchAll();

        Logger.LogInfo($"HealthRegenMod loaded. RegenPerSecond={RegenPerSecond.Value}, OutOfCombatSeconds={OutOfCombatSeconds.Value}");
    }
}
