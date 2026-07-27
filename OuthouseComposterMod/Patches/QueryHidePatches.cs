using System;
using System.Collections.Generic;
using HarmonyLib;
using SandSailorStudio.Inventory;

namespace OuthouseComposterMod.Patches;

// v1.3.104 — query-hide gate. Confirmed in-game 2026-07-26: villagers steal forced-in food/seeds out
// of the outhouse before they convert, and neither the shipped haul-gate (v1.1.0) nor eat-gate
// (v1.2.0) covers the path they actually use. Two prior fix attempts are dead ends, do NOT retry:
//   1. Harmony patches on GetItemManifest() (14 declarers, then 2) both crashed the game natively.
//   2. Denying the IsWhitelistedByStorage quest gates was rejected — those receive only the storage
//      SITE, never the item, so they can't distinguish "fetching garlic" from "fetching compost" and
//      would strand the compost.
//   3. SSSGame.ResourceStorage.onlyGatherNativeItems = true applied cleanly but did not stop the
//      theft — that flag does not govern this path. That code has since been removed.
//
// This approach instead makes the outhouse container itself UNDER-REPORT its non-compost contents to
// any query, while compost still answers truthfully — closing the theft off at the query source
// rather than trying to enumerate every quest-side consumer. Scoped strictly to the outhouse's
// ItemContainer via the SAME OuthouseGate.IsOuthouseContainer identity check AcceptancePatches.cs
// uses (ItemContainer is a plain class, base Il2CppSystem.Object — Cecil-confirmed — not a
// MonoBehaviour/NetworkBehaviour, so none of the Interaction-lifecycle or NetworkBehaviour-state-sync
// patch gotchas apply, same as AcceptancePatches). ItemInfo and IItemFilter are both confirmed SAFE
// patch-parameter types (already patched safely by AcceptancePatches/EatGatePatches since
// v1.0.0/v1.2.0) — neither belongs to the inventory-family crash group (Item/ItemCollection/
// ItemEventContext), and neither patched method below takes a by-ref primitive parameter.
//
// QueryHideBypass.Active suppresses BOTH postfixes below while set. ComposterDiag wraps every one of
// its own container-content read sites in it (CountPool, DoConvert, DoConvertSimultaneous,
// FindItemInfoByNameInContainer, SnapshotContainer) — if the converter or the Probe D tripwire read
// through this mod's own hidden view, composting and the tripwire would both break. See
// ComposterDiag.cs for the wrapped call sites.
internal static class QueryHideBypass
{
    // Plain static bool — the game is single-threaded for our purposes (same assumption every other
    // per-world cache in this mod makes). Save/restore the previous value around a bypassed read
    // rather than hardcoding false on exit, so nested bypassed reads (if any future code path adds
    // one) can never clobber an outer caller's bypass state.
    internal static bool Active;
}

// GetItemCount(ItemInfo) has THREE overloads on ItemContainer — GetItemCount(), GetItemCount(Func<Item,
// bool>), and GetItemCount(ItemInfo) — plus a fourth on IItemFilter, GetItemCount(IItemFilter, bool).
// The explicit Type[] disambiguates to the single-ItemInfo overload only; do NOT patch the others.
[HarmonyPatch(typeof(ItemContainer), nameof(ItemContainer.GetItemCount), new Type[] { typeof(ItemInfo) })]
internal static class GetItemCountQueryHidePatch
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
                Plugin.Logger.LogInfo("[OuthouseComposter][queryhide] GetItemCount patch alive.");
            }

            if (QueryHideBypass.Active) return;
            if (!Plugin.HideNonCompostFromQueries.Value || !Plugin.HideQueryGetItemCount.Value) return;
            if (!OuthouseGate.IsOuthouseContainer(__instance)) return;
            if (OuthouseGate.ItemIsNamed(itemInfo, Plugin.CompostItemName.Value)) return; // compost answers truthfully

            int before = __result;
            __result = 0;

            if (Plugin.EnableDiagnostics.Value && before != 0)
            {
                string name = SafeName(itemInfo);
                if (_loggedItems.Add(name))
                    Plugin.Logger.LogInfo($"[OuthouseComposter][queryhide] hid '{name}' from GetItemCount (returned 0 instead of {before}).");
            }
        }
        catch (Exception ex) { Plugin.Logger.LogError($"[OuthouseComposter] GetItemCountQueryHidePatch error: {ex}"); }
    }

    internal static void ClearLoggedItems() => _loggedItems.Clear();

    private static string SafeName(ItemInfo? info)
    {
        if (info == null) return "?";
        try { return info.Name ?? "?"; } catch { return "?"; }
    }
}

// A filter that does not accept compost must get false (its target is hidden). A filter that DOES
// accept compost gets the honest answer about compost only — this also closes the leak where a very
// broad filter (e.g. "any item") would otherwise reveal the hidden food/seeds via a true HasItem
// answer even with GetItemCount hidden.
[HarmonyPatch(typeof(ItemContainer), nameof(ItemContainer.HasItem), new Type[] { typeof(IItemFilter) })]
internal static class HasItemQueryHidePatch
{
    private static bool _aliveLogged;
    private static bool _resultLogged;

    static void Postfix(ItemContainer __instance, IItemFilter itemFilter, ref bool __result)
    {
        try
        {
            if (!_aliveLogged)
            {
                _aliveLogged = true;
                Plugin.Logger.LogInfo("[OuthouseComposter][queryhide] HasItem patch alive.");
            }

            if (QueryHideBypass.Active) return;
            if (!Plugin.HideNonCompostFromQueries.Value || !Plugin.HideQueryHasItem.Value) return;
            if (!OuthouseGate.IsOuthouseContainer(__instance)) return;

            var compostInfo = ComposterDiag.CompostInfo;
            if (compostInfo == null) return; // fail open — can't identify compost yet, never hide anything before we can

            if (itemFilter == null) return; // fail open

            bool containerHasCompost;
            bool prevBypass = QueryHideBypass.Active;
            QueryHideBypass.Active = true;
            try { containerHasCompost = __instance.GetItemCount(compostInfo) > 0; }
            finally { QueryHideBypass.Active = prevBypass; }

            bool filterAcceptsCompost;
            try { filterAcceptsCompost = itemFilter.Check(compostInfo); }
            catch (Exception ex)
            {
                Plugin.Logger.LogError($"[OuthouseComposter] HasItemQueryHidePatch itemFilter.Check error: {ex}");
                return; // fail open — leave __result untouched
            }

            __result = containerHasCompost && filterAcceptsCompost;

            if (Plugin.EnableDiagnostics.Value && !_resultLogged)
            {
                _resultLogged = true;
                Plugin.Logger.LogInfo($"[OuthouseComposter][queryhide] HasItem answered {__result} (containerHasCompost={containerHasCompost}, filterAcceptsCompost={filterAcceptsCompost}).");
            }
        }
        catch (Exception ex) { Plugin.Logger.LogError($"[OuthouseComposter] HasItemQueryHidePatch error: {ex}"); }
    }

    internal static void ClearLoggedItems() => _resultLogged = false;
}
