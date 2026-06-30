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

    // Per-node cooldown so an unresolved deactivated node doesn't re-attempt the handler refill every frame.
    private static readonly Dictionary<string, DateTime> _lastDeactAttempt = new();

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

        var toRemove = new List<string>();
        foreach (var kvp in Plugin.PendingRespawns)
        {
            string posKey = kvp.Key;
            float gameTimeFelled = kvp.Value;

            // Look up the live instance by position — safe after scene reloads and restarts.
            if (!Plugin.ActiveInstances.TryGetValue(posKey, out var inst))
            {
                if (overdueTreeNotLoaded != null)
                {
                    float elapsed = ws.GetTimeDifferenceFromCurrentGameTimeInSeconds(gameTimeFelled) / dayLength;
                    if (elapsed >= threshold) overdueTreeNotLoaded.Add($"{posKey}({elapsed:F1}d)");
                }
                continue;
            }

            if (inst.Destroyed)
            {
                // Stump was harvested — cancel the respawn
                toRemove.Add(posKey);
                Plugin.RegisteredStumps.Remove(posKey);
                Plugin.Logger.LogInfo(
                    $"[TreeRespawnMod] Stump harvested — cancelled respawn at {posKey}");
                continue;
            }

            float elapsedDays =
                ws.GetTimeDifferenceFromCurrentGameTimeInSeconds(gameTimeFelled) / dayLength;

            if (elapsedDays >= threshold)
            {
                toRemove.Add(posKey);
                Plugin.RegisteredStumps.Remove(posKey);
                try
                {
                    inst.Replenish();
                    Plugin.Logger.LogInfo(
                        $"[TreeRespawnMod] Tree at {posKey} respawned ({elapsedDays:F2} days elapsed).");
                }
                catch (Exception ex)
                {
                    Plugin.Logger.LogError(
                        $"[TreeRespawnMod] Replenish failed at {posKey}: {ex}");
                }
            }
        }

        // Gather resources (reeds, etc.) — no stump-cancel condition, just time-based.
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

            if (!Plugin.ActiveInstances.TryGetValue(posKey, out var inst))
            {
                if (overdueGatherNotLoaded != null)
                {
                    float elapsed = ws.GetTimeDifferenceFromCurrentGameTimeInSeconds(gameTimeExhausted) / dayLength;
                    if (elapsed >= gatherThreshold) overdueGatherNotLoaded.Add($"{posKey}({elapsed:F1}d,{itemName})");
                }
                continue;
            }

            float elapsedDays =
                ws.GetTimeDifferenceFromCurrentGameTimeInSeconds(gameTimeExhausted) / dayLength;

            if (elapsedDays >= gatherThreshold)
            {
                // When the cached pointer is stale/deactivated, don't fake the respawn against it — re-resolve
                // a usable instance through the data handler and replenish THAT (RefillUnloadedGatherNodes).
                // Only the not-live case is diverted; live nodes use the normal path below.
                bool live;
                try { live = inst.Active && !inst.Destroyed; } catch { live = false; }
                if (!live && Plugin.RefillUnloadedNodes.Value)
                {
                    // Throttle retries for a node we can't resolve yet (e.g. no wid, or GetInstance returns
                    // null) so it can't spin every frame. A resolved+refilled node is dropped immediately; an
                    // unresolved one is KEPT (liveness guard) and retried after the window — or serviced by the
                    // normal path once it streams back in.
                    if (_lastDeactAttempt.TryGetValue(posKey, out var last)
                        && (DateTime.UtcNow - last).TotalSeconds < 30) continue;
                    _lastDeactAttempt[posKey] = DateTime.UtcNow;

                    if (DeactivatedReplenish(posKey, itemName))
                    {
                        toRemoveGather.Add(posKey);
                        _lastDeactAttempt.Remove(posKey);
                    }
                    continue;
                }

                toRemoveGather.Add(posKey);

                // Snapshot liveness + stock BEFORE replenishing so the log can tell a real respawn
                // from a "fake" one fired against a stale/streamed-out instance (the M2 mechanism in
                // TREERESPAWN_HANDOFF.md Issue C/D). ActiveInstances is never pruned on a per-node
                // unload, so a dead pointer can linger here; a streamed-out node reports Active=false
                // (and/or Destroyed) even though it's still in the registry. Reads are wrapped so a
                // throw on a dead instance can't disturb the Replenish path below — purely additive.
                bool preDestroyed = false, preActive = false, preAvail = false; int preQty = -1;
                string bufInfo = "";
                if (diag)
                {
                    try { preDestroyed = inst.Destroyed; preActive = inst.Active; preAvail = inst.IsAvailable(); preQty = inst.GetQuantity(); }
                    catch { }
                    bufInfo = Plugin.ProbeBuffer(inst); // captured BEFORE Replenish so we see the deactivated state untouched
                }

                try
                {
                    inst.Replenish();

                    if (diag)
                    {
                        bool postAvail = false; int postQty = -1;
                        try { postAvail = inst.IsAvailable(); postQty = inst.GetQuantity(); } catch { }
                        bool fake = preDestroyed || !preActive || (!postAvail && postQty <= 0);
                        Plugin.Logger.LogInfo(
                            $"[TreeRespawnMod] [diag] gather-respawn \"{itemName}\" at {posKey}: " +
                            $"Destroyed={preDestroyed} Active={preActive} avail {preAvail}->{postAvail} qty {preQty}->{postQty} | {bufInfo}"
                            + (fake ? "  <-- FAKE RESPAWN (instance not live / did not refill)" : ""));
                    }

                    Plugin.Logger.LogInfo(
                        $"[TreeRespawnMod] Gather resource \"{itemName}\" at {posKey} respawned ({elapsedDays:F2} days elapsed).");
                }
                catch (Exception ex)
                {
                    Plugin.Logger.LogError(
                        $"[TreeRespawnMod] Replenish failed at {posKey}: {ex}");
                }
            }
        }

        // Throttled summary (Issue D): entries past their threshold that can't respawn because their
        // node isn't streamed in right now. Combined tree+gather under one throttle so a noisy one
        // can't crowd out the other.
        if (diag && (overdueTreeNotLoaded!.Count > 0 || overdueGatherNotLoaded!.Count > 0)
            && (DateTime.UtcNow - _lastOverdueDiagLog).TotalSeconds >= 5)
        {
            _lastOverdueDiagLog = DateTime.UtcNow;
            if (overdueTreeNotLoaded.Count > 0)
                Plugin.Logger.LogInfo(
                    $"[TreeRespawnMod] [diag] overdue-but-not-loaded (tree): {overdueTreeNotLoaded.Count} — {string.Join(", ", overdueTreeNotLoaded.Take(5))}");
            if (overdueGatherNotLoaded!.Count > 0)
                Plugin.Logger.LogInfo(
                    $"[TreeRespawnMod] [diag] overdue-but-not-loaded (gather): {overdueGatherNotLoaded.Count} — {string.Join(", ", overdueGatherNotLoaded.Take(5))}");
        }

        bool anyChanges = toRemove.Count > 0 || toRemoveGather.Count > 0;

        foreach (string key in toRemove)
            Plugin.PendingRespawns.Remove(key);
        foreach (string key in toRemoveGather)
            Plugin.PendingGatherRespawns.Remove(key);

        if (anyChanges)
            Plugin.SavePending();
    }

    // Refill a node whose chunk is deactivated, WITHOUT streaming the tile in. The cached ActiveInstances
    // pointer is stale by now (deregistered, UniqueId=0), so we re-resolve a usable instance through the data
    // handler — handler.GetInstance(onlyIfActive:false) — using the WorldItemInstanceId cached at harvest (or
    // reconstructed from the save), then call the game's own Replenish() on that valid handle and flag the cell
    // dirty so the change persists and the villager AI re-reads it. Returns true only when the refill actually
    // took (so the caller can drop the entry); false leaves it pending to retry. All game calls wrapped — this
    // writes live state. [diag] lines are EnableDiagnostics-gated; a real Replenish throw always logs.
    private bool DeactivatedReplenish(string posKey, string itemName)
    {
        const string p = "[TreeRespawnMod] [diag] exp-replenish";
        bool diag = Plugin.EnableDiagnostics.Value;

        var handler = Plugin.DataHandler;
        if (handler == null) { try { handler = BiomeItemInstance.Handler; } catch { } }   // Handler is a static (shared) field
        if (handler == null) { if (diag) Plugin.Logger.LogInfo($"{p} \"{itemName}\" at {posKey}: no handler"); return false; }

        if (!Plugin.GatherWid.TryGetValue(posKey, out var wid))
        { if (diag) Plugin.Logger.LogInfo($"{p} \"{itemName}\" at {posKey}: no cached wid"); return false; }

        WorldTileId tileId;
        try
        {
            ParsePosKey(posKey, out float px, out float pz);
            tileId = WorldTileId.GetLowest(px, pz, ReadTileSize());
        }
        catch (Exception e) { if (diag) Plugin.Logger.LogInfo($"{p} \"{itemName}\" at {posKey}: tileId err {e.GetType().Name}"); return false; }

        WorldItemInstance? fresh = null;
        try { fresh = handler.GetInstance(tileId, wid, false, true); }   // onlyIfActive:false, noPooling:true
        catch (Exception e) { if (diag) Plugin.Logger.LogInfo($"{p} \"{itemName}\" at {posKey}: GetInstance threw {e.GetType().Name}"); return false; }
        if (fresh == null) { if (diag) Plugin.Logger.LogInfo($"{p} \"{itemName}\" at {posKey}: GetInstance(onlyIfActive:false) returned NULL"); return false; }

        BiomeItemInstance? bi = null;
        try { bi = fresh.TryCast<BiomeItemInstance>(); } catch { }
        if (bi == null) { if (diag) Plugin.Logger.LogInfo($"{p} \"{itemName}\" at {posKey}: resolved instance not a BiomeItemInstance"); return false; }

        int preQty = -1, postQty = -1; bool preAvail = false, postAvail = false, freshActive = false;
        try { freshActive = bi.Active; } catch { }
        try { preQty = bi.GetQuantity(); preAvail = bi.IsAvailable(); } catch { }
        try { bi.Replenish(); }
        catch (Exception e) { Plugin.Logger.LogError($"{p} \"{itemName}\" at {posKey}: Replenish threw {e}"); return false; }
        try { handler.SetDirty(bi.Cell); } catch { }
        try { handler.OnInstanceDataChanged(bi); } catch { }
        try { postQty = bi.GetQuantity(); postAvail = bi.IsAvailable(); } catch { }

        bool stuck = postAvail || postQty > preQty;
        if (diag)
            Plugin.Logger.LogInfo(
                $"{p} \"{itemName}\" at {posKey}: freshActive={freshActive} " +
                $"avail {preAvail}->{postAvail} qty {preQty}->{postQty} {(stuck ? "OK (stock written)" : "NO-CHANGE")}");
        return stuck;   // only drop the entry when the refill actually took
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
}
