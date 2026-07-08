using System;
using System.Collections.Generic;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using SandSailorStudio.Inventory;
using SSSGame;
using SSSGame.UI;

namespace FishFilletMod;

// Fillet trigger: Shift+Right-click a qualifying fish in the inventory. This gesture does NOT invoke the
// native Shift+LMB harvest router (which fails its own knife check and pops a cosmetic "can't be harvested"
// toast — confirmed in-game 2026-07-08), so filleting via right-click is toast-free. We drive the game's own
// harvest (temp-null the 'Knives' tool requirement -> CommandHarvestItem -> restore) and return false to
// suppress the native right-click (move-to-container) for THIS gesture only; all other clicks are untouched.
[HarmonyPatch(typeof(ItemThumbnailPanel), nameof(ItemThumbnailPanel.OnPointerClick))]
public static class FilletRightClickPatch
{
    private static bool _filleting; // re-entrancy guard (our CommandHarvestItem call must not re-enter)

    [HarmonyPrefix]
    public static bool Prefix(ItemThumbnailPanel __instance, UnityEngine.EventSystems.PointerEventData eventData)
    {
        if (!Plugin.EnableFillet.Value || _filleting) return true; // let native handle
        try
        {
            if (eventData == null) return true;
            if (eventData.button != UnityEngine.EventSystems.PointerEventData.InputButton.Right) return true;
            bool shift = UnityEngine.Input.GetKey(UnityEngine.KeyCode.LeftShift)
                      || UnityEngine.Input.GetKey(UnityEngine.KeyCode.RightShift);
            if (!shift) return true;

            ItemInfo? info = __instance.ItemInfo;
            if (!Fillet.Qualifies(info)) return true;

            var res = Fillet.GetResource(info);
            var mv = res?.mainHarvestMoveset;
            ItemCategoryInfo? savedReq = null;
            if (mv != null && mv.requiredEqippmentCategory != null)
            {
                savedReq = mv.requiredEqippmentCategory;
                mv.requiredEqippmentCategory = null; // temporarily bare-hand so the harvest op's tool gate passes
            }

            string nm = "?";
            try { nm = info!.Name; } catch { }
            Plugin.Logger.LogInfo($"[FishFillet] Shift+RightClick fillet on '{nm}' -> driving CommandHarvestItem");

            _filleting = true;
            try { __instance.CommandHarvestItem(); }
            finally
            {
                _filleting = false;
                if (mv != null && savedReq != null) mv.requiredEqippmentCategory = savedReq; // restore immediately
            }
            return false; // suppress native right-click (move-to-container) for THIS gesture only
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"[FishFillet] shift+rclick err: {e.Message}");
            return true;
        }
    }
}

internal static class Fillet
{
    private static readonly Dictionary<int, bool> _qual = new();

    // Cached per item id — Compute touches interop and this gate is queried per click.
    public static bool Qualifies(ItemInfo? info)
    {
        if (info == null) return false;
        int id = info.id;
        if (_qual.TryGetValue(id, out var cached)) return cached;
        bool q = Compute(info);
        _qual[id] = q;
        return q;
    }

    private static bool Compute(ItemInfo info)
    {
        if ((object)info is not Il2CppObjectBase ob) return false;
        var ptr = ob.Pointer;
        if (ptr == IntPtr.Zero) return false;
        try
        {
            var itemClass = IL2CPP.il2cpp_object_get_class(ptr);
            var resClass = Il2CppInterop.Runtime.Il2CppClassPointerStore<ResourceInfo>.NativeClassPtr;
            if (resClass == IntPtr.Zero || !IL2CPP.il2cpp_class_is_subclass_of(itemClass, resClass, false))
                return false;

            var res = new ResourceInfo(ptr);
            if (!res.CanBeHarvested()) return false;

            var mv = res.mainHarvestMoveset;
            if (mv == null) return false; // bare-hand items already harvest fine; only unlock tool-gated ones
            var cat = mv.requiredEqippmentCategory;
            if (cat == null) return false;
            string catName = cat.Name ?? "";
            foreach (var allowed in Plugin.ToolCategories)
                if (allowed.Length > 0 && catName.IndexOf(allowed, StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            return false;
        }
        catch { return false; }
    }

    // Returns a ResourceInfo wrapper if the item is ResourceInfo-kind, else null. (Managed cast lies for
    // base-typed interop objects, so we native-subclass-check then rewrap via new ResourceInfo(ptr).)
    public static ResourceInfo? GetResource(ItemInfo? info)
    {
        if (info == null) return null;
        if ((object)info is not Il2CppObjectBase ob) return null;
        var ptr = ob.Pointer;
        if (ptr == IntPtr.Zero) return null;
        try
        {
            var itemClass = IL2CPP.il2cpp_object_get_class(ptr);
            var resClass = Il2CppInterop.Runtime.Il2CppClassPointerStore<ResourceInfo>.NativeClassPtr;
            if (resClass == IntPtr.Zero || !IL2CPP.il2cpp_class_is_subclass_of(itemClass, resClass, false))
                return null;
            return new ResourceInfo(ptr);
        }
        catch { return null; }
    }
}
