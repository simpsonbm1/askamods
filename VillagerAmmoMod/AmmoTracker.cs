using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using SandSailorStudio.Inventory;
using SSSGame;
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
        if (_cfgReloadTimer >= 30f)
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

        var pollSw = Stopwatch.StartNew();

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

        pollSw.Stop();
        double pollMs = pollSw.Elapsed.TotalMilliseconds;
        if (pollMs > 2.0)
            Plugin.Logger.LogInfo($"[Perf][VillagerAmmo] poll took {pollMs:F1} ms (n={snapshot.Length})");
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

    // v0.2.1: v0.2.0's ReleaseAllStuckObjects() cull never fired in-game - the ~2,548 accumulated
    // stuck arrows observed at the archery range turned out to be ordinary DynamicItemObject ground
    // items (category chain Arrows/Weapons), never registered in a target's hit-time _stuckObjects
    // list. This now culls those ground items directly (near-target, category-matched), then keeps
    // the original ReleaseAllStuckObjects() sweep as a secondary pass for whatever DOES register.
    private void RunTargetCleanup()
    {
        var sw = Stopwatch.StartNew();
        int itemSnapshotLen = 0;
        int targetSnapshotLen = 0;
        try
        {
            RunTargetCleanupCore(ref itemSnapshotLen, ref targetSnapshotLen);
        }
        finally
        {
            sw.Stop();
            double ms = sw.Elapsed.TotalMilliseconds;
            if (ms > 2.0)
                Plugin.Logger.LogInfo($"[Perf][VillagerAmmo] cleanup took {ms:F1} ms (items={itemSnapshotLen}, targets={targetSnapshotLen})");
        }
    }

    private void RunTargetCleanupCore(ref int itemSnapshotLen, ref int targetSnapshotLen)
    {
        bool diag = Plugin.EnableDiagnostics.Value;
        int threshold = Plugin.StuckArrowThreshold.Value;

        // 1. Host gate FIRST - the cull replicates a world-state change (ground-item removal).
        if (!IsHost())
        {
            if (diag) Plugin.Logger.LogInfo("[VillagerAmmo] cleanup pass skipped: not host.");
            return;
        }

        // 2. Snapshot target positions from the existing TargetRegistry; drop dead helpers.
        ProjectileTargetHelper[] targetSnapshot;
        lock (Plugin.TargetRegistryLock)
        {
            targetSnapshot = new ProjectileTargetHelper[Plugin.TargetRegistry.Count];
            Plugin.TargetRegistry.CopyTo(targetSnapshot);
        }
        targetSnapshotLen = targetSnapshot.Length;

        // v0.2.3: TargetNameMatch scoping - captured ProjectileTargetHelpers sit on far more than
        // just archery targets/dummies (v0.2.2 census: 112 helpers = 6 archery targets + 6 training
        // dummies + 79 villagers + skeletons/animals/harvest nodes), which made the arrow cull below
        // accidentally town-wide. Parsed fresh each pass (cheap - short comma list) so a mid-session
        // config edit takes effect on the next CleanupCheckSeconds tick.
        string[] nameMatchTokens = ParseNameMatchTokens();

        // v0.2.2: one-time-per-world-session census of what the tracked ProjectileTargetHelpers
        // actually sit on (archery targets vs dummies vs creatures vs structures), so a later
        // version can scope the arrow cull correctly. Diagnostics-gated, ~100 lines once per session.
        // Unfiltered by design (it's the diagnostic) - each line now also reports the same matcher's
        // verdict so a bad TargetNameMatch config is visible directly in the census.
        if (diag && !Plugin.CensusDone && targetSnapshot.Length > 0)
        {
            Plugin.CensusDone = true;
            foreach (var helper in targetSnapshot)
            {
                try
                {
                    if (helper == null) continue;
                    var go = helper.gameObject;
                    string path = BuildParentChain(go.transform, 4);
                    bool matched = MatchesTargetName(go, nameMatchTokens);
                    Plugin.Logger.LogInfo($"[VillagerAmmo][census] target: '{go.name}' path='{path}' matched={matched}");
                }
                catch (Exception ex)
                {
                    Plugin.Logger.LogWarning($"[VillagerAmmo][census] error: {ex}");
                }
            }
        }

        // liveTargets keeps EVERY live helper (feeds the unfiltered secondary ReleaseAllStuckObjects()
        // sweep below - a per-helper native call, not a radius cull, so it's unaffected by scoping).
        // targetPositions/matchedTargetCount are scoped to TargetNameMatch - these are the cull centers
        // for the ground-item radius check further down, which is what was accidentally town-wide.
        var liveTargets = new List<ProjectileTargetHelper>(targetSnapshot.Length);
        var targetPositions = new List<Vector3>(targetSnapshot.Length);
        int matchedTargetCount = 0;
        foreach (var helper in targetSnapshot)
        {
            try
            {
                if (helper == null) // Unity destroyed-object equality
                {
                    lock (Plugin.TargetRegistryLock) { Plugin.TargetRegistry.Remove(helper!); }
                    continue;
                }
                liveTargets.Add(helper);
                if (MatchesTargetName(helper.gameObject, nameMatchTokens))
                {
                    targetPositions.Add(helper.transform.position);
                    matchedTargetCount++;
                }
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning($"[VillagerAmmo] removing target from registry after exception: {ex}");
                lock (Plugin.TargetRegistryLock) { Plugin.TargetRegistry.Remove(helper); }
            }
        }

        // 3. Snapshot tracked ground items; keep near-target, category-matched arrows.
        DynamicItemObject[] itemSnapshot;
        lock (Plugin.TrackedGroundItemsLock)
        {
            itemSnapshot = new DynamicItemObject[Plugin.TrackedGroundItems.Count];
            Plugin.TrackedGroundItems.CopyTo(itemSnapshot);
        }
        itemSnapshotLen = itemSnapshot.Length;

        string categoryMatch = Plugin.ArrowCategoryMatch.Value ?? "Arrows";
        float radius = Plugin.TargetArrowRadius.Value;
        float radiusSqr = radius * radius;

        var candidates = new List<WorldItemObject>();
        foreach (var node in itemSnapshot)
        {
            try
            {
                if (node == null) continue;

                var itemObj = node._itemObject;
                if (itemObj == null) continue;

                var item = itemObj.ItemInstance;
                var info = item?.info;
                if (info == null) continue;

                string catChain = BuildCategoryChain(info.category);
                if (catChain.IndexOf(categoryMatch, StringComparison.OrdinalIgnoreCase) < 0) continue;

                Vector3 pos = node.transform.position;
                bool nearTarget = false;
                foreach (var tp in targetPositions)
                {
                    if ((pos - tp).sqrMagnitude <= radiusSqr) { nearTarget = true; break; }
                }
                if (!nearTarget) continue;

                candidates.Add(itemObj);
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning($"[VillagerAmmo] ground-item resolve error during cleanup: {ex}");
            }
        }

        // 4. Threshold gate.
        if (candidates.Count < threshold)
        {
            if (diag && candidates.Count > 0)
                Plugin.Logger.LogInfo($"[VillagerAmmo] {candidates.Count} stuck arrow(s) near targets (below threshold {threshold}).");
        }
        else
        {
            // 5. Cull.
            int removed = 0;
            foreach (var wobj in candidates)
            {
                try
                {
                    if (wobj != null)
                    {
                        wobj.RemoveObjectFromWorld();
                        removed++;
                    }
                }
                catch (Exception ex)
                {
                    Plugin.Logger.LogWarning($"[VillagerAmmo] failed to remove stuck arrow: {ex}");
                }
            }
            Plugin.Logger.LogInfo($"[VillagerAmmo] culled {removed}/{candidates.Count} stuck arrows near {matchedTargetCount} target(s).");
        }

        // 6. Secondary sweep: original ReleaseAllStuckObjects() path (unchanged logic), diagnostics
        // fixed to always report a per-pass summary and to log removals visibly (LogWarning).
        int targetsWithStuck = 0;
        foreach (var helper in liveTargets)
        {
            try
            {
                int count = helper._stuckObjects?.Count ?? 0;

                if (count > 0)
                {
                    targetsWithStuck++;
                    if (diag) Plugin.Logger.LogInfo($"[VillagerAmmo] target has {count} stuck object(s).");
                }

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
                Plugin.Logger.LogWarning($"[VillagerAmmo] removing target from registry after exception: {ex}");
                lock (Plugin.TargetRegistryLock) { Plugin.TargetRegistry.Remove(helper); }
            }
        }

        if (diag)
            Plugin.Logger.LogInfo($"[VillagerAmmo] cleanup pass: {liveTargets.Count} target(s), {targetsWithStuck} with stuck-registry entries.");
    }

    private bool IsHost()
    {
        try
        {
            var p = Plugin.LocalPlayer;
            if (p == null || p.NetworkObject == null || p.NetworkObject.Runner == null) return false;
            var runner = p.NetworkObject.Runner;
            return runner.IsServer || runner.IsSharedModeMasterClient;
        }
        catch { return false; }
    }

    private static string BuildCategoryChain(ItemCategoryInfo? cat)
    {
        if (cat == null) return "";
        var sb = new StringBuilder();
        int depth = 0;
        var c = cat;
        while (c != null && depth++ < 8)
        {
            string n = "";
            try { n = c.Name ?? ""; } catch { }
            if (!string.IsNullOrEmpty(n))
            {
                if (sb.Length > 0) sb.Append('/');
                sb.Append(n);
            }
            try { c = c.parent; } catch { break; }
        }
        return sb.ToString();
    }

    // v0.2.3: parse TargetNameMatch's comma-separated substrings, trimmed, empties dropped.
    private static string[] ParseNameMatchTokens()
    {
        string raw = Plugin.TargetNameMatch.Value ?? "";
        if (string.IsNullOrWhiteSpace(raw)) return Array.Empty<string>();
        var parts = raw.Split(',');
        var list = new List<string>(parts.Length);
        foreach (var part in parts)
        {
            string t = part.Trim();
            if (t.Length > 0) list.Add(t);
        }
        return list.ToArray();
    }

    // v0.2.3: true if the helper's own GameObject name OR its ancestor path (up to 4 parents)
    // contains any configured token (case-insensitive). Empty token list matches nothing (a blank
    // TargetNameMatch disables the cull entirely rather than reverting to town-wide).
    private static bool MatchesTargetName(GameObject go, string[] tokens)
    {
        if (tokens.Length == 0) return false;

        string name = "";
        try { name = go.name ?? ""; } catch { }
        foreach (var tok in tokens)
            if (name.IndexOf(tok, StringComparison.OrdinalIgnoreCase) >= 0) return true;

        string path = BuildParentChain(go.transform, 4);
        foreach (var tok in tokens)
            if (path.IndexOf(tok, StringComparison.OrdinalIgnoreCase) >= 0) return true;

        return false;
    }

    // Up to maxDepth ancestor GameObject names, outermost first / innermost (direct parent) last.
    // No positions - names only, per-ancestor try/catch.
    private static string BuildParentChain(Transform t, int maxDepth)
    {
        var names = new List<string>();
        try
        {
            var cur = t.parent;
            int depth = 0;
            while (cur != null && depth < maxDepth)
            {
                try { names.Add(cur.name); } catch { }
                cur = cur.parent;
                depth++;
            }
        }
        catch { }
        names.Reverse();
        return string.Join("/", names);
    }
}
