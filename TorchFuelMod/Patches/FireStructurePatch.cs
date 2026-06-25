using System;
using HarmonyLib;
using SSSGame;

namespace TorchFuelMod.Patches;

// Initialize(Structure) fires once per FireStructure as it spawns/loads, with the owning
// Structure already attached — exactly where we can read the structure's name and decide
// whether it's a torch we should keep fueled. Filtering by name here (rather than by which
// AI outlet component is attached) keeps non-torch fires — campfires, forges, kilns, which
// also use FireStructure — unaffected.
[HarmonyPatch(typeof(FireStructure), nameof(FireStructure.Initialize))]
internal static class FireStructurePatch
{
    static void Postfix(FireStructure __instance, Structure ownerStructure)
    {
        try
        {
            string? defaultName = ownerStructure != null ? ownerStructure.DefaultName : null;
            string? structureName = ownerStructure != null ? ownerStructure.StructureName : null;
            string? goName = null;
            try { goName = __instance != null && __instance.gameObject != null ? __instance.gameObject.name : null; }
            catch { /* gameObject may be unavailable mid-spawn; non-fatal */ }

            if (Plugin.LogAllFireStructures.Value)
                Plugin.Logger.LogInfo($"[TorchFuelMod][diag] FireStructure default='{defaultName}' name='{structureName}' go='{goName}'");

            // Match the owning structure's display name OR the fire's own GameObject name — the latter
            // catches fires whose owner is a building (e.g. a campfire whose owner is "Tavern") but whose
            // own object is still named like a torch/fire.
            if (Plugin.IsTargetStructure(defaultName) ||
                Plugin.IsTargetStructure(structureName) ||
                Plugin.IsTargetStructure(goName))
            {
                Plugin.TrackFireStructure(__instance, $"'{structureName ?? defaultName ?? goName}'");
            }
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"[TorchFuelMod] FireStructurePatch: {ex}");
        }
    }
}
