using System;
using System.Collections.Generic;
using SandSailorStudio.Inventory;
using SSSGame.Combat;
using UnityEngine;

namespace VillagerAmmoMod;

// v0.1.2: detection moved entirely to polling - NO patch on any ammo-event method (see
// Patches/AmmoPatches.cs header for why any patch on RangedManager._OnAmmoRemoved is fatal at
// mod-load). Every 0.5s this walks the registry of non-player RangedManagers (captured via a safe
// parameterless Awake postfix) and compares each one's current ammo count against its last-seen
// baseline to detect consumption. Also periodically reloads the config file so the gating options
// can be tuned mid-session without a relaunch (SeedScout / GroundItemVacuum pattern).
public class AmmoTracker : MonoBehaviour
{
    private const float PollInterval = 0.5f;

    private float _cfgReloadTimer = 0f;
    private float _pollTimer = 0f;
    private float _cleanupTimer = 0f;
    private bool _pollActiveLogged = false;

    private void Update()
    {
        _cfgReloadTimer += Time.deltaTime;
        if (_cfgReloadTimer >= 5f)
        {
            _cfgReloadTimer = 0f;
            try { Plugin.Cfg?.Reload(); } catch { }
        }

        _cleanupTimer += Time.deltaTime;
        if (_cleanupTimer >= Plugin.CleanupCheckSeconds.Value)
        {
            _cleanupTimer = 0f;
            if (Plugin.TargetCleanupEnabled.Value) RunTargetCleanup();
        }

        _pollTimer += Time.deltaTime;
        if (_pollTimer < PollInterval) return;
        _pollTimer = 0f;

        // Keep per-frame work at ~zero when there's nothing to do (framerate rule).
        RangedManager[] snapshot;
        lock (Plugin.RegistryLock)
        {
            if (Plugin.Registry.Count == 0) return;
            snapshot = new RangedManager[Plugin.Registry.Count];
            Plugin.Registry.CopyTo(snapshot);
        }

        bool diag = Plugin.EnableDiagnostics.Value;
        if (diag && !_pollActiveLogged)
        {
            _pollActiveLogged = true;
            Plugin.Logger.LogInfo($"[VillagerAmmo] polling active, {snapshot.Length} ranged manager(s) tracked.");
        }

        foreach (var mgr in snapshot)
        {
            try
            {
                ProcessManager(mgr, diag);
            }
            catch (Exception ex)
            {
                if (diag) Plugin.Logger.LogDebug($"[VillagerAmmo] removing manager from registry after exception: {ex}");
                lock (Plugin.RegistryLock) { Plugin.Registry.Remove(mgr); }
                Plugin.Baselines.Remove(mgr);
                Plugin.LastShootingSeen.Remove(mgr);
            }
        }
    }

    private void ProcessManager(RangedManager mgr, bool diag)
    {
        if (mgr == null) // Unity destroyed-object equality
        {
            lock (Plugin.RegistryLock) { Plugin.Registry.Remove(mgr!); }
            Plugin.Baselines.Remove(mgr!);
            Plugin.LastShootingSeen.Remove(mgr!);
            return;
        }

        bool isPlayer = true; // fail-safe: skip if we can't tell
        try { isPlayer = mgr.IsPlayer; } catch { }
        if (isPlayer) return;

        bool hasAuth = false; // fail-safe: skip if we can't tell
        try { hasAuth = mgr.HasAuthority; } catch { }
        if (!hasAuth) return;

        // Read State once per poll for every live tracked manager (not just on a drop) so a
        // shooting cycle that has already returned to StandBy by poll time is still remembered -
        // closes the poll-race leak (see RecentShootingWindowSeconds).
        RangedManager.AimState state = RangedManager.AimState.None;
        try { state = mgr.State; } catch { }
        if (state == RangedManager.AimState.Aim
            || state == RangedManager.AimState.Fire
            || state == RangedManager.AimState.Reload)
        {
            Plugin.LastShootingSeen[mgr] = Time.time;
        }

        var ammo = mgr.CurrentRangedAmmo;
        if (ammo == null)
        {
            Plugin.Baselines.Remove(mgr);
            return;
        }

        int count = ammo.RealAmmoCount;

        if (!Plugin.Baselines.TryGetValue(mgr, out int baseline))
        {
            Plugin.Baselines[mgr] = count;
            return;
        }

        if (count > baseline)
        {
            // Restock/transfer in - just re-baseline, nothing to refund.
            Plugin.Baselines[mgr] = count;
            return;
        }
        if (count == baseline) return;

        // count < baseline: ammo was consumed/removed since the last poll.
        int deficit = baseline - count;

        bool recentlyShooting = false;
        float shootingAge = -1f;
        if (Plugin.LastShootingSeen.TryGetValue(mgr, out float lastSeen))
        {
            shootingAge = Time.time - lastSeen;
            recentlyShooting = shootingAge <= Plugin.RecentShootingWindowSeconds.Value;
        }

        bool shouldRefund = Plugin.Enabled.Value
            && (!Plugin.RefundOnlyWhenShooting.Value
                || state == RangedManager.AimState.Aim
                || state == RangedManager.AimState.Fire
                || state == RangedManager.AimState.Reload
                || recentlyShooting);

        if (!shouldRefund)
        {
            // Not shooting-related (or mod disabled) - adopt the new count as the manual-withdrawal
            // path, don't refund it.
            string age = shootingAge < 0f ? "never" : $"{shootingAge:F1}s ago";
            if (diag) Plugin.Logger.LogInfo($"[VillagerAmmo] drop of {deficit} adopted (state={state}, lastShooting {age})");
            Plugin.Baselines[mgr] = count;
            return;
        }

        ItemContainer? container = null;
        ItemInfo? info = null;
        try
        {
            container = ammo._itemContainer;
            if (container != null)
            {
                info = container.GetItem(0)?.info;
                if (info != null)
                    Plugin.InfoCache[container.Pointer] = info;
                else
                    Plugin.InfoCache.TryGetValue(container.Pointer, out info);
            }
        }
        catch { }

        if (container == null || info == null)
        {
            if (diag) Plugin.Logger.LogInfo($"[VillagerAmmo] skip refund: container/info null (state={state}).");
            Plugin.Baselines[mgr] = count;
            return;
        }

        int added;
        try
        {
            added = container.AddItems(info, deficit);
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"[VillagerAmmo] refund failed: {ex}");
            Plugin.Baselines[mgr] = count;
            return;
        }

        Plugin.Baselines[mgr] = count + added;
        if (diag) Plugin.Logger.LogInfo($"[VillagerAmmo] refunded {added}/{deficit} '{info.Name}' (state={state})");
    }

    // v0.2.0: periodically cull stuck arrows from archery-range targets via the game's own
    // ReleaseAllStuckObjects() - unlimited villager ammo otherwise lets thousands of recoverable
    // arrows accumulate in targets (confirmed framerate collapse in co-op with ~2000 arrows).
    private void RunTargetCleanup()
    {
        bool diag = Plugin.EnableDiagnostics.Value;
        int threshold = Plugin.StuckArrowThreshold.Value;

        ProjectileTargetHelper[] snapshot;
        lock (Plugin.TargetRegistryLock)
        {
            if (Plugin.TargetRegistry.Count == 0) return;
            snapshot = new ProjectileTargetHelper[Plugin.TargetRegistry.Count];
            Plugin.TargetRegistry.CopyTo(snapshot);
        }

        foreach (var helper in snapshot)
        {
            try
            {
                if (helper == null) // Unity destroyed-object equality
                {
                    lock (Plugin.TargetRegistryLock) { Plugin.TargetRegistry.Remove(helper!); }
                    continue;
                }

                int count = helper._stuckObjects?.Count ?? 0;

                if (diag && count > 0)
                    Plugin.Logger.LogInfo($"[VillagerAmmo] target has {count} stuck object(s).");

                if (count < threshold) continue;

                bool auth = false;
                try { auth = helper._hasAuthority; } catch { }
                if (!auth) continue;

                helper.ReleaseAllStuckObjects();
                int after = helper._stuckObjects?.Count ?? 0;
                Plugin.Logger.LogInfo($"[VillagerAmmo] released stuck arrows: {count} -> {after}.");
            }
            catch (Exception ex)
            {
                if (diag) Plugin.Logger.LogDebug($"[VillagerAmmo] removing target from registry after exception: {ex}");
                lock (Plugin.TargetRegistryLock) { Plugin.TargetRegistry.Remove(helper); }
            }
        }
    }
}
