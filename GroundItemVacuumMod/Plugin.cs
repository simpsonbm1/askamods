using System.Collections.Generic;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Unity.IL2CPP;
using BepInEx.Logging;
using HarmonyLib;
using Il2CppInterop.Runtime.Injection;
using SSSGame;
using UnityEngine;

namespace GroundItemVacuumMod;

[BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
public class Plugin : BasePlugin
{
    internal static ManualLogSource Logger = null!;

    // --- config ---
    // Tracker re-reads the file periodically so settings can be changed mid-session (SeedScout pattern)
    internal static ConfigFile? Cfg;
    internal static ConfigEntry<string> VacuumHotkey = null!;
    internal static ConfigEntry<bool> DryRun = null!;
    internal static ConfigEntry<float> Radius = null!;
    internal static ConfigEntry<bool> VacuumEntireWorld = null!;
    internal static ConfigEntry<string> ExcludeCategories = null!;
    internal static ConfigEntry<string> ExcludeItems = null!;
    internal static ConfigEntry<string> OnlyItems = null!;
    internal static ConfigEntry<bool> HostOnly = null!;
    internal static ConfigEntry<float> AutoVacuumMinutes = null!;
    internal static ConfigEntry<bool> Diagnostics = null!;
    internal static ConfigEntry<bool> TraceEachItem = null!;
    internal static ConfigEntry<bool> IncludeCorpses = null!;
    internal static ConfigEntry<string> ExcludeCorpseNames = null!;

    // --- live state ---
    // We maintain our OWN set of live ground items via DynamicItemObject.OnEnable/OnDisable
    // (Unity-guaranteed lifecycle messages -> inlining-immune, and they bracket exactly the window
    // in which the object is a live, safe-to-touch ground item). We DELIBERATELY do NOT walk the
    // game's intrusive linked list (_head -> NextDynamicObject): traversing it across streaming
    // cells and calling native getters on a node mid-teardown caused a native access-violation
    // crash in v1.0.0 (coreclr+0x1d1fdd fatal-error signature). Cleared on world-leave so no stale
    // per-world wrapper survives a reload (the documented stale-wrapper native-crash gotcha).
    internal static readonly HashSet<DynamicItemObject> TrackedItems = new();
    internal static bool TrackingConfirmed;
    internal static PlayerCharacter? LocalPlayer;

    // Enemy/animal corpses are dead SSSGame.Monster instances awaiting despawn (confirmed
    // in-game 2026-07-18: CharacterRemains is the PLAYER-corpse system - Creature is not a
    // Character subclass, so Character._CreateCharacterRemains never runs for enemies). Tracks
    // Monsters ONLY - Den (spawner building; despawning one would PERMANENTLY destroy it, since
    // defeated dens legitimately read dead) and Pet are excluded by construction: we simply never
    // patch their lifecycle methods, so they're never added to this set.
    internal static readonly HashSet<Monster> TrackedCorpses = new();
    internal static bool CorpseTrackingConfirmed;

    public override void Load()
    {
        Logger = base.Log;
        Cfg = Config;

        VacuumHotkey = Config.Bind(
            "General", "VacuumHotkey", "n",
            "Hotkey (a letter or a Unity KeyCode name) that triggers a vacuum sweep. Pressed by the host.");

        DryRun = Config.Bind(
            "General", "DryRun", true,
            "SAFE MODE. When true the hotkey only SCANS: it logs and on-screen-reports what WOULD be removed (counts by item name) and removes NOTHING. Set to false to actually delete the ground items. Leave this true until you've reviewed a scan and set your exclusions.");

        Radius = Config.Bind(
            "General", "Radius", 60.0f,
            "Only ground items within this many meters of the player are affected. Ignored when VacuumEntireWorld is true.");

        VacuumEntireWorld = Config.Bind(
            "General", "VacuumEntireWorld", false,
            "When true, ignore Radius and consider every loose ground item in the whole world. Aggressive - review a DryRun scan first.");

        ExcludeCategories = Config.Bind(
            "Filters", "ExcludeCategories", "Weapon,Armor,Clothing,Tool,Equipment",
            "Comma-separated, case-insensitive substrings matched against each item's category name AND its parent-category chain. Any match spares the item. Protects gear from being vacuumed. (Category names are dumped by a DryRun scan when Diagnostics is on - tune this list from that dump.)");

        ExcludeItems = Config.Bind(
            "Filters", "ExcludeItems", "",
            "Comma-separated, case-insensitive substrings matched against the item's name. Any match spares the item. E.g. 'Iron,Gold' to never vacuum those.");

        OnlyItems = Config.Bind(
            "Filters", "OnlyItems", "",
            "Optional allow-list. If non-empty, ONLY items whose name contains one of these comma-separated, case-insensitive substrings are vacuumed (exclusions still apply on top). Empty = every item is a candidate. E.g. 'Stick,Log,Branch,Twig' to only clear wood clutter.");

        HostOnly = Config.Bind(
            "General", "HostOnly", true,
            "Only the host/server may execute a real removal (co-op safety). A DryRun scan is allowed for anyone (read-only).");

        AutoVacuumMinutes = Config.Bind(
            "General", "AutoVacuumMinutes", 0.0f,
            "If greater than 0, automatically run a sweep this often (in minutes) using the same settings (respects DryRun, Radius, exclusions, HostOnly). 0 = off. Opt-in.");

        Diagnostics = Config.Bind(
            "General", "Diagnostics", false,
            "Verbose logging: per-sweep breakdown of ground items by name and category to the BepInEx log. Shipped default false - flip to true when tuning filters or troubleshooting.");

        TraceEachItem = Config.Bind(
            "General", "TraceEachItem", false,
            "Debug: log each item step-by-step during a sweep so a hard-crash log pinpoints the failing item/step. Very verbose. Shipped default false - only enable when investigating a crash.");

        IncludeCorpses = Config.Bind(
            "Corpses", "IncludeCorpses", true,
            "When true, a sweep also sweeps dead enemy/animal corpses (dead Monster creatures) within the radius via the game's own network despawn; their unharvested loot is lost. Player and villager corpses are a different game system and are never touched. Default true during development, will be flipped to false before any public release.");

        ExcludeCorpseNames = Config.Bind(
            "Corpses", "ExcludeCorpseNames", "",
            "Comma-separated, case-insensitive substrings matched against the dead creature's name (as shown when targeting it, e.g. 'Wight'). Any match spares that corpse.");

        ClassInjector.RegisterTypeInIl2Cpp<VacuumTracker>();

        var go = new GameObject("GroundItemVacuumMod_Tracker");
        UnityEngine.Object.DontDestroyOnLoad(go);
        go.AddComponent<VacuumTracker>();

        var harmony = new Harmony(MyPluginInfo.PLUGIN_GUID);
        harmony.PatchAll();

        Logger.LogInfo($"GroundItemVacuumMod v{MyPluginInfo.PLUGIN_VERSION} loaded. Hotkey='{VacuumHotkey.Value}', DryRun={DryRun.Value}, Radius={Radius.Value}m, EntireWorld={VacuumEntireWorld.Value}, AutoMinutes={AutoVacuumMinutes.Value}, IncludeCorpses={IncludeCorpses.Value}");
    }
}
