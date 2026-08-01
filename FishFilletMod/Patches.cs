using System;
using System.Collections.Generic;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using SandSailorStudio.Inventory;
using SSSGame;
using SSSGame.UI;

namespace FishFilletMod;

// Click path for the fillet gesture — owns the Mouse0/Mouse1/Mouse2 bindings (default Shift+Mouse1).
// The default gesture does NOT invoke the native Shift+LMB harvest router (which fails its own knife check
// and pops a cosmetic "can't be harvested" toast — confirmed in-game 2026-07-08), so filleting via
// right-click is toast-free. Returning false suppresses the native handling of THIS gesture only; all other
// clicks are untouched. Keyboard and side-button bindings go through FilletTracker instead, since the
// EventSystem only dispatches clicks for buttons 0-2. The gesture is rebindable because other mods bind
// inventory right-click too (Nexus report, 2026-08-01).
[HarmonyPatch(typeof(ItemThumbnailPanel), nameof(ItemThumbnailPanel.OnPointerClick))]
public static class FilletRightClickPatch
{
    [HarmonyPrefix]
    public static bool Prefix(ItemThumbnailPanel __instance, UnityEngine.EventSystems.PointerEventData eventData)
    {
        if (!Plugin.EnableFillet.Value || Fillet.Busy) return true; // let native handle
        try
        {
            if (eventData == null) return true;
            if (!Binding.IsClickTrigger) return true;  // a keyboard/side-button binding: FilletTracker owns it
            if (eventData.button != Binding.Button) return true;
            if (!Binding.ModifierHeld()) return true;

            if (!Fillet.Execute(__instance)) return true;
            return false; // suppress native handling (right-click = move-to-container) for THIS gesture only
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"[FishFillet] fillet click err: {e.Message}");
            return true;
        }
    }
}

// Hover tracking for the non-click bindings.
//
// `ItemThumbnailPanel` declares NO OnPointerEnter/OnPointerExit — confirmed against the interop
// assemblies 2026-08-01, its base is plain MonoBehaviour. Hover reaches the panel indirectly:
// `SSSGame.ItemHighlightBehaviour : UnityEngine.UI.Selectable` owns the pointer handlers and raises
// `OnHighlighted : Il2CppSystem.Action<bool>`, and `ItemThumbnailPanel._OnHighlighted(bool)` is the
// subscriber. So THAT is the patch target: one hook, both edges, and `__instance` is already the
// panel we want — no component-to-panel mapping. It is a delegate target, so the IL2CPP AOT
// inliner cannot erase it.
//
// Patched MANUALLY (Plugin.Load) rather than by attribute, so a build without the handler degrades
// to "hover path unavailable" instead of throwing out of PatchAll and taking the whole mod down.
// Armed reports which way it went, and Seen reports whether it ever actually fired.
public static class Hover
{
    public static bool Armed;
    public static bool Seen;

    // The WRAPPER is stored, never a bare IntPtr: the wrapper holds an il2cpp GC handle, so the
    // object cannot be collected out from under us, and a Unity-destroyed panel (inventory closed
    // without an un-highlight) shows up as `== null` instead of a freed pointer we would dereference
    // into a native AV. Cleared as soon as it reads destroyed.
    private static ItemThumbnailPanel? _current;

    public static ItemThumbnailPanel? Current
    {
        get
        {
            var c = _current;
            if (c == null) { _current = null; return null; }  // covers Unity-destroyed, not just null
            return c;
        }
    }

    // value=true -> this panel became the highlighted one. value=false -> it stopped being it, but
    // only clear when the LEAVING panel is the one we recorded: sliding between two thumbnails can
    // deliver highlight(B) before un-highlight(A), and an unconditional clear would drop the live
    // hover. Reading .Pointer does not dereference native memory, so the compare is safe even if one
    // side has since been destroyed.
    public static void HighlightPostfix(ItemThumbnailPanel __instance, bool value)
    {
        try
        {
            if (value)
            {
                _current = __instance;
                if (!Seen)
                {
                    Seen = true;
                    Plugin.Logger.LogInfo("[FishFillet] Hover tracking is LIVE (first highlighted thumbnail seen) — keyboard / side-button bindings will fire.");
                }
            }
            else if (Ptr(__instance) == Ptr(_current))
            {
                _current = null;
            }
        }
        catch { _current = null; }
    }

    private static IntPtr Ptr(ItemThumbnailPanel? p)
        => p is not null && (object)p is Il2CppObjectBase ob ? ob.Pointer : IntPtr.Zero;
}

internal static class Fillet
{
    private static readonly Dictionary<int, bool> _qual = new();

    // Re-entrancy guard: the CommandHarvestItem we drive must not re-enter the click prefix.
    public static bool Busy { get; private set; }

    // The fillet drive, shared by both trigger paths. Returns true if the item qualified and the
    // harvest was driven (the click path then suppresses the native click), false to stand aside.
    public static bool Execute(ItemThumbnailPanel panel)
    {
        if (panel == null || Busy) return false;

        ItemInfo? info = panel.ItemInfo;
        if (!Qualifies(info)) return false;

        var res = GetResource(info);
        var mv = res?.mainHarvestMoveset;
        ItemCategoryInfo? savedReq = null;
        if (mv != null && mv.requiredEqippmentCategory != null)
        {
            savedReq = mv.requiredEqippmentCategory;
            mv.requiredEqippmentCategory = null; // temporarily bare-hand so the harvest op's tool gate passes
        }

        string nm = "?";
        try { nm = info!.Name; } catch { }
        Plugin.Logger.LogInfo($"[FishFillet] {Binding.Describe()} fillet on '{nm}' -> driving CommandHarvestItem");

        Busy = true;
        try { panel.CommandHarvestItem(); }
        finally
        {
            Busy = false;
            if (mv != null && savedReq != null) mv.requiredEqippmentCategory = savedReq; // restore immediately
        }
        return true;
    }

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
            foreach (var allowed in Plugin.ToolCategories)
                if (allowed.Length > 0 && CatMatches(cat, allowed))
                    return true;
            return false;
        }
        catch { return false; }
    }

    // Locale-proof (v1.2.0): the tool-category config token ("Knives") is matched against BOTH the
    // localized display name (cat.Name, e.g. "Messer" in German) AND the Unity asset name (cat.name,
    // e.g. "Categ_Tools_Knives") — the asset name is dev-authored, English-derived and never
    // translates, so the English default keeps working in every language. Verified additive against
    // the v0.3.0 locale audit: in English no category matches on asset name without also matching on
    // display name, so English behaviour is unchanged.
    private static bool CatMatches(ItemCategoryInfo cat, string token)
    {
        try { var n = cat.Name; if (!string.IsNullOrEmpty(n) && n.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0) return true; } catch { }
        try { var a = cat.name; if (!string.IsNullOrEmpty(a) && a.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0) return true; } catch { }
        return false;
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
