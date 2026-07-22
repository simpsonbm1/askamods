using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using Il2CppInterop.Runtime.Injection;
using UnityEngine;

namespace LocaleAuditMod;

// Throwaway diagnostic probe (NOT for Nexus). Answers one question in a single run:
// for every game entity our mods currently identify by its TRANSLATED display string, what is the
// locale-invariant identity to key on instead?
//
// Background: five shipped mods gate behaviour on ItemInfo.Name / ItemCategoryInfo.Name /
// Structure.DefaultName / Creature.GetTargetName(), all of which resolve through LocalizationManager
// and therefore only match in English. Two of the five were reported broken by non-English players
// on Nexus; the other three were found by audit and have never been reported.
[BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
public class Plugin : BasePlugin
{
    internal static ManualLogSource Logger = null!;

    internal static ConfigEntry<string> AuditHotkeyRaw = null!;
    internal static ConfigEntry<bool> DumpAllItems = null!;
    internal static ConfigEntry<bool> CaptureCreatures = null!;
    internal static ConfigEntry<int> MaxTargetExamples = null!;
    internal static ConfigEntry<string> SpawnerHotkeyRaw = null!;
    internal static ConfigEntry<int> SpawnerDumpMax = null!;

    internal static KeyCode HotKey = KeyCode.F5;
    internal static KeyCode SpawnerKey = KeyCode.F6;

    public override void Load()
    {
        Logger = base.Log;

        AuditHotkeyRaw = Config.Bind(
            "LocaleAudit", "AuditHotkey", "F5",
            "Key that dumps the locale-audit tables to this log. F5 is the only function key not " +
            "already claimed by another mod in this repo (F6/F7/F9/F10/F11/F12 are taken).");

        DumpAllItems = Config.Bind(
            "LocaleAudit", "DumpAllItems", true,
            "Dump the full per-item table. The deduped CATEGORIES and TARGETS sections print " +
            "regardless; set this false if the full table makes the log unwieldy.");

        CaptureCreatures = Config.Bind(
            "LocaleAudit", "CaptureCreatures", true,
            "Record creature identity via a postfix on Creature.Spawned(). Set false to skip the " +
            "Harmony patch entirely - the item/category/structure tables are unaffected either way.");

        MaxTargetExamples = Config.Bind(
            "LocaleAudit", "MaxTargetExamples", 12,
            "Per probe token, how many matching rows to print under the TARGETS section. The match " +
            "COUNT is always reported in full regardless of this cap.");

        SpawnerHotkeyRaw = Config.Bind(
            "LocaleAudit", "SpawnerHotkey", "F6",
            "Key that dumps nearby PopulationSpawners (bear dens / spires are these, not SSSGame.Den) — "
            + "position, what creature each spawns (invariant dataSheet), live count. Stand at a bear "
            + "den or spire and press it. May collide with another mod's F6, but this is a dev probe run "
            + "deliberately; change it here if that is a problem.");

        SpawnerDumpMax = Config.Bind(
            "LocaleAudit", "SpawnerDumpMax", 40,
            "How many nearest spawners to print per spawner-dump.");

        if (!Enum.TryParse<KeyCode>(AuditHotkeyRaw.Value, true, out HotKey))
        {
            HotKey = KeyCode.F5;
            Logger.LogWarning($"[LocaleAudit] Unrecognised AuditHotkey '{AuditHotkeyRaw.Value}' - using F5.");
        }

        if (!Enum.TryParse<KeyCode>(SpawnerHotkeyRaw.Value, true, out SpawnerKey))
        {
            SpawnerKey = KeyCode.F6;
            Logger.LogWarning($"[LocaleAudit] Unrecognised SpawnerHotkey '{SpawnerHotkeyRaw.Value}' - using F6.");
        }

        ClassInjector.RegisterTypeInIl2Cpp<AuditRunner>();
        var go = new GameObject("LocaleAuditMod_Runner");
        UnityEngine.Object.DontDestroyOnLoad(go);
        go.AddComponent<AuditRunner>();

        // Only wire the creature patch when it is actually wanted, so the toggle removes the patch
        // rather than merely short-circuiting inside it.
        if (CaptureCreatures.Value)
        {
            new Harmony(MyPluginInfo.PLUGIN_GUID).PatchAll();
            Logger.LogInfo("[LocaleAudit] Creature.Spawned capture ARMED.");
        }
        else
        {
            Logger.LogInfo("[LocaleAudit] CaptureCreatures=false - creature section will be empty.");
        }

        Logger.LogInfo($"LocaleAuditMod v{MyPluginInfo.PLUGIN_VERSION} loaded. "
                       + $"Load a world, then press {HotKey} for the locale-audit tables, "
                       + $"or {SpawnerKey} (standing at a bear den / spire) to dump nearby spawners.");
    }
}

// Registered MonoBehaviour + Update() polling - the project's standard input idiom (game Action
// events cannot be subscribed through this interop layer).
public class AuditRunner : MonoBehaviour
{
    private void Update()
    {
        try
        {
            if (Input.GetKeyDown(Plugin.HotKey)) AuditDump.Run();
            if (Input.GetKeyDown(Plugin.SpawnerKey)) SpawnerDump.Run();
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"[LocaleAudit] runner error: {ex}");
        }
    }
}
