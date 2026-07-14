using System;
using SSSGame.AI;
using SSSGame.Network;

namespace SupplyChainMod;

// QuotaActuator (v0.5.0, Phase 2b) — the quota write path, mirroring RankActuator's role for
// priority/rank: BOTH QuotaSpike (the manual hotkey experiment this version ships) and any future
// automated quota-raise controller should drive writes through this SAME method. Writes
// ResourceStorageTaskData.itemInfoQuantity.quantity directly (the game's own quantity setter — no
// list-move involved, unlike RankActuator; quota is a plain Int32 value, not a re-derived rank), then
// calls _RefreshQuantityRange() and probes for a network component to push the change to clients.
//
// Never cache the ResourceStorageTaskData/Workstation wrappers this method is handed — callers must
// re-resolve them fresh from the current station list every time (project-wide gotcha: no per-world
// interop wrappers held across ticks).
internal static class QuotaActuator
{
    // Returns a short path descriptor for the caller's log line, e.g. "write(5->55)".
    internal static string ApplyQuota(StationEntry entry, ResourceStorageTaskData rst, int newQty)
    {
        int orig = 0;
        try { orig = rst.itemInfoQuantity.quantity; }
        catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] [quota] read orig quantity error: {ex}"); }

        string itemName = RankActuator.SafeTaskItemName(rst);
        Plugin.Logger.LogInfo($"[SupplyChain] [quota] quota write: '{itemName}' {orig} -> {newQty}");

        try { rst.itemInfoQuantity.quantity = newQty; }
        catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] [quota] write quantity error: {ex}"); }

        int readback = orig;
        try { readback = rst.itemInfoQuantity.quantity; }
        catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] [quota] readback quantity error: {ex}"); }

        if (readback != newQty)
        {
            Plugin.Logger.LogError(
                $"[SupplyChain] [quota] LOUD: readback MISMATCH for '{itemName}' — wrote {newQty}, read back {readback}. " +
                "The quantity write did not stick (or was squashed).");
        }
        else
        {
            Plugin.Logger.LogInfo($"[SupplyChain] [quota] readback confirmed: '{itemName}' quantity={readback}");
        }

        try
        {
            rst._RefreshQuantityRange();
            Plugin.Logger.LogInfo("[SupplyChain] [quota] _RefreshQuantityRange() succeeded.");
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"[SupplyChain] [quota] _RefreshQuantityRange() failed (non-fatal): {ex}");
        }

        ProbeAndPushNetwork(entry);

        // One actuator per experiment (Phase 1a precedent) — Rpc_ChangeTaskQuantity exists on
        // NetworkWorkstation<T> (Cecil-confirmed) but is left untried this version.
        Plugin.Logger.LogInfo("[SupplyChain] [quota] note: Rpc_ChangeTaskQuantity exists as an untried fallback network path (not used this version).");

        return $"write({orig}->{newQty})";
    }

    // Probe order (copied from WarehouseWatch.ProbeNetwork): own GameObject composite storage, then
    // own GameObject simple storage (base-typed GetComponent finds the weaved numbered variants),
    // then a child walk for each. Calls HostUpdateTasks() on whichever is found first.
    private static void ProbeAndPushNetwork(StationEntry entry)
    {
        try
        {
            NetworkCompositeResourceStorage? composite = null;
            NetworkSimpleResourceStorage? simple = null;
            string via = "none";

            try { composite = entry.Ws.gameObject.GetComponent<NetworkCompositeResourceStorage>(); } catch { }
            if (composite != null) via = "gameObject/composite";

            if (composite == null)
            {
                try { simple = entry.Ws.gameObject.GetComponent<NetworkSimpleResourceStorage>(); } catch { }
                if (simple != null) via = "gameObject/simple";
            }
            if (composite == null && simple == null)
            {
                try { composite = StationWalker.FindComponent<NetworkCompositeResourceStorage>(entry.Ws.transform); } catch { }
                if (composite != null) via = "childWalk/composite";
            }
            if (composite == null && simple == null)
            {
                try { simple = StationWalker.FindComponent<NetworkSimpleResourceStorage>(entry.Ws.transform); } catch { }
                if (simple != null) via = "childWalk/simple";
            }

            object? found = composite != null ? composite : (object?)simple;
            string foundClass = found != null ? Common.NativeClassName(found) : "none";
            Plugin.Logger.LogInfo($"[SupplyChain] [quota] network probe: found={foundClass} via={via}");

            if (composite != null)
            {
                try { composite.HostUpdateTasks(); Plugin.Logger.LogInfo("[SupplyChain] [quota] HostUpdateTasks() on composite storage succeeded."); }
                catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] [quota] HostUpdateTasks() on composite storage failed: {ex}"); }
            }
            else if (simple != null)
            {
                try { simple.HostUpdateTasks(); Plugin.Logger.LogInfo("[SupplyChain] [quota] HostUpdateTasks() on simple storage succeeded."); }
                catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] [quota] HostUpdateTasks() on simple storage failed: {ex}"); }
            }
        }
        catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] [quota] network probe error: {ex}"); }
    }
}
