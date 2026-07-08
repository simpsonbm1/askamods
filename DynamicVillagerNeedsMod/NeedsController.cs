using System;
using System.Collections.Generic;
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
    private float _summaryTimer;
    // Accumulator for the periodic food/water re-check (FoodRecheckIntervalSeconds).
    private float _recheckTimer;
    private float _tickTimer;
    private const float TickInterval = 0.25f;   // 4 Hz — villager needs/behavior don't need per-frame updates

    void Update()
    {
        if (!Plugin.Enabled.Value) return;

        _tickTimer += Time.deltaTime;
        if (_tickTimer < TickInterval) return;

        var tracked = Plugin.TrackedSurvivals;
        if (tracked.Count == 0) { _tickTimer = 0f; return; }

        float dt = _tickTimer;   // real elapsed since last processed tick — keeps all rate*dt math correct
        _tickTimer = 0f;
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
        bool summaryNow = debug && _summaryTimer >= 5f;
        if (summaryNow) _summaryTimer = 0f;

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
                bool applied = ApplyScheduleIfNeeded(surv, villager, st);

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

                if (summaryNow && i == 0)
                    Plugin.Logger.LogInfo(
                        $"[DynamicNeeds] villager[0] mode={next} rest={restHours:F1}/24 | {Happy(villager, ht)} | {Needs(surv)} | {Diag(villager, surv)}");
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError($"[DynamicNeeds] controller: {ex}");
                tracked.RemoveAt(i);
            }
        }
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
    private bool ApplyScheduleIfNeeded(VillagerSurvival surv, Villager villager, ScheduleType st)
    {
        if (_applied.TryGetValue(surv, out var cur) && cur == st)
            return false;

        // Snapshot the original packed schedule once, before we ever touch it.
        if (!_originalSchedule.ContainsKey(surv))
            _originalSchedule[surv] = villager.__NetworkedSchedule;

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

    // Only the simulation authority (host in co-op, or the local player solo) should drive villager
    // behavior; doing it on a non-authority client would fight network sync.
    private static bool CanControl(VillagerSurvival surv)
    {
        try { return surv._hasAuthority; }
        catch { return true; }
    }
}
