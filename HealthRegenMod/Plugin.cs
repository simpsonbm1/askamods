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
    internal static ConfigEntry<float> HealPerTick = null!;
    internal static ConfigEntry<float> SecondsPerTick = null!;
    internal static ConfigEntry<float> OutOfCombatSeconds = null!;
    internal static ConfigEntry<bool> ApplyToVillagers = null!;
    internal static ConfigEntry<bool> VillagerDebugLogging = null!;

    // Set by PlayerCharacterPatch when the locally-controlled avatar spawns; cleared on despawn.
    internal static PlayerCharacter? LocalPlayer;

    // Villagers currently spawned in the world (added in VillagerPatch's Spawned postfix,
    // removed in Despawned postfix; RegenTracker also prunes destroyed entries).
    internal static readonly System.Collections.Generic.List<Villager> TrackedVillagers = new();

    public override void Load()
    {
        Logger = base.Log;

        HealPerTick = Config.Bind(
            section: "HealthRegen",
            key: "HealPerTick",
            defaultValue: 1.0f,
            description: "HP restored each tick while out of combat (e.g. 3 with SecondsPerTick=5 = 3 HP every 5s).");

        SecondsPerTick = Config.Bind(
            section: "HealthRegen",
            key: "SecondsPerTick",
            defaultValue: 1.0f,
            description: "Seconds between each heal tick. Set to 0 for smooth/continuous regen (HealPerTick treated as HP/sec).");

        OutOfCombatSeconds = Config.Bind(
            section: "HealthRegen",
            key: "OutOfCombatSeconds",
            defaultValue: 10.0f,
            description: "Seconds since last taking damage before regen starts.");

        ApplyToVillagers = Config.Bind(
            section: "VillagerRegen",
            key: "ApplyToVillagers",
            defaultValue: true,
            description: "Villagers (including warriors/soldiers) regenerate HP out of combat using the same HealPerTick/SecondsPerTick/OutOfCombatSeconds settings as the player.");

        VillagerDebugLogging = Config.Bind(
            section: "VillagerRegen",
            key: "DebugLogging",
            defaultValue: true,
            description: "Log villager registration and regen start/stop events.");

        ClassInjector.RegisterTypeInIl2Cpp<RegenTracker>();
        var go = new GameObject("HealthRegenMod_RegenTracker");
        Object.DontDestroyOnLoad(go);
        go.AddComponent<RegenTracker>();

        var harmony = new Harmony(MyPluginInfo.PLUGIN_GUID);
        harmony.PatchAll();

        Logger.LogInfo($"HealthRegenMod loaded. HealPerTick={HealPerTick.Value}, SecondsPerTick={SecondsPerTick.Value}, OutOfCombatSeconds={OutOfCombatSeconds.Value}, ApplyToVillagers={ApplyToVillagers.Value}");
    }
}
