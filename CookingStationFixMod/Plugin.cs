using System;
using System.Collections.Generic;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Unity.IL2CPP;
using BepInEx.Logging;
using HarmonyLib;
using SandSailorStudio.Inventory;  // ItemManifest
using SSSGame;
using SSSGame.AI;
using SSSGame.AI.FSM;
using UnityEngine;

namespace CookingStationFixMod;

// Mod 8: CookingStationFixMod.
//
// PURPOSE: cooks at a cooking hut run up to the station, "engage", then sit/stand/sit/stand forever
// without producing food — and ignore manually-restocked ingredients. Confirmed VANILLA (reproduces
// with DynamicVillagerNeedsMod Enabled=false; a Nexus user reports the same), so this is a standalone
// fix, not a Mod 5 change.
//
// PHASE 1 (this build): READ-ONLY DIAGNOSTICS. The cooking work runs through a chain of FSM states
// (FSM_CanStartCooking decision gate -> FSM_FetchCookingStockpile / FSM_FetchCookingSupplies ->
// FSM_SetCookingPlacePosition -> ... -> FSM_ReturnCookingResults). The sit/stand loop is one of these
// states being entered and immediately bailing. Each of those FSM states derives from
// FSM_QuestDecision / FSM_QuestAction, which expose GetQuestData(fsmBehaviour); the cook's quest data
// (CookingQuestData : CookingPlaceQuestData) carries the exact stall latches (_noSupplies/_noFuel/
// _noTool/_noStorageSpace/_badWeather) and the stockpile quest carries NeedsMore* flags. We log which
// state the cook cycles through and which latch is set, so we can see the real cause before writing the
// actual fix. Nothing is modified — every patch is a log-only postfix.
//
// PHASE 2 (next build, once the log shows the cause): the targeted fix. The leading hypothesis is the
// same "committed-quest parks and never re-evaluates" pattern Mod 5 already fixes for EATING via
// SurvivalObjectiveQuest.SetDirty() — the cooking equivalent is WorkstationQuest.SetDirty() /
// CookingStockpileQuestData._RecheckSupplies(). We'll confirm against the diagnostic trace first.
[BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
public class Plugin : BasePlugin
{
    internal static ManualLogSource Logger = null!;

    internal static ConfigEntry<bool> Enabled = null!;
    internal static ConfigEntry<bool> DebugLogging = null!;
    internal static ConfigEntry<float> LogThrottleSeconds = null!;

    public override void Load()
    {
        Logger = base.Log;

        Enabled = Config.Bind("CookingStationFix", "Enabled", true,
            "Master switch. When false, the mod does nothing (pure vanilla cooking).");

        DebugLogging = Config.Bind("CookingStationFix", "DebugLogging", true,
            "PHASE 1: log which cooking FSM state a stalled cook cycles through and which stall latch " +
            "(_noSupplies/_noFuel/_noTool/_noStorageSpace/_badWeather, NeedsMore*) is set. Read-only. " +
            "Watch a stuck cooking hut for ~20s, then send the [CookingFix][diag] lines.");

        LogThrottleSeconds = Config.Bind("CookingStationFix", "LogThrottleSeconds", 1.0f,
            "Minimum seconds between identical diagnostic lines per cook (keeps the oscillation readable " +
            "without flooding the log). Each line carries t=<realtime> so ordering/frequency stay visible.");

        var harmony = new Harmony(MyPluginInfo.PLUGIN_GUID);
        harmony.PatchAll();

        Logger.LogInfo(
            $"CookingStationFixMod loaded (PHASE 1 diagnostics). Enabled={Enabled.Value}, " +
            $"DebugLogging={DebugLogging.Value}, LogThrottleSeconds={LogThrottleSeconds.Value}. " +
            $"Watch a stalled cooking hut and collect the [CookingFix][diag] lines.");
    }

    // --- per-cook, per-event log throttle ---
    private static readonly Dictionary<string, float> _lastLog = new();

    private static bool ShouldLog(string key)
    {
        float t = Time.realtimeSinceStartup;
        if (_lastLog.TryGetValue(key, out var last) && t - last < LogThrottleSeconds.Value) return false;
        _lastLog[key] = t;
        return true;
    }

    // Single read-only logger shared by every cooking-FSM patch. `ev` is the state/decision that fired;
    // `qd` is that FSM's quest data (from FSM_Quest*.GetQuestData). We pull the cook's identity from the
    // FSM controller's gameObject and the stall latches from the quest data (cast down the cooking
    // hierarchy). Everything is wrapped in try/catch — a diagnostic must never destabilize the game.
    internal static void LogCookState(IFSMBehaviourController fsm, QuestData qd, string ev)
    {
        try
        {
            if (!Enabled.Value || !DebugLogging.Value) return;

            // Cook identity (stable per villager for the session).
            int id = 0; string vname = "?";
            try { var go = fsm != null ? fsm.gameObject : null; if (go != null) { id = go.GetInstanceID(); vname = go.name; } }
            catch { }

            if (!ShouldLog($"{id}|{ev}")) return;

            string quest = "?", state = "?";
            try { state = fsm != null ? fsm.NameOfCurrentState : "?"; } catch { }
            try { var q = qd != null ? qd.Quest : null; if (q != null) quest = q.Name; } catch { }

            string station = "?", recipe = "?", flags = "";
            bool matched = false;

            if (qd != null)
            {
                // Shared place-quest stall latches (CookingQuestData : CookingPlaceQuestData).
                try
                {
                    var place = qd.TryCast<CookingPlaceQuest.CookingPlaceQuestData>();
                    if (place != null)
                    {
                        matched = true;
                        flags += $"noSupplies={place._noSupplies} noFuel={place._noFuel} noTool={place._noTool} " +
                                 $"noStorage={place._noStorageSpace} badWeather={place._badWeather} working={place.IsWorking}";
                    }
                }
                catch { }

                // Cook-specific extras (the actual cook quest: recipe, station, filler complaint).
                try
                {
                    var cook = qd.TryCast<CookingQuest.CookingQuestData>();
                    if (cook != null)
                    {
                        matched = true;
                        try { flags += $" noFillers={cook._noFillers}"; } catch { }
                        try { var st = cook.CookingStation; if (st != null) station = st.GetName(); } catch { }
                        try { var ri = cook.RecipeInfo; recipe = ri != null ? ri.name : "null"; } catch { }
                    }
                }
                catch { }

                // Stockpile-supply needs + STATION-LEVEL state — the loop we're chasing lives here. Is there
                // an active cooking project/recipe at all (projects/target), and how much is already
                // stockpiled? If needs are all false AND a target exists, the cook is looping the (satisfied)
                // stockpile quest instead of switching to the cook quest.
                try
                {
                    var sp = qd.TryCast<CookingStockpileQuest.CookingStockpileQuestData>();
                    if (sp != null)
                    {
                        matched = true;
                        flags += $"needSpec={sp.NeedsMoreSpecificSupplies} needFill={sp.NeedsMoreFillerSupplies} " +
                                 $"needGath={sp.NeedsMoreGatheredSupplies}";
                        try { var nr = sp._nextRecipeInfo; recipe = nr != null ? nr.name : "null"; } catch { }
                        try
                        {
                            var st = sp.CookingStation;
                            if (st != null)
                            {
                                if (station == "?") station = st.GetName();
                                int proj = -1; try { var pl = st.cookingProjects; if (pl != null) proj = pl.Count; } catch { }
                                flags += $" | station[projects={proj} target={Tot(st.CookingTargetManifest)} " +
                                         $"stockpiled={Tot(st.GlobalInventoryManifest)}]";
                            }
                        }
                        catch { }
                        flags += $" gathered={Tot(sp.GatheredStockpileSupplies)} neededSpec={Tot(sp.NeededSpecificSupplies)} " +
                                 $"neededFill={Tot(sp.NeededFillerSupplies)}";
                    }
                }
                catch { }
            }
            if (!matched) flags = "(no recognized quest data)";

            Logger.LogInfo(
                $"[CookingFix][diag] t={Time.realtimeSinceStartup:F1} cook='{vname}#{id}' ev={ev} " +
                $"quest={quest} fsmState={state} station='{station}' recipe={recipe} | {flags}");
        }
        catch (Exception ex) { Logger.LogError($"[CookingFix] LogCookState: {ex}"); }
    }

    // Total item count in an ItemManifest, or "-"/"?" if null/unreadable. Used to show whether the station
    // has a cook target and how full its stockpile is.
    private static string Tot(ItemManifest m)
    {
        try { return m != null ? m.GetTotalQuantity().ToString() : "-"; }
        catch { return "?"; }
    }

    // Throttled priority probe. If "CookingQuest" never logs, no active cook project exists (nothing to
    // cook). If it logs but below "CookingStockpileQuest", the satisfied stockpile quest is out-competing
    // the cook quest in arbitration — the loop. Per-label throttle keeps it readable.
    private static readonly Dictionary<string, float> _lastPrio = new();
    internal static void LogPriority(string label, float result)
    {
        try
        {
            if (!Enabled.Value || !DebugLogging.Value) return;
            float t = Time.realtimeSinceStartup;
            if (_lastPrio.TryGetValue(label, out var last) && t - last < LogThrottleSeconds.Value) return;
            _lastPrio[label] = t;
            Logger.LogInfo($"[CookingFix][prio] t={t:F1} {label} priority={result:F1}");
        }
        catch { }
    }
}
