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
//
// RespectManualSchedule (v1.4.0): opt-in mode for player-staggered coverage. Villagers in a 2+
// same-workstation cohort whose painted schedule has off-hours follow THEIR OWN schedule instead of
// the all-Work baseline: present during painted Work hours (only a life-threatening food/water
// emergency pulls them off-post), sleep confined to their own painted off-hours (front-loaded,
// rest-boosted), leftover off-hours filled with leisure (never over-manning the post). While a
// villager holds the Manual baseline the mod writes nothing — the game natively executes painted
// schedules (confirmed in-game 2026-07-09) and the schedule UI keeps showing the player's real hours.
// Solo-station villagers and degenerate all-Work paints keep pure needs-based behavior.
public class NeedsController : MonoBehaviour
{
    // Manual = RespectManualSchedule baseline: follow the villager's own painted schedule
    // (write-silent while held — see ApplyManualIfNeeded).
    private enum Mode { Work, Sleep, Leisure, Manual }

    // Plateau window: how long happiness must fail to rise (relative to what our boost is pumping in)
    // before we treat the villager as happiness-capped and stop holding them in leisure.
    private const float PlateauWindowSeconds = 4f;
    // Fraction of the boost-expected rise below which we call it a plateau (the cap is clamping our writes).
    private const float PlateauRiseFraction = 0.25f;
    // After a capped exit, in-game hours to wait before happiness-leisure may trigger again.
    private const float HappyRetryHours = 4f;

    // Manual mode: safety buffer added to the estimated top-up sleep duration when deciding how close
    // to the on-window the sleep should start (covers the walk to bed + estimate error).
    private const float TopUpLeadBufferHours = 1.5f;

    // Manual mode: Sleep re-ENTRY requires rest below (wake threshold - this margin); only a sleep
    // already in progress holds to the full threshold. Without it the off-window logic flaps
    // Sleep<->Leisure at the threshold — wake at 23.0, leisure drains to 22.9, re-sleep, repeat —
    // yanking the villager in and out of bed all night (observed in-game 2026-07-09, v1.4.1).
    private const float ResleepMarginHours = 2f;

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
    // --- RespectManualSchedule Phase 1 state ---
    // Villagers currently holding the Manual baseline (their own painted schedule is live; staying in
    // it is write-free). A villager leaves this set whenever a collapse is applied to it.
    private readonly HashSet<VillagerSurvival> _appliedManual = new();
    // Members of a 2+ same-workstation cohort (recomputed every 10 s by RefreshCohorts) — the only
    // villagers manual-mode window gating applies to.
    private readonly HashSet<VillagerSurvival> _inCohort = new();
    // Villagers we have actually written a schedule to — the only ones RestoreAll must touch.
    private readonly HashSet<VillagerSurvival> _everWrote = new();
    // Manual mode: villagers whose CURRENT off-window already got its one top-up sleep. Armed
    // (removed) while the villager's painted hour is Work; spent (added) when an off-window sleep
    // exits rested. Without this, leisure drains rest back under the re-entry bar every ~2 game-hours
    // and the villager naps on a limit cycle all night — with the speed-scaled boost finishing each
    // nap before they even reach a bed (observed in-game 2026-07-09, v1.4.2).
    private readonly HashSet<VillagerSurvival> _toppedUp = new();
    // Sleep-overlap warnings already issued (station name+members -> overlap string) so the manual-mode
    // coverage warning fires on change, not on every 10 s report.
    private readonly Dictionary<string, string> _overlapWarned = new();
    private bool _cohortsComputed;

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
        bool manualMode = Plugin.RespectManualSchedule.Value;
        float criticalBelow = Plugin.CriticalNeedOffPostBelow.Value;

        // In-game hour length (seconds) drives the boost rates and the retry cooldown; TimeOfDay is the
        // linear 0..24 wall clock and schedule slot k == clock hour k (both confirmed in-game
        // 2026-07-09). WeatherSystem.Instance is read fresh each tick — never cache per-world singletons.
        float hourSeconds = 0f;
        int hourNow = -1;
        float timeSpeed = 1f;
        try
        {
            var w = WeatherSystem.Instance;
            if (w != null)
            {
                hourSeconds = w.dayLength / 24f;
                float tod = w.TimeOfDay;
                if (tod >= 0f) hourNow = Mathf.Clamp(Mathf.FloorToInt(tod), 0, 23);
                // Config durations are IN-GAME hours. Under a time accelerator (e.g. TimeWarp) game
                // hours pass faster than real seconds — scale by the game's own speed multiplier
                // (1 = vanilla) so boosts stay proportional to game time at any speed. Without this,
                // 10x acceleration made the rest boost 10x weaker per game-hour (2026-07-09 test:
                // a top-up sleep overran its off-window into the shift).
                float sm = w.TimeSpeedMultiplier;
                if (sm > 0f) timeSpeed = sm;
            }
        }
        catch { }
        bool boostOn = hoursToFull > 0f && hourSeconds > 0f;
        // Boost rates are in normalized (0..1 of full) per REAL second; BoostAttr scales by the attr's range.
        float boostPerSec = boostOn ? timeSpeed / (hoursToFull * hourSeconds) : 0f;
        float restBoostPerSec = restHoursToFull > 0f && hourSeconds > 0f ? timeSpeed / (restHoursToFull * hourSeconds) : 0f;
        float retrySeconds = hourSeconds > 0f ? HappyRetryHours * hourSeconds / timeSpeed : 120f;
        float warmthMult = Plugin.FireWarmthMultiplier.Value;
        float hungerMult = Plugin.HungerRateMultiplier.Value;
        float thirstMult = Plugin.ThirstRateMultiplier.Value;

        _summaryTimer += dt;
        bool fiveSec = _summaryTimer >= 5f;
        if (fiveSec) _summaryTimer = 0f;
        bool summaryNow = fiveSec && (debug || msDiag);

        // MSdiag cohort report / manual-mode cohort membership cadence (independent 10s accumulator).
        // Manual mode needs membership from the very FIRST tick (before the first 10 s elapses) so
        // painted schedules aren't collapsed during warm-up.
        _cohortTimer += dt;
        bool cohortTick = _cohortTimer >= 10f;
        if (cohortTick) _cohortTimer = 0f;
        if (manualMode && !_cohortsComputed) cohortTick = true;
        if (cohortTick && (msDiag || manualMode)) RefreshCohorts(tracked, msDiag);

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

                // Manual mode: capture the painted per-hour schedule BEFORE any possible write this
                // tick (first sight), and keep it fresh against player UI edits (5 s cadence).
                if (manualMode) EnsureSnapshot(surv, villager, i);
                if (manualMode && fiveSec) RefreshPaintedSnapshot(surv, villager, i, msDiag);

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

                // Manual-schedule gating applies only to members of a 2+ same-station cohort whose
                // painted schedule is a REAL mixed paint: at least one Work hour AND one off-hour.
                // All-Work paints would forbid discretionary sleep forever; uniform all-Sleep/all-
                // Leisure schedules are legacy junk from old collapse writes baked into saves, not
                // player intent — under them a villager cycles sleep/leisure forever and never works
                // (observed in-game 2026-07-09, v1.4.2: three non-cook villagers captured). Both
                // degenerate shapes fall back to pure needs.
                bool manualQualifies = false;
                int paintedNow = -1;
                // Hoisted (not `out var` inline) so it's definitely assigned at Edit 2's use point below:
                // that use sits behind `if (manualQualifies)`, a DIFFERENT bool than this one's condition,
                // and the compiler can't correlate the two — CS0165 otherwise, even though at runtime
                // manualQualifies is only ever true on the path where painted was actually populated.
                int[] painted = null!;
                if (manualMode && hourNow >= 0 && _inCohort.Contains(surv)
                    && _scheduleHours.TryGetValue(surv, out painted) && painted.Length > 0)
                {
                    paintedNow = painted[Mathf.Min(hourNow, painted.Length - 1)];
                    bool hasWork = false, hasOff = false;
                    for (int h = 0; h < painted.Length; h++)
                    {
                        if (painted[h] == (int)ScheduleType.Work) hasWork = true; else hasOff = true;
                        if (hasWork && hasOff) { manualQualifies = true; break; }
                    }
                }

                Mode next;
                bool manualProbe = false;
                float hoursUntilOn = -1f;
                if (manualQualifies)
                {
                    manualProbe = true;
                    bool onWindow = paintedNow == (int)ScheduleType.Work;
                    // Arm the once-per-off-window top-up while on shift; it is spent below by one
                    // completed (rested) off-window sleep.
                    if (onWindow) _toppedUp.Remove(surv);
                    bool emergency = critical || minNeed <= criticalBelow;
                    float resleepBelow = Mathf.Max(wakeAbove - ResleepMarginHours, 0f);

                    // BACK-loaded top-up: leisure first, sleep timed to END at the shift start. Sleep
                    // placement within one's own off-window is coverage-neutral (cohort off-windows are
                    // disjoint by design), and sleeping early wastes the top-up — leisure drains rest
                    // ~1h per game-hour, so a front-loaded sleep left a 12h-shift cook starting at
                    // rest 11.4 and ending at 0.4 (observed in-game 2026-07-09, v1.4.3). The lead
                    // estimate uses the boost's own refill rate (SleepHoursToFullRest; 0 = vanilla
                    // ≈ 1:1) plus a walk/error buffer. A genuinely exhausted villager (rest at or
                    // under the pure-needs sleep trigger) still sleeps immediately.
                    hoursUntilOn = HoursUntilNextWork(painted, hourNow);
                    float refillHours = restHoursToFull > 0f ? restHoursToFull : 24f;
                    float sleepLeadHours = (wakeAbove - restHours) * refillHours / 24f + TopUpLeadBufferHours;
                    bool topUpNow = !_toppedUp.Contains(surv) && restHours < resleepBelow
                        && (hoursUntilOn <= sleepLeadHours || restHours <= sleepBelow);

                    next = DecideManual(prev, restHours, surv.IsSleeping, minNeed, emergency,
                        needAbove, wakeAbove, resleepBelow, onWindow, topUpNow);
                    if (!onWindow && prev == Mode.Sleep && next != Mode.Sleep && restHours >= wakeAbove)
                        _toppedUp.Add(surv);
                }
                else
                {
                    next = Decide(prev, restHours, surv.IsSleeping, minNeed, critical, happiness,
                        sleepBelow, wakeAbove, needBelow, needAbove,
                        happyBelow, happyUntil, ht.plateaued, happyAllowed);
                }
                _mode[surv] = next;

                // A capped villager just gave up on happiness-leisure -> don't immediately retry it.
                if (prev == Mode.Leisure && next == Mode.Work && ht.plateaued
                    && happyBelow > 0f && happiness < happyUntil)
                    ht.cooldown = retrySeconds;

                bool applied = next == Mode.Manual
                    ? ApplyManualIfNeeded(surv, villager, i, msDiag)
                    : ApplyScheduleIfNeeded(surv, villager, ToSchedule(next), i);

                // MSdiag (d): detect the player editing/applying a schedule in the UI mid-session. Runs
                // every tick (independent of the debug/summary cadence) so a change is caught promptly,
                // but logs each distinct foreign value only once.
                if (msDiag) LogForeignScheduleChange(surv, villager, i);

                // Manual-mode state probe (5 s): mode vs. what the villager is ACTUALLY doing — shows
                // whether e.g. a Leisure-mode villager is still executing a station task the game
                // hadn't finished when the shift ended.
                if (msDiag && fiveSec && manualProbe)
                {
                    string task = "?";
                    try
                    {
                        var tr = villager.GetTaskRunner();
                        if (tr != null)
                            task = $"{(tr.GetActiveTask() != null ? "act" : "-")}/{(tr._runningTask != null ? "run" : "-")}";
                    }
                    catch { }
                    Plugin.Logger.LogInfo(
                        $"[DynamicNeeds] manual state villager[{i}] hour={hourNow} painted={(paintedNow >= 0 ? HourChar(paintedNow) : '-')} " +
                        $"mode={next} behavior={villager.CurrentBehaviorType} sleeping={surv.IsSleeping} rest={restHours:F1} untilWork={hoursUntilOn:F0} task={task}");
                }

                if (msDiag && manualQualifies && next != prev)
                    Plugin.Logger.LogInfo(
                        $"[DynamicNeeds] manual villager[{i}] hour={hourNow} painted={(paintedNow >= 0 ? HourChar(paintedNow) : '-')} " +
                        $"{prev}->{next} rest={restHours:F1} minNeed={minNeed:F2} sleeping={surv.IsSleeping}");

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
    }

    // Enabled=false + ManualScheduleDiagnostics=true observe-only tick: same 4 Hz cadence and the same
    // prune/CanControl/GetVillager guards as the control loop above, but performs ZERO writes — no
    // ApplyScheduleIfNeeded, no BoostAttr/ScaleDrain, no RecheckConsumeNeeds, no Rpc calls of any kind.
    // Advances the same _summaryTimer/_cohortTimer accumulators so LogHourCalibration/RefreshCohorts still
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

        if (cohortNow) RefreshCohorts(tracked, true);
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

    // Manual-schedule decision (RespectManualSchedule) for a 2+-cohort villager with painted off-hours.
    // The painted schedule is the player's coverage plan: man the post during painted Work hours, and
    // confine discretionary rest to the villager's OWN painted off-hours (front-loaded sleep, then
    // leisure fill) so same-station coworkers' rest cycles can never re-collide. Happiness never pulls
    // anyone off-shift here — the off-window leisure fill (plus its boost) is where happiness recovers.
    private static Mode DecideManual(Mode prev, float restHours, bool isSleeping, float minNeed,
        bool emergency, float needAbove, float wakeAbove, float resleepBelow, bool onWindow, bool topUpNow)
    {
        // 1. Life-threatening food/water pierces any window — a worker must never die at his post. Off
        //    the shift, hold the trip (hysteresis) until the need genuinely recovers; on-shift, release
        //    as soon as the emergency clears (the game's survival eat/drink behavior completes on its
        //    own — the mode only needed to free them for dispatch).
        if (emergency) return Mode.Leisure;
        if (prev == Mode.Leisure && minNeed < needAbove && !onWindow) return Mode.Leisure;

        // 2a. Hold a sleep in progress until genuinely rested (hysteresis top end).
        if (prev == Mode.Sleep && restHours < wakeAbove) return Mode.Sleep;

        // 2b. Adopt a sleep the game is running — but re-ENTER Sleep only when rest is meaningfully
        //     below the wake threshold (resleepBelow = wakeAbove - ResleepMarginHours), so the wake
        //     write can't immediately re-trigger a sleep (the v1.4.1 flap).
        if (isSleeping && restHours < resleepBelow) return Mode.Sleep;

        // 3. Asleep during the on-window though rested (game-forced): shove awake with a real schedule
        //    CHANGE (all-Work collapse); next tick returns to Manual, which restores the painted
        //    schedule. (Fired correctly in the 2026-07-09 test — woke an overslept cook at 01:00.)
        if (isSleeping && onWindow) return Mode.Work;

        // 4. Off-window: leisure fill, with ONE back-loaded top-up sleep timed (by the caller's
        //    topUpNow) to end at the shift start — or immediately if genuinely exhausted. The once-
        //    per-window flag and the re-entry margin (both folded into topUpNow) prevent the nap
        //    limit cycle. Never Work (over-mans the post a coworker is holding).
        if (!onWindow) return topUpNow ? Mode.Sleep : Mode.Leisure;

        // 5. On-window, nothing pressing: man the post — follow the painted schedule.
        return Mode.Manual;
    }

    // In-game hours from the current hour to the next painted Work slot (circular scan; 1..len).
    // Returns len when the paint has no Work hour (callers only use this for mixed paints).
    private static float HoursUntilNextWork(int[] painted, int hourNow)
    {
        int len = painted.Length;
        for (int k = 1; k <= len; k++)
        {
            if (painted[(hourNow + k) % len] == (int)ScheduleType.Work) return k;
        }
        return len;
    }

    // Enter/refresh the Manual baseline: make sure the villager's live schedule is their own painted
    // one. First entry with no prior collapse writes NOTHING (the painted schedule is already live —
    // the game natively executes it, confirmed in-game 2026-07-09). After a collapse, packs the stored
    // painted hours and writes them back through the same RPC path RestoreAll uses. While the villager
    // STAYS in Manual this is a no-op — the mod goes write-silent, so the schedule UI keeps showing
    // (and the player keeps editing) their real schedule.
    private bool ApplyManualIfNeeded(VillagerSurvival surv, Villager villager, int idx, bool msDiag)
    {
        if (_appliedManual.Contains(surv)) return false;

        EnsureSnapshot(surv, villager, idx);

        bool wasCollapsed = _applied.ContainsKey(surv);
        if (wasCollapsed && _scheduleHours.TryGetValue(surv, out var hoursArr) && hoursArr.Length > 0)
        {
            var arr = new Il2CppStructArray<int>(hoursArr.Length);
            for (int h = 0; h < hoursArr.Length; h++) arr[h] = hoursArr[h];
            long packed = villager._ScheduleToNetworkSchedule(arr);
            villager.overrideSchedule = false;
            villager.Rpc_ChangeSchedule(packed);
            _appliedPacked[surv] = packed;
            _everWrote.Add(surv);
        }
        _applied.Remove(surv);
        _appliedManual.Add(surv);
        if (msDiag)
            Plugin.Logger.LogInfo($"[DynamicNeeds] manual villager[{idx}] baseline " +
                (wasCollapsed ? "restored painted schedule" : "adopted live schedule (no write)"));
        return wasCollapsed;
    }

    // Active-mode painted-snapshot freshness (5 s cadence, RespectManualSchedule only). Player UI edits
    // never pulse __NetworkedSchedule (confirmed in-game 2026-07-09), so diff the ARRAY: in the Manual
    // baseline the live array IS the player's intent — any diff is an edit (re-snapshot; this is the
    // player-edit re-snapshot moved OUT of the diagnostics gate, per the idea-11 Phase-1 note). During
    // a collapse the live array is OUR uniform write — a diff from it means the player painted over it
    // mid-override: adopt the fresh intent (re-snapshot + flip to Manual; Decide may lawfully
    // re-override next tick).
    private void RefreshPaintedSnapshot(VillagerSurvival surv, Villager villager, int idx, bool msDiag)
    {
        try
        {
            var liveArr = villager._schedule;
            if (liveArr == null) return;
            if (!_scheduleHours.TryGetValue(surv, out var stored)) return;  // EnsureSnapshot owns first capture

            if (_appliedManual.Contains(surv))
            {
                bool differs = stored.Length != liveArr.Length;
                if (!differs)
                {
                    for (int h = 0; h < stored.Length; h++)
                        if (stored[h] != liveArr[h]) { differs = true; break; }
                }
                if (!differs) return;

                var tmp = new int[liveArr.Length];
                for (int h = 0; h < liveArr.Length; h++) tmp[h] = liveArr[h];
                _scheduleHours[surv] = tmp;
                if (msDiag)
                    Plugin.Logger.LogInfo(
                        $"[DynamicNeeds] manual villager[{idx}] player edit adopted: schedule={HoursString(tmp)}");
            }
            else if (_applied.TryGetValue(surv, out var st))
            {
                int stInt = (int)st;
                bool oursStill = true;
                for (int h = 0; h < liveArr.Length; h++)
                    if (liveArr[h] != stInt) { oursStill = false; break; }
                if (oursStill) return;

                var tmp = new int[liveArr.Length];
                for (int h = 0; h < liveArr.Length; h++) tmp[h] = liveArr[h];
                _scheduleHours[surv] = tmp;
                _applied.Remove(surv);
                _appliedManual.Add(surv);
                _mode[surv] = Mode.Manual;
                if (msDiag)
                    Plugin.Logger.LogInfo(
                        $"[DynamicNeeds] manual villager[{idx}] player edit during override adopted: schedule={HoursString(tmp)}");
            }
        }
        catch (Exception ex) { Plugin.Logger.LogError($"[DynamicNeeds] manual refresh: {ex}"); }
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

        // Snapshot the villager's own schedule once, before we ever touch it.
        EnsureSnapshot(surv, villager, idx);

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
        _appliedManual.Remove(surv);
        _everWrote.Add(surv);
        return true;
    }

    // First-touch capture of the villager's own schedule: the packed transient (legacy restore
    // fallback only) and the per-hour array (the real painted intent — what Manual mode and RestoreAll
    // use). Must run BEFORE the mod's first write to the villager.
    private void EnsureSnapshot(VillagerSurvival surv, Villager villager, int idx)
    {
        if (_originalSchedule.ContainsKey(surv)) return;

        // Known (2026-07-09): __NetworkedSchedule reads 0x0 at rest/load — this captures a packed
        // transient, not the painted schedule. Kept only as a last-resort restore fallback;
        // _scheduleHours is the truth.
        _originalSchedule[surv] = villager.__NetworkedSchedule;

        // MSdiag (a): snapshot the per-hour schedule array at the same "first touch" moment. Captured
        // UNCONDITIONALLY (Manual mode and RestoreAll need it); only the log line is gated.
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

    // Put back the real painted schedule for every villager we actually wrote to. The per-hour
    // _scheduleHours copy is the truth — packed _originalSchedule values are 0x0 at-rest transients
    // that restore nothing (confirmed in-game 2026-07-09; that's also why worlds played under the
    // active mod used to save uniform collapsed schedules). Best-effort on shutdown.
    private void RestoreAll()
    {
        foreach (var surv in _everWrote)
        {
            if (surv == null) continue;
            try
            {
                var villager = surv.GetVillager();
                if (villager == null) continue;
                villager.overrideSchedule = false;
                if (_scheduleHours.TryGetValue(surv, out var hoursArr) && hoursArr.Length > 0)
                {
                    var arr = new Il2CppStructArray<int>(hoursArr.Length);
                    for (int h = 0; h < hoursArr.Length; h++) arr[h] = hoursArr[h];
                    villager.Rpc_ChangeSchedule(villager._ScheduleToNetworkSchedule(arr));
                }
                else if (_originalSchedule.TryGetValue(surv, out var packed))
                {
                    villager.Rpc_ChangeSchedule(packed);
                }
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

    // (c) Same-workstation cohort report + manual-mode cohort membership, every 10 real seconds.
    // Groups currently-controlled villagers by workstation identity (native pointer — see the "managed
    // casts lie" / Pointer-boxing gotcha), records members of 2+ groups in _inCohort (what manual-mode
    // window gating keys on), and for any group of 2+ reports which hours ALL members' painted
    // schedules have them asleep. In RespectManualSchedule mode a NON-empty overlap is a coverage risk
    // the player should fix — warn once per distinct overlap value. Builds everything fresh each
    // report; no Workstation wrapper is stored past this method.
    private void RefreshCohorts(List<VillagerSurvival> tracked, bool logReport)
    {
        try
        {
            _cohortsComputed = true;
            _inCohort.Clear();

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
                    if (surv == null) continue;
                    _inCohort.Add(surv);
                    if (_scheduleHours.TryGetValue(surv, out var arr)) snaps.Add(arr);
                }

                string overlap;
                List<int>? overlapHours = null;
                if (snaps.Count < 2)
                {
                    overlap = "?";
                }
                else
                {
                    int hourCount = snaps[0].Length;
                    foreach (var arr in snaps) hourCount = Mathf.Min(hourCount, arr.Length);

                    overlapHours = new List<int>();
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

                if (logReport)
                    Plugin.Logger.LogInfo(
                        $"[DynamicNeeds] MSdiag cohort station=0x{kv.Key.ToInt64():X} name='{name}' members=[{members}] sleepOverlapHours={overlap}");

                // Manual-mode coverage warning: this cohort's painted off-windows collide, so
                // confinement cannot separate their sleep — the post can go unmanned. Warn once per
                // distinct overlap value (not every 10 s).
                if (Plugin.RespectManualSchedule.Value && overlapHours != null && overlapHours.Count > 0)
                {
                    string wkey = $"{name}|{members}";
                    if (!_overlapWarned.TryGetValue(wkey, out var prevOverlap) || prevOverlap != overlap)
                    {
                        _overlapWarned[wkey] = overlap;
                        Plugin.Logger.LogWarning(
                            $"[DynamicNeeds] manual-schedule coverage risk: station '{name}' members [{members}] " +
                            $"share painted sleep hours {overlap} — stagger their schedules so the post stays manned.");
                    }
                }
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
