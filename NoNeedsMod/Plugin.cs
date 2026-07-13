using BepInEx;
using BepInEx.Configuration;
using BepInEx.Unity.IL2CPP;
using BepInEx.Logging;
using HarmonyLib;
using Il2CppInterop.Runtime.Injection;
using SSSGame;
using UnityEngine;

namespace NoNeedsMod;

[BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
public class Plugin : BasePlugin
{
    internal static ManualLogSource Logger = null!;

    internal static ConfigEntry<bool> PlayerEnabled = null!;
    internal static ConfigEntry<bool> PlayerFood = null!;
    internal static ConfigEntry<bool> PlayerWater = null!;
    internal static ConfigEntry<bool> PlayerWarmth = null!;
    internal static ConfigEntry<bool> PlayerEnergy = null!;

    internal static ConfigEntry<bool> VillagersEnabled = null!;
    internal static ConfigEntry<bool> VillagersFood = null!;
    internal static ConfigEntry<bool> VillagersWater = null!;
    internal static ConfigEntry<bool> VillagersWarmth = null!;
    internal static ConfigEntry<bool> VillagersRest = null!;
    internal static ConfigEntry<bool> VillagersHappiness = null!;

    internal static ConfigEntry<float> TickSeconds = null!;
    internal static ConfigEntry<bool> DebugLogging = null!;

    // Set by PlayerCharacterPatch when the locally-controlled avatar spawns; cleared on despawn.
    internal static PlayerCharacter? LocalPlayer;

    // Villagers currently spawned in the world (added in VillagerPatch's Spawned postfix,
    // removed in Despawned postfix; NeedsTracker also prunes destroyed entries).
    internal static readonly System.Collections.Generic.List<Villager> TrackedVillagers = new();

    public override void Load()
    {
        Logger = base.Log;

        PlayerEnabled = Config.Bind(
            section: "Player",
            key: "Enabled",
            defaultValue: true,
            description: "Keep the player's needs pinned at max.");

        PlayerFood = Config.Bind(
            section: "Player",
            key: "Food",
            defaultValue: true,
            description: "Pin the player's food need at max.");

        PlayerWater = Config.Bind(
            section: "Player",
            key: "Water",
            defaultValue: true,
            description: "Pin the player's water need at max.");

        PlayerWarmth = Config.Bind(
            section: "Player",
            key: "Warmth",
            defaultValue: true,
            description: "Pin the player's warmth need at max.");

        PlayerEnergy = Config.Bind(
            section: "Player",
            key: "Energy",
            defaultValue: false,
            description: "Pin the player's stamina meter at max. Off by default; turn on for full god mode (stamina drains briefly during sprinting/combat, then re-pins every tick).");

        VillagersEnabled = Config.Bind(
            section: "Villagers",
            key: "Enabled",
            defaultValue: true,
            description: "Keep all villagers' needs pinned at max.");

        VillagersFood = Config.Bind(
            section: "Villagers",
            key: "Food",
            defaultValue: true,
            description: "Pin villagers' food need at max.");

        VillagersWater = Config.Bind(
            section: "Villagers",
            key: "Water",
            defaultValue: true,
            description: "Pin villagers' water need at max.");

        VillagersWarmth = Config.Bind(
            section: "Villagers",
            key: "Warmth",
            defaultValue: true,
            description: "Pin villagers' warmth need at max.");

        VillagersRest = Config.Bind(
            section: "Villagers",
            key: "Rest",
            defaultValue: true,
            description: "Pin rest at 24h — villagers never get tired. The game still forces sleep at nightfall; that's vanilla.");

        VillagersHappiness = Config.Bind(
            section: "Villagers",
            key: "Happiness",
            defaultValue: true,
            description: "Pin happiness — the game re-clamps it to each villager's HappinessCap (housing-based), so a plateau below 100% is expected.");

        TickSeconds = Config.Bind(
            section: "General",
            key: "TickSeconds",
            defaultValue: 2.0f,
            description: "Seconds between pin passes.");

        DebugLogging = Config.Bind(
            section: "General",
            key: "DebugLogging",
            defaultValue: false,
            description: "Log fire-verification markers and periodic summaries.");

        ClassInjector.RegisterTypeInIl2Cpp<NeedsTracker>();
        var go = new GameObject("NoNeedsMod_NeedsTracker");
        Object.DontDestroyOnLoad(go);
        go.AddComponent<NeedsTracker>();

        var harmony = new Harmony(MyPluginInfo.PLUGIN_GUID);
        harmony.PatchAll();

        Logger.LogInfo($"NoNeedsMod loaded. Player: Enabled={PlayerEnabled.Value}, Food={PlayerFood.Value}, Water={PlayerWater.Value}, Warmth={PlayerWarmth.Value}, Energy={PlayerEnergy.Value}. Villagers: Enabled={VillagersEnabled.Value}, Food={VillagersFood.Value}, Water={VillagersWater.Value}, Warmth={VillagersWarmth.Value}, Rest={VillagersRest.Value}, Happiness={VillagersHappiness.Value}. TickSeconds={TickSeconds.Value}, DebugLogging={DebugLogging.Value}.");
    }
}
