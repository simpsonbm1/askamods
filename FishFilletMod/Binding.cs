using System;
using UnityEngine;
using UnityEngine.EventSystems;

namespace FishFilletMod;

// Parses the configured fillet gesture (modifier key + trigger key) from the two config strings.
// The trigger is ANY Unity KeyCode — mouse buttons (Mouse0..Mouse6) and keyboard keys alike — which
// is why there are two execution paths downstream:
//   * Mouse0/1/2 are dispatched by Unity's EventSystem as pointer clicks, so those bind through the
//     ItemThumbnailPanel.OnPointerClick prefix, which can also SUPPRESS the native click.
//   * Everything else (keyboard keys, and side buttons Mouse3+ which the EventSystem never turns
//     into clicks) binds through hover-tracking + Input.GetKeyDown polling in FilletTracker.
// Re-parses only when a raw string changes, so a live config reload rebinds without a restart and
// the per-click path does no work in the common case.
internal static class Binding
{
    private static string _rawKey = "\0";   // sentinel: never equal to a real config value on first check
    private static string _rawMod = "\0";

    private static KeyCode[] _modKeys = new[] { KeyCode.LeftShift, KeyCode.RightShift };
    private static string _modLabel = "Shift";
    private static KeyCode _key = KeyCode.Mouse1;
    private static bool _isClick = true;
    private static PointerEventData.InputButton _button = PointerEventData.InputButton.Right;

    public static KeyCode Key { get { Refresh(); return _key; } }

    // True when the trigger is a mouse button the EventSystem delivers as a click (Mouse0/1/2),
    // meaning the OnPointerClick prefix owns this binding and the polling path must stay out.
    public static bool IsClickTrigger { get { Refresh(); return _isClick; } }

    public static PointerEventData.InputButton Button { get { Refresh(); return _button; } }

    // True when the configured modifier is held (always true when the modifier is "None").
    public static bool ModifierHeld()
    {
        Refresh();
        if (_modKeys.Length == 0) return true;
        foreach (var k in _modKeys)
            if (Input.GetKey(k)) return true;
        return false;
    }

    public static string Describe()
    {
        Refresh();
        string mod = _modKeys.Length == 0 ? "" : _modLabel + "+";
        return mod + _key;
    }

    private static void Refresh()
    {
        string rawKey = Plugin.FilletKeyRaw.Value ?? "";
        string rawMod = Plugin.ModifierKeyRaw.Value ?? "";
        if (rawKey == _rawKey && rawMod == _rawMod) return;
        _rawKey = rawKey;
        _rawMod = rawMod;
        _modKeys = ParseModifier(rawMod);
        _key = ParseKey(rawKey);
        _isClick = _key == KeyCode.Mouse0 || _key == KeyCode.Mouse1 || _key == KeyCode.Mouse2;
        _button = _key == KeyCode.Mouse0 ? PointerEventData.InputButton.Left
                : _key == KeyCode.Mouse2 ? PointerEventData.InputButton.Middle
                : PointerEventData.InputButton.Right;

        Plugin.Logger.LogInfo($"[FishFillet] Fillet gesture bound to {Describe()} ({(_isClick ? "click" : "hover+press")} path).");
        if (_modKeys.Length == 0 && _key == KeyCode.Mouse0)
            Plugin.Logger.LogWarning("[FishFillet] Modifier is 'None' and the trigger is Mouse0 — every plain left-click on a qualifying item will fillet it. Set FilletModifierKey if that is not intended.");
        if (!_isClick && !Hover.Armed)
            Plugin.Logger.LogWarning($"[FishFillet] '{rawKey}' needs the hover path, but hover tracking did not arm — filleting will not trigger. Use Mouse0, Mouse1 or Mouse2 instead.");
    }

    // "None"/"" -> no modifier. "Shift"/"Ctrl"/"Alt" -> either side. Anything else -> a Unity KeyCode name.
    private static KeyCode[] ParseModifier(string raw)
    {
        raw = raw.Trim();
        _modLabel = raw;
        if (raw.Length == 0 || raw.Equals("none", StringComparison.OrdinalIgnoreCase))
            return Array.Empty<KeyCode>();

        switch (raw.ToLowerInvariant())
        {
            case "shift": _modLabel = "Shift"; return new[] { KeyCode.LeftShift, KeyCode.RightShift };
            case "ctrl":
            case "control": _modLabel = "Ctrl"; return new[] { KeyCode.LeftControl, KeyCode.RightControl };
            case "alt": _modLabel = "Alt"; return new[] { KeyCode.LeftAlt, KeyCode.RightAlt };
        }

        if (Enum.TryParse<KeyCode>(raw, true, out var parsed))
        {
            _modLabel = parsed.ToString();
            return new[] { parsed };
        }

        Plugin.Logger.LogWarning($"[FishFillet] FilletModifierKey '{raw}' is not a recognised key — falling back to Shift.");
        _modLabel = "Shift";
        return new[] { KeyCode.LeftShift, KeyCode.RightShift };
    }

    // Any Unity KeyCode name, plus friendly aliases for the three EventSystem mouse buttons.
    private static KeyCode ParseKey(string raw)
    {
        switch (raw.Trim().ToLowerInvariant())
        {
            case "": return KeyCode.Mouse1;
            case "left":
            case "lmb":
            case "leftclick":
            case "0": return KeyCode.Mouse0;
            case "right":
            case "rmb":
            case "rightclick":
            case "1": return KeyCode.Mouse1;
            case "middle":
            case "mmb":
            case "middleclick":
            case "2": return KeyCode.Mouse2;
        }

        if (Enum.TryParse<KeyCode>(raw.Trim(), true, out var parsed))
            return parsed;

        Plugin.Logger.LogWarning($"[FishFillet] FilletKey '{raw}' is not a recognised Unity KeyCode — falling back to Mouse1 (right-click).");
        return KeyCode.Mouse1;
    }
}
