using System;
using System.Runtime.InteropServices;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Unity.IL2CPP;
using BepInEx.Logging;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using Il2CppInterop.Runtime.Injection;
using SSSGame;
using UnityEngine;

namespace CraftFromStorageMod;

// Phase 0 of idea-17 (NEW_MOD_IDEAS_PLAN.md "17. Craft from settlement storage"). READ-ONLY
// diagnostic spike: every patch below only LOGS - none of them write inventory, transfer items,
// or flip a gate result. Purpose: trace the live player-crafting call flow (CraftInteraction
// family) so Phase 1 (the actual storage-pull mod) can be designed against verified facts instead
// of Cecil-inferred guesses. All log lines carry the "[CFS]" prefix.
[BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
public class Plugin : BasePlugin
{
    internal static ManualLogSource Logger = null!;
    internal static ConfigFile? Cfg;

    // --- config: Trace (all default true - unverified diagnostic mod, project rule) ---
    internal static ConfigEntry<bool> TraceCheckOwnedRequirements = null!;
    internal static ConfigEntry<bool> TraceCheckOwnedBlueprintManifest = null!;
    internal static ConfigEntry<bool> TraceBeginCraftingSequence = null!;
    internal static ConfigEntry<bool> TraceOnCraftingSuccess = null!;
    internal static ConfigEntry<bool> TraceActivateBlueprint = null!;
    internal static ConfigEntry<bool> TraceRecipeListUI = null!;

    // --- config: Census ---
    internal static ConfigEntry<string> CensusHotkey = null!;
    internal static ConfigEntry<bool> CensusTryQuerySettlementResources = null!;

    // --- live state ---
    internal static PlayerCharacter? LocalPlayer;

    public override void Load()
    {
        Logger = base.Log;
        Cfg = Config;

        TraceCheckOwnedRequirements = Config.Bind(
            "Trace", "TraceCheckOwnedRequirements", true,
            "Postfix-log CraftInteraction.CheckOwnedRequirements(Blueprint, IInteractionAgent). This target is " +
            "public but NON-virtual, so it may have been inlined by the IL2CPP AOT compiler and this patch may " +
            "silently never fire - that is itself a key Phase 0 finding. Logs agent native class, blueprint name, and __result.");

        TraceCheckOwnedBlueprintManifest = Config.Bind(
            "Trace", "TraceCheckOwnedBlueprintManifest", true,
            "HIGHEST RISK patch. Its target takes an ItemManifest parameter; inventory-family parameter types " +
            "have caused native crashes at plugin load in this game. If the game hard-crashes on startup with " +
            "this mod installed, set this to false FIRST.");

        TraceBeginCraftingSequence = Config.Bind(
            "Trace", "TraceBeginCraftingSequence", true,
            "Prefix-log BeginCraftingSequence(InteractionSession) on CraftInteraction, AnvilInteraction, and " +
            "DyeingInteraction (each declares its own override - all three are patched together under this flag). " +
            "Logs which type fired, the session's native class name (reveals player vs villager session), and useAgentInventory.");

        TraceOnCraftingSuccess = Config.Bind(
            "Trace", "TraceOnCraftingSuccess", true,
            "Prefix AND Postfix snapshot of the station's ItemInventory around _OnCraftingSuccess(IInteractionAgent) " +
            "on CraftInteraction and DyeingInteraction (both patched together under this flag) - shows where " +
            "ingredient consumption actually happens (PRE vs POST totals + per-item breakdown).");

        TraceActivateBlueprint = Config.Bind(
            "Trace", "TraceActivateBlueprint", true,
            "Postfix-log CraftInteraction.ActivateBlueprint(CraftBlueprint) - blueprint name + __result.");

        TraceRecipeListUI = Config.Bind(
            "Trace", "TraceRecipeListUI", true,
            "Postfix-log SSSGame.UI.CreateItemsTabPage.Show(bool, TabButton) - AvailableBlueprints/UnavailableBlueprints " +
            "counts at recipe-list-open time (may be null/empty at this point, which is itself informative).");

        CensusHotkey = Config.Bind(
            "Census", "CensusHotkey", "F12",
            "Hotkey (a Unity KeyCode name, parsed via Enum.TryParse<KeyCode>) that dumps a read-only settlement " +
            "storage census to the log: per-structure container contents and a settlement-wide item total. " +
            "F6-F11 are taken by other mods in this repo - do not change the default.");

        CensusTryQuerySettlementResources = Config.Bind(
            "Census", "CensusTryQuerySettlementResources", false,
            "LEAVE THIS FALSE. Settlement.QuerySettlementResources() FROZE the game when the census called it " +
            "(confirmed in-game 2026-07-20, v0.1.1: Windows logged AppHangB1 - a hang, not a crash; no managed " +
            "exception is possible and try/catch cannot rescue it). The census now uses the proven per-structure " +
            "GetStructures() walk instead. Setting this true re-attempts the hanging call, bracketed by log " +
            "markers that isolate whether QuerySettlementResources() or GetTotalQuantity() is the one that hangs.");

        ClassInjector.RegisterTypeInIl2Cpp<StorageCensus>();
        var go = new GameObject("CraftFromStorageMod_Census");
        UnityEngine.Object.DontDestroyOnLoad(go);
        go.AddComponent<StorageCensus>();

        var harmony = new Harmony(MyPluginInfo.PLUGIN_GUID);

        // Lifecycle (local-player capture) is always patched - safe param types (PlayerCharacter only),
        // no inventory-family exposure, needed for the census hotkey regardless of which Trace flags are on.
        harmony.PatchAll(typeof(Patches.PlayerSpawnedPatch));
        harmony.PatchAll(typeof(Patches.PlayerDespawnedPatch));

        // Per-patch config gating (NOT harmony.PatchAll() bare): TraceCheckOwnedBlueprintManifest is a
        // known native-crash risk (inventory-family ItemManifest parameter on the target method). If it
        // crashes the game at plugin load, the user must be able to disable JUST that patch by editing
        // the config file without a rebuild (rebuilds are expensive here - Smart App Control).
        if (TraceCheckOwnedRequirements.Value)
            harmony.PatchAll(typeof(Patches.CheckOwnedRequirementsPatch));
        else
            Logger.LogInfo("[CFS] TraceCheckOwnedRequirements=false - CheckOwnedRequirements patch NOT applied.");

        if (TraceCheckOwnedBlueprintManifest.Value)
            harmony.PatchAll(typeof(Patches.CheckOwnedBlueprintManifestPatch));
        else
            Logger.LogInfo("[CFS] TraceCheckOwnedBlueprintManifest=false - _CheckOwnedBlueprintManifest patch NOT applied.");

        if (TraceBeginCraftingSequence.Value)
        {
            harmony.PatchAll(typeof(Patches.BeginCraftingSequenceCraftPatch));
            harmony.PatchAll(typeof(Patches.BeginCraftingSequenceAnvilPatch));
            harmony.PatchAll(typeof(Patches.BeginCraftingSequenceDyeingPatch));
        }
        else
        {
            Logger.LogInfo("[CFS] TraceBeginCraftingSequence=false - BeginCraftingSequence patches NOT applied.");
        }

        if (TraceOnCraftingSuccess.Value)
        {
            harmony.PatchAll(typeof(Patches.OnCraftingSuccessCraftPatch));
            harmony.PatchAll(typeof(Patches.OnCraftingSuccessDyeingPatch));
        }
        else
        {
            Logger.LogInfo("[CFS] TraceOnCraftingSuccess=false - _OnCraftingSuccess patches NOT applied.");
        }

        if (TraceActivateBlueprint.Value)
            harmony.PatchAll(typeof(Patches.ActivateBlueprintPatch));
        else
            Logger.LogInfo("[CFS] TraceActivateBlueprint=false - ActivateBlueprint patch NOT applied.");

        if (TraceRecipeListUI.Value)
            harmony.PatchAll(typeof(Patches.RecipeListUIPatch));
        else
            Logger.LogInfo("[CFS] TraceRecipeListUI=false - CreateItemsTabPage.Show patch NOT applied.");

        Logger.LogInfo($"[CFS] CraftFromStorageMod v{MyPluginInfo.PLUGIN_VERSION} loaded (READ-ONLY diagnostic spike - " +
            $"changes NO game state). CensusHotkey='{CensusHotkey.Value}'.");
    }

    // Managed casts LIE for interop objects materialized under a base declared type (project-wide
    // gotcha) - always read the NATIVE class name via IL2CPP.il2cpp_object_get_class, never as/is.
    // Shared by GatePatches.cs and StorageCensus.cs. Falls back to the managed wrapper's own type
    // name for non-Il2Cpp objects.
    internal static string NativeClassName(object? obj)
    {
        if (obj == null) return "null";
        try
        {
            if (obj is Il2CppObjectBase b)
            {
                IntPtr cls = IL2CPP.il2cpp_object_get_class(b.Pointer);
                return Marshal.PtrToStringAnsi(IL2CPP.il2cpp_class_get_name(cls)) ?? "?";
            }
        }
        catch { }
        return obj.GetType().Name;
    }
}
