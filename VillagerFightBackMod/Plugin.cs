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
                       $"BoostCombatPriority={BoostCombatPriority.Value}, PreventFlee={PreventFlee.Value}, " +
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
}
