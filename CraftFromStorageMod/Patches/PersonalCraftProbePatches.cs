using System;
using HarmonyLib;
using SandSailorStudio.Inventory;
using SandSailorStudio.UI;
using SSSGame;
using SSSGame.UI;
using UnityEngine;

namespace CraftFromStorageMod.Patches;

// v1.3.0 READ-ONLY probe: identify the PERSONAL (bench-free) crafting menu and its craft trigger.
//
// Why this exists. In-game 2026-08-13 the crafting table menu showed the mod's settlement-wide
// requirement count and crafted normally, while the personal crafting menu showed the player's own
// count and refused with "Cannot craft this item: Missing component". The session log recorded ONE
// CraftMenu.OnActivate for two menus opened five seconds apart, so the personal menu is not a
// SSSGame.UI.CraftMenu and nothing the mod does reaches it.
//
// Confirmed signatures (Cecil 2026-08-13, _explore/cecil_cfs_personal_menu.ps1 and
// _explore/cecil_cfs_personal_gate.ps1):
//   SSSGame.UI.CraftMenu : ContextMenu          - property SelectItemTabPage : CreateItemsTabPage
//   SSSGame.UI.PlayerMenu : ContextMenu         - Void OnActivate() / Void OnClosed(), zero-param
//   SSSGame.UI.CreateItemsTabPage : TabPage     - Void Show(Boolean, TabButton), virtual
//                                                 errorMessage : TMP_Text  (the red refusal line)
//                                                 AvailableBlueprints / UnavailableBlueprints
//   SSSGame.CraftBlueprint : Blueprint          - Void Activate(), zero-param
//   SandSailorStudio.Inventory.Blueprint : Item - Boolean Use(GameObject target)
//
// Every target here is free of the inventory-family parameter types (Item, ItemCollection,
// ItemEventContext) that cause the project's known plugin-load native crash. The craftability tests
// that DO take an ItemCollection - BlueprintInfo.CanBeUsed / _CheckParts / _CheckCost /
// GetAvailablePartsPercent - are therefore never patched, only reasoned about from the outside.
//
// READ-ONLY: every patch below logs through Plugin.Logger and nothing else. No inventory is touched,
// no gate result is altered, no __result is written.
internal static class PersonalCraftProbe
{
    // Set by the PlayerMenu lifecycle patches so the craftability-gate probe can say whether the
    // station gate the mod already hooks fires at all while the personal menu is on screen. If it
    // never fires, the personal menu uses a different gate and widening CheckOwnedRequirements can
    // never help it.
    internal static bool PlayerMenuOpen;

    private const int MaxTriggerLogs = 25;
    private const int MaxGateLogs = 10;
    private const int WalkDepthCap = 10;
    private const int WalkNodeCap = 400;

    private static int _triggerLogs;
    private static int _gateLogs;
    private static int _gateHitsWhileOpen;
    private static int _nodesWalked;

    // World-leave: drop the open flag and re-arm every rate limiter so a second world load produces
    // its own evidence. Project-wide gotcha - never carry per-world state across sessions.
    internal static void ClearWorldState()
    {
        PlayerMenuOpen = false;
        _triggerLogs = 0;
        _gateLogs = 0;
        _gateHitsWhileOpen = 0;
    }

    internal static void NoteGateHit()
    {
        if (!PlayerMenuOpen) return;
        _gateHitsWhileOpen++;
        if (_gateLogs >= MaxGateLogs) return;
        _gateLogs++;
        Plugin.Logger.LogInfo($"[CFS] [CFS-PCM] gate hit while the personal menu is OPEN " +
            $"[{_gateLogs}/{MaxGateLogs}]: CraftInteraction.CheckOwnedRequirements fired. " +
            $"totalWhileOpen={_gateHitsWhileOpen}");
    }

    internal static int GateHitsWhileOpen => _gateHitsWhileOpen;

    // Reports every CreateItemsTabPage found beneath a menu root. The PLURAL
    // GetComponentsInChildren<T>(bool) throws MissingMethodException through the interop trampoline
    // (project-wide gotcha), so the hierarchy is walked by hand with the singular generic.
    internal static void ReportTabPagesUnder(Transform root, string ownerLabel)
    {
        _nodesWalked = 0;
        int found = Walk(root, 0, ownerLabel);
        Plugin.Logger.LogInfo($"[CFS] [CFS-PCM] {ownerLabel}: {found} CreateItemsTabPage(s) found " +
            $"in its hierarchy ({_nodesWalked} node(s) walked, depth cap {WalkDepthCap}).");
    }

    private static int Walk(Transform t, int depth, string ownerLabel)
    {
        if (depth > WalkDepthCap || _nodesWalked >= WalkNodeCap) return 0;
        _nodesWalked++;

        int count = 0;
        try
        {
            var page = t.gameObject.GetComponent<CreateItemsTabPage>();
            if (page != null)
            {
                count++;
                string avail = "?", unavail = "?";
                try { var c = page.AvailableBlueprints; if (c != null) avail = c.Count.ToString(); } catch { }
                try { var c = page.UnavailableBlueprints; if (c != null) unavail = c.Count.ToString(); } catch { }
                Plugin.Logger.LogInfo($"[CFS] [CFS-PCM] {ownerLabel} -> CreateItemsTabPage on " +
                    $"node='{SafeName(t)}' depth={depth} available={avail} unavailable={unavail} " +
                    $"pageClass={Plugin.NativeClassName(page)}");
            }
        }
        catch { }

        int n;
        try { n = t.childCount; } catch { return count; }
        for (int i = 0; i < n; i++)
        {
            Transform? c = null;
            try { c = t.GetChild(i); } catch { }
            if (c != null) count += Walk(c, depth + 1, ownerLabel);
        }
        return count;
    }

    // Walks UP from a tab page to whichever ContextMenu owns it, and reports that menu's NATIVE
    // class name. A managed cast would lie here - the wrapper is the declared base type even when
    // the native object is the derived one (project-wide gotcha), so the native name is the only
    // trustworthy identity.
    internal static string ResolveOwningMenuClass(Transform? t)
    {
        int guard = 0;
        while (t != null && guard++ < 20)
        {
            try
            {
                // Fully qualified: bare ContextMenu is ambiguous with UnityEngine.ContextMenu.
                var menu = t.gameObject.GetComponent<SSSGame.UI.ContextMenu>();
                if (menu != null) return Plugin.NativeClassName(menu);
            }
            catch { }
            try { t = t.parent; } catch { return "unresolved(parent-threw)"; }
        }
        return "unresolved(no ContextMenu ancestor)";
    }

    internal static void LogTrigger(string what, string detail)
    {
        if (_triggerLogs >= MaxTriggerLogs) return;
        _triggerLogs++;
        Plugin.Logger.LogInfo($"[CFS] [CFS-PCM] craft trigger [{_triggerLogs}/{MaxTriggerLogs}] " +
            $"{what}: playerMenuOpen={PlayerMenuOpen} {detail}");
    }

    internal static string SafeName(Transform? t)
    {
        if (t == null) return "null";
        try { return t.gameObject.name; } catch { return "unreadable"; }
    }
}

// ---- PlayerMenu lifecycle. Zero-parameter virtuals, the same shape as the CraftMenu pair the mod
// already patches. Fire-verification is unconditional on the first call of each: a patch that never
// runs is indistinguishable from a wrong target, which is exactly the question this probe answers. ----
[HarmonyPatch(typeof(PlayerMenu), nameof(PlayerMenu.OnActivate))]
internal static class PlayerMenuActivateProbePatch
{
    static void Postfix(PlayerMenu __instance)
    {
        try
        {
            PersonalCraftProbe.PlayerMenuOpen = true;
            Plugin.Logger.LogInfo("[CFS] [CFS-PCM] PlayerMenu.OnActivate FIRED - personal menu is open.");

            Transform? root = null;
            try { root = __instance.transform; } catch { }
            if (root == null)
            {
                Plugin.Logger.LogWarning("[CFS] [CFS-PCM] PlayerMenu.OnActivate: transform unreadable - cannot walk for a recipe list.");
                return;
            }
            PersonalCraftProbe.ReportTabPagesUnder(root, "PlayerMenu");
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"[CFS] PlayerMenuActivateProbePatch error: {ex}");
        }
    }
}

[HarmonyPatch(typeof(PlayerMenu), nameof(PlayerMenu.OnClosed))]
internal static class PlayerMenuClosedProbePatch
{
    static void Postfix()
    {
        try
        {
            Plugin.Logger.LogInfo("[CFS] [CFS-PCM] PlayerMenu.OnClosed FIRED - personal menu closed. " +
                $"CheckOwnedRequirements fired {PersonalCraftProbe.GateHitsWhileOpen} time(s) while it was open.");
            PersonalCraftProbe.PlayerMenuOpen = false;
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"[CFS] PlayerMenuClosedProbePatch error: {ex}");
        }
    }
}

// ---- The recipe list itself. This runs for BOTH menus, so the owning-menu class name is the field
// that matters: it says which container the instance belongs to. Separate patch class from
// RecipeListUIPatch so the two diagnostics stay independently switchable. ----
[HarmonyPatch(typeof(CreateItemsTabPage), nameof(CreateItemsTabPage.Show))]
internal static class CreateItemsTabPageProbePatch
{
    static void Postfix(CreateItemsTabPage __instance, bool value, TabButton button)
    {
        try
        {
            Transform? t = null;
            try { t = __instance.transform; } catch { }

            string avail = "?", unavail = "?";
            try { var c = __instance.AvailableBlueprints; if (c != null) avail = c.Count.ToString(); } catch { }
            try { var c = __instance.UnavailableBlueprints; if (c != null) unavail = c.Count.ToString(); } catch { }

            string errorText = "null";
            try { var e = __instance.errorMessage; if (e != null) errorText = "'" + e.text + "'"; } catch { }

            Plugin.Logger.LogInfo($"[CFS] [CFS-PCM] CreateItemsTabPage.Show(value={value}) " +
                $"owningMenu={PersonalCraftProbe.ResolveOwningMenuClass(t)} " +
                $"node='{PersonalCraftProbe.SafeName(t)}' available={avail} unavailable={unavail} " +
                $"errorMessage={errorText} playerMenuOpen={PersonalCraftProbe.PlayerMenuOpen}");
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"[CFS] CreateItemsTabPageProbePatch error: {ex}");
        }
    }
}

// ---- Does the station gate the mod already widens fire at all while the personal menu is open?
// A second patch on a method the mod already patches is safe; Harmony composes postfixes. ----
[HarmonyPatch(typeof(CraftInteraction), nameof(CraftInteraction.CheckOwnedRequirements))]
internal static class CheckOwnedRequirementsProbePatch
{
    static void Postfix()
    {
        try { PersonalCraftProbe.NoteGateHit(); }
        catch (Exception ex) { Plugin.Logger.LogError($"[CFS] CheckOwnedRequirementsProbePatch error: {ex}"); }
    }
}

// ---- Craft trigger. CraftBlueprint.Activate() is zero-parameter and the safest target in the game
// for this question. Whichever method fires when the player presses the craft control is the
// personal-menu equivalent of BeginCraftingSequence, i.e. where a just-in-time pull would have to
// hang.
//
// DEAD-END, confirmed in-game 2026-08-13 (v1.3.0): DO NOT patch
// SandSailorStudio.Inventory.Blueprint.Use(GameObject). A postfix on it throws inside the interop
// trampoline the moment the chainloader finishes, before any user code runs, and the game never
// reaches a playable state:
//   [Error :Il2CppInterop] During invoking native->managed trampoline
//   Exception: System.NullReferenceException: Object reference not set to an instance of an object.
//      at DMD<SandSailorStudio.Inventory.Blueprint::Use>(Blueprint this, GameObject target)
// The try/catch inside the postfix cannot help - the throw is in the marshalling layer, not in the
// patch body. GameObject is not an inventory-family parameter type, so the deciding factor is the
// DECLARING type: Blueprint derives from Item. Blueprint as a PARAMETER remains safe, which is why
// CheckOwnedRequirements(Blueprint, IInteractionAgent) has been patched since v0.1.0 without
// incident. Declaring type and parameter type are separate risks. ----
[HarmonyPatch(typeof(CraftBlueprint), nameof(CraftBlueprint.Activate))]
internal static class CraftBlueprintActivateProbePatch
{
    static void Postfix(CraftBlueprint __instance)
    {
        try
        {
            PersonalCraftProbe.LogTrigger("CraftBlueprint.Activate()",
                $"bp='{GateLog.SafeBlueprintName(__instance)}' bpClass={Plugin.NativeClassName(__instance)}");
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"[CFS] CraftBlueprintActivateProbePatch error: {ex}");
        }
    }
}
