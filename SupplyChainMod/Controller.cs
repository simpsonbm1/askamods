using System;
using System.Collections.Generic;
using System.Text;
using SSSGame.AI;
using UnityEngine;

namespace SupplyChainMod;

// SupplyController (v0.3.0, Phase 1b) — the complaint-driven autopilot. Consumes demand signals fed
// by ComplaintPatches.ComplaintLog (NoteDemand/NoteAlarm), runs a per-item state machine on the
// tracker's MasterTick (Tick), and actuates through the SAME rank-move actuator + ledger the v0.2.x
// spike uses (RankActuator/SpikeLedger) — never its own write path. Starts in DRY-RUN (logs/toasts
// intended actions, writes nothing); the ControllerArmHotkey (default F11) toggles armed/dry-run at
// runtime. Shares RankActuator.BoostedStationKeys with ActuationSpike so the two actuators never
// target the same station.
//
// State machine per demanded item name: Pending -> Boosting -> Cooldown -> (Pending again, or
// CapacityLockout after MaxBoostCyclesPerDemand cycles with the demand still fresh) -> Pending.
// A demand that goes stale (no re-add for DemandRetainSeconds) is removed outright from any state,
// reverting first if it was Boosting.
//
// v0.3.1 — in-game test 1 showed item complaints arrive in BURSTS with >20s gaps between bursts, so
// every demand went stale (the old DemandStaleSeconds=20) before ever reaching the 45s hysteresis —
// zero demands ever became boost-eligible. DemandStaleSeconds is renamed DemandRetainSeconds
// (default 180s, comfortably spans the burst gaps seen) — the KEY is renamed, not just the default,
// so BepInEx's "keep stale values for kept keys" behavior can't silently re-apply the old 20s value
// to existing user configs; the orphaned old key line (if any) is harmless. A new
// MinDemandSpanSeconds (default 30) gate requires FirstSeen..LastSeen to span at least that long
// before a demand is boost-eligible — chronic bursty problems accumulate span across bursts, a
// single one-off burst never does.
//
// v0.3.2 — in-game test 2: no bug, state machine correct. Purely a UX gap: gathered-material
// demands (no crafting task) correctly hit "no boostable task" but looked identical, in the log,
// to a craftable demand still maturing through hysteresis/span — the player had zero feedback for
// minutes either way. Added a one-time creation-time target peek (PeekNewDemands) that tells the
// player up front whether a new demand is craftable and watching, or has no crafting task at all
// (advisory only — never gates actuation, which always re-checks at eligibility time).
//
// v0.3.3 — in-game test 3 succeeded end-to-end (watching -> dry-run -> armed -> real BOOST -> hold
// -> release, ledger clean) but exposed two gaps: (1) the boosted task was already at rank 0 — a
// no-op boost that still burns a cycle toward a bogus capacity verdict; the player had already
// top-ranked the starving item themselves, so priority wasn't the bottleneck, and the mod should
// SAY so instead of pretending to act. Fixed with TryFindTarget's three-way TargetOutcome
// (Found/AlreadyTop/None) — the first real F4 "verify the action can take effect" precondition in
// this mod: an already-top match is never selected, backs off with a strike counter, and issues its
// own VERDICT after 2 strikes (fires in both armed and dry-run — it's a diagnosis, not an
// actuation). (2) a slow-cadence demand (bursts minutes apart) went stale mid-hold and had to
// re-mature from zero on re-creation — fixed with demand maturity memory (_matureClearedAt): a
// matured demand's item name is remembered for DemandMemoryMinutes after going stale, and a
// same-item re-create within that window gets its span gate pre-credited.
//
// v0.3.4 — PeekNewDemands was collapsing AlreadyTop into the same bucket as None ("no crafting
// task"), which mislabeled an already-top-ranked item (e.g. cooked fish) as uncraftable and dropped
// its watching toast. The peek now treats AlreadyTop as its own truthful outcome: still craftable,
// just already maxed — logged and toasted as such.
//
// v0.3.5 — run 5 milestone: the control loop CLOSED end-to-end (boost -> production resumed ->
// complaints stopped -> early release on demand-cleared, zero errors). Two ground-truth findings
// from that run: (1) Campfire task[0] is a HIDDEN SSSGame.AI.FiltersTaskData (fuel filters — the UI
// never shows it) — boosts were landing at data index 0, ABOVE the game's own fuel-filter task,
// which worked but shouldn't cut ahead of engine machinery. Fixed with EffectiveTopIndex: the
// controller's real target is BoostToRank clamped up past any leading run of FiltersTaskData, computed
// per station. (2) a demand (mushrooms) matured craftable at the SAME station another demand's boost
// was holding, and TryFindTarget mislabeled that contention as None ("no boostable task"), backing
// off 60s at a time until it went stale — fixed with the StationBusy outcome: a locked station is
// still scanned for a matching task (no candidate log, it's contention detection not a real
// candidate) so the demand retries in 20s with no strike/backoff instead of being punished for
// another demand's boost.
internal static class SupplyController
{
    private enum State { Pending, Boosting, Cooldown, CapacityLockout }

    // v0.3.3 — F4 "verify the action can take effect" precondition: a task whose current index is
    // already at/above the effective target rank has nothing to gain from a boost — selecting it
    // would be a no-op that still burns a cycle toward a bogus capacity verdict. AlreadyTop
    // distinguishes "nothing boostable, but the priority lever is maxed" from a genuine None (no
    // crafting task). v0.3.5 — StationBusy distinguishes "a matching task exists but its station is
    // already locked by another boost" from None, so a same-station demand rotates in once the
    // current boost releases instead of being mislabeled "no boostable task" and going stale.
    private enum TargetOutcome { Found, AlreadyTop, StationBusy, None }

    private sealed class DemandEntry
    {
        public string ItemName = "?";
        public DateTime FirstSeen;
        public DateTime LastSeen;
        public bool Important;
        public int CycleCount;
        public State State = State.Pending;
        public float StateSinceTime;

        // Boost bookkeeping — re-resolved by PosKey/name at revert time, never a cached wrapper.
        public string? BoostPosKey;
        public string? BoostStationName;
        public int BoostOrigIndex;

        public DateTime NextEligibleAt = DateTime.MinValue;
        public bool WouldBoostLogged;

        // v0.3.1 — how many times NoteDemand has seen this item (create counts as 1). Burst
        // visibility: a chronic problem racks up sightings across many bursts; a one-off doesn't.
        public int Sightings;

        // v0.3.2 — creation-time target peek (advisory only — the real boost path re-runs
        // TryFindTarget at eligibility time since tasks/stations may have changed by then; nothing
        // gates on Craftable). PeekPending fires once from Tick, NEVER from NoteDemand itself —
        // NoteDemand runs inside the Harmony complaint postfix and must not do a station scan.
        public bool PeekPending = true;
        public bool? Craftable;
        public string? CraftableStation;

        // v0.3.3 — F4 "already at top rank" precondition strike counter. Reset to 0 on a real
        // boost and on CapacityLockout expiry; never gates on its own — TickPending drives it.
        public int AlreadyTopStrikes;
    }

    private static readonly Dictionary<string, DemandEntry> _demands = new();

    // v0.3.3 — demand maturity memory: item -> UTC time its demand entry was removed by
    // StaleSweep WHILE matured (span >= MinDemandSpanSeconds). A same-item re-create within
    // DemandMemoryMinutes gets span credit instead of restarting maturation from zero — needed
    // for slow-cadence demands (bursts minutes apart) that would otherwise never re-mature.
    private static readonly Dictionary<string, DateTime> _matureClearedAt = new();

    // v0.17.0 — Armed/its hotkey handling moved to ArmState.cs (shared with TierCaseController);
    // this is now a forwarding property so every existing read site (this file, ClogController)
    // needed no change.
    internal static bool Armed => ArmState.Armed;

    private static DateTime _lastAlarmAt = DateTime.MinValue;
    private static bool _alarmLoggedThisWindow;

    // v0.6.0 (Phase 2c) — ClogController shares this same militia-alarm lockout window so a fresh
    // combat complaint pauses BOTH controllers' new boosts, not just this one's.
    internal static bool AlarmLockoutActive => (DateTime.UtcNow - _lastAlarmAt).TotalSeconds < Plugin.AlarmLockoutSeconds.Value;

    private static DateTime _lastStatusAt = DateTime.MinValue;

    // ── Public surface (all wrapped so no exception escapes the caller) ────────────────────────

    // Called at complaint re-add cadence (~20/s per held complaint) — keep allocation-light: no
    // LINQ, no logging here.
    internal static void NoteDemand(List<string> items, bool important)
    {
        try
        {
            var now = DateTime.UtcNow;
            for (int i = 0; i < items.Count; i++)
            {
                string name = items[i];
                if (string.IsNullOrEmpty(name)) continue;

                if (_demands.TryGetValue(name, out var entry))
                {
                    entry.LastSeen = now;
                    entry.Sightings++;
                    if (important) entry.Important = true;
                }
                else
                {
                    var newEntry = new DemandEntry
                    {
                        ItemName = name,
                        FirstSeen = now,
                        LastSeen = now,
                        Important = important,
                        Sightings = 1,
                    };

                    // v0.3.3 — maturity memory credit: dictionary lookup only, O(1) and
                    // allocation-free, safe on this hot Harmony-postfix path (unlike a station
                    // scan, which must never run here).
                    if (_matureClearedAt.TryGetValue(name, out var clearedAt))
                    {
                        double minutesSince = (now - clearedAt).TotalMinutes;
                        if (minutesSince <= Plugin.DemandMemoryMinutes.Value)
                        {
                            newEntry.FirstSeen = now.AddSeconds(-Plugin.MinDemandSpanSeconds.Value);
                            _matureClearedAt.Remove(name);
                            double secondsAgo = (now - clearedAt).TotalSeconds;
                            Plugin.Logger.LogInfo(
                                $"[SupplyChain] [controller] demand re-created with maturity credit '{name}' (matured before, cleared {secondsAgo:F0}s ago)");
                        }
                        else
                        {
                            _matureClearedAt.Remove(name); // lazy prune — past DemandMemoryMinutes
                            Plugin.Logger.LogInfo($"[SupplyChain] [controller] demand created '{name}' (important={important})");
                        }
                    }
                    else
                    {
                        Plugin.Logger.LogInfo($"[SupplyChain] [controller] demand created '{name}' (important={important})");
                    }

                    _demands[name] = newEntry;
                }
            }
        }
        catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] [controller] NoteDemand error: {ex}"); }
    }

    internal static void NoteAlarm()
    {
        try
        {
            _lastAlarmAt = DateTime.UtcNow;
            _alarmLoggedThisWindow = false;
        }
        catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] [controller] NoteAlarm error: {ex}"); }
    }

    internal static void NoteWorldLeft()
    {
        try
        {
            int boostingCount = 0;
            foreach (var kv in _demands)
                if (kv.Value.State == State.Boosting) boostingCount++;

            if (boostingCount > 0)
            {
                Plugin.Logger.LogWarning(
                    $"[SupplyChain] [controller] World left with {boostingCount} active boost(s) — their ledger entries remain for restore at the next load of that world.");
            }

            _demands.Clear();
            _matureClearedAt.Clear();
            RankActuator.BoostedStationKeys.Clear();
            ArmState.ResetForWorldLeave();
            _lastAlarmAt = DateTime.MinValue;
            _alarmLoggedThisWindow = false;
            _lastStatusAt = DateTime.MinValue;
        }
        catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] [controller] NoteWorldLeft error: {ex}"); }
    }

    // Called from SupplyChainTracker.MasterTick (TickSeconds cadence, default 5s).
    internal static void Tick(List<StationEntry> stations, string worldId)
    {
        try
        {
            // Guards against the static Armed field reading its bare C# default (false) before
            // any NoteWorldLeft has synced it to config — NoteWorldLeft doesn't reliably fire
            // before the FIRST real world session on every launch path, but Tick only ever runs
            // once a world is genuinely active, so this is the safe place to sync once. (v0.17.0 —
            // moved to ArmState so a second actuator, TierCaseController, shares the same guard.)
            ArmState.EnsureInitialized();

            var now = DateTime.UtcNow;

            PeekNewDemands(stations);
            StaleSweep(stations, worldId, now);

            bool alarmLockoutActive = (now - _lastAlarmAt).TotalSeconds < Plugin.AlarmLockoutSeconds.Value;
            if (alarmLockoutActive)
            {
                if (!_alarmLoggedThisWindow)
                {
                    _alarmLoggedThisWindow = true;
                    Plugin.Logger.LogInfo($"[SupplyChain] [controller] alarm lockout active — no new boosts for {Plugin.AlarmLockoutSeconds.Value}s from the last militia alarm.");
                }
            }
            else
            {
                _alarmLoggedThisWindow = false;
            }

            TickBoosting(stations, worldId, now);
            TickCooldown(now);
            TickCapacityLockout(now);

            if (!alarmLockoutActive)
                TickPending(stations, worldId, now);

            MaybeLogStatus(now);
        }
        catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] [controller] Tick error: {ex}"); }
    }

    // ── Step 0: creation-time target peek (v0.3.2) ──────────────────────────────────────────────
    // Advisory only, runs once per new demand: tells the player whether anything is actionable
    // while a demand is still maturing through hysteresis/span, instead of leaving them guessing
    // for minutes with zero feedback. The real boost path (TickPending) always re-runs
    // TryFindTarget at eligibility time — this peek never gates actuation.
    private static void PeekNewDemands(List<StationEntry> stations)
    {
        foreach (var kv in _demands)
        {
            var entry = kv.Value;
            if (!entry.PeekPending) continue;
            entry.PeekPending = false;

            // v0.3.4 — AlreadyTop is a real crafting task, just already maxed. Conflating it with
            // None (v0.3.3) mislabeled it "has no crafting task" and dropped the watching toast —
            // factually wrong in both the log and the status line's craftable=false.
            // v0.3.5 — same fix for StationBusy: a demand peeked while its only matching station is
            // locked by another boost is still genuinely craftable, not "no crafting task".
            var outcome = TryFindTarget(stations, entry.ItemName, out var t, out _, out _, out _, out var alreadyTopStation, out var busyStation);
            if (outcome == TargetOutcome.Found && t != null)
            {
                entry.Craftable = true;
                entry.CraftableStation = t.StructureName;
                Plugin.Logger.LogInfo(
                    $"[SupplyChain] [controller] demand '{entry.ItemName}' is CRAFTABLE at '{t.StructureName}' — watching (span/hysteresis maturing)");
                SupplyChainTracker.ShowMessage($"Autopilot: watching '{entry.ItemName}' — craftable at '{t.StructureName}'", 6f);
            }
            else if (outcome == TargetOutcome.AlreadyTop)
            {
                entry.Craftable = true;
                entry.CraftableStation = alreadyTopStation;
                Plugin.Logger.LogInfo(
                    $"[SupplyChain] [controller] demand '{entry.ItemName}' craft task exists but is ALREADY top-ranked at '{alreadyTopStation}' — watching for the inputs/capacity diagnosis");
                SupplyChainTracker.ShowMessage($"Autopilot: watching '{entry.ItemName}' — already top priority at '{alreadyTopStation}'", 6f);
            }
            else if (outcome == TargetOutcome.StationBusy)
            {
                entry.Craftable = true;
                entry.CraftableStation = busyStation;
                Plugin.Logger.LogInfo($"[SupplyChain] [controller] demand '{entry.ItemName}' craftable at '{busyStation}' (station currently busy)");
                SupplyChainTracker.ShowMessage($"Autopilot: watching '{entry.ItemName}' — craftable at '{busyStation}'", 6f);
            }
            else
            {
                entry.Craftable = false;
                Plugin.Logger.LogInfo($"[SupplyChain] [controller] demand '{entry.ItemName}' has no crafting task — logged for the Phase 4 task-creation feed");
            }
        }
    }

    // ── Step 1: stale sweep ──────────────────────────────────────────────────────────────────────
    private static void StaleSweep(List<StationEntry> stations, string worldId, DateTime now)
    {
        List<string>? toRemove = null;
        foreach (var kv in _demands)
        {
            var entry = kv.Value;
            double sinceLastSeen = (now - entry.LastSeen).TotalSeconds;
            if (sinceLastSeen <= Plugin.DemandRetainSeconds.Value) continue;

            if (entry.State == State.Boosting)
                RevertEntry(entry, stations, worldId, "demand-cleared");

            double lifetime = (now - entry.FirstSeen).TotalSeconds;
            double span = (entry.LastSeen - entry.FirstSeen).TotalSeconds;

            // v0.3.3 — record maturity memory only if this demand actually matured (passed the
            // span gate) before going stale; a demand that never spanned long enough gets no credit.
            if (span >= Plugin.MinDemandSpanSeconds.Value)
                _matureClearedAt[entry.ItemName] = now;

            Plugin.Logger.LogInfo(
                $"[SupplyChain] [controller] demand '{entry.ItemName}' cleared (stale) after {lifetime:F0}s " +
                $"(span={span:F0}s sightings={entry.Sightings})");

            (toRemove ??= new List<string>()).Add(kv.Key);
        }

        if (toRemove != null)
            foreach (var key in toRemove) _demands.Remove(key);
    }

    // ── Step 3: boosting entries — hold timer ───────────────────────────────────────────────────
    private static void TickBoosting(List<StationEntry> stations, string worldId, DateTime now)
    {
        foreach (var kv in _demands)
        {
            var entry = kv.Value;
            if (entry.State != State.Boosting) continue;
            if (Time.time - entry.StateSinceTime >= Plugin.BoostHoldSeconds.Value)
                RevertEntry(entry, stations, worldId, "hold-elapsed");
        }
    }

    // ── Step 4: cooldown entries — re-boost, or capacity verdict ────────────────────────────────
    private static void TickCooldown(DateTime now)
    {
        foreach (var kv in _demands)
        {
            var entry = kv.Value;
            if (entry.State != State.Cooldown) continue;
            if (Time.time - entry.StateSinceTime < Plugin.CooldownSeconds.Value) continue;

            // Demand is still tracked here (the stale sweep already removed anything that went
            // stale during cooldown), so it's fresh by definition.
            if (entry.CycleCount < Plugin.MaxBoostCyclesPerDemand.Value)
            {
                entry.State = State.Pending; // re-evaluated by TickPending later in this same tick
            }
            else
            {
                Plugin.Logger.LogWarning(
                    $"[SupplyChain] [controller] VERDICT: demand '{entry.ItemName}' persists after {entry.CycleCount} boost cycles — " +
                    $"capacity-limited, alerting + locking out {Plugin.CapacityLockoutMinutes.Value} min");
                SupplyChainTracker.ShowMessage(
                    $"Autopilot ALERT: '{entry.ItemName}' demand exceeds production capacity — add producers or reduce consumers", 12f);
                entry.State = State.CapacityLockout;
                entry.StateSinceTime = Time.time;
            }
        }
    }

    // ── Step 5: capacity-lockout entries — expiry ───────────────────────────────────────────────
    private static void TickCapacityLockout(DateTime now)
    {
        foreach (var kv in _demands)
        {
            var entry = kv.Value;
            if (entry.State != State.CapacityLockout) continue;
            if (Time.time - entry.StateSinceTime < Plugin.CapacityLockoutMinutes.Value * 60f) continue;

            entry.CycleCount = 0;
            entry.State = State.Pending;
            entry.WouldBoostLogged = false;
            entry.NextEligibleAt = DateTime.MinValue;
            entry.AlreadyTopStrikes = 0;
        }
    }

    // ── Step 6: pending entries — target resolution + boost/dry-run ────────────────────────────
    private static void TickPending(List<StationEntry> stations, string worldId, DateTime now)
    {
        int boostingCount = 0;
        foreach (var kv in _demands)
            if (kv.Value.State == State.Boosting) boostingCount++;

        List<DemandEntry>? eligible = null;
        foreach (var kv in _demands)
        {
            var entry = kv.Value;
            if (entry.State != State.Pending) continue;

            double sinceLastSeen = (now - entry.LastSeen).TotalSeconds;
            if (sinceLastSeen > Plugin.DemandRetainSeconds.Value) continue; // defensive — stale sweep runs first

            if ((now - entry.FirstSeen).TotalSeconds < Plugin.HysteresisSeconds.Value) continue;

            // v0.3.1 — burst-tolerant span gate: the demand must have been OBSERVED across a
            // minimum span (bursty chronic problems accumulate span across bursts; a single
            // transient burst never spans long enough to qualify).
            if ((entry.LastSeen - entry.FirstSeen).TotalSeconds < Plugin.MinDemandSpanSeconds.Value) continue;

            if (now < entry.NextEligibleAt) continue;

            (eligible ??= new List<DemandEntry>()).Add(entry);
        }

        if (eligible == null) return;

        // Important first, then FirstSeen ascending. Short list, ticked once per TickSeconds —
        // a plain manual sort (not LINQ, per the NoteDemand allocation-light rule's spirit).
        eligible.Sort((a, b) =>
        {
            if (a.Important != b.Important) return a.Important ? -1 : 1;
            return a.FirstSeen.CompareTo(b.FirstSeen);
        });

        foreach (var entry in eligible)
        {
            if (boostingCount >= Plugin.MaxConcurrentBoosts.Value) break;

            var outcome = TryFindTarget(stations, entry.ItemName, out var target, out var task, out int taskIndex, out int targetRank, out string? alreadyTopStation, out string? busyStation);

            if (outcome == TargetOutcome.StationBusy)
            {
                // v0.3.5 — the item IS boostable, just not on this station right now (another
                // demand's boost has it locked). No strike, no WouldBoostLogged, state stays
                // Pending — this gives a same-station demand a rotation chance once the current
                // boost releases instead of mislabeling it "no boostable task" until it goes stale.
                Plugin.Logger.LogInfo($"[SupplyChain] [controller] '{entry.ItemName}' target station '{busyStation}' busy with another boost — retrying shortly");
                entry.NextEligibleAt = now.AddSeconds(20);
                continue;
            }

            if (outcome == TargetOutcome.AlreadyTop)
            {
                entry.AlreadyTopStrikes++;
                string stationName = alreadyTopStation ?? "?";
                string dryRunPrefix = Armed ? "" : "DRY-RUN ";

                if (entry.AlreadyTopStrikes < 2)
                {
                    Plugin.Logger.LogInfo(
                        $"[SupplyChain] [controller] {dryRunPrefix}'{entry.ItemName}' task already at top rank on '{stationName}' " +
                        $"— priority lever maxed; backing off (strike {entry.AlreadyTopStrikes}/2)");
                    entry.NextEligibleAt = now.AddSeconds(120);
                }
                else
                {
                    Plugin.Logger.LogWarning(
                        $"[SupplyChain] [controller] {dryRunPrefix}VERDICT: '{entry.ItemName}' already highest priority on '{stationName}' but demand persists " +
                        $"— production limited by inputs/capacity, not priority; locking out {Plugin.CapacityLockoutMinutes.Value} min");
                    SupplyChainTracker.ShowMessage(
                        $"Autopilot ALERT: '{entry.ItemName}' already highest priority — limited by inputs/capacity, not priority", 12f);
                    entry.State = State.CapacityLockout;
                    entry.StateSinceTime = Time.time;
                    entry.AlreadyTopStrikes = 0;
                }
                continue;
            }

            if (outcome != TargetOutcome.Found || target == null || task == null)
            {
                Plugin.Logger.LogInfo($"[SupplyChain] [controller] no boostable task for '{entry.ItemName}' — Phase 4 (task creation) feed.");
                entry.NextEligibleAt = now.AddSeconds(60);
                continue;
            }

            // v0.3.5 — targetRank is the SELECTED station's own effective target (BoostToRank
            // clamped up past any leading hidden FiltersTaskData run), not the raw config value.
            int toRank = targetRank;

            if (Armed)
            {
                var ledgerEntry = new LedgerEntry
                {
                    WorldId = worldId,
                    PosKey = target.PosKey,
                    StationName = target.StructureName,
                    TaskIndex = taskIndex,
                    ItemName = entry.ItemName,
                    OrigPriority = taskIndex,
                    BoostPriority = toRank,
                    UtcTicks = DateTime.UtcNow.Ticks,
                };
                SpikeLedger.Append(ledgerEntry);

                string path = RankActuator.ApplyRank(target.Ws, task, taskIndex, toRank);

                RankActuator.BoostedStationKeys.Add(target.PosKey);
                entry.State = State.Boosting;
                entry.CycleCount++;
                entry.StateSinceTime = Time.time;
                entry.BoostPosKey = target.PosKey;
                entry.BoostStationName = target.StructureName;
                entry.BoostOrigIndex = taskIndex;
                entry.AlreadyTopStrikes = 0;
                boostingCount++;

                Plugin.Logger.LogInfo(
                    $"[SupplyChain] [controller] BOOST '{entry.ItemName}' station='{target.StructureName}' " +
                    $"rank {taskIndex}->{toRank} cycle {entry.CycleCount}/{Plugin.MaxBoostCyclesPerDemand.Value} path={path}");
                SupplyChainTracker.ShowMessage($"Autopilot BOOST: '{entry.ItemName}' → rank {toRank} on '{target.StructureName}'", 8f);
            }
            else
            {
                Plugin.Logger.LogInfo(
                    $"[SupplyChain] [controller] DRY-RUN would BOOST '{entry.ItemName}' task[{taskIndex}] on '{target.StructureName}' (rank {taskIndex}->{toRank})");
                SupplyChainTracker.ShowMessage(
                    $"Autopilot [dry-run] would boost '{entry.ItemName}' on '{target.StructureName}' — press F11 to arm", 8f);
                entry.WouldBoostLogged = true;
                entry.NextEligibleAt = now.AddSeconds(60);
                // Entry stays Pending — dry-run never advances the state machine past Pending.
            }
        }
    }

    // Target resolution: allow-listed station classes, first non-pinned task whose item name
    // matches, then require HasStateAuthority true-or-unreadable (spike's gate semantics). Logs
    // EVERY item-name-matching candidate on an UNLOCKED station it examines (win or not) — doubles
    // as research-task discriminator data collection.
    //
    // v0.3.3 — F4 precondition: a matching task whose current index is already at/above the
    // effective target rank (see EffectiveTopIndex) has nothing to gain from a boost (a no-op would
    // still burn a cycle toward a bogus capacity verdict) — it is NOT selected, but scanning
    // continues for a genuinely boostable candidate on another station. AlreadyTop is returned (with
    // the first such station's name) only if no boostable candidate was found anywhere.
    //
    // v0.3.5 — a station already locked by RankActuator.BoostedStationKeys is still scanned for a
    // matching task (cheap; no candidate log — this is contention detection, not a real candidate)
    // so it can be told apart from a genuine None: StationBusy means "this item IS boostable, just
    // not on THIS boost cycle" — the caller should retry soon with no strike/backoff, instead of
    // mislabeling it "no boostable task" until the demand goes stale. targetRank carries the
    // selected station's own effective target back to the caller (BoostToRank clamped up past any
    // leading hidden FiltersTaskData run on THAT station).
    private static TargetOutcome TryFindTarget(List<StationEntry> stations, string itemName, out StationEntry? target, out WorkstationTaskData? task, out int taskIndex, out int targetRank, out string? alreadyTopStation, out string? busyStation)
    {
        target = null;
        task = null;
        taskIndex = -1;
        targetRank = Plugin.BoostToRank.Value;
        alreadyTopStation = null;
        busyStation = null;
        bool sawAlreadyTop = false;
        bool sawBusy = false;

        string allowListRaw = Plugin.StationClassAllowList.Value ?? "";
        var prefixes = allowListRaw.Split(',');

        foreach (var entry in stations)
        {
            if (entry == null) continue;

            string cls;
            try { cls = Common.NativeClassName(entry.Ws); } catch { continue; }

            bool allowed = false;
            for (int i = 0; i < prefixes.Length; i++)
            {
                var p = prefixes[i].Trim();
                if (p.Length == 0) continue;
                if (cls.StartsWith(p, StringComparison.Ordinal)) { allowed = true; break; }
            }
            if (!allowed) continue;

            bool stationLocked = RankActuator.BoostedStationKeys.Contains(entry.PosKey);

            Il2CppSystem.Collections.Generic.List<WorkstationTaskData>? datas = null;
            try { datas = entry.Ws.GetWorkstationTaskDatas(); } catch { continue; }
            if (datas == null) continue;

            WorkstationTaskData? candidateTask = null;
            int candidateIndex = -1;
            for (int i = 0; i < datas.Count; i++)
            {
                WorkstationTaskData? t = null;
                try { t = datas[i]; } catch { continue; }
                if (t == null) continue;

                bool pinned = false;
                try { pinned = t.pinnedTask; } catch { pinned = false; }
                if (pinned) continue;

                if (RankActuator.SafeTaskItemName(t) != itemName) continue;

                candidateTask = t;
                candidateIndex = i;
                break;
            }

            if (candidateTask == null) continue;

            if (stationLocked)
            {
                if (busyStation == null) busyStation = entry.StructureName;
                sawBusy = true;
                continue;
            }

            int effectiveTarget = Math.Max(Plugin.BoostToRank.Value, EffectiveTopIndex(datas));

            if (candidateIndex <= effectiveTarget)
            {
                LogCandidate(itemName, entry, candidateTask, candidateIndex, " — already at/above target rank, skipping");
                if (alreadyTopStation == null) alreadyTopStation = entry.StructureName;
                sawAlreadyTop = true;
                continue;
            }

            LogCandidate(itemName, entry, candidateTask, candidateIndex, "");

            bool authorityOk = true;
            try { authorityOk = entry.Ws.HasStateAuthority; }
            catch { authorityOk = true; } // unreadable — treated as allowed, spike's gate semantics

            if (!authorityOk)
            {
                Plugin.Logger.LogInfo($"[SupplyChain] [controller] target for '{itemName}': station='{entry.StructureName}' skipped — not host (HasStateAuthority=false).");
                continue;
            }

            target = entry;
            task = candidateTask;
            taskIndex = candidateIndex;
            targetRank = effectiveTarget;
            return TargetOutcome.Found;
        }

        if (sawBusy) return TargetOutcome.StationBusy;
        if (sawAlreadyTop) return TargetOutcome.AlreadyTop;
        return TargetOutcome.None;
    }

    // v0.3.5 — cooking/charcoal-family stations keep a hidden fuel/filler FiltersTaskData at index
    // 0 (the UI does not show it; confirmed in-game 2026-07-12, Campfire: task[0] is fuel filters —
    // Firewood/Coal/Stick/Bark/Thatch/Wood Shaft, priority -1 each, removable=False). Boosting above
    // it would cut ahead of the game's own filter machinery. Walks from index 0 and returns the
    // first non-FiltersTaskData index; a cast failure is treated as non-filter (returns that index);
    // an all-filters list (degenerate) returns datas.Count.
    private static int EffectiveTopIndex(Il2CppSystem.Collections.Generic.List<WorkstationTaskData> datas)
    {
        for (int i = 0; i < datas.Count; i++)
        {
            WorkstationTaskData? t = null;
            try { t = datas[i]; } catch { continue; }
            if (t == null) continue;

            FiltersTaskData? filters = null;
            try { filters = t.TryCast<FiltersTaskData>(); } catch { filters = null; }
            if (filters == null) return i;
        }
        return datas.Count;
    }

    private static void LogCandidate(string itemName, StationEntry station, WorkstationTaskData task, int index, string suffix)
    {
        string typeName = "?";
        try { typeName = task.GetIl2CppType().FullName; } catch { }

        string qtyRange = "?";
        try { qtyRange = task.GetQuantityRange().ToString(); } catch { }

        int villagerCount = -1;
        try { var v = task.VillagersInCharge; villagerCount = v != null ? v.Count : 0; } catch { }

        Plugin.Logger.LogInfo(
            $"[SupplyChain] [controller] target for '{itemName}': station='{station.StructureName}' task[{index}] " +
            $"(type='{typeName}' qtyRange={qtyRange} villagersInCharge={villagerCount}){suffix}");
    }

    // ── Shared revert (stale sweep + hold-elapsed) ──────────────────────────────────────────────
    // Station/task not found: log warning, unregister posKey, drop to Cooldown anyway — mirrors
    // ActuationSpike's own asymmetric ledger policy exactly: station-not-found KEEPS the ledger
    // entry (nothing conclusive to act on — a future world load may still find it); task-not-found
    // REMOVES it (the station is there but the task itself is gone, nothing to restore).
    private static void RevertEntry(DemandEntry entry, List<StationEntry> stations, string worldId, string reason)
    {
        string posKey = entry.BoostPosKey ?? "?";
        string stationName = entry.BoostStationName ?? "?";
        int origIndex = entry.BoostOrigIndex;
        string itemName = entry.ItemName;

        StationEntry? target = RankActuator.FindStation(stations, posKey, stationName);
        if (target == null)
        {
            Plugin.Logger.LogWarning(
                $"[SupplyChain] [controller] Revert({reason}): station '{stationName}' (posKey={posKey}) not found for '{itemName}' " +
                "— ledger entry kept for next world load; dropping to Cooldown.");
            RankActuator.BoostedStationKeys.Remove(posKey);
            MoveToCooldown(entry);
            return;
        }

        var ws = target.Ws;
        Il2CppSystem.Collections.Generic.List<WorkstationTaskData>? datas = null;
        try { datas = ws.GetWorkstationTaskDatas(); }
        catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] [controller] Revert GetWorkstationTaskDatas error: {ex}"); }

        int taskIndex = RankActuator.FindTaskIndex(datas, origIndex, itemName, out var task);
        if (task == null)
        {
            Plugin.Logger.LogWarning(
                $"[SupplyChain] [controller] Revert({reason}): task '{itemName}' not found on station '{target.StructureName}' " +
                "— dropping ledger entry; dropping to Cooldown.");
            SpikeLedger.Remove(new LedgerEntry { WorldId = worldId, PosKey = posKey, TaskIndex = origIndex, ItemName = itemName });
            RankActuator.BoostedStationKeys.Remove(posKey);
            MoveToCooldown(entry);
            return;
        }

        string path = RankActuator.ApplyRank(ws, task, taskIndex, origIndex);

        SpikeLedger.Remove(new LedgerEntry { WorldId = worldId, PosKey = posKey, TaskIndex = origIndex, ItemName = itemName });
        RankActuator.BoostedStationKeys.Remove(posKey);

        Plugin.Logger.LogInfo($"[SupplyChain] [controller] REVERT ({reason}) '{itemName}' station='{target.StructureName}' rank back to {origIndex} path={path}");
        SupplyChainTracker.ShowMessage($"Autopilot REVERT ({reason}): '{itemName}' back to rank {origIndex} on '{target.StructureName}'.", 6f);

        MoveToCooldown(entry);
    }

    private static void MoveToCooldown(DemandEntry entry)
    {
        entry.State = State.Cooldown;
        entry.StateSinceTime = Time.time;
        entry.BoostPosKey = null;
        entry.BoostStationName = null;
    }

    // ── Status line ──────────────────────────────────────────────────────────────────────────────
    private static void MaybeLogStatus(DateTime now)
    {
        if (_demands.Count == 0) return;
        if ((now - _lastStatusAt).TotalSeconds < 60) return;
        _lastStatusAt = now;

        int pending = 0, boosting = 0, cooldown = 0, lockout = 0, dryRunWouldBoost = 0;
        foreach (var kv in _demands)
        {
            switch (kv.Value.State)
            {
                case State.Pending: pending++; break;
                case State.Boosting: boosting++; break;
                case State.Cooldown: cooldown++; break;
                case State.CapacityLockout: lockout++; break;
            }
            if (kv.Value.WouldBoostLogged) dryRunWouldBoost++;
        }

        string detail = "";
        if (_demands.Count <= 8)
        {
            var sb = new StringBuilder();
            foreach (var kv in _demands)
            {
                var e = kv.Value;
                double age = (now - e.FirstSeen).TotalSeconds;
                double span = (e.LastSeen - e.FirstSeen).TotalSeconds;
                string craftableStr = e.Craftable.HasValue ? (e.Craftable.Value ? "true" : "false") : "?";
                sb.Append(" | ").Append(e.ItemName)
                  .Append("(state=").Append(e.State)
                  .Append(" age=").Append(age.ToString("F0")).Append('s')
                  .Append(" span=").Append(span.ToString("F0")).Append('s')
                  .Append(" sightings=").Append(e.Sightings)
                  .Append(" craftable=").Append(craftableStr)
                  .Append(')');
            }
            detail = sb.ToString();
        }

        Plugin.Logger.LogInfo(
            $"[SupplyChain] [controller] status: demands={_demands.Count} pending={pending} boosting={boosting} " +
            $"cooldown={cooldown} lockout={lockout} armed={Armed} dryRunWouldBoost={dryRunWouldBoost}{detail}");
    }
}
