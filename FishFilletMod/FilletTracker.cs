using System;
using UnityEngine;

namespace FishFilletMod;

// Drives the non-click bindings: any keyboard key, or a side mouse button (Mouse3+) that Unity's
// EventSystem never turns into a pointer click. Fires on the hovered inventory thumbnail, so the
// gesture stays "point at the fish, press the key" regardless of which key that is.
// Also carries the periodic config reload — BepInEx does not re-read an edited .cfg on its own
// (GroundItemVacuum/SeedScout pattern), so without this a rebind would need a game restart.
public class FilletTracker : MonoBehaviour
{
    private float _cfgReloadTimer;

    private void Update()
    {
        _cfgReloadTimer += Time.deltaTime;
        if (_cfgReloadTimer >= 30f)
        {
            _cfgReloadTimer = 0f;
            try { Plugin.Cfg?.Reload(); } catch { }
            Plugin.RefreshToolCategories();
        }

        if (!Plugin.EnableFillet.Value) return;
        if (Binding.IsClickTrigger) return;   // the OnPointerClick prefix owns mouse0/1/2 bindings
        if (!Input.GetKeyDown(Binding.Key)) return;
        if (!Binding.ModifierHeld()) return;
        if (IsTextInputFocused()) return;

        var panel = Hover.Current;
        if (panel == null) return;

        try { Fillet.Execute(panel); }
        catch (Exception e) { Plugin.Logger.LogWarning($"[FishFillet] fillet keypress err: {e.Message}"); }
    }

    private static bool IsTextInputFocused()
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
}
