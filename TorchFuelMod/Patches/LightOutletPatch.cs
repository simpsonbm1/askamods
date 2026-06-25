using System;
using HarmonyLib;
using SSSGame;

namespace TorchFuelMod.Patches;

// LightOutlet is the "light duty" task dispatcher attached to every fire that exists to give off
// light — free-standing torches, braziers, AND fires built into buildings (e.g. the tavern campfire).
// It wraps the same FireStructure the fuel lives on (LightOutlet.fireStructure). Cooking stations,
// forges and kilns use CookingOutlet/WarmthOutlet instead and have no LightOutlet, so tracking by
// this component naturally keeps base lighting fueled without touching crafting fires.
//
// This is the escape hatch for fires the name list (FireStructurePatch) can't catch because their
// owning Structure is the whole building. Gated behind the KeepAllLightSources config (default off).
//
// NOTE: cave wall sconces are NOT covered here — they are SSSGame.CaveTorchOutlet, an equipment-item
// system with no FireStructure/fuel volume, so there is nothing for this mod to top off.
[HarmonyPatch(typeof(LightOutlet), nameof(LightOutlet.Initialize))]
internal static class LightOutletPatch
{
    static void Postfix(LightOutlet __instance, Structure ownerStructure)
    {
        try
        {
            if (__instance == null) return;

            var fire = __instance.fireStructure;
            string? structureName = ownerStructure != null ? ownerStructure.StructureName : null;

            if (Plugin.LogAllFireStructures.Value)
                Plugin.Logger.LogInfo($"[TorchFuelMod][diag] LightOutlet owner='{structureName}' hasFire={(fire != null)}");

            if (!Plugin.KeepAllLightSources.Value) return;

            Plugin.TrackFireStructure(fire, $"light source '{structureName ?? "(unnamed)"}'");
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"[TorchFuelMod] LightOutletPatch: {ex}");
        }
    }
}
