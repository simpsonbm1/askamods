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
// with needs-based behavior: a villager sleeps when tired, takes leisure to address a basic need
// (hunger/thirst/cold) or to recover low happiness, and otherwise works. We rewrite the villager's
// real per-hour schedule (Rpc_ChangeSchedule) to the needs-chosen activity so the native task pipeline
// runs. Leisure can also actively top happiness up (so it's worth taking). Villager-only; the player
// is never touched.
[BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
public class Plugin : BasePlugin
{
    internal static ManualLogSource Logger = null!;

    internal static ConfigEntry<bool> Enabled = null!;

    // Sleep need — rest is a 0..24 "hours of rest" pool that drains while awake, fills while asleep.
    internal static ConfigEntry<float> SleepWhenRestBelowHours = null!;
    internal static ConfigEntry<float> WakeWhenRestAboveHours = null!;
    // While asleep, optionally speed rest recovery so each sleep is shorter (= less total sleep). Lowering
    // the wake threshold alone doesn't reduce total sleep; the sleep fraction is set by gain vs drain rate.
    internal static ConfigEntry<float> SleepHoursToFullRest = null!;

    // Leisure (basic-need safety net) — triggered by low food/water only. Cold is left to the game's own
    // warm-up behavior (forcing leisure for warmth made villagers thrash fire<->work). 0 disables it.
    internal static ConfigEntry<float> LeisureWhenNeedBelow = null!;
    internal static ConfigEntry<float> LeisureUntilNeedAbove = null!;

    // Food/water re-check — vanilla sends a hungry villager to eat ONCE; if the storage is empty when they
    // arrive (FSM_SurvivalConsume -> ERROR_NO_ITEM) they park there and don't notice food restocked later.
    // We periodically re-arm the game's own survival-replenish quests (SurvivalObjectiveQuest.SetDirty) so
    // they fetch and consume the real food once it's available, then resume. 0 disables the re-check.
    internal static ConfigEntry<float> FoodRecheckIntervalSeconds = null!;
    internal static ConfigEntry<float> FoodRecheckWhenNeedBelow = null!;

    // Needs drain rate — scale how fast villagers get hungry/thirsty. 1 = vanilla. A general control, and
    // raising it (e.g. 5-10x) is the quick way to test the food/water re-check above.
    internal static ConfigEntry<float> HungerRateMultiplier = null!;
    internal static ConfigEntry<float> ThirstRateMultiplier = null!;

    // Fire warm-up speed — cold itself is handled by the game, but this scales how fast villagers warm up
    // once they're at a heat source, so warm-up trips are shorter and they get back to work sooner.
    internal static ConfigEntry<float> FireWarmthMultiplier = null!;

    // Leisure (happiness management) — take leisure when happiness dips, return when it recovers. To keep
    // this from trapping a villager (the old failure mode), leisure can actively pump happiness up, and we
    // also bail out of leisure if happiness plateaus (e.g. capped below the exit threshold by housing).
    internal static ConfigEntry<float> LeisureWhenHappinessBelow = null!;
    internal static ConfigEntry<float> LeisureUntilHappinessAbove = null!;
    // While in leisure, add happiness so an empty->full restore would take this many in-game hours
    // (on top of the game's own leisure rate). 0 = don't boost (use vanilla rate only).
    internal static ConfigEntry<float> LeisureHoursToFullHappiness = null!;

    internal static ConfigEntry<bool> DebugLogging = null!;

    // Read-only groundwork for the upcoming RespectManualSchedule (manual-schedule / coverage-staggering)
    // mode. Logs schedule snapshots, an in-game hour calibration line, same-workstation cohort sleep-hour
    // overlaps, and player schedule edits — but makes NO control decisions and writes nothing extra.
    internal static ConfigEntry<bool> ManualScheduleDiagnostics = null!;
    internal static ConfigEntry<bool> RespectManualSchedule = null!;
    internal static ConfigEntry<float> CriticalNeedOffPostBelow = null!;

    // Villager survival components, registered as they spawn (pruned in the controller when destroyed).
    internal static readonly List<VillagerSurvival> TrackedSurvivals = new();

    public override void Load()
    {
        Logger = base.Log;

        Enabled = Config.Bind("DynamicNeeds", "Enabled", true,
            "Master switch. When true, villagers ignore their assigned schedule and act on needs instead.");

        SleepWhenRestBelowHours = Config.Bind("DynamicNeeds", "SleepWhenRestBelowHours", 8.0f,
            "Go to sleep when rest (0..24) drops below this many hours (lower = only nap when genuinely tired). " +
            "The mod also adopts any sleep the game forces at nightfall and shortens it, so this value no longer " +
            "has to beat the game's bedtime. To make villagers sleep LESS overall, lower SleepHoursToFullRest " +
            "(faster recovery) — not this value.");

        WakeWhenRestAboveHours = Config.Bind("DynamicNeeds", "WakeWhenRestAboveHours", 23.0f,
            "While sleeping, wake up once rest reaches this many hours (max is 24).");

        SleepHoursToFullRest = Config.Bind("DynamicNeeds", "SleepHoursToFullRest", 3.0f,
            "While asleep, actively add rest so a full empty->full (0->24) recovery takes about this many " +
            "in-game hours (on top of the game's own rate). This is how you make villagers sleep LESS overall " +
            "— lower = faster recovery = shorter sleeps. Set to 0 to use only the game's (slow) rest rate.");

        LeisureWhenNeedBelow = Config.Bind("DynamicNeeds", "LeisureWhenNeedBelow", 0.15f,
            "Take leisure to address a basic need when the lowest of food/water/warmth (each 0..1) drops " +
            "below this, or when the villager is already starving/dehydrated/freezing. This is just a " +
            "safety net — villagers handle most needs on their own while working. Set to 0 to disable it.");

        LeisureUntilNeedAbove = Config.Bind("DynamicNeeds", "LeisureUntilNeedAbove", 0.5f,
            "While taking leisure for a basic need, return to work once food and water recover above this " +
            "(0..1). Must be >= LeisureWhenNeedBelow.");

        FoodRecheckIntervalSeconds = Config.Bind("DynamicNeeds", "FoodRecheckIntervalSeconds", 15.0f,
            "How often (real seconds) to nudge a hungry/thirsty villager's survival quests to re-check for " +
            "food/water. Vanilla only sends a villager to eat ONCE; if the food storage is empty when they " +
            "arrive they can get stuck standing there and won't notice food you restock later. This re-arms " +
            "the game's own eat/drink path so they fetch and consume the real food once it's available again, " +
            "then resume their routine. Set to 0 to disable the re-check (pure vanilla behavior).");

        FoodRecheckWhenNeedBelow = Config.Bind("DynamicNeeds", "FoodRecheckWhenNeedBelow", 0.2f,
            "Only run the food/water re-check while the villager's food or water (each 0..1) is below this, " +
            "or they're already starving/dehydrated. Keeps the nudge from firing on well-fed villagers. " +
            "Set it just below the game's natural 'go eat' point: too high re-arms mildly-peckish villagers " +
            "and causes a mass dash to storage (notably right after loading a save); too low delays un-sticking " +
            "a parked villager until they're nearly starving.");

        HungerRateMultiplier = Config.Bind("DynamicNeeds", "HungerRateMultiplier", 1.0f,
            "Multiply how fast villagers get hungry (food meter drains). 1 = vanilla; 2 = twice as fast; " +
            "0.5 = half as fast. A general control, and setting it high (e.g. 5-10) is the quick way to test " +
            "the food re-check / eating behavior without waiting in-game. Only ever scales the natural drain — " +
            "eating is never affected.");

        ThirstRateMultiplier = Config.Bind("DynamicNeeds", "ThirstRateMultiplier", 1.0f,
            "Same as HungerRateMultiplier but for the water/thirst meter. 1 = vanilla.");

        FireWarmthMultiplier = Config.Bind("DynamicNeeds", "FireWarmthMultiplier", 2.0f,
            "Multiply how fast villagers warm up at a fire/heat source. 1 = vanilla speed; 2 = twice as " +
            "fast (shorter warm-up trips, more time working). Only speeds up warming while they're already " +
            "near heat — it never warms them out in the cold and never overrides the game's warm-up timing.");

        LeisureWhenHappinessBelow = Config.Bind("DynamicNeeds", "LeisureWhenHappinessBelow", 0.6f,
            "Take leisure when happiness (0..1) drops below this. Set to 0 to disable happiness-based leisure " +
            "(villagers then only take leisure for basic needs).");

        LeisureUntilHappinessAbove = Config.Bind("DynamicNeeds", "LeisureUntilHappinessAbove", 0.78f,
            "While taking leisure for happiness, return to work once happiness recovers above this (0..1). " +
            "If a villager's happiness is capped below this (bad housing etc.) they leave leisure anyway once " +
            "it plateaus, so they don't get stuck. Must be >= LeisureWhenHappinessBelow.");

        LeisureHoursToFullHappiness = Config.Bind("DynamicNeeds", "LeisureHoursToFullHappiness", 4.0f,
            "While in leisure, actively add happiness so a full empty->full restore takes about this many " +
            "in-game hours (on top of the game's own leisure rate) — this makes leisure actually worth taking. " +
            "Lower = faster happiness recovery (near 2 pegs villagers at max quickly). Set to 0 for vanilla rate.");

        DebugLogging = Config.Bind("DynamicNeeds", "DebugLogging", false,
            "Log each villager's need-driven mode changes plus a periodic rest/happiness/needs summary. " +
            "Off by default (it's very verbose); turn on only when tuning thresholds.");

        ManualScheduleDiagnostics = Config.Bind("DynamicNeeds", "ManualScheduleDiagnostics", true,
            "Read-only diagnostics for the upcoming manual-schedule (RespectManualSchedule) mode: logs each " +
            "villager's original per-hour schedule snapshot, an in-game hour calibration line, same-workstation " +
            "cohorts with sleep-hour overlaps, and externally-changed schedules (player edits). No behavior " +
            "change. Verbose; will default to false once the feature ships.");

        RespectManualSchedule = Config.Bind("DynamicNeeds", "RespectManualSchedule", false,
            "Manual-schedule mode: villagers sharing a workstation with a coworker (2+ at the same station) follow " +
            "their player-painted schedule — present during painted Work hours (only a life-threatening food/water " +
            "emergency pulls them off-post), sleeping front-loaded within their own painted off-hours (the rest boost " +
            "still shortens it), and filling leftover off-hours with leisure instead of over-manning the post. " +
            "Villagers on solo stations, and cohort members whose painted schedule has no off-hours, keep pure " +
            "needs-based behavior. Preserves player-staggered 24/7 coverage of shared posts (towers, kitchens).");
        CriticalNeedOffPostBelow = Config.Bind("DynamicNeeds", "CriticalNeedOffPostBelow", 0.05f,
            "Manual-schedule mode: normalized food/water level below which an on-shift villager takes an emergency " +
            "off-post trip (the game's starving/dehydrated flags also trigger it). Keep near-death-low — off-hours " +
            "top-ups should normally prevent it ever firing.");

        ClassInjector.RegisterTypeInIl2Cpp<NeedsController>();
        var go = new GameObject("DynamicVillagerNeedsMod_Controller");
        Object.DontDestroyOnLoad(go);
        go.AddComponent<NeedsController>();

        var harmony = new Harmony(MyPluginInfo.PLUGIN_GUID);
        harmony.PatchAll();

        Logger.LogInfo($"DynamicVillagerNeedsMod loaded. Enabled={Enabled.Value}, " +
                       $"sleep<{SleepWhenRestBelowHours.Value}h wake>{WakeWhenRestAboveHours.Value}h restBoost={SleepHoursToFullRest.Value}h, " +
                       $"need-leisure<{LeisureWhenNeedBelow.Value} until>{LeisureUntilNeedAbove.Value} (food/water only) warmthx{FireWarmthMultiplier.Value}, " +
                       $"food-recheck every {FoodRecheckIntervalSeconds.Value}s while<{FoodRecheckWhenNeedBelow.Value}, " +
                       $"hungerx{HungerRateMultiplier.Value} thirstx{ThirstRateMultiplier.Value}, " +
                       $"happy-leisure<{LeisureWhenHappinessBelow.Value} until>{LeisureUntilHappinessAbove.Value} " +
                       $"boost={LeisureHoursToFullHappiness.Value}h, " +
                       $"ManualScheduleDiagnostics={ManualScheduleDiagnostics.Value}, RespectManualSchedule={RespectManualSchedule.Value}");
    }
}
