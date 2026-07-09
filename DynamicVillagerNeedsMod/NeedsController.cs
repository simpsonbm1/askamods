using System;
using System.Collections.Generic;
using System.Text;
using Il2CppInterop.Runtime.InteropTypes;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using SandSailorStudio.Attributes;  // VariableAttribute
using SSSGame;
using SSSGame.UI;        // ScheduleType
using SSSGame.Weather;   // WeatherSystem (day length + day/night diagnostics)
using UnityEngine;

namespace DynamicVillagerNeedsMod;

// Poller (MonoBehaviour Update) — same pattern as the other AskaMods.
//
// We drive villager behavior through the game's REAL per-hour schedule (Rpc_ChangeSchedule), NOT the
// overrideSchedule fields. Confirmed in-game: setting overrideSchedule/scheduleOverride forces the
// behavior LABEL the FSM reads but bypasses the task-dispatch pipeline entirely — villagers just idle.
// The vanilla schedule path is the only thing that actually dispatches work/sleep/leisure tasks, so we
// rewrite the schedule to our needs-chosen activity and let the native pipeline run.
//
// Each tick, for every villager we own (host/authority), pick a behavior from its needs:
//   - rest below SleepWhenRestBelowHours            -> Sleep (until rest reaches WakeWhenRestAboveHours)
//   - else food or water critically low              -> Leisure (until they recover; rare safety net)
//   - else happiness below LeisureWhenHappinessBelow  -> Leisure (until it recovers / plateaus)
//   - else                                            -> Work
// COLD IS DELIBERATELY NOT A LEISURE TRIGGER. The game warms villagers fully on its own during work
// (its warmUpBehaviour/_warmUpQuest), confirmed in testing; forcing a leisure trip for warmth only
// warmed them partway and made them thrash fire<->work. So we read warmth for logging only and leave
// cold to the game. Food/water self-refill to full when addressed, so their safety net can't thrash.
// Hysteresis (the "until" thresholds) prevents flip-flopping. While in Leisure we also actively top
// happiness up (LeisureHoursToFullHappiness) by writing the happiness VariableAttribute, and while in
// Sleep we can speed rest gain (SleepHoursToFullRest) so sleeps are shorter — both via direct VAttr
// writes. If happiness can't rise (capped by housing etc.) we detect the plateau, send the villager
// back to work, and suppress happiness-leisure re-entry for a while so they don't oscillate. We only
// rewrite the schedule when the chosen mode changes.
public class NeedsController : MonoBehaviour
{
    private enum Mode { Work, Sleep, Leisure }

    // Plateau window: how long happiness must fail to rise (relative to what our boost is pumping in)
    // before we treat the villager as happiness-capped and stop holding them in leisure.
    private const float PlateauWindowSeconds = 4f;
    // Fraction of the boost-expected rise below which we call it a plateau (the cap is clamping our writes).
    private const float PlateauRiseFraction = 0.25f;
    // After a capped exit, in-game hours to wait before happiness-leisure may trigger again.
    private const float HappyRetryHours = 4f;

    private sealed class HappyTrack
    {
        public float windowStart;   // happiness sampled at the start of the current plateau window
        public float windowTimer;   // seconds elapsed in the current window
        public bool plateaued;      // last verdict: happiness not rising despite the boost (capped)
        public float cooldown;      // seconds remaining during which happiness-leisure is suppressed
    }

    private readonly Dictionary<VillagerSurvival, Mode> _mode = new();
    // Last schedule type we wrote per villager — drives idempotency (only re-apply on change).
    private readonly Dictionary<VillagerSurvival, ScheduleType> _applied = new();
    // Last packed schedule value we wrote — compared against the live __NetworkedSchedule each tick to
    // detect whether the game is keeping our schedule or reverting it (the sched=match/REVERTED diag).
    private readonly Dictionary<VillagerSurvival, long> _appliedPacked = new();
    // Each villager's original packed schedule, captured the first time we touch it, so we can put it
    // back on shutdown rather than leaving a collapsed needs-schedule baked in.
    private readonly Dictionary<VillagerSurvival, long> _originalSchedule = new();
    // Per-villager happiness movement tracking for plateau/cap detection.
    private readonly Dictionary<VillagerSurvival, HappyTrack> _happy = new();
    // Per-villager last warmth (VAttr-normalized) — lets us detect when the game is warming them and
    // accelerate it (FireWarmthMultiplier) without touching cooling.
    private readonly Dictionary<VillagerSurvival, float> _lastWarmth = new();
    // Per-villager last food/water (VAttr-normalized) — lets us observe the game's drain each frame and
    // scale it (HungerRateMultiplier / ThirstRateMultiplier).
    private readonly Dictionary<VillagerSurvival, float> _lastFood = new();
    private readonly Dictionary<VillagerSurvival, float> _lastWater = new();

    // --- RespectManualSchedule Phase 0 (read-only diagnostics; ManualScheduleDiagnostics gate) ---
    // Each villager's ORIGINAL per-hour schedule, captured the same moment as _originalSchedule (first
    // touch, before our first write). Captured unconditionally — Phase 1 (actual manual-schedule mode)
    // will need it; only the diagnostic logging around it is gated on ManualScheduleDiagnostics.
    private readonly Dictionary<VillagerSurvival, int[]> _scheduleHours = new();
    // Last live packed schedule value we've already logged as "externally changed", so the player-edit
    // log line fires once per change instead of every tick while it stays changed.
    private readonly Dictionary<VillagerSurvival, long> _lastLoggedForeignPacked = new();
    // Last live packed schedule value observed in the Enabled=false observe-only path (distinct from
    // _appliedPacked/_lastLoggedForeignPacked, which only exist once the control loop has written
    // something) — lets LogObservedSchedule re-log/re-snapshot only when the player's schedule changes.
    private readonly Dictionary<VillagerSurvival, long> _observedPacked = new();
    // Accumulator for the periodic (10s) same-workstation cohort report.
    private float _cohortTimer;

    private float _summaryTimer;
    // Accumulator for the periodic food/water re-check (FoodRecheckIntervalSeconds).
    private float _recheckTimer;
    private float _tickTimer;
    private const float TickInterval = 0.25f;   // 4 Hz — villager needs/behavior don't need per-frame updates

    void Update()
    {
        bool enabledCtl = Plugin.Enabled.Value;
        bool msDiag = Plugin.ManualScheduleDiagnostics.Value;
        if (!enabledCtl && !msDiag) return;

        _tickTimer += Time.deltaTime;
        if (_tickTimer < TickInterval) return;

        var tracked = Plugin.TrackedSurvivals;
        if (tracked.Count == 0) { _tickTimer = 0f; return; }

        float dt = _tickTimer;   // real elapsed since last processed tick — keeps all rate*dt math correct
        _tickTimer = 0f;

        // Enabled=false + ManualScheduleDiagnostics=true: fully read-only observe path. Zero writes (no
        // ApplyScheduleIfNeeded/Rpc/VAttr writes/RecheckConsumeNeeds) — this is what lets the MSdiag hour
        // calibration + cohort reports watch the player's LIVE painted schedule instead of whatever the
        // control loop below would otherwise collapse it to. Still advances the same 5s/10s accumulators.
        if (!enabledCtl)
        {
            ObserveTick(tracked, dt);
            return;
        }

        float sleepBelow = Plugin.SleepWhenRestBelowHours.Value;
        float wakeAbove = Plugin.WakeWhenRestAboveHours.Value;
        float needBelow = Plugin.LeisureWhenNeedBelow.Value;
        float needAbove = Mathf.Max(Plugin.LeisureUntilNeedAbove.Value, needBelow);
        float happyBelow = Plugin.LeisureWhenHappinessBelow.Value;
        float happyUntil = Mathf.Max(Plugin.LeisureUntilHappinessAbove.Value, happyBelow);
        float hoursToFull = Plugin.LeisureHoursToFullHappiness.Value;
        float restHoursToFull = Plugin.SleepHoursToFullRest.Value;
        bool debug = Plugin.DebugLogging.Value;

        // In-game hour length (seconds) drives the boost rates and the retry cooldown.
        float hourSeconds = 0f;
        try { var w = WeatherSystem.Instance; if (w != null) hourSeconds = w.dayLength / 24f; } catch { }
        bool boostOn = hoursToFull > 0f && hourSeconds > 0f;
        // Boost rates are in normalized (0..1 of full) per second; BoostAttr scales by the attr's range.
        float boostPerSec = boostOn ? 1f / (hoursToFull * hourSeconds) : 0f;
        float restBoostPerSec = restHoursToFull > 0f && hourSeconds > 0f ? 1f / (restHoursToFull * hourSeconds) : 0f;
        float retrySeconds = hourSeconds > 0f ? HappyRetryHours * hourSeconds : 120f;
        float warmthMult = Plugin.FireWarmthMultiplier.Value;
        float hungerMult = Plugin.HungerRateMultiplier.Value;
        float thirstMult = Plugin.ThirstRateMultiplier.Value;

        _summaryTimer += dt;
        bool summaryNow = (debug || msDiag) && _summaryTimer >= 5f;
        if (summaryNow) _summaryTimer = 0f;

        // MSdiag cohort report cadence (independent 10s accumulator).
        _cohortTimer += dt;
        bool cohortNow = msDiag && _cohortTimer >= 10f;
        if (cohortNow) _cohortTimer = 0f;

        // Food/water re-check cadence: every FoodRecheckIntervalSeconds, re-arm hungry villagers' survival
        // quests so they pick up newly-restocked food instead of parking at an empty store (see RecheckConsumeNeeds).
        float recheckInterval = Plugin.FoodRecheckIntervalSeconds.Value;
        float recheckBelow = Plugin.FoodRecheckWhenNeedBelow.Value;
        _recheckTimer += dt;
        bool recheckNow = recheckInterval > 0f && _recheckTimer >= recheckInterval;
        if (recheckNow) _recheckTimer = 0f;

        for (int i = tracked.Count - 1; i >= 0; i--)
        {
            var surv = tracked[i];
            if (surv == null) { tracked.RemoveAt(i); continue; }

            try
            {
                if (!CanControl(surv)) continue;

                var villager = surv.GetVillager();
                if (villager == null) continue;

                var rest = surv._restVariableAttribute;
                if (rest == null) continue;

                float restHours = rest.GetValue();
                float happiness = villager.NormalizedHappiness;

                // Basic-need leisure trigger uses food + water only (warmth/cold is left to the game's
                // own warm-up behavior — see the class header). Warmth is still read for logging.
                float food = surv.GetNormalizedFood();
                float water = surv.GetNormalizedWater();
                float minNeed = Mathf.Min(food, water);
                bool critical = surv.IsStarving || surv.IsDehydrated;

                // Independent of the schedule decision below: keep the vanilla eat/drink path re-armed for
                // hungry villagers so they don't get stuck at an empty store (see RecheckConsumeNeeds).
                if (recheckNow)
                    RecheckConsumeNeeds(surv, food, water, recheckBelow, debug, i);

                Mode prev = _mode.TryGetValue(surv, out var m) ? m : Mode.Work;

                // Track happiness movement so we can spot a capped villager (the boost can't lift them).
                var ht = _happy.TryGetValue(surv, out var h0)
                    ? h0 : (_happy[surv] = new HappyTrack { windowStart = happiness });
                if (ht.cooldown > 0f) ht.cooldown -= dt;
                if (boostOn && prev == Mode.Leisure)
                {
                    ht.windowTimer += dt;
                    if (ht.windowTimer >= PlateauWindowSeconds)
                    {
                        float expected = boostPerSec * ht.windowTimer;
                        ht.plateaued = (happiness - ht.windowStart) < expected * PlateauRiseFraction;
                        ht.windowStart = happiness;
                        ht.windowTimer = 0f;
                    }
                }
                else
                {
                    ht.windowStart = happiness;
                    ht.windowTimer = 0f;
                    ht.plateaued = false;
                }
                bool happyAllowed = ht.cooldown <= 0f;

                Mode next = Decide(prev, restHours, surv.IsSleeping, minNeed, critical, happiness,
                    sleepBelow, wakeAbove, needBelow, needAbove,
                    happyBelow, happyUntil, ht.plateaued, happyAllowed);
                _mode[surv] = next;

                // A capped villager just gave up on happiness-leisure -> don't immediately retry it.
                if (prev == Mode.Leisure && next == Mode.Work && ht.plateaued
                    && happyBelow > 0f && happiness < happyUntil)
                    ht.cooldown = retrySeconds;

                ScheduleType st = ToSchedule(next);
                bool applied = ApplyScheduleIfNeeded(surv, villager, st, i);

                // MSdiag (d): detect the player editing/applying a schedule in the UI mid-session. Runs
                // every tick (independent of the debug/summary cadence) so a change is caught promptly,
                // but logs each distinct foreign value only once.
                if (msDiag) LogForeignScheduleChange(surv, villager, i);

                // While taking leisure, actively pump happiness up so the time isn't wasted.
                if (next == Mode.Leisure && boostPerSec > 0f)
                    BoostAttr(villager._happinessVAttr, boostPerSec * dt);

                // While sleeping, speed rest recovery so each sleep is shorter (less total sleep).
                if (next == Mode.Sleep && restBoostPerSec > 0f)
                    BoostAttr(rest, restBoostPerSec * dt);

                // When the game is warming this villager (warmth rising = near a heat source), add extra
                // on top so the warm-up finishes faster. Mode-independent; only ever speeds up warming.
                if (warmthMult > 1f)
                {
                    var wv = surv._warmthVAttr;
                    if (wv != null)
                    {
                        float curW = wv.GetNormalizedValue();
                        if (_lastWarmth.TryGetValue(surv, out var prevW) && curW > prevW)
                            BoostAttr(wv, (curW - prevW) * (warmthMult - 1f));
                        _lastWarmth[surv] = wv.GetNormalizedValue();
                    }
                }

                // Scale how fast food/water drain. Mode-independent; only touched when the multiplier isn't 1.
                if (hungerMult != 1f) ScaleDrain(surv._foodVAttr, hungerMult, _lastFood, surv);
                if (thirstMult != 1f) ScaleDrain(surv._waterVAttr, thirstMult, _lastWater, surv);

                if (debug && (next != prev || applied))
                    Plugin.Logger.LogInfo(
                        $"[DynamicNeeds] villager[{i}] {prev} -> {next} (rest={restHours:F1}/24) | {Happy(villager, ht)} | {Needs(surv)} | {Diag(villager, surv)}");

                if (summaryNow && i == 0 && debug)
                    Plugin.Logger.LogInfo(
                        $"[DynamicNeeds] villager[0] mode={next} rest={restHours:F1}/24 | {Happy(villager, ht)} | {Needs(surv)} | {Diag(villager, surv)}");

                if (summaryNow && i == 0 && msDiag)
                    LogHourCalibration(surv, villager);
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError($"[DynamicNeeds] controller: {ex}");
                tracked.RemoveAt(i);
            }
        }

        // MSdiag (c): same-workstation cohort report, every 10 real seconds.
        if (cohortNow) LogCohorts(tracked);
    }

    // Enabled=false + ManualScheduleDiagnostics=true observe-only tick: same 4 Hz cadence and the same
    // prune/CanControl/GetVillager guards as the control loop above, but performs ZERO writes — no
    // ApplyScheduleIfNeeded, no BoostAttr/ScaleDrain, no RecheckConsumeNeeds, no Rpc calls of any kind.
    // Advances the same _summaryTimer/_cohortTimer accumulators so LogHourCalibration/LogCohorts still
    // fire on their usual cadence while the control loop is off.
    private void ObserveTick(List<VillagerSurvival> tracked, float dt)
    {
        _summaryTimer += dt;
        bool summaryNow = _summaryTimer >= 5f;
        if (summaryNow) _summaryTimer = 0f;

        _cohortTimer += dt;
        bool cohortNow = _cohortTimer >= 10f;
        if (cohortNow) _cohortTimer = 0f;

        // Player UI schedule edits do NOT reliably pulse __NetworkedSchedule (confirmed in-game
        // 2026-07-09: painting staggered cook schedules produced zero packed changes), so the
        // array-content diff in ObserveScheduleArrayAndProbe below is the only reliable edit
        // detector. TimeOfDay is read once per 5 s summary for the mixed-schedule behavior probes.
        float timeOfDay = -1f;
        if (summaryNow)
        {
            try
            {
                var w = WeatherSystem.Instance;
                if (w != null) timeOfDay = w.TimeOfDay;
            }
            catch { }
        }

        for (int i = tracked.Count - 1; i >= 0; i--)
        {
            var surv = tracked[i];
            if (surv == null) { tracked.RemoveAt(i); continue; }

            try
            {
                if (!CanControl(surv)) continue;

                var villager = surv.GetVillager();
                if (villager == null) continue;

                LogObservedSchedule(surv, villager, i);

                if (summaryNow)
                    ObserveScheduleArrayAndProbe(surv, villager, i, timeOfDay);

                if (summaryNow && i == 0)
                    LogHourCalibration(surv, villager);
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError($"[DynamicNeeds] MSdiag observe: {ex}");
                tracked.RemoveAt(i);
            }
        }

        if (cohortNow) LogCohorts(tracked);
    }

    // Read-only: watches the player's live schedule while the control loop is off. On first sight of a
    // villager, or whenever the live packed schedule changes, re-reads villager._schedule fresh into
    // _scheduleHours[surv] (the same array the hour-calibration and cohort reports read from) — this is
    // what lets those reports reflect the player's REAL painted stagger instead of a stale/absent snapshot.
    private void LogObservedSchedule(VillagerSurvival surv, Villager villager, int idx)
    {
        try
        {
            long live = villager.__NetworkedSchedule;
            bool changed = !_observedPacked.TryGetValue(surv, out var last) || last != live;
            if (!changed) return;

            try
            {
                var liveArr = villager._schedule;
                if (liveArr != null)
                {
                    var hoursArr = new int[liveArr.Length];
                    for (int h = 0; h < liveArr.Length; h++) hoursArr[h] = liveArr[h];
                    _scheduleHours[surv] = hoursArr;

                    Plugin.Logger.LogInfo(
                        $"[DynamicNeeds] MSdiag observe villager[{idx}] schedule={HoursString(hoursArr)} packed=0x{live:X}");
                }
            }
            catch (Exception ex) { Plugin.Logger.LogError($"[DynamicNeeds] MSdiag observe schedule: {ex}"); }

            _observedPacked[surv] = live;
        }
        catch (Exception ex) { Plugin.Logger.LogError($"[DynamicNeeds] MSdiag observe: {ex}"); }
    }

    // Observe-mode, every 5 s per villager: (1) diff the per-hour _schedule ARRAY against the stored
    // snapshot — player UI edits don't reliably change __NetworkedSchedule (confirmed in-game
    // 2026-07-09), so packed-diff alone misses them; (2) for any villager whose schedule is MIXED
    // (a painted stagger), log a behavior probe line so slot<->clock-hour alignment can be read off
    // the log (does behavior flip when hourTod crosses a painted boundary?).
    private void ObserveScheduleArrayAndProbe(VillagerSurvival surv, Villager villager, int idx, float timeOfDay)
    {
        try
        {
            var liveArr = villager._schedule;
            if (liveArr == null) return;

            _scheduleHours.TryGetValue(surv, out var stored);
            bool changed = stored == null || stored.Length != liveArr.Length;
            if (!changed)
            {
                for (int h = 0; h < stored!.Length; h++)
                    if (stored[h] != liveArr[h]) { changed = true; break; }
            }

            var arr = stored;
            if (changed)
            {
                arr = new int[liveArr.Length];
                for (int h = 0; h < liveArr.Length; h++) arr[h] = liveArr[h];
                _scheduleHours[surv] = arr;
                Plugin.Logger.LogInfo(
                    $"[DynamicNeeds] MSdiag observe villager[{idx}] schedule={HoursString(arr)} packed=0x{villager.__NetworkedSchedule:X} (array change)");
            }
            if (arr == null) return;

            bool mixed = false;
            for (int h = 1; h < arr.Length; h++)
                if (arr[h] != arr[0]) { mixed = true; break; }
            if (!mixed) return;

            int hourTod = timeOfDay >= 0f ? Mathf.Clamp(Mathf.FloorToInt(timeOfDay), 0, arr.Length - 1) : -1;
            char slot = hourTod >= 0 ? HourChar(arr[hourTod]) : '-';
            Plugin.Logger.LogInfo(
                $"[DynamicNeeds] MSdiag mixed villager[{idx}] timeOfDay={timeOfDay:F3} hourTod={hourTod} slot={slot} " +
                $"behavior={villager.CurrentBehaviorType} sleeping={surv.IsSleeping}");
        }
        catch (Exception ex) { Plugin.Logger.LogError($"[DynamicNeeds] MSdiag mixed: {ex}"); }
    }

    private static Mode Decide(Mode prev, float restHours, bool isSleeping, float minNeed, bool critical, float happiness,
        float sleepBelow, float wakeAbove, float needBelow, float needAbove,
        float happyBelow, float happyUntil, bool happyPlateaued, bool happyAllowed)
    {
        // Sleep has top priority. Keep sleeping until rested (hysteresis); start when tired; OR adopt a
        // sleep the GAME forced on us (isSleeping while not yet rested). The game puts villagers to bed at
        // nightfall regardless of our all-Work schedule — adopting it lets the rest boost shorten that
        // sleep and lets us wake them at the threshold, so a low SleepWhenRestBelowHours can't lose the
        // "race" against the game's night sleep. Wake (schedule Sleep->Work) then ends the adopted sleep.
        if (prev == Mode.Sleep)
        {
            if (restHours < wakeAbove) return Mode.Sleep;
        }
        else if (restHours <= sleepBelow || (isSleeping && restHours < wakeAbove))
        {
            return Mode.Sleep;
        }

        bool needsEnabled = needBelow > 0f;
        bool happyEnabled = happyBelow > 0f;

        if (prev == Mode.Leisure)
        {
            // Stay in leisure while a basic need is still recovering, or while happiness is still
            // climbing toward its target. A capped villager (plateaued) stops holding for happiness so
            // they go back to work instead of relaxing forever.
            bool holdNeed = needsEnabled && minNeed < needAbove;
            bool holdHappy = happyEnabled && happiness < happyUntil && !happyPlateaued;
            if (holdNeed || holdHappy) return Mode.Leisure;
        }
        else
        {
            bool startNeed = needsEnabled && (critical || minNeed <= needBelow);
            bool startHappy = happyEnabled && happyAllowed && happiness <= happyBelow;
            if (startNeed || startHappy) return Mode.Leisure;
        }

        return Mode.Work;
    }

    // Add `addNormalized` (0..1 of the attribute's full range) to a writable VariableAttribute, clamped
    // to its max. Used for the happiness boost (Villager._happinessVAttr) and the sleep rest boost
    // (VillagerSurvival._restVariableAttribute). For happiness the game re-clamps to HappinessCap each
    // tick, which is exactly how a capped villager surfaces as a plateau.
    private static void BoostAttr(VariableAttribute va, float addNormalized)
    {
        try
        {
            if (va == null) return;
            float range = va.max - va.min;
            if (range <= 0f) return;
            // Clamp both ends: most callers add (clamp to max matters); ScaleDrain can subtract (clamp to min).
            va.SetValue(Mathf.Clamp(va.GetValue() + addNormalized * range, va.min, va.max));
        }
        catch { }
    }

    // Scale how fast a draining need (food/water) falls, without needing to know the game's base drain rate.
    // Mirrors the warmth multiplier but acts on DROPS: we observe the meter's per-frame change and add
    // (mult-1)x of any decrease, so the net drain ends up mult x vanilla. mult>1 = gets hungry/thirsty faster
    // (handy for testing the food re-check), <1 = slower, =1 = untouched (guarded by the caller). A rise
    // (the villager just ate/drank) is never scaled, so this only ever tunes the natural drain.
    private void ScaleDrain(VariableAttribute va, float mult, Dictionary<VillagerSurvival, float> last, VillagerSurvival surv)
    {
        try
        {
            if (va == null) return;
            float cur = va.GetNormalizedValue();
            if (last.TryGetValue(surv, out var prev) && cur < prev)
                BoostAttr(va, (cur - prev) * (mult - 1f));
            last[surv] = va.GetNormalizedValue();
        }
        catch { }
    }

    // Vanilla dispatches a villager's "go eat/drink" survival behavior ONCE when a need goes low. If the
    // food/water store is empty when they arrive, FSM_SurvivalConsume returns ERROR_NO_ITEM and the villager
    // parks there — it does NOT re-attempt when you restock the store, so you have to hand-feed to un-stick
    // them. Here we periodically re-arm the game's own survival-replenish quests via SetDirty() (the same
    // re-evaluation primitive the AI's FSM_QuestSetDirty state uses), but only while a need is actually low.
    // This keeps the vanilla eat path live, so the villager fetches and consumes the REAL food (decrementing
    // storage) as soon as it's available again, then returns to their routine — no hand-feeding needed.
    private static void RecheckConsumeNeeds(VillagerSurvival surv, float food, float water, float below, bool debug, int idx)
    {
        try
        {
            if (food < below || surv.IsStarving)
            {
                var q = surv.GetReplenishFoodQuest();
                if (q != null)
                {
                    q.SetDirty();
                    if (debug) Plugin.Logger.LogInfo($"[DynamicNeeds] villager[{idx}] food re-check (food={food:F2}) -> SetDirty");
                }
            }
            if (water < below || surv.IsDehydrated)
            {
                var q = surv.GetReplenishWaterQuest();
                if (q != null)
                {
                    q.SetDirty();
                    if (debug) Plugin.Logger.LogInfo($"[DynamicNeeds] villager[{idx}] water re-check (water={water:F2}) -> SetDirty");
                }
            }
        }
        catch (Exception ex) { Plugin.Logger.LogError($"[DynamicNeeds] recheck: {ex}"); }
    }

    private static ScheduleType ToSchedule(Mode mode) => mode switch
    {
        Mode.Sleep => ScheduleType.Sleep,
        Mode.Leisure => ScheduleType.Leisure,
        _ => ScheduleType.Work,
    };

    // Rewrite the villager's whole schedule to a single activity via the game's own networked path.
    // Idempotent: only fires when our chosen activity actually changed. Returns true if it applied.
    private bool ApplyScheduleIfNeeded(VillagerSurvival surv, Villager villager, ScheduleType st, int idx)
    {
        if (_applied.TryGetValue(surv, out var cur) && cur == st)
            return false;

        // Snapshot the original packed schedule once, before we ever touch it.
        if (!_originalSchedule.ContainsKey(surv))
        {
            // Known (2026-07-09): __NetworkedSchedule reads 0x0 at rest/load — this captures a packed
            // transient, not the painted schedule. Writing 0x0 back at shutdown is harmless (it IS the
            // at-rest value), but Phase 1's Manual mode must work from _scheduleHours, not this.
            _originalSchedule[surv] = villager.__NetworkedSchedule;

            // MSdiag (a): also snapshot the per-hour schedule array at this same "first touch" moment.
            // Captured UNCONDITIONALLY (Phase 1 will need it); only the log line is gated.
            try
            {
                var existing = villager._schedule;
                if (existing != null)
                {
                    var hoursArr = new int[existing.Length];
                    for (int h = 0; h < existing.Length; h++) hoursArr[h] = existing[h];
                    _scheduleHours[surv] = hoursArr;

                    if (Plugin.ManualScheduleDiagnostics.Value)
                        Plugin.Logger.LogInfo(
                            $"[DynamicNeeds] MSdiag snapshot villager[{idx}] hours={HoursString(hoursArr)} packed=0x{villager.__NetworkedSchedule:X}");
                }
            }
            catch (Exception ex) { Plugin.Logger.LogError($"[DynamicNeeds] MSdiag snapshot: {ex}"); }
        }

        // Build an all-`st` schedule array of the villager's hour count and pack it the way the game
        // does, then apply through the network-safe RPC. Leave overrideSchedule off so the vanilla
        // schedule pipeline (which dispatches tasks) is fully in control.
        int hours = Villager.scheduleMaxHourCount;
        if (hours <= 0)
        {
            var existing = villager._schedule;
            hours = existing != null ? existing.Length : 24;
        }

        // The schedule array is packed as ints (the ScheduleType underlying values), not the enum type.
        int stInt = (int)st;
        var arr = new Il2CppStructArray<int>(hours);
        for (int h = 0; h < hours; h++) arr[h] = stInt;

        long packed = villager._ScheduleToNetworkSchedule(arr);
        villager.overrideSchedule = false;
        villager.Rpc_ChangeSchedule(packed);

        _applied[surv] = st;
        _appliedPacked[surv] = packed;
        return true;
    }

    // Put every villager's original schedule back. Best-effort on shutdown so we don't leave a
    // collapsed needs-schedule baked into the save.
    private void RestoreAll()
    {
        foreach (var kv in _originalSchedule)
        {
            var surv = kv.Key;
            if (surv == null) continue;
            try
            {
                var villager = surv.GetVillager();
                if (villager == null) continue;
                villager.overrideSchedule = false;
                villager.Rpc_ChangeSchedule(kv.Value);
            }
            catch { /* shutting down — best effort */ }
        }
    }

    void OnDestroy() => RestoreAll();
    void OnApplicationQuit() => RestoreAll();

    // Happiness readout for tuning: NormalizedHappiness, the raw happiness VAttr value/max (so we can see
    // our boost actually moving it), the HappinessCap (why a villager plateaus), plus our plateau/cooldown
    // state. If hv climbs while in leisure, the direct-write boost is working.
    private static string Happy(Villager villager, HappyTrack ht)
    {
        try
        {
            var hv = villager._happinessVAttr;
            string pl = ht.plateaued ? " PLATEAU" : "";
            string cd = ht.cooldown > 0f ? $" cd={ht.cooldown:F0}s" : "";
            if (hv != null)
                return $"hap={villager.NormalizedHappiness:F2} hv={hv.GetValue():F1}/{hv.max:F1} cap={villager.HappinessCap:F2}{pl}{cd}";
        }
        catch { }
        return "hap=?";
    }

    // Read-only basic-need readout (hunger/thirst/cold), via CharacterSurvival's normalized getters
    // (0..1, lower = more urgent). These now drive the basic-need leisure trigger. The flags are the
    // game's own danger states (already starving/dehydrated/freezing).
    private static string Needs(VillagerSurvival surv)
    {
        string food = "?", water = "?", warmth = "?";
        try { food = surv.GetNormalizedFood().ToString("F2"); } catch { }
        try { water = surv.GetNormalizedWater().ToString("F2"); } catch { }
        try { warmth = surv.GetNormalizedWarmth().ToString("F2"); } catch { }
        string flags = "";
        try
        {
            if (surv.IsStarving) flags += "STARVING ";
            if (surv.IsDehydrated) flags += "DEHYDRATED ";
            if (surv.IsFreezing) flags += "FREEZING ";
            if (surv.IsSuffering) flags += "SUFFERING ";
        }
        catch { }
        return $"food={food} water={water} warmth={warmth}" +
               (flags.Length > 0 ? $" [{flags.TrimEnd()}]" : "");
    }

    // Read-back diagnostics: what did the game actually do with our schedule this tick? behavior/task
    // tell us whether the vanilla pipeline picked it up and dispatched (task=active) or is idling.
    // sched=match/REVERTED compares the live packed schedule to what we last wrote — REVERTED means the
    // game overwrote our schedule (which would explain villagers still sleeping the night on all-Work).
    private string Diag(Villager villager, VillagerSurvival surv)
    {
        string ws = "?", task = "?", dn = "?", night = "?", sched = "?";
        try { ws = villager.GetNonVikingWorkstation() != null ? "yes" : "none"; } catch { }
        // GetActiveTask() reads null even while villagers visibly work, so log both it and the
        // _runningTask field (act/run) until we know which actually reflects "is working".
        try
        {
            var tr = villager.GetTaskRunner();
            if (tr != null)
                task = $"{(tr.GetActiveTask() != null ? "act" : "-")}/{(tr._runningTask != null ? "run" : "-")}";
        }
        catch { }
        try
        {
            if (_appliedPacked.TryGetValue(surv, out var ap))
                sched = villager.__NetworkedSchedule == ap ? "match" : "REVERTED";
        }
        catch { }
        try { var w = WeatherSystem.Instance; if (w != null) { dn = w.DayNightValue.ToString("F2"); night = w.IsNight.ToString(); } } catch { }
        return $"override={villager.overrideSchedule} behavior={villager.CurrentBehaviorType} " +
               $"sleeping={surv.IsSleeping} workstation={ws} task={task} sched={sched} night={night} dayNight={dn}";
    }

    // --- RespectManualSchedule Phase 0 diagnostics (ManualScheduleDiagnostics gate) ---
    // Read-only: none of these write anything except their own bookkeeping dictionaries used to
    // de-duplicate log lines. No behavior change to villager control.

    private static char HourChar(int scheduleTypeInt) => scheduleTypeInt == (int)ScheduleType.Work ? 'W'
        : scheduleTypeInt == (int)ScheduleType.Sleep ? 'S'
        : scheduleTypeInt == (int)ScheduleType.Leisure ? 'L'
        : '?';

    private static string HoursString(int[] hours)
    {
        var sb = new StringBuilder(hours.Length);
        foreach (var h in hours) sb.Append(HourChar(h));
        return sb.ToString();
    }

    // (b) In-game hour calibration: piggybacks the 5s summary cadence (i == 0 only). Confirms which
    // schedule-array index the game currently reads as "now" (DayNightValue -> hourIndex), so later
    // phases can trust hour math when deciding what to write per-hour.
    private void LogHourCalibration(VillagerSurvival surv, Villager villager)
    {
        try
        {
            int hourCount = Villager.scheduleMaxHourCount;
            if (hourCount <= 0)
            {
                var existing = villager._schedule;
                hourCount = existing != null ? existing.Length : 24;
            }
            if (hourCount <= 0) hourCount = 24;

            float dayNight = 0f;
            bool isNight = false;
            float timeOfDay = -1f;
            try
            {
                var w = WeatherSystem.Instance;
                if (w != null) { dayNight = w.DayNightValue; isNight = w.IsNight; timeOfDay = w.TimeOfDay; }
            }
            catch { }
            // DayNightValue saturates at exactly 0/1 for long stretches (it's a lighting blend, not a
            // clock — confirmed in-game 2026-07-09), so this hourIndex pins at 0/23. TimeOfDay is the
            // linear 0..24 in-game clock; hourTod is the candidate replacement being calibrated.
            int hourIndex = Mathf.Clamp(Mathf.FloorToInt(dayNight * hourCount), 0, hourCount - 1);
            int hourTod = timeOfDay >= 0f ? Mathf.Clamp(Mathf.FloorToInt(timeOfDay), 0, hourCount - 1) : -1;

            char snap = '-', snapTod = '-';
            if (_scheduleHours.TryGetValue(surv, out var snapArr))
            {
                if (hourIndex >= 0 && hourIndex < snapArr.Length) snap = HourChar(snapArr[hourIndex]);
                if (hourTod >= 0 && hourTod < snapArr.Length) snapTod = HourChar(snapArr[hourTod]);
            }

            char live = '-', liveTod = '-';
            try
            {
                var liveArr = villager._schedule;
                if (liveArr != null)
                {
                    if (hourIndex >= 0 && hourIndex < liveArr.Length) live = HourChar(liveArr[hourIndex]);
                    if (hourTod >= 0 && hourTod < liveArr.Length) liveTod = HourChar(liveArr[hourTod]);
                }
            }
            catch { }

            Plugin.Logger.LogInfo(
                $"[DynamicNeeds] MSdiag hour dayNight={dayNight:F3} count={hourCount} hourIndex={hourIndex} " +
                $"snap={snap} live={live} timeOfDay={timeOfDay:F3} hourTod={hourTod} snapTod={snapTod} liveTod={liveTod} " +
                $"behavior={villager.CurrentBehaviorType} sleeping={surv.IsSleeping} night={isNight}");
        }
        catch (Exception ex) { Plugin.Logger.LogError($"[DynamicNeeds] MSdiag hour: {ex}"); }
    }

    // (c) Same-workstation cohort report, every 10 real seconds. Groups currently-controlled villagers
    // by workstation identity (native pointer — see the "managed casts lie" / Pointer-boxing gotcha) and,
    // for any group of 2+, reports which hours ALL members' original schedules have them asleep — the
    // future manual-schedule mode will need this to confine off-window sleep to same-station cohorts.
    // Builds everything fresh each report; no Workstation wrapper is stored past this method.
    private void LogCohorts(List<VillagerSurvival> tracked)
    {
        try
        {
            var groups = new Dictionary<IntPtr, List<int>>();
            var names = new Dictionary<IntPtr, string>();

            for (int i = 0; i < tracked.Count; i++)
            {
                var surv = tracked[i];
                if (surv == null) continue;
                try
                {
                    if (!CanControl(surv)) continue;
                    var villager = surv.GetVillager();
                    if (villager == null) continue;

                    var ws = villager.GetNonVikingWorkstation();
                    if (ws == null) continue;
                    // SSSGame.Workstation's compile-time base chain runs through unity-libs stubs, so
                    // ws.Pointer won't compile directly — box through object + Il2CppObjectBase instead
                    // (same pattern as the universal "managed casts lie" gotcha).
                    if (!((object)ws is Il2CppObjectBase wsBase)) continue;

                    IntPtr key = wsBase.Pointer;
                    if (!groups.TryGetValue(key, out var list))
                    {
                        list = new List<int>();
                        groups[key] = list;
                        try { names[key] = ws._structure?.GetName() ?? "?"; } catch { names[key] = "?"; }
                    }
                    list.Add(i);
                }
                catch (Exception ex) { Plugin.Logger.LogError($"[DynamicNeeds] MSdiag cohort member: {ex}"); }
            }

            foreach (var kv in groups)
            {
                if (kv.Value.Count < 2) continue;

                var snaps = new List<int[]>();
                foreach (var idx in kv.Value)
                {
                    var surv = tracked[idx];
                    if (surv != null && _scheduleHours.TryGetValue(surv, out var arr)) snaps.Add(arr);
                }

                string overlap;
                if (snaps.Count < 2)
                {
                    overlap = "?";
                }
                else
                {
                    int hourCount = snaps[0].Length;
                    foreach (var arr in snaps) hourCount = Mathf.Min(hourCount, arr.Length);

                    var overlapHours = new List<int>();
                    for (int h = 0; h < hourCount; h++)
                    {
                        bool allSleep = true;
                        foreach (var arr in snaps)
                        {
                            if (arr[h] != (int)ScheduleType.Sleep) { allSleep = false; break; }
                        }
                        if (allSleep) overlapHours.Add(h);
                    }
                    overlap = "[" + string.Join(",", overlapHours) + "]";
                }

                string members = string.Join(",", kv.Value);
                string name = names.TryGetValue(kv.Key, out var n) ? n : "?";
                Plugin.Logger.LogInfo(
                    $"[DynamicNeeds] MSdiag cohort station=0x{kv.Key.ToInt64():X} name='{name}' members=[{members}] sleepOverlapHours={overlap}");
            }
        }
        catch (Exception ex) { Plugin.Logger.LogError($"[DynamicNeeds] MSdiag cohort: {ex}"); }
    }

    // (d) Player-edit detection: if the live packed schedule no longer matches what we last wrote (and
    // we haven't already logged this exact foreign value), the player changed+applied a schedule in the
    // UI mid-session. Re-reads villager._schedule fresh so we can confirm it reflects the NEW hours, AND
    // (deliberate small behavior change, v1.3.1): moves the shutdown restore target (_originalSchedule)
    // to this latest edit and refreshes _scheduleHours to match, so RestoreAll on shutdown puts back the
    // player's LATEST painted schedule instead of the stale one captured at session start.
    private void LogForeignScheduleChange(VillagerSurvival surv, Villager villager, int idx)
    {
        try
        {
            if (!_appliedPacked.TryGetValue(surv, out var applied)) return;

            long live = villager.__NetworkedSchedule;
            if (live == applied) return;
            if (_lastLoggedForeignPacked.TryGetValue(surv, out var lastLogged) && lastLogged == live) return;

            // __NetworkedSchedule is a transient change-transport field, not an at-rest schedule
            // mirror: it reads 0x0 at world load, and the game resets it to 0x0 mid-session (seen for
            // all 45 tracked villagers at once, 2026-07-09). A reset is NOT a player edit — keep the
            // snapshot and the restore target. Real edits arrive as a NONZERO packed value.
            if (live == 0)
            {
                Plugin.Logger.LogInfo(
                    $"[DynamicNeeds] MSdiag packed reset villager[{idx}]: applied=0x{applied:X} live=0x0 (native reset — ignored, snapshot kept)");
                _lastLoggedForeignPacked[surv] = live;
                return;
            }

            // Restore target update — independent of whether the per-hour array read below succeeds.
            _originalSchedule[surv] = live;

            string liveHours = "-";
            try
            {
                var liveArr = villager._schedule;
                if (liveArr != null)
                {
                    var tmp = new int[liveArr.Length];
                    for (int h = 0; h < liveArr.Length; h++) tmp[h] = liveArr[h];
                    liveHours = HoursString(tmp);
                    _scheduleHours[surv] = tmp;
                }
            }
            catch { }

            Plugin.Logger.LogInfo(
                $"[DynamicNeeds] MSdiag schedule externally changed villager[{idx}]: applied=0x{applied:X} live=0x{live:X} " +
                $"_schedule={liveHours} (snapshot refreshed, restore target updated)");
            _lastLoggedForeignPacked[surv] = live;
        }
        catch (Exception ex) { Plugin.Logger.LogError($"[DynamicNeeds] MSdiag foreign: {ex}"); }
    }

    // Only the simulation authority (host in co-op, or the local player solo) should drive villager
    // behavior; doing it on a non-authority client would fight network sync.
    private static bool CanControl(VillagerSurvival surv)
    {
        try { return surv._hasAuthority; }
        catch { return true; }
    }
}
