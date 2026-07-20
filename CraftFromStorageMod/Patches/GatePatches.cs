using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
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
//   SSSGame.CraftingAgent : MonoBehaviour
//     Void _OnCraftingFinished()                                                    - zero-param, safe to patch
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
}

// v0.2.0: aggregates CheckOwnedRequirements / _CheckOwnedBlueprintManifest call volume instead of
// logging one line per call - at ~96 calls per menu-open the old per-call logging spammed the log,
// and measuring that call frequency is itself a Phase 1 design input (does the settlement walk fit
// inside this postfix?). Flushes a rollup line after 1.5s of no further calls; ticked every frame
// from CraftWatcher.Update() (this file has no MonoBehaviour of its own to drive a timer).
internal static class GateRollup
{
    private const long FlushIdleMs = 1500;

    private static int _totalCalls;
    private static readonly Dictionary<string, int> _perAgentCounts = new();
    private static int _trueCount;
    private static int _falseCount;
    private static readonly HashSet<string> _distinctBlueprints = new();
    private static int _innerManifestChecks;
    private static long _lastCallMs = -1;
    private static readonly Stopwatch _sw = new();

    internal static void RecordCheckOwnedRequirements(string agentClass, string blueprintName, bool result)
    {
        try
        {
            EnsureBurstStarted();
            _lastCallMs = _sw.ElapsedMilliseconds;
            _totalCalls++;
            _perAgentCounts.TryGetValue(agentClass, out int c);
            _perAgentCounts[agentClass] = c + 1;
            if (result) _trueCount++; else _falseCount++;
            _distinctBlueprints.Add(blueprintName);
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"[CFS] GateRollup.RecordCheckOwnedRequirements error: {ex}");
        }
    }

    internal static void RecordInnerManifestCheck()
    {
        try
        {
            EnsureBurstStarted();
            _lastCallMs = _sw.ElapsedMilliseconds;
            _innerManifestChecks++;
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"[CFS] GateRollup.RecordInnerManifestCheck error: {ex}");
        }
    }

    private static void EnsureBurstStarted()
    {
        if (!_sw.IsRunning) _sw.Restart();
    }

    // Called every frame from CraftWatcher.Update(). Flushes when FlushIdleMs pass with no
    // further calls recorded.
    internal static void Tick()
    {
        if (_totalCalls == 0 && _innerManifestChecks == 0) return;
        if (!_sw.IsRunning) return;
        if (_sw.ElapsedMilliseconds - _lastCallMs < FlushIdleMs) return;
        Flush();
    }

    private static void Flush()
    {
        try
        {
            long elapsedMs = Math.Max(1, _lastCallMs);
            double callsPerSec = _totalCalls / (elapsedMs / 1000.0);
            string agentBreakdown = string.Join(", ", _perAgentCounts.Select(kv => $"{kv.Key}={kv.Value}"));
            Plugin.Logger.LogInfo($"[CFS] GATE rollup: {_totalCalls} calls in {elapsedMs}ms ({callsPerSec:F1}/s) " +
                $"agents=[{agentBreakdown}] distinctBlueprints={_distinctBlueprints.Count} true={_trueCount} " +
                $"false={_falseCount} innerManifestChecks={_innerManifestChecks}");
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"[CFS] GateRollup.Flush error: {ex}");
        }
        finally
        {
            _totalCalls = 0;
            _perAgentCounts.Clear();
            _trueCount = 0;
            _falseCount = 0;
            _distinctBlueprints.Clear();
            _innerManifestChecks = 0;
            _sw.Reset();
            _lastCallMs = -1;
        }
    }
}

// v0.2.0: logs one line the first time each distinct station TYPE is seen (keyed by the
// interaction's own native class - CraftInteraction/AnvilInteraction/CarpenterInteraction/
// DyeingInteraction all pass through the same 3 BeginCraftingSequence patches thanks to the
// managed-cast-lies gotcha, so the native class read here can reveal subclasses the compile-time
// parameter type hides). Answers "does useAgentInventory vary per station family" in one line
// per family instead of once per craft.
internal static class StationProfile
{
    private static readonly HashSet<string> _profiled = new();

    internal static void ProfileOnce(CraftInteraction interaction)
    {
        try
        {
            string nativeClass = Plugin.NativeClassName(interaction);
            if (!_profiled.Add(nativeClass)) return;

            bool useAgentInv = false;
            try { useAgentInv = interaction.useAgentInventory; } catch { }

            UnityEngine.Transform? t = null;
            try { t = interaction.transform; } catch { }
            SSSGame.Workstation? ws = FindWorkstation(t);

            string wsClass = "none";
            string wsInvTotal = "?";
            string neededCount = "?";
            string neededNames = "";

            if (ws != null)
            {
                wsClass = Plugin.NativeClassName(ws);
                try
                {
                    var inv = ws.GetInventory();
                    if (inv != null) wsInvTotal = inv.GetTotalItemsQuantity().ToString();
                }
                catch { }

                // GetItemsNeededFromSettlement()'s element type isn't nailed down by the Cecil
                // dump (it prints as the bare open generic "List`1"), so the element is read back
                // as `object` and its display name is discovered via reflection rather than a
                // guessed compile-time cast - avoids gambling this whole file's compile on a type
                // name that was never actually confirmed.
                try
                {
                    var needed = ws.GetItemsNeededFromSettlement();
                    if (needed != null)
                    {
                        int cnt = 0;
                        try { cnt = needed.Count; } catch { }
                        neededCount = cnt.ToString();

                        var sb = new StringBuilder();
                        int shown = 0;
                        foreach (var raw in needed)
                        {
                            object? item = raw;
                            if (item == null) continue;
                            if (shown >= 8) { sb.Append(", ..."); break; }
                            if (sb.Length > 0) sb.Append(", ");
                            sb.Append(DisplayName(item));
                            shown++;
                        }
                        if (sb.Length > 0) neededNames = $" [{sb}]";
                    }
                }
                catch { }
            }

            Plugin.Logger.LogInfo($"[CFS] STATION PROFILE {nativeClass}: useAgentInventory={useAgentInv} " +
                $"workstation={wsClass} workstationInventoryTotal={wsInvTotal} itemsNeededFromSettlement={neededCount}{neededNames}");
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"[CFS] StationProfile.ProfileOnce error: {ex}");
        }
    }

    private static SSSGame.Workstation? FindWorkstation(UnityEngine.Transform? t, int maxDepth = 12)
    {
        int depth = 0;
        while (t != null && depth <= maxDepth)
        {
            SSSGame.Workstation? w = null;
            try { w = t.GetComponent<SSSGame.Workstation>(); } catch { }
            if (w != null) return w;
            try { t = t.parent; } catch { t = null; }
            depth++;
        }
        return null;
    }

    private static string DisplayName(object item)
    {
        try
        {
            var t = item.GetType();
            var nameProp = t.GetProperty("Name");
            if (nameProp != null)
            {
                var val = nameProp.GetValue(item) as string;
                if (!string.IsNullOrEmpty(val)) return val!;
            }
            var infoProp = t.GetProperty("info") ?? t.GetProperty("Info");
            if (infoProp != null)
            {
                var infoVal = infoProp.GetValue(item);
                if (infoVal != null)
                {
                    var innerNameProp = infoVal.GetType().GetProperty("Name");
                    var val2 = innerNameProp?.GetValue(infoVal) as string;
                    if (!string.IsNullOrEmpty(val2)) return val2!;
                }
            }
        }
        catch { }
        return "?";
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
            GateRollup.RecordCheckOwnedRequirements(agentClass, bpName, __result);
            if (Plugin.VerboseGateLogging.Value)
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
            GateRollup.RecordInnerManifestCheck();
            if (Plugin.VerboseGateLogging.Value)
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
//          subclasses). Prefix so it logs before any consumption. Also arms the CraftWatcher delta
//          watcher and profiles the station type (both v0.2.0). ----
[HarmonyPatch(typeof(CraftInteraction), nameof(CraftInteraction.BeginCraftingSequence))]
internal static class BeginCraftingSequenceCraftPatch
{
    static void Prefix(CraftInteraction __instance, InteractionSession session)
    {
        try { GateLog.LogBeginCraftingSequence("CraftInteraction", __instance, session); }
        catch (Exception ex) { Plugin.Logger.LogError($"[CFS] BeginCraftingSequenceCraftPatch: {ex}"); }
        try { StationProfile.ProfileOnce(__instance); }
        catch (Exception ex) { Plugin.Logger.LogError($"[CFS] BeginCraftingSequenceCraftPatch StationProfile: {ex}"); }
        try { CraftWatcher.Arm(__instance, session, "CraftInteraction"); }
        catch (Exception ex) { Plugin.Logger.LogError($"[CFS] BeginCraftingSequenceCraftPatch Arm: {ex}"); }
    }
}

[HarmonyPatch(typeof(AnvilInteraction), nameof(AnvilInteraction.BeginCraftingSequence))]
internal static class BeginCraftingSequenceAnvilPatch
{
    static void Prefix(AnvilInteraction __instance, InteractionSession session)
    {
        try { GateLog.LogBeginCraftingSequence("AnvilInteraction", __instance, session); }
        catch (Exception ex) { Plugin.Logger.LogError($"[CFS] BeginCraftingSequenceAnvilPatch: {ex}"); }
        try { StationProfile.ProfileOnce(__instance); }
        catch (Exception ex) { Plugin.Logger.LogError($"[CFS] BeginCraftingSequenceAnvilPatch StationProfile: {ex}"); }
        // __instance is typed as the AnvilInteraction subclass here - CraftWatcher.Arm takes the
        // base CraftInteraction type, which AnvilInteraction (and CarpenterInteraction) derive from.
        try { CraftWatcher.Arm(__instance, session, "AnvilInteraction"); }
        catch (Exception ex) { Plugin.Logger.LogError($"[CFS] BeginCraftingSequenceAnvilPatch Arm: {ex}"); }
    }
}

[HarmonyPatch(typeof(DyeingInteraction), nameof(DyeingInteraction.BeginCraftingSequence))]
internal static class BeginCraftingSequenceDyeingPatch
{
    static void Prefix(DyeingInteraction __instance, InteractionSession session)
    {
        try { GateLog.LogBeginCraftingSequence("DyeingInteraction", __instance, session); }
        catch (Exception ex) { Plugin.Logger.LogError($"[CFS] BeginCraftingSequenceDyeingPatch: {ex}"); }
        try { StationProfile.ProfileOnce(__instance); }
        catch (Exception ex) { Plugin.Logger.LogError($"[CFS] BeginCraftingSequenceDyeingPatch StationProfile: {ex}"); }
        // __instance is typed as the DyeingInteraction subclass here - CraftWatcher.Arm takes the
        // base CraftInteraction type, which DyeingInteraction derives from.
        try { CraftWatcher.Arm(__instance, session, "DyeingInteraction"); }
        catch (Exception ex) { Plugin.Logger.LogError($"[CFS] BeginCraftingSequenceDyeingPatch Arm: {ex}"); }
    }
}

// ---- 4. _OnCraftingSuccess - patched by name on BOTH CraftInteraction and DyeingInteraction.
//          v0.2.0: no longer takes its own PRE/POST ItemInventory snapshot (that only ever covered
//          one candidate collection, and _OnCraftingSuccess is confirmed NOT the consumption site -
//          architecture.md "Player crafting pipeline"). Marks the CraftWatcher instead, which
//          covers every candidate collection in one delta reading. ----
[HarmonyPatch(typeof(CraftInteraction), "_OnCraftingSuccess")]
internal static class OnCraftingSuccessCraftPatch
{
    static void Prefix(CraftInteraction __instance, IInteractionAgent agent)
    {
        try { CraftWatcher.Mark("_OnCraftingSuccess[CraftInteraction] PRE"); }
        catch (Exception ex) { Plugin.Logger.LogError($"[CFS] OnCraftingSuccessCraftPatch Prefix: {ex}"); }
    }

    static void Postfix(CraftInteraction __instance, IInteractionAgent agent)
    {
        try { CraftWatcher.Mark("_OnCraftingSuccess[CraftInteraction] POST"); }
        catch (Exception ex) { Plugin.Logger.LogError($"[CFS] OnCraftingSuccessCraftPatch Postfix: {ex}"); }
    }
}

[HarmonyPatch(typeof(DyeingInteraction), "_OnCraftingSuccess")]
internal static class OnCraftingSuccessDyeingPatch
{
    static void Prefix(DyeingInteraction __instance, IInteractionAgent agent)
    {
        try { CraftWatcher.Mark("_OnCraftingSuccess[DyeingInteraction] PRE"); }
        catch (Exception ex) { Plugin.Logger.LogError($"[CFS] OnCraftingSuccessDyeingPatch Prefix: {ex}"); }
    }

    static void Postfix(DyeingInteraction __instance, IInteractionAgent agent)
    {
        try { CraftWatcher.Mark("_OnCraftingSuccess[DyeingInteraction] POST"); }
        catch (Exception ex) { Plugin.Logger.LogError($"[CFS] OnCraftingSuccessDyeingPatch Postfix: {ex}"); }
    }
}

// ---- 4b. _OnCraftingFinished (NEW v0.2.0) - zero-parameter, so it avoids the inventory-family
//          parameter crash class entirely (unlike _OnCraftingSuccess/_CheckOwnedBlueprintManifest).
//          Marks the CraftWatcher as a craft-timeline endpoint. Gated under the same
//          TraceOnCraftingSuccess flag in Plugin.cs (no new flag). ----
[HarmonyPatch(typeof(CraftingAgent), "_OnCraftingFinished")]
internal static class OnCraftingFinishedPatch
{
    static void Postfix(CraftingAgent __instance)
    {
        try { CraftWatcher.Mark("_OnCraftingFinished"); }
        catch (Exception ex) { Plugin.Logger.LogError($"[CFS] OnCraftingFinishedPatch: {ex}"); }
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
