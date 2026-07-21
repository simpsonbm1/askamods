using SSSGame.UI;

namespace CraftFromStorageMod;

// v0.4.0 (idea-17 UI follow-up): tracks the currently-open CraftMenu so the availability-UI patches
// (Patches/UiAvailabilityPatches.cs) can scope their rewrite to panels inside an active crafting menu
// only, and so CraftUiAvailability can resolve the active station's own inventory
// (CraftMenu.GetCraftStationInventory()) for the double-count-trap correction (see CraftUiAvailability.cs).
//
// Captured in CraftMenu.OnActivate, cleared in CraftMenu.OnClosed AND on world-leave
// (CraftWatcher.ClearWorldState -> here) - project-wide gotcha: never cache interop wrappers of
// per-world objects across world sessions.
internal static class CraftUiState
{
    internal static CraftMenu? ActiveCraftMenu;

    internal static void ClearWorldState()
    {
        if (ActiveCraftMenu != null)
        {
            ActiveCraftMenu = null;
            Plugin.Logger.LogInfo("[CFS] CraftUiState: world-leave - active craft menu reference cleared.");
        }
    }
}
