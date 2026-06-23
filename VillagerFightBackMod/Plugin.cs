using System;
using System.Collections.Generic;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Unity.IL2CPP;
using BepInEx.Logging;
using HarmonyLib;
using SSSGame.AI;
using SSSGame.Combat;
using UnityEngine;

namespace VillagerFightBackMod;

// Makes regular (non-warrior) villagers stand and fight a WHITELISTED set of enemies instead of
// fleeing — e.g. Wisps (which anything can kill) — while still fleeing from everything dangerous
// (draugar, wolves, ...). Done by post-processing the game's own flee-vs-fight decision
// (FleeCombatQuestData.ShouldFight): when a villager would flee an enemy whose name/faction is on
// the whitelist, we flip ShouldFight to true. Pure read + return-a-bool, so it's co-op-safe.
[BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
public class Plugin : BasePlugin
{
    internal static ManualLogSource Logger = null!;

    internal static ConfigEntry<bool> EnabledCfg = null!;
    internal static ConfigEntry<string> FightBackAgainst = null!;
    internal static ConfigEntry<string> FightBackFactions = null!;
    internal static ConfigEntry<bool> SuppressSpook = null!;
    internal static ConfigEntry<bool> ForceEngage = null!;
    internal static ConfigEntry<bool> BoostCombatPriority = null!;
    internal static ConfigEntry<bool> BoostTriggerPriority = null!;
    internal static ConfigEntry<bool> SuspendWorkWhileEngaged = null!;
    internal static ConfigEntry<bool> UseNaturalCombatBehaviour = null!;
    internal static ConfigEntry<bool> PreventFlee = null!;
    internal static ConfigEntry<bool> TreatAsWarrior = null!;
    internal static ConfigEntry<bool> PreventRunMovement = null!;
    internal static ConfigEntry<bool> KeepCombatAlive = null!;
    internal static ConfigEntry<bool> DebugLogging = null!;

    private static string[] _nameTokens = Array.Empty<string>();
    private static string[] _factionTokens = Array.Empty<string>();

    public override void Load()
    {
        Logger = base.Log;

        EnabledCfg = Config.Bind(
            section: "VillagerFightBack",
            key: "Enabled",
            defaultValue: true,
            description: "Master switch. When false, villagers use vanilla flee behavior.");

        FightBackAgainst = Config.Bind(
            section: "VillagerFightBack",
            key: "FightBackAgainst",
            defaultValue: "Wisp",
            description: "Comma-separated list of enemy NAME substrings villagers will fight instead of " +
                         "flee (case-insensitive, matched against the attacker's display name). " +
                         "Empty = fight nobody (vanilla flee). Turn on DebugLogging to discover exact names.");

        FightBackFactions = Config.Bind(
            section: "VillagerFightBack",
            key: "FightBackFactions",
            defaultValue: "",
            description: "Optional comma-separated list of FACTIONS to also fight back against " +
                         "(Critters, Danger, Undead, Vikings, Neutral, Structures, Ignore). Coarser than " +
                         "names — e.g. Undead would include draugar. Usually leave empty and use names.");

        SuppressSpook = Config.Bind(
            section: "VillagerFightBack",
            key: "SuppressSpook",
            defaultValue: true,
            description: "PRIMARY fight-back mechanism. When a whitelisted enemy would spook a villager, " +
                         "skip the spook entirely so they're never frightened into fleeing it — their " +
                         "normal chase/attack behavior (which already exists) then continues uninterrupted. " +
                         "Non-whitelisted enemies (draugar, wolves) still spook & flee normally.");

        ForceEngage = Config.Bind(
            section: "VillagerFightBack",
            key: "ForceEngage",
            defaultValue: true,
            description: "When a whitelisted enemy attacks, actively set it as the villager's combat " +
                         "target so they ENTER combat and attack it — instead of ignoring it while " +
                         "walking to a job (a villager doing a job never enters the combat state on " +
                         "their own, so they only ever flee). Only sets a target if the villager isn't " +
                         "already targeting something.");

        BoostCombatPriority = Config.Bind(
            section: "VillagerFightBack",
            key: "BoostCombatPriority",
            defaultValue: true,
            description: "THE key lever. While a villager is engaged with a whitelisted enemy, raise their " +
                         "combat quest's priority above work so it HOLDS — a villager with a job otherwise " +
                         "drops combat instantly because the work quest out-prioritizes it (which is why " +
                         "they fight when idle but not when busy). Self-limiting: reverts when the enemy is gone.");

        BoostTriggerPriority = Config.Bind(
            section: "VillagerFightBack",
            key: "BoostTriggerPriority",
            defaultValue: true,
            description: "THE arbitration fix. The QuestRunner selects the active quest by each quest's " +
                         "TriggerPriority — NOT GetPriority (boosting GetPriority fired in-game but combat " +
                         "still lost to work, proving the runner gates eligibility on TriggerPriority). A busy " +
                         "villager's flee-combat quest only spikes its TriggerPriority for an instant when " +
                         "freshly spooked, so the work quest reclaims her in the gaps and drags her back to the " +
                         "job marker. This pins her flee-combat quest's TriggerPriority at the flee tier for the " +
                         "whole encounter (while a whitelisted enemy is remembered), so combat stays selected and " +
                         "she commits to the fight. Self-reverting: expires ~8s after the enemy is gone.");

        SuspendWorkWhileEngaged = Config.Bind(
            section: "VillagerFightBack",
            key: "SuspendWorkWhileEngaged",
            defaultValue: true,
            description: "THE arbitration fix (v1.0.13). Idle villagers already fight Wisps fine — the only thing " +
                         "dragging a busy one off is her active WORK quest out-competing combat. So while she's " +
                         "engaged with a whitelisted enemy, remove her active work quest (via the game's own " +
                         "QuestRunner.RemoveQuest, from a QuestRunner.Update poll) so she drops to idle and fights; " +
                         "add it back (RemoveQuest/AddQuest) once the enemy is gone. Survival/combat/idle quests are " +
                         "name-protected and never touched. Replaces the priority-boost and FindNextBestQuest " +
                         "approaches (the latter crashed: its Single& out-param is an IL2CPP trampoline hazard).");

        UseNaturalCombatBehaviour = Config.Bind(
            section: "VillagerFightBack",
            key: "UseNaturalCombatBehaviour",
            defaultValue: true,
            description: "THE likely real fix (v1.0.14). A villager has TWO combat FSM behaviours: " +
                         "'fleeCombatBehaviour' (kite/run) and 'naturalCombatBehaviour' (stand & fight). The " +
                         "FleeCombatQuest runs the FLEE one — which is why she runs even after we remove her job " +
                         "and force ShouldFight/melee: the running IS the flee combat behaviour itself, not the " +
                         "job. This postfixes CombatQuest.GetFSMBehavior and, for a whitelisted enemy, returns " +
                         "VillagerSurvival.naturalCombatBehaviour instead, routing her into the stand-and-fight " +
                         "FSM (the same one idle villagers use to kill Wisps). Safe signature (QuestData in, " +
                         "vFSMBehaviour out — both reference types).");

        PreventFlee = Config.Bind(
            section: "VillagerFightBack",
            key: "PreventFlee",
            defaultValue: true,
            description: "THE actual fix. Blocks the FSM from setting a villager's 'fleeing' flag when " +
                         "their current combat target is whitelisted, so their combat FSM falls through " +
                         "to the melee branch (the one that already works when idle) instead of running. " +
                         "Only applies while targeting a whitelisted enemy; real threats flee normally.");

        TreatAsWarrior = Config.Bind(
            section: "VillagerFightBack",
            key: "TreatAsWarrior",
            defaultValue: true,
            description: "THE fix. The villager combat FSM branches on 'is this a warrior?' — warriors " +
                         "melee, non-warriors run. While a villager's current target is whitelisted, we " +
                         "answer that decision 'yes', routing them into the existing melee behavior " +
                         "instead of the run-away branch. Only applies vs whitelisted targets.");

        PreventRunMovement = Config.Bind(
            section: "VillagerFightBack",
            key: "PreventRunMovement",
            defaultValue: true,
            description: "Blocks the FSM_RunFromTarget action (the actual run-away movement) while the " +
                         "villager's current target is whitelisted. The fear flag is already suppressed; " +
                         "this stops the remaining physical retreat so the combat FSM can melee instead.");

        KeepCombatAlive = Config.Bind(
            section: "VillagerFightBack",
            key: "KeepCombatAlive",
            defaultValue: true,
            description: "Keeps a villager's combat timer topped up while engaged with a whitelisted enemy. " +
                         "A non-warrior's combat ends quickly, dropping them out of melee back to their " +
                         "previous quest (which reads as running off); this holds them in the fight until " +
                         "the enemy is gone.");

        DebugLogging = Config.Bind(
            section: "VillagerFightBack",
            key: "DebugLogging",
            defaultValue: true,
            description: "Log the name + faction of each enemy that spooks a villager (so you can fill " +
                         "FightBackAgainst), and log when a villager is flipped to fight. Defaults ON while " +
                         "this mod is being verified in-game; turn off once your whitelist is dialed in.");

        ParseTokens();
        Config.SettingChanged += (_, __) => ParseTokens();

        var harmony = new Harmony(MyPluginInfo.PLUGIN_GUID);
        harmony.PatchAll();

        Logger.LogInfo($"VillagerFightBackMod loaded. Enabled={EnabledCfg.Value}, " +
                       $"FightBackAgainst=\"{FightBackAgainst.Value}\", FightBackFactions=\"{FightBackFactions.Value}\", " +
                       $"SuppressSpook={SuppressSpook.Value}, ForceEngage={ForceEngage.Value}, " +
                       $"BoostCombatPriority={BoostCombatPriority.Value}, BoostTriggerPriority={BoostTriggerPriority.Value}, " +
                       $"SuspendWorkWhileEngaged={SuspendWorkWhileEngaged.Value}, " +
                       $"UseNaturalCombatBehaviour={UseNaturalCombatBehaviour.Value}, PreventFlee={PreventFlee.Value}, " +
                       $"TreatAsWarrior={TreatAsWarrior.Value}, PreventRunMovement={PreventRunMovement.Value}, " +
                       $"KeepCombatAlive={KeepCombatAlive.Value}, DebugLogging={DebugLogging.Value}");

        if (DebugLogging.Value)
        {
            try
            {
                Logger.LogInfo($"[VillagerFightBack] QuestPriority scale: Flee={QuestPriority.c_Flee}, " +
                               $"SelfDefense={QuestPriority.c_SelfDefense}, AvoidImminentDanger={QuestPriority.c_AvoidImminentDanger}, " +
                               $"WorkstationWork={QuestPriority.c_WorkstationWork}, ImportantWork={QuestPriority.c_ImportantWork}, " +
                               $"Idle={QuestPriority.c_Idle}. Combat boost will be {GetCombatBoost()}.");
            }
            catch (Exception ex) { Logger.LogError($"[VillagerFightBack] couldn't read QuestPriority scale: {ex}"); }
        }
    }

    private static void ParseTokens()
    {
        _nameTokens = SplitCsv(FightBackAgainst.Value);
        _factionTokens = SplitCsv(FightBackFactions.Value);
    }

    private static string[] SplitCsv(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return Array.Empty<string>();
        var list = new List<string>();
        foreach (var part in s.Split(','))
        {
            var t = part.Trim();
            if (t.Length > 0) list.Add(t);
        }
        return list.ToArray();
    }

    // Is this attacker one the villager should fight rather than flee?
    internal static bool IsWhitelisted(IAttackTarget target)
    {
        try
        {
            if (_nameTokens.Length > 0)
            {
                string name = target.GetTargetName();
                if (!string.IsNullOrEmpty(name))
                {
                    foreach (var token in _nameTokens)
                        if (name.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0)
                            return true;
                }
            }

            if (_factionTokens.Length > 0)
            {
                string fac = FactionName(target.Faction);
                foreach (var token in _factionTokens)
                    if (string.Equals(fac, token, StringComparison.OrdinalIgnoreCase))
                        return true;
            }
        }
        catch { /* identity read failed — treat as not-whitelisted (flee) */ }
        return false;
    }

    internal static string FactionName(Faction f)
    {
        if (f == Faction.Critters) return "Critters";
        if (f == Faction.Danger) return "Danger";
        if (f == Faction.Ignore) return "Ignore";
        if (f == Faction.Neutral) return "Neutral";
        if (f == Faction.Structures) return "Structures";
        if (f == Faction.Undead) return "Undead";
        if (f == Faction.Vikings) return "Vikings";
        return ((int)f).ToString();
    }

    // One-shot: confirms the GetPriority boost actually fires (i.e. GetPriority is the arbiter path).
    internal static bool BoostLoggedOnce = false;

    // One-shot: confirms the TriggerPriority boost fires — the lever that actually holds combat selected.
    internal static bool TriggerBoostLoggedOnce = false;

    // One-shot: did get_TriggerPriority ever run (confirms whether v1.0.11's boosted getter is cached).
    internal static bool TriggerGetterLoggedOnce = false;

    // One-shot: confirms the flee->natural combat-behaviour swap fired (the v1.0.14 lever).
    internal static bool BehaviourSwapLoggedOnce = false;

    // Quests we must never suspend: survival, combat/flee, idle, and other non-work objectives. Matched as
    // case-insensitive substrings against Quest.Name. Anything NOT matching is treated as a work quest and
    // is eligible to be removed while she fights a whitelisted enemy.
    private static readonly string[] _protectedQuestSubstrings =
    {
        "Flee", "Combat", "Survival", "Replenish", "Sleep", "Rest", "WarmUp", "Warmup", "Warm",
        "AvoidImminentDanger", "Danger", "Idle", "DressUp", "Paint", "CallToArms", "Homeless",
        "Complain", "Party", "WaveTo", "TalkingTo", "ProtectMaster", "Alarm", "Retreat", "Sailing", "Ship"
    };

    internal static bool IsProtectedQuest(Quest quest)
    {
        if (quest == null) return true; // unknown → don't touch
        string name;
        try { name = quest.Name; } catch { return true; }
        if (string.IsNullOrEmpty(name)) return true;
        foreach (var s in _protectedQuestSubstrings)
            if (name.IndexOf(s, StringComparison.OrdinalIgnoreCase) >= 0) return true;
        return false;
    }

    // --- debug-logging throttles ---
    private static readonly HashSet<string> _loggedSpooks = new();

    // Log each distinct spook source only once, so the discovery log isn't spammed.
    internal static bool AlreadyLoggedSpook(string name)
    {
        if (_loggedSpooks.Contains(name)) return true;
        _loggedSpooks.Add(name);
        return false;
    }

    private static float _lastFlipLogTime = -999f;

    // Throttle the "forced to fight" log to once every couple seconds (getter is polled often).
    internal static bool ShouldLogFlip()
    {
        float t = Time.realtimeSinceStartup;
        if (t - _lastFlipLogTime < 2f) return false;
        _lastFlipLogTime = t;
        return true;
    }

    private static float _lastDiagLogTime = -999f;

    // Throttle the per-call ShouldFight diagnostic to once every couple seconds.
    internal static bool ShouldLogDiag()
    {
        float t = Time.realtimeSinceStartup;
        if (t - _lastDiagLogTime < 2f) return false;
        _lastDiagLogTime = t;
        return true;
    }

    private static float _lastBlockLogTime = -999f;

    // Dedicated throttle for the flee-block log so it isn't starved by the other throttled logs.
    internal static bool ShouldLogBlock()
    {
        float t = Time.realtimeSinceStartup;
        if (t - _lastBlockLogTime < 2f) return false;
        _lastBlockLogTime = t;
        return true;
    }

    private static float _lastWarriorLogTime = -999f;

    // Dedicated throttle for the treat-as-warrior log.
    internal static bool ShouldLogWarrior()
    {
        float t = Time.realtimeSinceStartup;
        if (t - _lastWarriorLogTime < 2f) return false;
        _lastWarriorLogTime = t;
        return true;
    }

    private static float _lastRunLogTime = -999f;

    // Dedicated throttle for the run-state / melee-entry probes.
    internal static bool ShouldLogRun()
    {
        float t = Time.realtimeSinceStartup;
        if (t - _lastRunLogTime < 2f) return false;
        _lastRunLogTime = t;
        return true;
    }

    private static float _lastArbLogTime = -999f;

    // Throttle the FindNextBestQuest arbitration log (called every frame per villager).
    internal static bool ShouldLogArb()
    {
        float t = Time.realtimeSinceStartup;
        if (t - _lastArbLogTime < 2f) return false;
        _lastArbLogTime = t;
        return true;
    }

    // Reading an IL2CPP target's name can throw if the underlying object was destroyed.
    internal static string SafeName(IAttackTarget t)
    {
        try { var n = t.GetTargetName(); return string.IsNullOrEmpty(n) ? "(unnamed)" : n; }
        catch { return "(name-threw)"; }
    }

    // Priority to give a combat quest engaged with a whitelisted enemy so it outranks work. We sit
    // just above c_Flee — flee already interrupts work in vanilla, so matching it +1 guarantees we
    // beat work AND any lingering flee, without exceeding survival/imminent-danger quests.
    private static float _combatBoost = -1f;
    internal static float GetCombatBoost()
    {
        if (_combatBoost < 0f)
        {
            try { _combatBoost = QuestPriority.c_Flee + 1f; }
            catch { _combatBoost = 1000f; }
        }
        return _combatBoost;
    }

    // TriggerPriority value to pin a flee-combat quest at while engaged with a whitelisted enemy.
    // The QuestRunner selects the active quest by TriggerPriority; a spook momentarily raises it to
    // c_Flee, which beats work — so holding it at c_Flee for the whole encounter keeps combat selected
    // (no oscillation) without exceeding genuine emergencies (AvoidImminentDanger=46) or changing how
    // she behaves vs a real threat (a draugr in range still flees via the same quest's per-target logic).
    private static int _triggerBoost = -1;
    internal static int GetTriggerBoost()
    {
        if (_triggerBoost < 0)
        {
            try { _triggerBoost = QuestPriority.c_Flee; }
            catch { _triggerBoost = 100; }
        }
        return _triggerBoost;
    }

    // --- per-instance spook-target memory ---
    // ShouldFight is read with _engagedEnemy frequently null (combat target not latched on the quest
    // data at getter time). _GetSpooked DOES carry the attacker, so we stash the last attacker per
    // FleeCombatQuestData instance (keyed by native pointer) and consult it as a fallback. Latest
    // spook wins, so if a non-whitelisted enemy (draugr) spooks after a wisp, the fallback sees the
    // draugr and the villager correctly still flees it. Short TTL so it can't keep them aggressive
    // long after the encounter ends.
    private struct SpookRecord { public IAttackTarget Target; public float Time; }
    private static readonly Dictionary<IntPtr, SpookRecord> _recentSpooks = new();
    private const float SpookMemorySeconds = 8f;

    internal static void RememberSpook(IntPtr key, IAttackTarget target)
    {
        if (key == IntPtr.Zero || target == null) return;
        _recentSpooks[key] = new SpookRecord { Target = target, Time = Time.realtimeSinceStartup };
    }

    // Most-recent spook target for this quest instance, or null if none / older than the TTL.
    internal static IAttackTarget? GetRememberedSpook(IntPtr key)
    {
        if (key == IntPtr.Zero) return null;
        if (_recentSpooks.TryGetValue(key, out var rec))
        {
            if (Time.realtimeSinceStartup - rec.Time <= SpookMemorySeconds) return rec.Target;
            _recentSpooks.Remove(key); // stale
        }
        return null;
    }

    // --- per-runner combat-quest registry (for the FindNextBestQuest arbiter patch) ---
    // FindNextBestQuest runs on the QuestRunner and has no quest/target context, so we stash — keyed by
    // runner pointer — this villager's FleeCombatQuest object + its quest-data pointer (for the spook
    // lookup). Populated/refreshed from _GetSpooked and the polled ShouldFight getter, both of which carry
    // FleeCombatQuestData (→ .QuestRunner, .Quest). Short TTL so we only steer the arbiter during a live
    // engagement; outside the window the entry expires and the runner behaves vanilla.
    private struct CombatRec { public Quest Quest; public IntPtr QuestDataPtr; public float Time; }
    private static readonly Dictionary<IntPtr, CombatRec> _runnerCombat = new();

    // Native object pointer for any IL2CPP wrapper. QuestRunner sits on the UnityEngine.Object chain;
    // because the csproj references the stripped unity-libs UnityEngine.CoreModule (not the interop one),
    // the compiler can't see QuestRunner -> Il2CppObjectBase, so a direct cast won't compile. At runtime
    // every interop wrapper IS an Il2CppObjectBase, so cast through `object` and read the pointer.
    internal static IntPtr PtrOf(object obj)
    {
        var b = obj as Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase;
        return b == null ? IntPtr.Zero : Il2CppInterop.Runtime.IL2CPP.Il2CppObjectBaseToPtr(b);
    }

    internal static void RememberVillagerCombat(QuestRunner runner, Quest combatQuest, IntPtr questDataPtr)
    {
        if (runner == null || combatQuest == null) return;
        IntPtr runnerKey = PtrOf(runner);
        if (runnerKey == IntPtr.Zero) return;
        _runnerCombat[runnerKey] = new CombatRec { Quest = combatQuest, QuestDataPtr = questDataPtr, Time = Time.realtimeSinceStartup };
    }

    // This villager's flee-combat quest + its quest-data pointer, if a fresh engagement is on record.
    internal static bool TryGetVillagerCombat(QuestRunner runner, out Quest combatQuest, out IntPtr questDataPtr)
    {
        combatQuest = null!; questDataPtr = IntPtr.Zero;
        IntPtr runnerKey = PtrOf(runner);
        if (runnerKey == IntPtr.Zero) return false;
        if (_runnerCombat.TryGetValue(runnerKey, out var rec))
        {
            if (Time.realtimeSinceStartup - rec.Time <= SpookMemorySeconds) { combatQuest = rec.Quest; questDataPtr = rec.QuestDataPtr; return true; }
            _runnerCombat.Remove(runnerKey); // stale
        }
        return false;
    }

    // --- suspended work quests (removed while engaged, re-added when the enemy is gone) ---
    // Keyed by runner pointer; holds the Quest objects we RemoveQuest'd so we can AddQuest them back.
    private static readonly Dictionary<IntPtr, List<Quest>> _suspended = new();

    internal static int SuspendedCount(QuestRunner runner)
    {
        IntPtr key = PtrOf(runner);
        return _suspended.TryGetValue(key, out var list) ? list.Count : 0;
    }

    // Returns true if this quest was newly stashed; false if it was already suspended for this runner
    // (guards against re-stashing the same quest if RemoveQuest doesn't clear ActiveQuest the same frame).
    internal static bool StashSuspended(QuestRunner runner, Quest quest)
    {
        if (quest == null) return false;
        IntPtr key = PtrOf(runner);
        if (key == IntPtr.Zero) return false;
        if (!_suspended.TryGetValue(key, out var list)) { list = new List<Quest>(); _suspended[key] = list; }
        IntPtr qp = PtrOf(quest);
        foreach (var q in list) if (PtrOf(q) == qp) return false; // already suspended
        list.Add(quest);
        return true;
    }

    // Hands back (and clears) the quests suspended for this runner; false if there were none.
    internal static bool TryRestoreSuspended(QuestRunner runner, out List<Quest>? quests)
    {
        quests = null;
        IntPtr key = PtrOf(runner);
        if (key == IntPtr.Zero) return false;
        if (_suspended.TryGetValue(key, out var list))
        {
            _suspended.Remove(key);
            quests = list;
            return true;
        }
        return false;
    }
}
