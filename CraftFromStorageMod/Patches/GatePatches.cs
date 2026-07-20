using System;
using System.Text;
using HarmonyLib;
using SandSailorStudio.Inventory;
using SandSailorStudio.UI;
using SSSGame;
using SSSGame.UI;

namespace CraftFromStorageMod.Patches;

// Phase 0 (idea-17) trace patches on the CraftInteraction family. READ-ONLY: every patch here only
// logs via Plugin.Logger - none writes inventory, transfers items, or flips a gate/__result value.
// Confirmed signatures (Cecil 2026-07-20, _explore/cecil_craft_from_storage*.ps1):
//   SSSGame.CraftInteraction : Interaction
//     Boolean CheckOwnedRequirements(Blueprint bp, IInteractionAgent agent)          - public, NON-virtual (inlining risk)
//     Boolean _CheckOwnedBlueprintManifest(ItemManifest, IInteractionAgent)          - protected virtual (ItemManifest = highest crash risk)
//     Void BeginCraftingSequence(InteractionSession session)                        - virtual (also on AnvilInteraction, DyeingInteraction)
//     Void _OnCraftingSuccess(IInteractionAgent agent)                              - protected virtual (also on DyeingInteraction)
//     Boolean ActivateBlueprint(CraftBlueprint bp)                                  - virtual
//   SSSGame.UI.CreateItemsTabPage : TabPage
//     Void Show(Boolean value, TabButton button)                                   - virtual
internal static class GateLog
{
    // Blueprint (base of CraftBlueprint) has no direct ".name" - it derives from Item, whose own
    // ".info : ItemInfo" carries the blueprint's OWN item name (e.g. "Iron Sword Blueprint"). Falls
    // back to the crafted RESULT item's name (Blueprint.GetResultInfo()) if that's unavailable.
    internal static string SafeBlueprintName(Blueprint? bp)
    {
        if (bp == null) return "null";
        try
        {
            string? own = bp.info?.Name;
            if (!string.IsNullOrEmpty(own)) return own!;
        }
        catch { }
        try
        {
            string? result = bp.GetResultInfo()?.Name;
            if (!string.IsNullOrEmpty(result)) return result! + " (result name)";
        }
        catch { }
        return "?";
    }

    internal static void LogBeginCraftingSequence(string typeName, CraftInteraction instance, InteractionSession session)
    {
        string sessionClass = Plugin.NativeClassName(session);
        bool useAgentInv = false;
        try { useAgentInv = instance.useAgentInventory; } catch { }
        Plugin.Logger.LogInfo($"[CFS] BeginCraftingSequence[{typeName}]: session={sessionClass} useAgentInventory={useAgentInv}");
    }

    // PRE (Prefix, before consumption) / POST (Postfix, after) snapshot of the station's ItemInventory
    // so a diff is readable at a glance. Capped at ~15 breakdown entries.
    internal static void LogInventorySnapshot(string typeName, string phase, CraftInteraction instance, IInteractionAgent? agent)
    {
        string agentClass = Plugin.NativeClassName(agent);

        ItemCollection? inv = null;
        try { inv = instance.ItemInventory; } catch { }
        if (inv == null)
        {
            Plugin.Logger.LogInfo($"[CFS] _OnCraftingSuccess[{typeName}] {phase}: agent={agentClass} ItemInventory=null");
            return;
        }

        int total = 0;
        try { total = inv.GetTotalItemsQuantity(); } catch { }

        var sb = new StringBuilder();
        try
        {
            var infos = inv.GetItemInfos();
            if (infos != null)
            {
                int count = 0;
                foreach (var info in infos)
                {
                    if (info == null) continue;
                    if (count >= 15) { sb.Append(", ..."); break; }
                    int qty = 0;
                    try { qty = inv.GetItemQuantity(info); } catch { }
                    string name = "?";
                    try { name = info.Name ?? "?"; } catch { }
                    if (sb.Length > 0) sb.Append(", ");
                    sb.Append(name).Append('x').Append(qty);
                    count++;
                }
            }
        }
        catch (Exception ex) { sb.Append($"<breakdown error: {ex.Message}>"); }

        Plugin.Logger.LogInfo($"[CFS] _OnCraftingSuccess[{typeName}] {phase}: agent={agentClass} total={total} items=[{sb}]");
    }
}

// ---- 1. CheckOwnedRequirements (public, NON-virtual - fire-verify; may be AOT-inlined) ----
[HarmonyPatch(typeof(CraftInteraction), nameof(CraftInteraction.CheckOwnedRequirements))]
internal static class CheckOwnedRequirementsPatch
{
    static void Postfix(CraftInteraction __instance, Blueprint bp, IInteractionAgent agent, bool __result)
    {
        try
        {
            string agentClass = Plugin.NativeClassName(agent);
            string bpName = GateLog.SafeBlueprintName(bp);
            Plugin.Logger.LogInfo($"[CFS] CheckOwnedRequirements: agent={agentClass} bp='{bpName}' result={__result}");
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"[CFS] CheckOwnedRequirementsPatch: {ex}");
        }
    }
}

// ---- 2. _CheckOwnedBlueprintManifest (protected virtual - HIGHEST RISK: ItemManifest param on the
//          real target). Patched BY NAME ONLY, and the Postfix deliberately does NOT declare the
//          ItemManifest parameter (minimizes early il2cpp class-init exposure of the inventory-family
//          type). If this patch hard-crashes plugin load, set TraceCheckOwnedBlueprintManifest=false
//          in the config FIRST. ----
[HarmonyPatch(typeof(CraftInteraction), "_CheckOwnedBlueprintManifest")]
internal static class CheckOwnedBlueprintManifestPatch
{
    static void Postfix(CraftInteraction __instance, bool __result)
    {
        try
        {
            string instClass = Plugin.NativeClassName(__instance);
            Plugin.Logger.LogInfo($"[CFS] _CheckOwnedBlueprintManifest: instance={instClass} result={__result}");
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"[CFS] CheckOwnedBlueprintManifestPatch: {ex}");
        }
    }
}

// ---- 3. BeginCraftingSequence - THREE separate patch classes (CraftInteraction, AnvilInteraction,
//          DyeingInteraction each declare their own override; patching only the base would miss the
//          subclasses). Prefix so it logs before any consumption. ----
[HarmonyPatch(typeof(CraftInteraction), nameof(CraftInteraction.BeginCraftingSequence))]
internal static class BeginCraftingSequenceCraftPatch
{
    static void Prefix(CraftInteraction __instance, InteractionSession session)
    {
        try { GateLog.LogBeginCraftingSequence("CraftInteraction", __instance, session); }
        catch (Exception ex) { Plugin.Logger.LogError($"[CFS] BeginCraftingSequenceCraftPatch: {ex}"); }
    }
}

[HarmonyPatch(typeof(AnvilInteraction), nameof(AnvilInteraction.BeginCraftingSequence))]
internal static class BeginCraftingSequenceAnvilPatch
{
    static void Prefix(AnvilInteraction __instance, InteractionSession session)
    {
        try { GateLog.LogBeginCraftingSequence("AnvilInteraction", __instance, session); }
        catch (Exception ex) { Plugin.Logger.LogError($"[CFS] BeginCraftingSequenceAnvilPatch: {ex}"); }
    }
}

[HarmonyPatch(typeof(DyeingInteraction), nameof(DyeingInteraction.BeginCraftingSequence))]
internal static class BeginCraftingSequenceDyeingPatch
{
    static void Prefix(DyeingInteraction __instance, InteractionSession session)
    {
        try { GateLog.LogBeginCraftingSequence("DyeingInteraction", __instance, session); }
        catch (Exception ex) { Plugin.Logger.LogError($"[CFS] BeginCraftingSequenceDyeingPatch: {ex}"); }
    }
}

// ---- 4. _OnCraftingSuccess - patched by name on BOTH CraftInteraction and DyeingInteraction, each
//          with Prefix (PRE) AND Postfix (POST) so a diff of the station's ItemInventory is readable
//          at a glance - shows where consumption actually happens. ----
[HarmonyPatch(typeof(CraftInteraction), "_OnCraftingSuccess")]
internal static class OnCraftingSuccessCraftPatch
{
    static void Prefix(CraftInteraction __instance, IInteractionAgent agent)
    {
        try { GateLog.LogInventorySnapshot("CraftInteraction", "PRE", __instance, agent); }
        catch (Exception ex) { Plugin.Logger.LogError($"[CFS] OnCraftingSuccessCraftPatch Prefix: {ex}"); }
    }

    static void Postfix(CraftInteraction __instance, IInteractionAgent agent)
    {
        try { GateLog.LogInventorySnapshot("CraftInteraction", "POST", __instance, agent); }
        catch (Exception ex) { Plugin.Logger.LogError($"[CFS] OnCraftingSuccessCraftPatch Postfix: {ex}"); }
    }
}

[HarmonyPatch(typeof(DyeingInteraction), "_OnCraftingSuccess")]
internal static class OnCraftingSuccessDyeingPatch
{
    static void Prefix(DyeingInteraction __instance, IInteractionAgent agent)
    {
        try { GateLog.LogInventorySnapshot("DyeingInteraction", "PRE", __instance, agent); }
        catch (Exception ex) { Plugin.Logger.LogError($"[CFS] OnCraftingSuccessDyeingPatch Prefix: {ex}"); }
    }

    static void Postfix(DyeingInteraction __instance, IInteractionAgent agent)
    {
        try { GateLog.LogInventorySnapshot("DyeingInteraction", "POST", __instance, agent); }
        catch (Exception ex) { Plugin.Logger.LogError($"[CFS] OnCraftingSuccessDyeingPatch Postfix: {ex}"); }
    }
}

// ---- 5. ActivateBlueprint (virtual) ----
[HarmonyPatch(typeof(CraftInteraction), nameof(CraftInteraction.ActivateBlueprint))]
internal static class ActivateBlueprintPatch
{
    static void Postfix(CraftInteraction __instance, CraftBlueprint bp, bool __result)
    {
        try
        {
            string bpName = GateLog.SafeBlueprintName(bp);
            Plugin.Logger.LogInfo($"[CFS] ActivateBlueprint: bp='{bpName}' result={__result}");
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"[CFS] ActivateBlueprintPatch: {ex}");
        }
    }
}

// ---- 6. Recipe-list UI ----
[HarmonyPatch(typeof(CreateItemsTabPage), nameof(CreateItemsTabPage.Show))]
internal static class RecipeListUIPatch
{
    static void Postfix(CreateItemsTabPage __instance, bool value, TabButton button)
    {
        try
        {
            string avail = "null", unavail = "null";
            try { var c = __instance.AvailableBlueprints; if (c != null) avail = c.Count.ToString(); } catch { }
            try { var c = __instance.UnavailableBlueprints; if (c != null) unavail = c.Count.ToString(); } catch { }
            Plugin.Logger.LogInfo($"[CFS] CreateItemsTabPage.Show(value={value}): AvailableBlueprints={avail} UnavailableBlueprints={unavail}");
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"[CFS] RecipeListUIPatch: {ex}");
        }
    }
}
