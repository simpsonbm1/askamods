using System;
using SSSGame;
using SSSGame.Weather;

namespace TreeRespawnMod;

// Shared host-side registration for hooks that discover a depleted node WITHOUT a live interaction
// call: the network data-sync catch (Patches/DataSyncPatch.cs) and the bind-time catch-up
// (Patches/Captures.cs SetWorldInstance postfixes). All conditions here are DATA-level reads on the
// BiomeItemInstance — no GameObject/GetComponent involved — which is what makes the co-op case
// ("client depletes a node whose GameObject the host doesn't have") catchable at all.
//
// The direct patches (GatherPatch/HarvestPatch) keep their own confirmed-working interaction-level
// registration; this module deliberately mirrors their guards and log formats ("(data sync)" etc.).
internal static class Registration
{
    private static DateTime _lastWeatherWarn = DateTime.MinValue;

    // Register a fully-exhausted gather node (data-level condition: quantity <= 0).
    // Returns true only when a NEW registration happened. `src` tags the log line.
    internal static bool TryRegisterGather(BiomeItemInstance bi, string posKey, string itemName, string src)
    {
        try
        {
            if (Plugin.PendingGatherRespawns.ContainsKey(posKey)) return false;

            float threshold = Plugin.GetGatherThreshold(itemName);
            // Respawn disabled for this item — don't register at all. (The direct patch registers
            // and lets DayTracker drop it, which is fine once per harvest; but data-sync/catch-up
            // re-evaluate the same node on every data change / re-stream, and a register+drop cycle
            // each time would churn the pending list and the save file.)
            if (threshold <= 0) return false;

            int qty;
            try { qty = bi.GetQuantity(); } catch { return false; }
            if (qty > 0) return false; // not exhausted

            if (!TryWeather(out var weather)) return false;

            // Make sure the per-world state is current before writing — SetWorldInstance catch-ups
            // fire during world load, inside the window where CurrentWorldId can still point at the
            // PREVIOUS world (the poll only runs every ~60 frames once resolved). Without this, a
            // catch-up registration could be flushed into the old world's save file (Issue B redux).
            Plugin.PollWorldId();
            if (Plugin.PendingGatherRespawns.ContainsKey(posKey)) return false; // re-check after a possible world switch

            Plugin.PendingGatherRespawns[posKey] = (weather!.NetworkedCurrentGameTime, itemName);
            try { Plugin.GatherWid[posKey] = bi.GetWorldItemInstanceId(); } catch { }
            Plugin.SavePending();
            Plugin.Logger.LogInfo(
                $"[TreeRespawnMod] Gather resource \"{itemName}\" exhausted ({src}) at {posKey}. " +
                $"Will respawn in {threshold} days.");
            return true;
        }
        catch (Exception ex)
        {
            try { Plugin.Logger.LogError($"[TreeRespawnMod] TryRegisterGather({src}): {ex}"); } catch { }
            return false;
        }
    }

    // Register a felled tree from data alone. Data-level stump condition: IsExhausted() && !Destroyed —
    // confirmed for stumps by the v1.1.1 gate diagnostic (a stump reads IsExhausted=True while every
    // standing/partial tree reads False; see architecture.md → Resource/Tree). Callers must only pass
    // nodes already classified as multi-piece trees (KnownNodes / harvestPieces >= 2), so single-piece
    // harvestables never get here.
    internal static bool TryRegisterTree(BiomeItemInstance bi, string posKey, string src)
    {
        try
        {
            if (Plugin.RegisteredStumps.Contains(posKey) || Plugin.PendingRespawns.ContainsKey(posKey)) return false;

            bool exhausted, destroyed;
            try { exhausted = bi.IsExhausted(); destroyed = bi.Destroyed; } catch { return false; }
            if (!exhausted || destroyed) return false; // standing tree, or stump already cleared

            if (!TryWeather(out var weather)) return false;

            Plugin.PollWorldId(); // see TryRegisterGather — same world-switch race guard
            if (Plugin.RegisteredStumps.Contains(posKey) || Plugin.PendingRespawns.ContainsKey(posKey)) return false;

            Plugin.RegisteredStumps.Add(posKey);
            Plugin.PendingRespawns[posKey] = weather!.NetworkedCurrentGameTime;
            try { Plugin.TreeWid[posKey] = bi.GetWorldItemInstanceId(); } catch { }
            Plugin.SavePending();
            Plugin.Logger.LogInfo(
                $"[TreeRespawnMod] Tree felled ({src}) at {posKey} (day={weather.GetDaysPassed()}). " +
                $"Will respawn in {Plugin.RespawnDays.Value} days if stump remains.");
            return true;
        }
        catch (Exception ex)
        {
            try { Plugin.Logger.LogError($"[TreeRespawnMod] TryRegisterTree({src}): {ex}"); } catch { }
            return false;
        }
    }

    // Host/authority + weather gate. On a co-op CLIENT this returns false for every candidate — that
    // is the expected topology (only the host registers), so authority-denied is NOT warned about;
    // only a genuinely missing WeatherSystem is (throttled — these paths re-fire constantly).
    private static bool TryWeather(out WeatherSystem? weather)
    {
        if (Plugin.TryGetServerWeather(out weather, out var reason)) return true;
        if (reason != "LocalPlayer.IsServer=false" && reason != "IsServer=false"
            && (DateTime.UtcNow - _lastWeatherWarn).TotalSeconds >= 5)
        {
            _lastWeatherWarn = DateTime.UtcNow;
            Plugin.Logger.LogWarning(
                $"[TreeRespawnMod] WeatherSystem not available ({reason}), skipping registration. " +
                $"world={Plugin.CurrentWorldId ?? "?"}");
        }
        return false;
    }
}
