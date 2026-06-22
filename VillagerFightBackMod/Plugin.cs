using System;
using System.Collections.Generic;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Unity.IL2CPP;
using BepInEx.Logging;
using HarmonyLib;
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
                       $"DebugLogging={DebugLogging.Value}");
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
}
