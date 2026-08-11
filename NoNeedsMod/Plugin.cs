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

    internal static ConfigEntry<float> PlayerFoodDrain = null!;
    internal static ConfigEntry<float> PlayerFoodGain = null!;
    internal static ConfigEntry<float> PlayerWaterDrain = null!;
    internal static ConfigEntry<float> PlayerWaterGain = null!;
    internal static ConfigEntry<float> PlayerWarmthDrain = null!;
    internal static ConfigEntry<float> PlayerWarmthGain = null!;
    internal static ConfigEntry<float> PlayerEnergyDrain = null!;
    internal static ConfigEntry<float> PlayerEnergyGain = null!;

    internal static ConfigEntry<bool> VillagersEnabled = null!;
    internal static ConfigEntry<bool> VillagersFood = null!;
    internal static ConfigEntry<bool> VillagersWater = null!;
    internal static ConfigEntry<bool> VillagersWarmth = null!;
    internal static ConfigEntry<bool> VillagersRest = null!;
    internal static ConfigEntry<bool> VillagersHappiness = null!;

    internal static ConfigEntry<float> VillagersFoodDrain = null!;
    internal static ConfigEntry<float> VillagersFoodGain = null!;
    internal static ConfigEntry<float> VillagersWaterDrain = null!;
    internal static ConfigEntry<float> VillagersWaterGain = null!;
    internal static ConfigEntry<float> VillagersWarmthDrain = null!;
    internal static ConfigEntry<float> VillagersWarmthGain = null!;
    internal static ConfigEntry<float> VillagersRestDrain = null!;
    internal static ConfigEntry<float> VillagersRestGain = null!;
    internal static ConfigEntry<float> VillagersHappinessDrain = null!;
    internal static ConfigEntry<float> VillagersHappinessGain = null!;

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

        PlayerFoodDrain = BindDrain("Player", "FoodDrainRate", "food");
        PlayerFoodGain = BindGain("Player", "FoodGainRate", "food", "eating");
        PlayerWaterDrain = BindDrain("Player", "WaterDrainRate", "water");
        PlayerWaterGain = BindGain("Player", "WaterGainRate", "water", "drinking");
        PlayerWarmthDrain = BindDrain("Player", "WarmthDrainRate", "warmth");
        PlayerWarmthGain = BindGain("Player", "WarmthGainRate", "warmth", "standing at a fire");
        PlayerEnergyDrain = BindDrain("Player", "EnergyDrainRate", "stamina");
        PlayerEnergyGain = BindGain("Player", "EnergyGainRate", "stamina", "resting after exertion");

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

        VillagersFoodDrain = BindDrain("Villagers", "FoodDrainRate", "food");
        VillagersFoodGain = BindGain("Villagers", "FoodGainRate", "food", "eating");
        VillagersWaterDrain = BindDrain("Villagers", "WaterDrainRate", "water");
        VillagersWaterGain = BindGain("Villagers", "WaterGainRate", "water", "drinking");
        VillagersWarmthDrain = BindDrain("Villagers", "WarmthDrainRate", "warmth");
        VillagersWarmthGain = BindGain("Villagers", "WarmthGainRate", "warmth", "standing at a fire");
        VillagersRestDrain = BindDrain("Villagers", "RestDrainRate", "rest");
        VillagersRestGain = BindGain("Villagers", "RestGainRate", "rest", "sleeping");
        VillagersHappinessDrain = BindDrain("Villagers", "HappinessDrainRate", "happiness");
        VillagersHappinessGain = BindGain("Villagers", "HappinessGainRate", "happiness", "the game's own happiness sources");

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

        Logger.LogInfo(
            $"NoNeedsMod loaded. Player: Enabled={PlayerEnabled.Value}, "
            + $"{Describe("Food", PlayerFood, PlayerFoodDrain, PlayerFoodGain)}, "
            + $"{Describe("Water", PlayerWater, PlayerWaterDrain, PlayerWaterGain)}, "
            + $"{Describe("Warmth", PlayerWarmth, PlayerWarmthDrain, PlayerWarmthGain)}, "
            + $"{Describe("Energy", PlayerEnergy, PlayerEnergyDrain, PlayerEnergyGain)}. "
            + $"Villagers: Enabled={VillagersEnabled.Value}, "
            + $"{Describe("Food", VillagersFood, VillagersFoodDrain, VillagersFoodGain)}, "
            + $"{Describe("Water", VillagersWater, VillagersWaterDrain, VillagersWaterGain)}, "
            + $"{Describe("Warmth", VillagersWarmth, VillagersWarmthDrain, VillagersWarmthGain)}, "
            + $"{Describe("Rest", VillagersRest, VillagersRestDrain, VillagersRestGain)}, "
            + $"{Describe("Happiness", VillagersHappiness, VillagersHappinessDrain, VillagersHappinessGain)}. "
            + $"TickSeconds={TickSeconds.Value}, DebugLogging={DebugLogging.Value}.");
    }

    // One need's on/off state and both of its rate multipliers, e.g. "Food=True/5x drain/5x gain".
    private static string Describe(string need, ConfigEntry<bool> on, ConfigEntry<float> drain, ConfigEntry<float> gain)
    {
        return $"{need}={on.Value}/{drain.Value}x drain/{gain.Value}x gain";
    }

    // How fast this need falls, as a multiple of the game's own rate. 0 keeps the original
    // behaviour: the need is held at maximum and never falls at all.
    private ConfigEntry<float> BindDrain(string section, string key, string need)
    {
        return Config.Bind(
            section: section,
            key: key,
            defaultValue: 0.0f,
            configDescription: new ConfigDescription(
                $"How fast {need} drains, as a multiple of the game's own rate. 0 = held at maximum (never drains). 1 = vanilla speed. 0.5 = half speed. 2 = twice as fast. Ignored unless the matching on/off setting above is true.",
                new AcceptableValueRange<float>(0f, 10f)));
    }

    // How fast this need refills from the game's own sources, as a multiple of the vanilla amount.
    // Only consulted when the matching drain rate is above 0.
    private ConfigEntry<float> BindGain(string section, string key, string need, string example)
    {
        return Config.Bind(
            section: section,
            key: key,
            defaultValue: 1.0f,
            configDescription: new ConfigDescription(
                $"How much {need} is restored by the game's own sources ({example}), as a multiple of the vanilla amount. 1 = vanilla. 2 = double. 0 = nothing restores it. Has no effect while the matching drain rate is 0, because the need is already held at maximum.",
                new AcceptableValueRange<float>(0f, 10f)));
    }
}
