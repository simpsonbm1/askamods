using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using SSSGame;
using SandSailorStudio.WorldGen;
using SandSailorStudio.Streaming;

namespace TreeRespawnMod;

public class DayTracker : MonoBehaviour
{
    private int _worldCheck;
    private static DateTime _lastOverdueDiagLog = DateTime.MinValue;
    private static DateTime _lastWeatherWarn = DateTime.MinValue;

    // Per-node cooldown so a node the handler can't service yet isn't re-attempted every frame.
    // Shared by trees and gather (a tree and a gather node never share a 0.1m-rounded position).
    private static readonly Dictionary<string, DateTime> _lastHandlerAttempt = new();

    // Called on world switch (Plugin.OnWorldChanged) — posKeys collide across worlds, so a stale
    // cooldown from world A must not delay (or leak into) world B.
    internal static void ClearTransientState()
    {
        _lastHandlerAttempt.Clear();
        WellRefill.ClearTransientState();
        MushroomDiag.ResetForWorld();
        MushroomAvailability.ResetForWorld();
    }

    // True while a recent failed attempt should suppress another try; otherwise stamps NOW and
    // lets this attempt proceed.
    private static bool HandlerOnCooldown(string posKey)
    {
        if (_lastHandlerAttempt.TryGetValue(posKey, out var last)
            && (DateTime.UtcNow - last).TotalSeconds < 30) return true;
        _lastHandlerAttempt[posKey] = DateTime.UtcNow;
        return false;
    }

    private enum HandlerResult { Retry, Replenished, AlreadyDone, Cancelled }

    void Update()
    {
        // Resolve which world we're in (StorageManager.ActiveSessionID) so we use the right per-world
        // save file. Poll every frame until known; afterwards poll occasionally to catch a world
        // switch (loading a different save without restarting the game).
        if (Plugin.CurrentWorldId == null)
        {
            Plugin.PollWorldId();
            if (Plugin.CurrentWorldId == null) return;
        }
        else if (++_worldCheck >= 60)
        {
            _worldCheck = 0;
            Plugin.PollWorldId();
        }

        // Mushroom availability (v1.4.7): apply the year-round / rain-independent gate edit once the
        // descriptors are registered (once per world; the underlying SO edit is process-global). Then
        // the read-only diagnostic: auto-dump once per world + on-demand re-dump hotkey. Neither is
        // host-gated — the apply is a local SO edit (host's copy drives spawning in co-op) and the dump
        // only reads game state.
        MushroomAvailability.MaybeApply();
        MushroomDiag.MaybeAutoDump();
        try
        {
            string mk = Plugin.MushroomDiagHotkey.Value;
            if (!string.IsNullOrWhiteSpace(mk))
            {
                bool mDown = Enum.TryParse<KeyCode>(mk, true, out var mkc)
                    ? Input.GetKeyDown(mkc)
                    : Input.GetKeyDown(mk);
                if (mDown) MushroomDiag.Dump("hotkey");
            }
        }
        catch { }

        bool isHotkeyDown = false;
        try
        {
            string keyStr = Plugin.ManualRespawnHotkey.Value;
            if (Enum.TryParse<KeyCode>(keyStr, true, out var kc))
                isHotkeyDown = Input.GetKeyDown(kc);
            else
                isHotkeyDown = Input.GetKeyDown(keyStr);
        }
        catch { }

        if (isHotkeyDown)
        {
            if (Plugin.LocalPlayer != null && Plugin.LocalPlayer.NetworkObject != null && Plugin.LocalPlayer.NetworkObject.Runner != null && (Plugin.LocalPlayer.NetworkObject.Runner.IsServer || Plugin.LocalPlayer.NetworkObject.Runner.IsSharedModeMasterClient))
            {
                ProcessManualRespawn();
            }
            else
            {
                Plugin.Logger.LogWarning("[TreeRespawnMod] Manual respawn hotkey ignored — must be host.");
            }
        }

        // Well refill (v1.4.0) — independent of the pending-respawn queues, so it must run before
        // the early-out below. Host-gated by the same server-weather check the queues use; a
        // client (or the main menu) simply skips it.
        if (Plugin.WellChargesPerDay.Value > 0f && Plugin.TryGetServerWeather(out var wellWs, out _))
        {
            try { WellRefill.Tick(wellWs!); }
            catch (Exception e) { Plugin.Logger.LogError($"[TreeRespawnMod] [well] tick failed: {e}"); }
        }

        if (Plugin.PendingRespawns.Count == 0 && Plugin.PendingGatherRespawns.Count == 0) return;

        if (!Plugin.TryGetServerWeather(out var ws, out var reason))
        {
            if ((DateTime.UtcNow - _lastWeatherWarn).TotalSeconds >= 5)
            {
                _lastWeatherWarn = DateTime.UtcNow;
                Plugin.Logger.LogWarning(
                    $"[TreeRespawnMod] WeatherSystem not available ({reason}), skipping respawn servicing " +
                    $"({Plugin.PendingRespawns.Count} tree + {Plugin.PendingGatherRespawns.Count} gather pending). " +
                    $"world={Plugin.CurrentWorldId ?? "?"}");
            }
            return;
        }

        float dayLength = ws!.dayLength;
        float threshold = Plugin.RespawnDays.Value;

        bool diag = Plugin.EnableDiagnostics.Value;
        List<string>? overdueTreeNotLoaded = diag ? new List<string>() : null;
        List<string>? overdueGatherNotLoaded = diag ? new List<string>() : null;

        // ── Trees ─────────────────────────────────────────────────────────────────────────────
        // v1.3.0 hardening — mirrors the gather side: validate the cached pointer by WID before
        // trusting it (pooled wrappers are reused for OTHER nodes once a chunk unloads — v1.2.19
        // finding), never drop an entry unless the replenish verifiably took, and service unloaded
        // stumps through the data handler instead of waiting for the player to walk back.
        var toRemove = new List<string>();
        foreach (var kvp in Plugin.PendingRespawns)
        {
            string posKey = kvp.Key;
            float gameTimeFelled = kvp.Value;

            bool haveWid = Plugin.TreeWid.TryGetValue(posKey, out var wid);
            Plugin.ActiveInstances.TryGetValue(posKey, out var inst);

            // Only trust the cached pointer when it still addresses OUR node. Without a wid
            // (legacy entries from pre-v1.3.0 saves) we keep the old trust-the-pointer behavior —
            // best information available; those entries age out as they respawn.
            bool pointerTrusted = false;
            if (inst != null)
            {
                if (!haveWid) pointerTrusted = true;
                else { try { pointerTrusted = inst.GetWorldItemInstanceId().Raw == wid.Raw; } catch { } }
            }

            // Stump cleared by hand is the ONE legitimate cancel (trees only — gather always renews).
            // Only honored on a trusted pointer: a reused wrapper's Destroyed describes an unrelated
            // node and must not cancel a live stump's respawn permanently.
            bool destroyed = false;
            if (pointerTrusted) { try { destroyed = inst!.Destroyed; } catch { } }
            if (pointerTrusted && destroyed)
            {
                toRemove.Add(posKey);
                CleanupTree(posKey);
                Plugin.Logger.LogInfo(
                    $"[TreeRespawnMod] Stump harvested — cancelled respawn at {posKey}");
                continue;
            }

            float elapsedDays =
                ws.GetTimeDifferenceFromCurrentGameTimeInSeconds(gameTimeFelled) / dayLength;
            if (elapsedDays < threshold) continue;

            bool live = false;
            if (pointerTrusted && !destroyed) { try { live = inst!.Active; } catch { } }

            if (live)
            {
                bool success = false;
                try
                {
                    inst!.Replenish();
                    // Verify the write took before consuming the entry — pre-v1.3.0 the entry was
                    // dropped unconditionally, so a no-op Replenish silently lost the tree forever.
                    // A replenished tree reads IsExhausted=false (a stump reads true — v1.1.1 gates).
                    bool postExhausted = true;
                    try { postExhausted = inst.IsExhausted(); } catch { }
                    success = !postExhausted;
                    if (success)
                        Plugin.Logger.LogInfo(
                            $"[TreeRespawnMod] Tree at {posKey} respawned ({elapsedDays:F2} days elapsed).");
                    else
                        Plugin.Logger.LogWarning(
                            $"[TreeRespawnMod] Tree at {posKey}: Replenish on live instance left IsExhausted=true — keeping entry, will retry.");
                }
                catch (Exception ex)
                {
                    Plugin.Logger.LogError($"[TreeRespawnMod] Replenish failed at {posKey}: {ex}");
                }

                if (success)
                {
                    toRemove.Add(posKey);
                    CleanupTree(posKey);
                }
                else if (Plugin.RefillUnloadedNodes.Value && haveWid && !HandlerOnCooldown(posKey))
                {
                    ApplyTreeHandlerResult(HandlerReplenishTree(posKey, wid), posKey, elapsedDays, toRemove);
                }
            }
            else if (Plugin.RefillUnloadedNodes.Value && haveWid)
            {
                // Not live: pointer stale/reused, or the posKey isn't in ActiveInstances at all
                // (chunk never streamed this session). The handler path needs only posKey+wid, so
                // both cases are serviceable without the tile being loaded.
                if (HandlerOnCooldown(posKey)) continue;
                ApplyTreeHandlerResult(HandlerReplenishTree(posKey, wid), posKey, elapsedDays, toRemove);
            }
            else if (overdueTreeNotLoaded != null)
            {
                overdueTreeNotLoaded.Add($"{posKey}({elapsedDays:F1}d)");
            }
        }

        // ── Gather resources (reeds, etc.) ────────────────────────────────────────────────────
        // No cancel condition of any kind — gather nodes ALWAYS renew on their timers (only trees
        // have a legitimate "intentionally cleared" state, via stump clearing).
        var toRemoveGather = new List<string>();
        foreach (var kvp in Plugin.PendingGatherRespawns)
        {
            string posKey = kvp.Key;
            var (gameTimeExhausted, itemName) = kvp.Value;

            float gatherThreshold = Plugin.GetGatherThreshold(itemName);
            if (gatherThreshold <= 0)
            {
                // Respawn disabled for this resource type — drop it from the queue.
                toRemoveGather.Add(posKey);
                continue;
            }

            float elapsedDays =
                ws.GetTimeDifferenceFromCurrentGameTimeInSeconds(gameTimeExhausted) / dayLength;
            if (elapsedDays < gatherThreshold) continue;

            bool haveWid = Plugin.GatherWid.TryGetValue(posKey, out var wid);
            Plugin.ActiveInstances.TryGetValue(posKey, out var inst);

            bool live = false;
            if (inst != null)
            {
                try
                {
                    live = inst.Active && !inst.Destroyed;
                    // A stale unpooled wrapper can falsely claim Active (v1.2.19) — or genuinely be
                    // Active because the pool reassigned it to a DIFFERENT node. The wid is the truth.
                    if (live && haveWid && inst.GetWorldItemInstanceId().Raw != wid.Raw) live = false;
                }
                catch { live = false; }
            }

            if (live)
            {
                // Pre-state read UNCONDITIONALLY — v1.2.20 gated these reads behind EnableDiagnostics,
                // which left preActive=false in normal play, so `fake` was ALWAYS true and the live
                // path never succeeded (everything silently fell through to the handler fallback).
                bool preDestroyed = true, preActive = false, preAvail = false; int preQty = -1;
                try { preDestroyed = inst!.Destroyed; preActive = inst.Active; preAvail = inst.IsAvailable(); preQty = inst.GetQuantity(); }
                catch { }
                string bufInfo = diag ? Plugin.ProbeBuffer(inst!) : "";

                bool success = false;
                try
                {
                    inst!.Replenish();

                    bool postAvail = false; int postQty = -1;
                    try { postAvail = inst.IsAvailable(); postQty = inst.GetQuantity(); } catch { }

                    // A real respawn must leave actual stock. If qty is still 0, it failed
                    // (stale pointer with a garbage 'Available=True' bit).
                    bool fake = preDestroyed || !preActive || postQty <= 0;

                    if (diag)
                    {
                        Plugin.Logger.LogInfo(
                            $"[TreeRespawnMod] [diag] gather-respawn \"{itemName}\" at {posKey}: " +
                            $"Destroyed={preDestroyed} Active={preActive} avail {preAvail}->{postAvail} qty {preQty}->{postQty} | {bufInfo}"
                            + (fake ? "  <-- FAKE RESPAWN (instance not live / did not refill)" : ""));
                    }

                    if (!fake)
                    {
                        success = true;
                        Plugin.Logger.LogInfo(
                            $"[TreeRespawnMod] Gather resource \"{itemName}\" at {posKey} respawned ({elapsedDays:F2} days elapsed).");
                    }
                }
                catch (Exception ex)
                {
                    Plugin.Logger.LogError($"[TreeRespawnMod] Replenish failed at {posKey}: {ex}");
                }

                if (success)
                {
                    toRemoveGather.Add(posKey);
                    _lastHandlerAttempt.Remove(posKey);
                }
                else if (Plugin.RefillUnloadedNodes.Value && haveWid && !HandlerOnCooldown(posKey)
                         && DeactivatedReplenish(posKey, itemName, wid))
                {
                    toRemoveGather.Add(posKey);
                    _lastHandlerAttempt.Remove(posKey);
                }
            }
            else if (Plugin.RefillUnloadedNodes.Value && haveWid)
            {
                // Not live — stale/reused pointer OR posKey absent from ActiveInstances entirely
                // (never streamed this session). Pre-v1.3.0 the absent case was skipped before the
                // handler refill was ever considered, stranding the entry until a walk-by; the
                // handler path only needs posKey+wid, so service both cases here.
                if (HandlerOnCooldown(posKey)) continue;
                if (DeactivatedReplenish(posKey, itemName, wid))
                {
                    toRemoveGather.Add(posKey);
                    _lastHandlerAttempt.Remove(posKey);
                }
            }
            else if (overdueGatherNotLoaded != null)
            {
                overdueGatherNotLoaded.Add($"{posKey}({elapsedDays:F1}d,{itemName})");
            }
        }

        // Throttled summary: entries past their threshold that currently have NO servicing route
        // (no wid + not streamed in, or RefillUnloadedGatherNodes off). Combined tree+gather under
        // one throttle so a noisy one can't crowd out the other.
        if (diag && (overdueTreeNotLoaded!.Count > 0 || overdueGatherNotLoaded!.Count > 0)
            && (DateTime.UtcNow - _lastOverdueDiagLog).TotalSeconds >= 5)
        {
            _lastOverdueDiagLog = DateTime.UtcNow;
            if (overdueTreeNotLoaded.Count > 0)
                Plugin.Logger.LogInfo(
                    $"[TreeRespawnMod] [diag] overdue-but-unserviceable (tree): {overdueTreeNotLoaded.Count} — {string.Join(", ", overdueTreeNotLoaded.Take(5))}");
            if (overdueGatherNotLoaded!.Count > 0)
                Plugin.Logger.LogInfo(
                    $"[TreeRespawnMod] [diag] overdue-but-unserviceable (gather): {overdueGatherNotLoaded.Count} — {string.Join(", ", overdueGatherNotLoaded.Take(5))}");
        }

        bool anyChanges = toRemove.Count > 0 || toRemoveGather.Count > 0;

        foreach (string key in toRemove)
            Plugin.PendingRespawns.Remove(key);
        foreach (string key in toRemoveGather)
            Plugin.PendingGatherRespawns.Remove(key);

        if (anyChanges)
            Plugin.SavePending();
    }

    // Shared bookkeeping when a tree entry leaves the pending list for any reason.
    private static void CleanupTree(string posKey)
    {
        Plugin.RegisteredStumps.Remove(posKey);
        _lastHandlerAttempt.Remove(posKey);
    }

    private static void ApplyTreeHandlerResult(HandlerResult r, string posKey, float elapsedDays, List<string> toRemove)
    {
        switch (r)
        {
            case HandlerResult.Replenished:
                toRemove.Add(posKey);
                CleanupTree(posKey);
                Plugin.Logger.LogInfo(
                    $"[TreeRespawnMod] Tree at {posKey} respawned via data handler ({elapsedDays:F2} days elapsed).");
                break;
            case HandlerResult.AlreadyDone:
                toRemove.Add(posKey);
                CleanupTree(posKey);
                break;
            case HandlerResult.Cancelled:
                toRemove.Add(posKey);
                CleanupTree(posKey);
                Plugin.Logger.LogInfo(
                    $"[TreeRespawnMod] Stump gone (persistent data reads Destroyed) — cancelled respawn at {posKey}");
                break;
                // Retry: keep the entry pending; the cooldown gates the next attempt.
        }
    }

    // Resolve a fresh, writable instance handle through the persistent data handler — works whether
    // or not the node's chunk is streamed in (onlyIfActive:false is the load-bearing part, confirmed
    // in-game for gather nodes 2026-06-30). Returns null when the node can't be resolved yet (caller
    // keeps the entry pending and retries after the cooldown).
    private static BiomeItemInstance? ResolveViaHandler(string posKey, string label, SSSGame.WorldItemInstanceId wid, string p)
    {
        bool diag = Plugin.EnableDiagnostics.Value;

        var handler = Plugin.DataHandler;
        if (handler == null) { try { handler = BiomeItemInstance.Handler; } catch { } }   // static (shared) field
        if (handler == null) { if (diag) Plugin.Logger.LogInfo($"{p} \"{label}\" at {posKey}: no handler"); return null; }

        WorldTileId tileId;
        try
        {
            ParsePosKey(posKey, out float px, out float pz);
            tileId = WorldTileId.GetLowest(px, pz, ReadTileSize());
        }
        catch (Exception e) { if (diag) Plugin.Logger.LogInfo($"{p} \"{label}\" at {posKey}: tileId err {e.GetType().Name}"); return null; }

        WorldItemInstance? fresh = null;
        try { fresh = handler.GetInstance(tileId, wid, false, true); }   // onlyIfActive:false, noPooling:true
        catch (Exception e) { if (diag) Plugin.Logger.LogInfo($"{p} \"{label}\" at {posKey}: GetInstance threw {e.GetType().Name}"); return null; }
        if (fresh == null) { if (diag) Plugin.Logger.LogInfo($"{p} \"{label}\" at {posKey}: GetInstance(onlyIfActive:false) returned NULL"); return null; }

        BiomeItemInstance? bi = null;
        try { bi = fresh.TryCast<BiomeItemInstance>(); } catch { }
        if (bi == null && diag) Plugin.Logger.LogInfo($"{p} \"{label}\" at {posKey}: resolved instance not a BiomeItemInstance");
        return bi;
    }

    // Flag the cell dirty + notify so the change persists and the villager AI/streaming re-reads it.
    private static void FlushHandler(BiomeItemInstance bi)
    {
        var handler = Plugin.DataHandler;
        if (handler == null) { try { handler = BiomeItemInstance.Handler; } catch { } }
        if (handler == null) return;
        try { handler.SetDirty(bi.Cell); } catch { }
        try { handler.OnInstanceDataChanged(bi); } catch { }
    }

    // Refill a gather node through the data handler, without needing its chunk streamed in. Returns
    // true only when the entry should be dropped (stock verified written, or nothing needed doing);
    // false leaves it pending to retry. All game calls wrapped — this writes live state. [diag]
    // lines are EnableDiagnostics-gated; a real Replenish throw always logs.
    private bool DeactivatedReplenish(string posKey, string itemName, SSSGame.WorldItemInstanceId wid)
    {
        const string p = "[TreeRespawnMod] [diag] exp-replenish";
        bool diag = Plugin.EnableDiagnostics.Value;

        var bi = ResolveViaHandler(posKey, itemName, wid, p);
        if (bi == null) return false;

        int preQty = -1, postQty = -1; bool preAvail = false, postAvail = false, freshActive = false;
        try { freshActive = bi.Active; } catch { }
        try { preQty = bi.GetQuantity(); preAvail = bi.IsAvailable(); } catch { }

        // Already stocked → nothing to refill; consume the entry. Also self-heals stale entries
        // (e.g. registered, then the game was quit without saving — the world reverted but the mod's
        // file kept the entry; pre-v1.3.0 such an entry retried NO-CHANGE every 30s forever).
        if (preQty > 0)
        {
            if (diag) Plugin.Logger.LogInfo($"{p} \"{itemName}\" at {posKey}: already stocked (qty={preQty}) — dropping entry");
            return true;
        }

        try { bi.Replenish(); }
        catch (Exception e) { Plugin.Logger.LogError($"{p} \"{itemName}\" at {posKey}: Replenish threw {e}"); return false; }
        FlushHandler(bi);
        try { postQty = bi.GetQuantity(); postAvail = bi.IsAvailable(); } catch { }

        bool stuck = postAvail || postQty > preQty;
        if (diag)
            Plugin.Logger.LogInfo(
                $"{p} \"{itemName}\" at {posKey}: freshActive={freshActive} " +
                $"avail {preAvail}->{postAvail} qty {preQty}->{postQty} {(stuck ? "OK (stock written)" : "NO-CHANGE")}");
        return stuck;   // only drop the entry when the refill actually took
    }

    // Tree variant of the handler refill (v1.3.0, ⚠️ same mechanism as the gather path but not yet
    // confirmed in-game for trees). Key semantic difference: the persistent data reading Destroyed
    // means the stump was genuinely cleared (the one legitimate tree cancel) — honored even while
    // the chunk is unloaded, e.g. a co-op client cleared it away from the host.
    private static HandlerResult HandlerReplenishTree(string posKey, SSSGame.WorldItemInstanceId wid)
    {
        const string p = "[TreeRespawnMod] [diag] exp-replenish-tree";
        bool diag = Plugin.EnableDiagnostics.Value;

        var bi = ResolveViaHandler(posKey, "tree", wid, p);
        if (bi == null) return HandlerResult.Retry;

        bool destroyed = false;
        try { destroyed = bi.Destroyed; } catch { }
        if (destroyed) return HandlerResult.Cancelled;

        bool preExhausted = true, postExhausted = true, freshActive = false;
        try { freshActive = bi.Active; } catch { }
        try { preExhausted = bi.IsExhausted(); } catch { }

        if (!preExhausted)
        {
            // Already standing (stale entry, or respawned through some other path) — nothing to do.
            if (diag) Plugin.Logger.LogInfo($"{p} at {posKey}: not exhausted (already standing) — dropping entry");
            return HandlerResult.AlreadyDone;
        }

        try { bi.Replenish(); }
        catch (Exception e) { Plugin.Logger.LogError($"{p} at {posKey}: Replenish threw {e}"); return HandlerResult.Retry; }
        FlushHandler(bi);
        try { postExhausted = bi.IsExhausted(); } catch { }

        bool stuck = !postExhausted;
        if (diag)
            Plugin.Logger.LogInfo(
                $"{p} at {posKey}: freshActive={freshActive} exhausted {preExhausted}->{postExhausted} {(stuck ? "OK" : "NO-CHANGE")}");
        return stuck ? HandlerResult.Replenished : HandlerResult.Retry;
    }

    private static void ParsePosKey(string posKey, out float x, out float z)
    {
        x = 0f; z = 0f;
        var parts = posKey.Split(':');
        if (parts.Length >= 3)
        {
            float.TryParse(parts[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out x);
            float.TryParse(parts[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out z);
        }
    }

    private static float ReadTileSize()
    {
        try { var cfg = WorldConfiguration.GetActive(); if (cfg != null && cfg.tileSize > 0) return cfg.tileSize; }
        catch { }
        return 128f;
    }

    private void ProcessManualRespawn()
    {
        if (Plugin.LocalPlayer == null) return;
        Vector3 playerPos = Plugin.LocalPlayer.transform.position;
        float radiusSq = Plugin.ManualRespawnRadius.Value * Plugin.ManualRespawnRadius.Value;
        bool includeGather = Plugin.ManualRespawnIncludeGather.Value;
        bool diag = Plugin.EnableDiagnostics.Value;

        int respawnedCount = 0;
        var toRemoveTree = new List<string>();
        var toRemoveGather = new List<string>();

        // 1. Gather nodes (via ActiveInstances)
        if (includeGather)
        {
            foreach (var kvp in Plugin.ActiveInstances)
            {
                var inst = kvp.Value;
                if (inst == null || inst.Destroyed) continue;

                Vector3 nodePos;
                try { nodePos = inst.GetPosition(); } catch { continue; }
                float distSq = Vector3.SqrMagnitude(playerPos - nodePos);

                if (distSq <= radiusSq)
                {
                    try
                    {
                        bool avail = inst.IsAvailable();
                        int qty = inst.GetQuantity();
                        if (!avail && qty <= 0)
                        {
                            inst.Replenish();
                            respawnedCount++;
                            // Recompute the key from the instance's CURRENT position — a pooled
                            // wrapper may be registered under an old posKey while now describing a
                            // different node; the pending entry to consume is the current node's.
                            toRemoveGather.Add(Plugin.PosKey(nodePos));
                            if (diag) Plugin.Logger.LogInfo($"[TreeRespawnMod] Manual respawn triggered for gather node at {Plugin.PosKey(nodePos)}");
                        }
                    }
                    catch (Exception e)
                    {
                        if (diag) Plugin.Logger.LogInfo($"[TreeRespawnMod] Gather check err at {kvp.Key}: {e.Message}");
                    }
                }
            }
        }

        // 2. Tree stumps (via HarvestInteraction in the scene)
        try
        {
            var allHiObjs = Plugin.LiveHarvestInteractions;
            if (allHiObjs != null)
            {
                allHiObjs.RemoveWhere(hi => hi == null || hi.gameObject == null);
                foreach (var hi in allHiObjs)
                {
                    try
                    {
                        if (hi == null || hi.gameObject == null || !hi.gameObject.scene.isLoaded) continue;
                        if (hi.harvestPieces == null || hi.harvestPieces.Count < 2) continue;

                        if (hi.GetCurrentPieceIndex() == hi.harvestPieces.Count - 1)
                        {
                            float distSq = Vector3.SqrMagnitude(playerPos - hi.transform.position);
                            if (distSq <= radiusSq)
                            {
                                var biomeInst = hi._worldInstance?.TryCast<BiomeItemInstance>();
                                if (biomeInst != null && !biomeInst.Destroyed)
                                {
                                    biomeInst.Replenish();
                                    respawnedCount++;
                                    string posKey = Plugin.PosKey(biomeInst.GetPosition());
                                    toRemoveTree.Add(posKey);
                                    if (diag) Plugin.Logger.LogInfo($"[TreeRespawnMod] Manual respawn triggered for tree stump at {posKey}");
                                }
                            }
                        }
                    }
                    catch { } // skip problematic instances
                }
            }
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"[TreeRespawnMod] Error scanning for tree stumps: {ex.Message}");
        }

        bool anyRemoved = false;
        foreach (var k in toRemoveTree)
        {
            if (Plugin.PendingRespawns.Remove(k)) anyRemoved = true;
            CleanupTree(k);
        }
        foreach (var k in toRemoveGather)
        {
            if (Plugin.PendingGatherRespawns.Remove(k)) anyRemoved = true;
            _lastHandlerAttempt.Remove(k);
        }

        if (anyRemoved) Plugin.SavePending();

        Plugin.Logger.LogInfo($"[TreeRespawnMod] Manual respawn complete: respawned {respawnedCount} nearby nodes.");
    }
}
