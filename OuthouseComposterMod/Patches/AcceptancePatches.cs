using System;
using System.Collections.Generic;
using HarmonyLib;
using SandSailorStudio.Inventory;

namespace OuthouseComposterMod.Patches;

// Acceptance-gate overrides, scoped strictly to the Outhouse's ItemContainer (identified by
// OuthouseGate.IsOuthouseContainer via its containerType asset name). ItemContainer is a plain
// class (base Il2CppSystem.Object — Cecil-confirmed), not a MonoBehaviour/NetworkBehaviour, so none
// of the project's Interaction-lifecycle or NetworkBehaviour-state-sync patch gotchas apply here.
//
// Each of the four methods is patched independently (class-level [HarmonyPatch] + a plain
// "Postfix" method — the exact pattern GroundItemVacuumMod\Patches\LifecyclePatches.cs uses), each
// postfix in its own try/catch, with fire-verification logging (mandatory — IL2CPP's AOT compiler
// can silently inline small/single-caller methods and kill a patch with no error; a missing "patch
// alive" line in the log is the signal that happened). The alive-marker fires once ever per patch
// regardless of diagnostics gating, so the hook's liveness is always confirmable; the rest of the
// logging is gated by EnableDiagnostics and doubles as FoodCategoryMatch tuning data.
[HarmonyPatch(typeof(ItemContainer), nameof(ItemContainer.CanStoreItemType))]
internal static class CanStoreItemTypePatch
{
    private static bool _aliveLogged;
    private static readonly HashSet<string> _loggedItems = new();

    static void Postfix(ItemContainer __instance, ItemInfo info, ref bool __result)
    {
        try
        {
            if (!_aliveLogged)
            {
                _aliveLogged = true;
                Plugin.Logger.LogInfo("[OuthouseComposter][accept] CanStoreItemType patch alive.");
            }

            if (__result) return;
            if (!OuthouseGate.IsOuthouseContainer(__instance)) return;
            if (!OuthouseGate.IsAcceptedInput(info)) return;

            __result = true;

            if (Plugin.EnableDiagnostics.Value)
            {
                string name = SafeName(info);
                if (_loggedItems.Add(name))
                    Plugin.Logger.LogInfo($"[OuthouseComposter][accept] CanStoreItemType fired: {OuthouseGate.DescribeItem(info)} → forced=True");
            }
        }
        catch (Exception ex) { Plugin.Logger.LogError($"[OuthouseComposter] CanStoreItemTypePatch error: {ex}"); }
    }

    private static string SafeName(ItemInfo? info)
    {
        if (info == null) return "?";
        try { return info.Name ?? "?"; } catch { return "?"; }
    }
}

[HarmonyPatch(typeof(ItemContainer), nameof(ItemContainer.Check))]
internal static class CheckPatch
{
    private static bool _aliveLogged;
    private static readonly HashSet<string> _loggedItems = new();

    static void Postfix(ItemContainer __instance, ItemInfo itemInfo, ref bool __result)
    {
        try
        {
            if (!_aliveLogged)
            {
                _aliveLogged = true;
                Plugin.Logger.LogInfo("[OuthouseComposter][accept] Check patch alive.");
            }

            if (__result) return;
            if (!OuthouseGate.IsOuthouseContainer(__instance)) return;
            if (!OuthouseGate.IsAcceptedInput(itemInfo)) return;

            __result = true;

            if (Plugin.EnableDiagnostics.Value)
            {
                string name = SafeName(itemInfo);
                if (_loggedItems.Add(name))
                    Plugin.Logger.LogInfo($"[OuthouseComposter][accept] Check fired: {OuthouseGate.DescribeItem(itemInfo)} → forced=True");
            }
        }
        catch (Exception ex) { Plugin.Logger.LogError($"[OuthouseComposter] CheckPatch error: {ex}"); }
    }

    private static string SafeName(ItemInfo? info)
    {
        if (info == null) return "?";
        try { return info.Name ?? "?"; } catch { return "?"; }
    }
}

[HarmonyPatch(typeof(ItemContainer), nameof(ItemContainer.HasSpace))]
internal static class HasSpacePatch
{
    private static bool _aliveLogged;
    private static readonly HashSet<string> _loggedItems = new();

    static void Postfix(ItemContainer __instance, ItemInfo itemInfo, int quantity, ref bool __result)
    {
        try
        {
            if (!_aliveLogged)
            {
                _aliveLogged = true;
                Plugin.Logger.LogInfo("[OuthouseComposter][accept] HasSpace patch alive.");
            }

            if (__result) return;
            if (!OuthouseGate.IsOuthouseContainer(__instance)) return;
            if (!OuthouseGate.IsAcceptedInput(itemInfo)) return;

            // v0.2.0 simple fallback: an empty slot is enough to force acceptance. Does not check
            // for an existing non-full stack of the same item — acceptable for the throw-it-in use
            // case (a documented v0.2.0 simplification, not a correctness requirement).
            bool forced = false;
            try { forced = __instance.GetEmptySlots() > 0; } catch { }
            __result = forced;

            if (Plugin.EnableDiagnostics.Value)
            {
                string name = SafeName(itemInfo);
                if (_loggedItems.Add(name))
                    Plugin.Logger.LogInfo($"[OuthouseComposter][accept] HasSpace fired: {OuthouseGate.DescribeItem(itemInfo)} → forced={forced}");
            }
        }
        catch (Exception ex) { Plugin.Logger.LogError($"[OuthouseComposter] HasSpacePatch error: {ex}"); }
    }

    private static string SafeName(ItemInfo? info)
    {
        if (info == null) return "?";
        try { return info.Name ?? "?"; } catch { return "?"; }
    }
}

[HarmonyPatch(typeof(ItemContainer), nameof(ItemContainer.GetStackSize))]
internal static class GetStackSizePatch
{
    private static bool _aliveLogged;
    private static readonly HashSet<string> _loggedItems = new();

    static void Postfix(ItemContainer __instance, ItemInfo itemInfo, ref int __result)
    {
        try
        {
            if (!_aliveLogged)
            {
                _aliveLogged = true;
                Plugin.Logger.LogInfo("[OuthouseComposter][accept] GetStackSize patch alive.");
            }

            if (__result != 0) return;
            if (!OuthouseGate.IsOuthouseContainer(__instance)) return;
            if (!OuthouseGate.IsAcceptedInput(itemInfo)) return;

            __result = OuthouseGate.IsSeed(itemInfo) ? Plugin.SeedStackSize.Value : Plugin.FoodStackSize.Value;

            if (Plugin.EnableDiagnostics.Value)
            {
                string name = SafeName(itemInfo);
                if (_loggedItems.Add(name))
                    Plugin.Logger.LogInfo($"[OuthouseComposter][accept] GetStackSize fired: {OuthouseGate.DescribeItem(itemInfo)} → forced={__result}");
            }
        }
        catch (Exception ex) { Plugin.Logger.LogError($"[OuthouseComposter] GetStackSizePatch error: {ex}"); }
    }

    private static string SafeName(ItemInfo? info)
    {
        if (info == null) return "?";
        try { return info.Name ?? "?"; } catch { return "?"; }
    }
}
