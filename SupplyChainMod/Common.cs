using System;
using System.Runtime.InteropServices;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using SSSGame;
using SandSailorStudio.Inventory;

namespace SupplyChainMod;

// Shared, READ-ONLY helpers used by every component. Phase 0 (v0.1.0) never writes game state —
// no priority changes, no task creation, no inventory writes. Everything here only observes and
// logs.
internal static class Common
{
    // Typing guard: keystrokes in the game's text fields also reach Input.GetKeyDown, so
    // letter-bound hotkeys fire while the player types. Copied verbatim from the repo-wide proven
    // pattern (TreeRespawnMod/GroundItemVacuumMod/OuthouseComposterMod).
    internal static bool IsTextInputFocused()
    {
        try
        {
            var es = UnityEngine.EventSystems.EventSystem.current;
            var go = es != null ? es.currentSelectedGameObject : null;
            if (go == null) return false;
            return go.GetComponent<TMPro.TMP_InputField>() != null
                || go.GetComponent<UnityEngine.UI.InputField>() != null;
        }
        catch { return false; }
    }

    // DefaultName is the pristine type name (survives player renames); StructureName/GetName() is
    // the display name. Same pattern as OuthouseComposterMod.SafeName/SafeDefaultName.
    internal static string SafeName(Structure? s)
    {
        if (s == null) return "?";
        try { var n = s.GetName(); if (!string.IsNullOrEmpty(n)) return n; } catch { }
        try { var n = s.StructureName; if (!string.IsNullOrEmpty(n)) return n; } catch { }
        return "?";
    }

    internal static string SafeDefaultName(Structure? s)
    {
        if (s == null) return "?";
        try { return s.DefaultName ?? "?"; } catch { return "?"; }
    }

    // World-position string key — never key long-lived state by UniqueId/wrapper reference
    // (project-wide gotcha). Used to identify a structure/workstation across polling ticks without
    // holding onto its interop wrapper.
    internal static string PosKey(Structure? s)
    {
        if (s == null) return "?";
        try { return s.GetPosition().ToString(); } catch { return "?"; }
    }

    internal static string SafeItemName(ItemInfo? info)
    {
        if (info == null) return "?";
        try { return info.Name ?? "?"; } catch { return "?"; }
    }

    // Managed casts LIE for interop objects materialized under a base declared type (project-wide
    // gotcha) — always read the NATIVE class name via IL2CPP.il2cpp_object_get_class, never as/is.
    // Works for ANY interop wrapper (UnityEngine.Object-derived types included), unlike
    // GetIl2CppType() below which only compiles on Il2CppSystem.Object-derived types.
    internal static string NativeClassName(object? obj) => NativeClassName(obj, out _);

    internal static string NativeClassName(object? obj, out IntPtr pointer)
    {
        pointer = IntPtr.Zero;
        if (obj == null) return "null";
        try
        {
            if (obj is Il2CppObjectBase b)
            {
                pointer = b.Pointer;
                IntPtr cls = IL2CPP.il2cpp_object_get_class(pointer);
                return Marshal.PtrToStringAnsi(IL2CPP.il2cpp_class_get_name(cls)) ?? "?";
            }
        }
        catch { }
        return obj.GetType().Name;
    }

    // Native pointer of an interop wrapper, hex-formatted (identity/debug use only).
    internal static string PointerHex(object? obj)
    {
        NativeClassName(obj, out var ptr);
        return ptr == IntPtr.Zero ? "n/a" : "0x" + ptr.ToString("X");
    }

    // Raw native pointer, for identity COMPARISON (v0.1.3 BomDump pointer-identity diagnostic —
    // is a task item's ItemInfo the SAME native instance as a blueprint's produced ItemInfo?).
    // ItemInfo's compile-time chain runs through the unity-libs UnityEngine.Object stub (project-
    // wide gotcha), so `.Pointer` isn't directly available on it — same box-to-Il2CppObjectBase
    // pattern as NativeClassName/PointerHex above.
    internal static IntPtr GetPointer(object? obj)
    {
        NativeClassName(obj, out var ptr);
        return ptr;
    }
}
