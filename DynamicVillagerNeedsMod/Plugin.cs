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

namespace DynamicVillagerNeedsMod;

// RespectManualSchedule off-window surplus fill policy (see Plugin.OffWindowFill / NeedsController.DecideManual).
public enum OffWindowFillMode { Leisure, Work, Builder }

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

    // v1.9.3: NeedsController re-reads the cfg FILE every 5 s via Cfg.Reload() so mid-session edits
    // apply without a relaunch (BepInEx never re-reads an edited file on its own; .Value is
    // in-memory only — GroundItemVacuum/SeedScout pattern). Note: BuilderTestKey is parsed once at
    // startup and is the one setting a reload does NOT re-apply.
    internal static ConfigFile? Cfg;

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
    // v1.7.2: whitelist restricting which cohorts manual mode applies to (Buildstation-family stations
    // are ALWAYS excluded regardless of this list — see NeedsController.IsBuildstation).
    internal static ConfigEntry<string> ManualScheduleStations = null!;
    internal static ConfigEntry<OffWindowFillMode> OffWindowFill = null!;
    // OffWindowFill=Builder (v1.7.0): while loaned, leave the loan once this close to the on-window so
    // there's time to walk back to the post. Fallback only — the back-loaded top-up sleep normally
    // preempts it.
    internal static ConfigEntry<float> BuilderReturnLeadHours = null!;
    // Save fix (v1.7.0): mask a live needs-collapse write with the player's painted schedule for the
    // duration of Villager.Serialize, so quitting/autosaving mid-collapse can't bake the collapse into
    // the save file over the player's real painted schedule.
    internal static ConfigEntry<bool> PreserveScheduleInSaves = null!;

    // Panel-mask fix (v1.8.0): the schedule UI panel reads a villager's LIVE _schedule, which this mod
    // temporarily collapses to a uniform activity while driving needs-based behavior (all-Work during a
    // builder loan, all-Leisure during off-window fill, etc.). Without this, opening the panel mid-collapse
    // showed the mod's temporary operating schedule instead of the player's real painted intent — reading
    // as "my schedule got wiped" even though it's correctly preserved and restored underneath. When true,
    // the panel always shows the real painted schedule for the duration it's open/reading.
    internal static ConfigEntry<bool> ShowIntendedScheduleInUI = null!;

    // v1.6.0 builder-fill diagnostics: read-only baseline dump + hotkey lend/restore experiment for
    // testing whether Buildstation.SetTaskAgent alone can inject build quests without touching
    // Villager._workstation (see BuilderDiag.cs). Diagnostics-only — no existing behavior mode changes.
    internal static ConfigEntry<bool> BuilderDiagnostics = null!;
    internal static ConfigEntry<string> BuilderTestKey = null!;
    // Parsed once at startup from BuilderTestKey.Value (falls back to F9 on parse failure).
    internal static KeyCode BuilderTestKeyCode = KeyCode.F9;

    // Villager survival components, registered as they spawn (pruned in the controller when destroyed).
    internal static readonly List<VillagerSurvival> TrackedSurvivals = new();

    public override void Load()
    {
        Logger = base.Log;
        Cfg = Config;

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

        ManualScheduleDiagnostics = Config.Bind("DynamicNeeds", "ManualScheduleDiagnostics", false,
            "Read-only diagnostics for the manual-schedule (RespectManualSchedule) mode: logs each villager's " +
            "original per-hour schedule snapshot, an in-game hour calibration line, same-workstation cohorts " +
            "with sleep-hour overlaps (including each cohort's resolved off-window fill), and externally-changed " +
            "schedules (player edits). No behavior change. Verbose — the feature is verified/shipped; enable " +
            "only when debugging.");

        RespectManualSchedule = Config.Bind("DynamicNeeds", "RespectManualSchedule", false,
            "Manual-schedule mode: villagers sharing a workstation with a coworker (2+ at the same station) follow " +
            "their player-painted schedule via three brushes. Painted Work hours are followed as painted — present " +
            "at the post (only a life-threatening food/water emergency pulls them off-post). Painted Leisure hours " +
            "are also followed as painted — the villager relaxes then, regardless of OffWindowFill. Painted Sleep " +
            "blocks are flexible rest time the mod optimizes: one shorter, well-timed sleep (back-loaded to end at " +
            "shift start; the rest boost shortens it further), with the hours freed up by that optimization filled " +
            "per OffWindowFill. Villagers on solo stations, and cohort members whose painted schedule has no " +
            "off-hours, keep pure needs-based behavior. Preserves player-staggered 24/7 coverage of shared posts " +
            "(towers, kitchens).");
        CriticalNeedOffPostBelow = Config.Bind("DynamicNeeds", "CriticalNeedOffPostBelow", 0.05f,
            "Manual-schedule mode: normalized food/water level below which an on-shift villager takes an emergency " +
            "off-post trip (the game's starving/dehydrated flags also trigger it). Keep near-death-low — off-hours " +
            "top-ups should normally prevent it ever firing.");

        ManualScheduleStations = Config.Bind("DynamicNeeds", "ManualScheduleStations", "",
            "Manual-schedule mode: comma-separated, case-insensitive substrings matched against each 2+ cohort's " +
            "station display name. Empty (default) = current behavior: any non-Buildstation 2+ same-station cohort " +
            "with a mixed (Work + off-hour) painted schedule qualifies for manual mode, using the global " +
            "OffWindowFill. Non-empty = manual mode applies ONLY to cohorts whose station name contains at least " +
            "one of these substrings; every other villager (e.g. newly summoned/unassigned workers at other " +
            "stations) falls back to pure needs-based behavior instead of being treated as manual-scheduled. Each " +
            "entry is either a bare substring (that cohort uses the global OffWindowFill) or 'Substring:Fill' " +
            "where Fill is Leisure, Work, or Builder (case-insensitive) to override the off-window fill for just " +
            "that cohort. An invalid Fill token (e.g. a typo) logs a one-time warning naming the bad token and " +
            "falls back to treating the entry as bare rather than dropping it. Matching is FIRST-MATCH-WINS in " +
            "list order — the first entry whose substring is found in the station name supplies the fill, so put " +
            "more specific substrings before broader ones if they could both match. Buildstation-family stations " +
            "are ALWAYS excluded from manual mode regardless of this list. Example: 'Cooking House:Leisure, " +
            "Workshop:Builder, Watchtower' — cooks off-window relax, workshop workers off-window build, and " +
            "watchtower guards use whatever OffWindowFill (the global default) is set to. Station display names " +
            $"vary by save and game language, so don't guess them: {NeedsController.StationsFileName} (written " +
            "next to this config file, refreshed automatically while the mod is running) lists the exact names " +
            "in your settlement and whether each currently matches an entry here. Entries here match both a " +
            "station's CURRENT (possibly player-renamed, e.g. via right-click Rename) name and its ORIGINAL " +
            "type name, so an entry 'Tavern' keeps matching a tavern renamed 'Tomaso's'; the stations list file " +
            "shows both forms as 'Tomaso's (Tavern)' when they differ. Whichever fill applies " +
            "(global or per-station) only governs flexible off-hours (the hours freed by the optimized painted-" +
            "Sleep block) — hours the player painted as Leisure are always spent as Leisure regardless of the " +
            "resolved fill (see RespectManualSchedule).");

        OffWindowFill = Config.Bind("DynamicNeeds", "OffWindowFill", OffWindowFillMode.Leisure,
            "Manual-schedule mode only: the GLOBAL DEFAULT for what an off-shift cohort villager does with " +
            "surplus off-hours after their timed top-up sleep and needs are handled. Used by any cohort whose " +
            "ManualScheduleStations entry is bare (no ':Fill' suffix), and by every cohort when " +
            "ManualScheduleStations is empty. A per-station ':Fill' suffix in ManualScheduleStations (e.g. " +
            "'Workshop:Builder') overrides this default for just that cohort — see ManualScheduleStations. " +
            "Leisure = hangout + happiness recovery (default, v1.4.4 behavior). Work = go back to the post " +
            "instead (deliberately over-mans a post a coworker holds; the normal happiness-leisure thresholds " +
            "still apply so mood can't bottom out). Builder (v1.7.0) = lend the off-duty villager to the " +
            "settlement's builder pool — they build/repair/haul like a dedicated builder WITHOUT their job " +
            "assignment or schedule UI changing. They still take their timed top-up sleep and are back at their " +
            "post by shift start (or earlier, via BuilderReturnLeadHours), and needs emergencies / the happiness " +
            "safety valve pull them off the loan just like the other fills. If the lend itself fails (no " +
            "Buildstation resolved, etc.) that villager falls back to Leisure for the rest of the off-window and " +
            "a warning is logged. Applies only to flexible off-hours — the hours freed by the optimized painted-" +
            "Sleep block; hours the player painted as Leisure are always spent as Leisure regardless of this " +
            "setting (see RespectManualSchedule).");

        BuilderReturnLeadHours = Config.Bind("DynamicNeeds", "BuilderReturnLeadHours", 1.0f,
            "OffWindowFill=Builder only: while a villager is on loan, leave the loan once the time remaining " +
            "until their painted on-window starts drops to this many in-game hours (gives them time to walk " +
            "back to their post). Fallback only — the back-loaded top-up sleep normally ends the loan first.");

        PreserveScheduleInSaves = Config.Bind("DynamicNeeds", "PreserveScheduleInSaves", true,
            "Fixes a save bug: this mod only restores a villager's real painted schedule at app-quit, so " +
            "quitting to the main menu (or any autosave) while a needs-driven collapse write is live permanently " +
            "bakes that collapse over the player's painted schedule into the save file. When true, the mod masks " +
            "the live schedule with the player's painted one for the instant the game actually saves a villager, " +
            "then puts the collapse back immediately after — so saves always capture the real painted schedule " +
            "and the mod's own tracking is unaffected. Leave this on unless you're debugging the save patch itself.");

        ShowIntendedScheduleInUI = Config.Bind("DynamicNeeds", "ShowIntendedScheduleInUI", true,
            "Fixes a UI-confusion bug: this mod temporarily collapses a villager's schedule to a single " +
            "activity while driving needs-based behavior (e.g. all-Work during a builder loan, all-Leisure " +
            "during off-window fill). Without this option, opening the villager schedule panel while a " +
            "collapse is live showed that temporary operating schedule instead of what you actually painted " +
            "— it looks like your schedule got erased, even though the mod correctly preserves and restores " +
            "it underneath. When true, the panel always shows your real painted schedule, and editing/applying " +
            "a new paint in the panel still works normally. Leave this on unless you're debugging the mask itself.");

        BuilderDiagnostics = Config.Bind("DynamicNeeds", "BuilderDiagnostics", false,
            "Read-only diagnostics + a hotkey-driven lend/restore EXPERIMENT for the OffWindowFill=Builder mode. " +
            "~10s after the first villager is tracked, logs a one-time baseline dump (each tracked villager's " +
            "name/workstation/Buildstation-agent membership, then the default Buildstation's own agent counts). " +
            "While active, pressing BuilderTestKey lends the nearest eligible villager to the settlement's " +
            "default Buildstation to test whether Buildstation.SetTaskAgent alone injects build quests into the " +
            "villager's QuestRunner WITHOUT changing Villager._workstation (the field the UI job icon reads) — " +
            "logging before/after state so the variants can be compared. Every code path here is try/catch-" +
            "guarded and can never affect normal mod behavior or any shipped mode. Verbose — builder-fill is " +
            "verified/shipped; enable only when debugging.");

        BuilderTestKey = Config.Bind("DynamicNeeds", "BuilderTestKey", "F9",
            "Hotkey for the BuilderDiagnostics lend/restore experiment (host/solo only — non-authority clients " +
            "will never find an eligible target). Parsed once at startup as a Unity KeyCode name. Plain key = " +
            "variant FULL_LOAN (release from home so its quests leave the runner, join the Buildstation for " +
            "real via SetTaskAgent+AddToTaskDatas, then paint an all-Work schedule so nothing else competes " +
            "for the villager's dispatch — the v1.6.0 test showed the home station's quests otherwise stay in " +
            "the runner and out-priority the build quests); Shift+key = variant AGENT+TASKDATA (SetTaskAgent " +
            "then AddToTaskDatas, home workstation left untouched, no schedule write); Ctrl+key = variant SWAP " +
            "(Villager.AssignToWorkstation — the normal reassignment, kept as the comparison baseline). While " +
            "a lend is active, NeedsController's own control loop skips that villager entirely. Press again " +
            "(any modifier) while an experiment is active to restore.");

        if (Enum.TryParse<KeyCode>(BuilderTestKey.Value, true, out var builderKey))
            BuilderTestKeyCode = builderKey;
        else
        {
            BuilderTestKeyCode = KeyCode.F9;
            Logger.LogWarning($"[DynamicNeeds] Could not parse BuilderTestKey '{BuilderTestKey.Value}'. Falling back to F9.");
        }

        ClassInjector.RegisterTypeInIl2Cpp<NeedsController>();
        var go = new GameObject("DynamicVillagerNeedsMod_Controller");
        UnityEngine.Object.DontDestroyOnLoad(go);
        // v1.7.0: kept as a static so the save-preservation Harmony patch (a static class — patches
        // can't take constructor args) can reach the mod's live per-villager schedule snapshots.
        NeedsController.Instance = go.AddComponent<NeedsController>();

        var harmony = new Harmony(MyPluginInfo.PLUGIN_GUID);
        harmony.PatchAll();

        // v1.9.0: echo the parsed per-station fill assignments (if any) alongside the raw config string —
        // parsing here (via the same NeedsController.ParseStationRules the control loop uses) also means
        // an invalid ':Fill' token's one-time warning fires immediately at startup rather than waiting for
        // the first 10 s cohort report.
        string stationFillSummary = "";
        try
        {
            var stationRules = NeedsController.Instance!.ParseStationRules(ManualScheduleStations.Value);
            stationFillSummary = NeedsController.DescribeStationRules(stationRules);
        }
        catch (Exception ex) { Logger.LogError($"[DynamicNeeds] startup station-rule parse: {ex}"); }

        Logger.LogInfo($"DynamicVillagerNeedsMod loaded. Enabled={Enabled.Value}, " +
                       $"sleep<{SleepWhenRestBelowHours.Value}h wake>{WakeWhenRestAboveHours.Value}h restBoost={SleepHoursToFullRest.Value}h, " +
                       $"need-leisure<{LeisureWhenNeedBelow.Value} until>{LeisureUntilNeedAbove.Value} (food/water only) warmthx{FireWarmthMultiplier.Value}, " +
                       $"food-recheck every {FoodRecheckIntervalSeconds.Value}s while<{FoodRecheckWhenNeedBelow.Value}, " +
                       $"hungerx{HungerRateMultiplier.Value} thirstx{ThirstRateMultiplier.Value}, " +
                       $"happy-leisure<{LeisureWhenHappinessBelow.Value} until>{LeisureUntilHappinessAbove.Value} " +
                       $"boost={LeisureHoursToFullHappiness.Value}h, " +
                       $"ManualScheduleDiagnostics={ManualScheduleDiagnostics.Value}, RespectManualSchedule={RespectManualSchedule.Value}, " +
                       $"ManualScheduleStations='{ManualScheduleStations.Value}'" +
                       (stationFillSummary.Length > 0 ? $" fills=[{stationFillSummary}]" : "") + ", " +
                       $"OffWindowFill={OffWindowFill.Value} builderReturnLead={BuilderReturnLeadHours.Value}h, " +
                       $"PreserveScheduleInSaves={PreserveScheduleInSaves.Value}, " +
                       $"ShowIntendedScheduleInUI={ShowIntendedScheduleInUI.Value}, " +
                       $"BuilderDiagnostics={BuilderDiagnostics.Value} testKey={BuilderTestKeyCode}");
    }
}
