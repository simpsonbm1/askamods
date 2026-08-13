using System;
using HarmonyLib;
using SSSGame.UI;

namespace CraftFromStorageMod.Patches;

// v1.4.0: build-menu lifecycle for the personal (bench-free) crafting pull in HandcraftPull.cs.
// Confirmed signatures (Cecil 2026-08-13): SSSGame.UI.BuildMenu : ContextMenu declares
// Void OnActivate() and Void OnDeactivate(), both zero-parameter. It does NOT declare OnClosed(),
// so the close hook goes on OnDeactivate - patching an undeclared name would throw at patch time.
// Both are free of inventory-family parameter types, the family that natively crashes this game.
[HarmonyPatch(typeof(BuildMenu), nameof(BuildMenu.OnActivate))]
internal static class BuildMenuActivatePatch
{
    private static bool _logged;

    static void Postfix(BuildMenu __instance)
    {
        try
        {
            if (!_logged) { _logged = true; Plugin.Logger.LogInfo("[CFS] [CFS-HC] BuildMenu.OnActivate FIRED."); }
            HandcraftPull.OnBuildMenuOpened(__instance);
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"[CFS] [CFS-HC] BuildMenuActivatePatch error: {ex}");
        }
    }
}

[HarmonyPatch(typeof(BuildMenu), nameof(BuildMenu.OnDeactivate))]
internal static class BuildMenuDeactivatePatch
{
    private static bool _logged;

    static void Postfix()
    {
        try
        {
            if (!_logged) { _logged = true; Plugin.Logger.LogInfo("[CFS] [CFS-HC] BuildMenu.OnDeactivate FIRED."); }
            HandcraftPull.OnBuildMenuClosed();
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"[CFS] [CFS-HC] BuildMenuDeactivatePatch error: {ex}");
        }
    }
}
