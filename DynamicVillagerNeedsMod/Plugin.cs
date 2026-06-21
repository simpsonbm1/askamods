using System.Collections.Generic;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Unity.IL2CPP;
using BepInEx.Logging;
using HarmonyLib;
using Il2CppInterop.Runtime.Injection;
using SSSGame;
using UnityEngine;

namespace DynamicVillagerNeedsMod;

// Replaces ASKA's clock-based villager schedule (Sleep/Work/Leisure hours you assign per villager)
// with needs-based behavior: a villager sleeps when tired, takes leisure when its happiness dips,
// and otherwise works. We drive Villager.scheduleOverride / CurrentBehaviorType every tick so the
// assigned schedule is ignored. Villager-only; the player is never touched.
[BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
public class Plugin : BasePlugin
{
    internal static ManualLogSource Logger = null!;

    internal static ConfigEntry<bool> Enabled = null!;

    // Sleep need — rest is a 0..24 "hours of rest" pool that drains while awake, fills while asleep.
    internal static ConfigEntry<float> SleepWhenRestBelowHours = null!;
    internal static ConfigEntry<float> WakeWhenRestAboveHours = null!;

    // Leisure need — derived from happiness (there is no separate leisure stat). Set the trigger to 0
    // to disable leisure entirely (villagers then only sleep or work).
    internal static ConfigEntry<float> LeisureWhenHappinessBelow = null!;
    internal static ConfigEntry<float> LeisureUntilHappinessAbove = null!;

    internal static ConfigEntry<bool> DebugLogging = null!;

    // Villager survival components, registered as they spawn (pruned in the controller when destroyed).
    internal static readonly List<VillagerSurvival> TrackedSurvivals = new();

    public override void Load()
    {
        Logger = base.Log;

        Enabled = Config.Bind("DynamicNeeds", "Enabled", true,
            "Master switch. When true, villagers ignore their assigned schedule and act on needs instead.");

        SleepWhenRestBelowHours = Config.Bind("DynamicNeeds", "SleepWhenRestBelowHours", 8.0f,
            "Go to sleep when rest (0..24) drops below this many hours. Lower = sleep less often = less total sleep.");

        WakeWhenRestAboveHours = Config.Bind("DynamicNeeds", "WakeWhenRestAboveHours", 23.0f,
            "While sleeping, wake up once rest reaches this many hours (max is 24). Lower = shorter sleeps.");

        LeisureWhenHappinessBelow = Config.Bind("DynamicNeeds", "LeisureWhenHappinessBelow", 0.6f,
            "Take leisure when happiness (0..1) drops below this. This is the self-correcting safety net: " +
            "if working more / sleeping less ever lowers happiness, villagers relax until it recovers. " +
            "Set to 0 to disable leisure (villagers only sleep or work).");

        LeisureUntilHappinessAbove = Config.Bind("DynamicNeeds", "LeisureUntilHappinessAbove", 0.78f,
            "While taking leisure, return to work once happiness recovers above this (0..1). " +
            "Must be >= LeisureWhenHappinessBelow.");

        DebugLogging = Config.Bind("DynamicNeeds", "DebugLogging", true,
            "Log each villager's need-driven mode changes plus a periodic rest/happiness summary. " +
            "Set to false once you're happy with the values.");

        ClassInjector.RegisterTypeInIl2Cpp<NeedsController>();
        var go = new GameObject("DynamicVillagerNeedsMod_Controller");
        Object.DontDestroyOnLoad(go);
        go.AddComponent<NeedsController>();

        var harmony = new Harmony(MyPluginInfo.PLUGIN_GUID);
        harmony.PatchAll();

        Logger.LogInfo($"DynamicVillagerNeedsMod loaded. Enabled={Enabled.Value}, " +
                       $"sleep<{SleepWhenRestBelowHours.Value}h wake>{WakeWhenRestAboveHours.Value}h, " +
                       $"leisure<{LeisureWhenHappinessBelow.Value} until>{LeisureUntilHappinessAbove.Value}");
    }
}
