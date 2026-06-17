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
            if (ownerStructure == null) return;
            if (!Plugin.IsTargetStructure(ownerStructure.DefaultName) &&
                !Plugin.IsTargetStructure(ownerStructure.StructureName))
                return;

            if (!Plugin.TrackedFireStructures.Contains(__instance))
            {
                Plugin.TrackedFireStructures.Add(__instance);
                Plugin.Logger.LogInfo($"[TorchFuelMod] Tracking '{ownerStructure.StructureName}' for infinite fuel.");
            }
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"[TorchFuelMod] FireStructurePatch: {ex}");
        }
    }
}
