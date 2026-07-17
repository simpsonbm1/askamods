using System;

namespace SupplyChainMod;

// ArmState (v0.17.0) — hoists SupplyController's F11 armed toggle out of the EnableController-gated
// tick so a second actuator (TierCaseController) can read/toggle the SAME shared armed flag even
// when the live cfg sets EnableController=false (the v0.17.0 test path retires the legacy
// complaint-driven SupplyController from the test save via EnableController=false, cfg-only — see
// DEMAND_MODEL_PLAN.md's "Tier arming v0.17.0 spec"). Before this, the F11 hotkey check in
// SupplyChainTracker.Update() and the lazy Armed-sync in SupplyController.Tick() were both gated on
// Plugin.EnableController — with EnableController=false neither ever ran, so a second controller
// could never observe an armed toggle at all.
//
// SupplyController.Armed is now a forwarding property onto ArmState.Armed (no other file needed to
// change its read sites — ClogController still reads SupplyController.Armed). World-leave reset-to-
// ControllerStartArmed is unchanged in effect: SupplyController.NoteWorldLeft (itself called
// unconditionally from SupplyChainTracker.NoteWorldLeft, regardless of EnableController) now calls
// ArmState.ResetForWorldLeave() instead of assigning its own field directly.
//
// v0.17.1 — F11 kill-switch protocol: the armed->disarmed transition (only) additionally calls
// TierCaseController.NoteDisarmed(), which ends every active tier-case hold IMMEDIATELY (a real
// revert for a hold that actually wrote, a silent drop for a dry-run hold) instead of merely
// stopping new writes and leaving live holds to ride out their normal judge/revert cycle. Disarming
// is meant to be an instant, trustworthy stop.
internal static class ArmState
{
    internal static bool Armed;
    internal static bool ArmedInitialized;

    internal static bool ToggleArmed()
    {
        try
        {
            Armed = !Armed;
            if (Armed)
            {
                Plugin.Logger.LogInfo("[SupplyChain] [arm] Autopilot ARMED — live actuation.");
                SupplyChainTracker.ShowMessage("Autopilot ARMED — live actuation", 6f);
            }
            else
            {
                Plugin.Logger.LogInfo("[SupplyChain] [arm] Autopilot DRY-RUN — logging only.");
                SupplyChainTracker.ShowMessage("Autopilot DRY-RUN — logging only", 6f);

                // v0.17.1 — F11 kill-switch protocol: disarming ends every active tier-case hold
                // IMMEDIATELY (real revert if it actually wrote, silent drop otherwise) instead of
                // just stopping new writes. Armed->disarmed transition only.
                try { TierCaseController.NoteDisarmed(); }
                catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] [arm] TierCaseController.NoteDisarmed error: {ex}"); }
            }
            return Armed;
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"[SupplyChain] [arm] ToggleArmed error: {ex}");
            return Armed;
        }
    }

    // Called from SupplyController.NoteWorldLeft (chained unconditionally from
    // SupplyChainTracker.NoteWorldLeft) — preserves the pre-v0.17.0 reset-to-ControllerStartArmed
    // behavior regardless of EnableController/EnableTierCases.
    internal static void ResetForWorldLeave()
    {
        Armed = Plugin.ControllerStartArmed.Value;
        ArmedInitialized = true;
    }

    // Guards against the bare C# default (false) if NoteWorldLeft hasn't synced it yet this launch
    // (mirrors SupplyController's own former lazy-init comment: NoteWorldLeft doesn't reliably fire
    // before the FIRST real world session on every launch path). Callers invoke this once per own
    // Tick/poll — cheap no-op once ArmedInitialized is true.
    internal static void EnsureInitialized()
    {
        if (ArmedInitialized) return;
        Armed = Plugin.ControllerStartArmed.Value;
        ArmedInitialized = true;
    }
}
