using System;
using HarmonyLib;
using SSSGame;
using SandSailorStudio.Attributes;  // VariableAttribute

namespace TorchFuelMod.Patches;

// READ-ONLY DIAGNOSTIC (gated behind config LogBurnableBuildings, default off).
//
// Maps every smithing/coal building to (a) its in-game display name and (b) the fuel mechanism it uses,
// so we can confirm which building "requires coal" and which lever keeps it fueled. Two mechanisms exist:
//   * FireStructure fuel volume (Rpc_AddFuel)  — bellows fire, forge fire, charcoal pyre
//   * coal-item _fuelVAttr (VariableAttribute)  — the bloomery's kiln (NOT a FireStructure)
//
// Each target's Initialize(Structure ownerStructure) is a safe single-reference-param signature (no by-ref
// trampoline hazard). Patches are postfix + fully try/caught.
//
// CRASH NOTE (v1.2.1): Initialize also fires during early-boot prefab/pool init, where the instance is NOT
// fully wired. Reading simple property getters there is fine (ForgeInteraction.fireStructure read null
// safely at boot), but calling a *method* that walks internal state (e.g. KilnInteraction.GetFuelCount())
// on an uninitialized prefab can trigger a native IL2CPP access violation that try/catch CANNOT catch —
// which hard-froze the game. Two safeguards: only act on a REAL, named, in-world structure (boot prefabs
// have empty names), and read simple getters only (no container-walking methods).
internal static class BurnDiag
{
    // Boot-time prefab inits arrive with an empty owner name; real placed/loaded structures have one.
    // Reading DefaultName/StructureName is the same proven-safe access the shipped FireStructurePatch
    // does at Initialize. Gate ALL deeper member access behind this so we never touch a raw prefab.
    internal static bool IsRealStructure(Structure? owner)
    {
        if (owner == null) return false;
        try
        {
            if (!string.IsNullOrEmpty(owner.DefaultName)) return true;
            if (!string.IsNullOrEmpty(owner.StructureName)) return true;
        }
        catch { }
        return false;
    }

    internal static string FireDetail(FireStructure? fire)
    {
        if (fire == null) return "fireStructure=null";
        try
        {
            return $"mechanism=FireStructure(Rpc_AddFuel) fuel={fire.CurrentFuelVolume:0.#}/{fire.MaxFuelVolume:0.#}";
        }
        catch (Exception ex) { return $"mechanism=FireStructure (read failed: {ex.Message})"; }
    }

    internal static string FuelAttrDetail(VariableAttribute? attr)
    {
        if (attr == null) return "mechanism=_fuelVAttr(coal items) attr=null";
        try
        {
            return $"mechanism=_fuelVAttr(coal items) fuel={attr.GetValue():0.#} (norm={attr.GetNormalizedValue():0.00})";
        }
        catch (Exception ex) { return $"mechanism=_fuelVAttr(coal items) (read failed: {ex.Message})"; }
    }
}

// Bloomery's smelting kiln — coal burned as items via _fuelVAttr; NOT a FireStructure.
[HarmonyPatch(typeof(KilnInteraction), "Initialize", new Type[] { typeof(Structure) })]
internal static class KilnInteractionDiagPatch
{
    static void Postfix(KilnInteraction __instance, Structure ownerStructure)
    {
        if (!Plugin.LogBurnableBuildings.Value) return;
        if (!BurnDiag.IsRealStructure(ownerStructure)) return;
        try
        {
            VariableAttribute? attr = null;
            try { attr = __instance._fuelVAttr; } catch { }
            Plugin.LogBurnableBuilding("KilnInteraction", ownerStructure, BurnDiag.FuelAttrDetail(attr));
        }
        catch (Exception ex) { Plugin.Logger.LogError($"[TorchFuelMod] KilnInteractionDiagPatch: {ex}"); }
    }
}

// Bloomery's bellows — drives air intake AND carries its own FireStructure.
[HarmonyPatch(typeof(BellowsInteraction), "Initialize", new Type[] { typeof(Structure) })]
internal static class BellowsInteractionDiagPatch
{
    static void Postfix(BellowsInteraction __instance, Structure ownerStructure)
    {
        if (!Plugin.LogBurnableBuildings.Value) return;
        if (!BurnDiag.IsRealStructure(ownerStructure)) return;
        try
        {
            FireStructure? fire = null;
            try { fire = __instance.fireStructure; } catch { }
            Plugin.LogBurnableBuilding("BellowsInteraction", ownerStructure, BurnDiag.FireDetail(fire));
        }
        catch (Exception ex) { Plugin.Logger.LogError($"[TorchFuelMod] BellowsInteractionDiagPatch: {ex}"); }
    }
}

// Forge — wraps a FireStructure.
[HarmonyPatch(typeof(ForgeInteraction), "Initialize", new Type[] { typeof(Structure) })]
internal static class ForgeInteractionDiagPatch
{
    static void Postfix(ForgeInteraction __instance, Structure ownerStructure)
    {
        if (!Plugin.LogBurnableBuildings.Value) return;
        if (!BurnDiag.IsRealStructure(ownerStructure)) return;
        try
        {
            FireStructure? fire = null;
            try { fire = __instance.fireStructure; } catch { }
            Plugin.LogBurnableBuilding("ForgeInteraction", ownerStructure, BurnDiag.FireDetail(fire));
        }
        catch (Exception ex) { Plugin.Logger.LogError($"[TorchFuelMod] ForgeInteractionDiagPatch: {ex}"); }
    }
}

// Charcoal station (coalmaker) — owns a PyreStructure : FireStructure that converts wood -> charcoal.
[HarmonyPatch(typeof(CharcoalStation), "Initialize", new Type[] { typeof(Structure) })]
internal static class CharcoalStationDiagPatch
{
    static void Postfix(CharcoalStation __instance, Structure ownerStructure)
    {
        if (!Plugin.LogBurnableBuildings.Value) return;
        if (!BurnDiag.IsRealStructure(ownerStructure)) return;
        try
        {
            PyreStructure? pyre = null;
            try { pyre = __instance.pyre; } catch { }
            // PyreStructure : FireStructure, so the same fuel-volume read applies.
            Plugin.LogBurnableBuilding("CharcoalStation", ownerStructure,
                pyre == null ? "pyre=null (makes coal; not a consumer)" : BurnDiag.FireDetail(pyre) + " [pyre: wood->charcoal]");
        }
        catch (Exception ex) { Plugin.Logger.LogError($"[TorchFuelMod] CharcoalStationDiagPatch: {ex}"); }
    }
}

// Bloomery workstation itself — logged for the building-level name and which sub-stations it owns.
[HarmonyPatch(typeof(Bloomstation), "Initialize", new Type[] { typeof(Structure) })]
internal static class BloomstationDiagPatch
{
    static void Postfix(Bloomstation __instance, Structure ownerStructure)
    {
        if (!Plugin.LogBurnableBuildings.Value) return;
        if (!BurnDiag.IsRealStructure(ownerStructure)) return;
        try
        {
            bool hasKiln = false, hasBellows = false, hasAnvil = false;
            try { hasKiln = __instance.kiln != null; } catch { }
            try { hasBellows = __instance.bellows != null; } catch { }
            try { hasAnvil = __instance.anvil != null; } catch { }
            Plugin.LogBurnableBuilding("Bloomstation", ownerStructure,
                $"workstation; kiln={hasKiln} bellows={hasBellows} anvil={hasAnvil}");
        }
        catch (Exception ex) { Plugin.Logger.LogError($"[TorchFuelMod] BloomstationDiagPatch: {ex}"); }
    }
}
