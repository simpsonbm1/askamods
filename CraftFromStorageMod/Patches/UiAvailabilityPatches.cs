using System;
using HarmonyLib;
using SSSGame.UI;

namespace CraftFromStorageMod.Patches;

// v0.4.0 (idea-17 UI follow-up): the settlement-stock requirement-UI feature. Purely additive - folds
// cached settlement stock into the crafting menu's per-ingredient "have/need" display text
// (ItemThumbnailPanel.availability). Writes NO game state; the pull/verify/sweep-back logic in
// CraftTransfer.cs is untouched. See CraftUiAvailability.cs for the math/parsing/rate-limited
// diagnostics and CraftUiState.cs for the captured-menu reference this depends on.
//
// v0.5.0: the ItemThumbnailPanel postfixes below are now DIAGNOSTICS ONLY (fire-verification +
// scoping evidence) - the actual write moved to CraftUiPoller.cs, a polling MonoBehaviour, because
// count.text is still the prefab placeholder at postfix time (see CraftUiAvailability.cs header).
//
// Confirmed signatures (Cecil 2026-07-20, _explore/cecil_craft_ui_out.txt, cecil_craft_ui2_out.txt):
//   SSSGame.UI.CraftMenu : ContextMenu
//     Void OnActivate() [virt]
//     Void OnClosed() [virt]
//     InventoryComponent GetCraftStationInventory()
//   SSSGame.UI.ItemThumbnailPanel : UnityEngine.MonoBehaviour
//     Void _UpdateAvailablility()        - zero-param (note the game's own misspelling)
//     Void _UpdateAvailabilityStatus()   - zero-param
// Both ItemThumbnailPanel targets are zero-parameter, avoiding the inventory-family plugin-load-crash
// class (project-wide gotcha - see CLAUDE.md). Do NOT patch _OnContainerItemAdded/_OnContainerItemRemoved
// on this same type - both take ItemCollection/Item/ItemEventContext parameters (confirmed crash risk
// class); calling into ItemThumbnailPanel is fine, only patching those two is fatal.

// ---- CraftMenu lifecycle: captures/clears the active menu so CraftUiAvailability can scope its
// rewrite to panels inside an open crafting menu, and resolve the active station's own inventory. ----
[HarmonyPatch(typeof(CraftMenu), nameof(CraftMenu.OnActivate))]
internal static class CraftMenuActivatePatch
{
    private static bool _logged;

    static void Postfix(CraftMenu __instance)
    {
        try
        {
            CraftUiState.ActiveCraftMenu = __instance;
            if (!_logged) { _logged = true; Plugin.Logger.LogInfo("[CFS] CraftMenu.OnActivate FIRED - active craft menu captured."); }
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"[CFS] CraftMenuActivatePatch error: {ex}");
        }
    }
}

[HarmonyPatch(typeof(CraftMenu), nameof(CraftMenu.OnClosed))]
internal static class CraftMenuClosedPatch
{
    private static bool _logged;

    static void Postfix(CraftMenu __instance)
    {
        try
        {
            if (!_logged) { _logged = true; Plugin.Logger.LogInfo("[CFS] CraftMenu.OnClosed FIRED - active craft menu will be cleared."); }
            if (CraftUiState.ActiveCraftMenu == __instance)
                CraftUiState.ActiveCraftMenu = null;
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"[CFS] CraftMenuClosedPatch error: {ex}");
        }

        // v0.5.0: the poller's per-label last-written record must not survive a menu close (panels
        // are pooled/reused - see CraftUiAvailability.ClearOnMenuClose) - reopening starts fresh.
        try { CraftUiAvailability.ClearOnMenuClose(); }
        catch (Exception ex) { Plugin.Logger.LogError($"[CFS] CraftMenuClosedPatch ClearOnMenuClose error: {ex}"); }
    }
}

// ---- ItemThumbnailPanel availability text: BOTH possible update methods are patched; fire-verified
// via CraftUiAvailability's own once-each "FIRED" log (AOT inlining is the top risk on both - a patch
// that never fires must be visible in the log, not silent). ----
[HarmonyPatch(typeof(ItemThumbnailPanel), "_UpdateAvailablility")]
internal static class UpdateAvailablilityPatch
{
    static void Postfix(ItemThumbnailPanel __instance)
    {
        try { CraftUiAvailability.OnUpdateAvailability(__instance, "_UpdateAvailablility"); }
        catch (Exception ex) { Plugin.Logger.LogError($"[CFS] UpdateAvailablilityPatch: {ex}"); }
    }
}

[HarmonyPatch(typeof(ItemThumbnailPanel), "_UpdateAvailabilityStatus")]
internal static class UpdateAvailabilityStatusPatch
{
    static void Postfix(ItemThumbnailPanel __instance)
    {
        try { CraftUiAvailability.OnUpdateAvailability(__instance, "_UpdateAvailabilityStatus"); }
        catch (Exception ex) { Plugin.Logger.LogError($"[CFS] UpdateAvailabilityStatusPatch: {ex}"); }
    }
}
