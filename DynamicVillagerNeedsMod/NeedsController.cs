using System;
using System.Collections.Generic;
using SSSGame;
using SSSGame.UI;   // ScheduleType
using UnityEngine;

namespace DynamicVillagerNeedsMod;

// Poller (MonoBehaviour Update) — same pattern as the other AskaMods.
//
// Each tick, for every villager we own (host/authority), we pick a behavior from its actual needs
// and force it via Villager.scheduleOverride + CurrentBehaviorType, with overrideSchedule=true so the
// assigned clock-schedule is bypassed:
//   - rest below SleepWhenRestBelowHours        -> Sleep (until rest reaches WakeWhenRestAboveHours)
//   - else happiness below LeisureWhenHappinessBelow -> Leisure (until it recovers past LeisureUntil)
//   - else                                       -> Work
// Hysteresis (the "until" thresholds) prevents flip-flopping between modes.
public class NeedsController : MonoBehaviour
{
    private enum Mode { Work, Sleep, Leisure }

    private readonly Dictionary<VillagerSurvival, Mode> _mode = new();
    private float _summaryTimer;

    void Update()
    {
        if (!Plugin.Enabled.Value) return;

        var tracked = Plugin.TrackedSurvivals;
        if (tracked.Count == 0) return;

        float sleepBelow = Plugin.SleepWhenRestBelowHours.Value;
        float wakeAbove = Plugin.WakeWhenRestAboveHours.Value;
        float leisureBelow = Plugin.LeisureWhenHappinessBelow.Value;
        float leisureUntil = Mathf.Max(Plugin.LeisureUntilHappinessAbove.Value, leisureBelow);
        bool debug = Plugin.DebugLogging.Value;

        _summaryTimer += Time.deltaTime;
        bool summaryNow = debug && _summaryTimer >= 5f;
        if (summaryNow) _summaryTimer = 0f;

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

                Mode prev = _mode.TryGetValue(surv, out var m) ? m : Mode.Work;
                Mode next = Decide(prev, restHours, happiness, sleepBelow, wakeAbove, leisureBelow, leisureUntil);
                _mode[surv] = next;

                Apply(villager, next);

                if (debug && next != prev)
                    Plugin.Logger.LogInfo(
                        $"[DynamicNeeds] villager[{i}] {prev} -> {next} (rest={restHours:F1}/24, happiness={happiness:F2})");

                if (summaryNow && i == 0)
                    Plugin.Logger.LogInfo(
                        $"[DynamicNeeds] villager[0] mode={next} rest={restHours:F1}/24 happiness={happiness:F2} " +
                        $"sleeping={surv.IsSleeping} behavior={villager.CurrentBehaviorType}");
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError($"[DynamicNeeds] controller: {ex}");
                tracked.RemoveAt(i);
            }
        }
    }

    private static Mode Decide(Mode prev, float restHours, float happiness,
        float sleepBelow, float wakeAbove, float leisureBelow, float leisureUntil)
    {
        // Sleep has top priority. Keep sleeping until rested (hysteresis), or start when tired.
        if (prev == Mode.Sleep)
        {
            if (restHours < wakeAbove) return Mode.Sleep;
        }
        else if (restHours <= sleepBelow)
        {
            return Mode.Sleep;
        }

        // Not sleeping — decide leisure vs work. Leisure disabled when threshold is 0.
        if (leisureBelow > 0f)
        {
            if (prev == Mode.Leisure)
            {
                if (happiness < leisureUntil) return Mode.Leisure;
            }
            else if (happiness <= leisureBelow)
            {
                return Mode.Leisure;
            }
        }

        return Mode.Work;
    }

    private static void Apply(Villager villager, Mode mode)
    {
        ScheduleType st = mode switch
        {
            Mode.Sleep => ScheduleType.Sleep,
            Mode.Leisure => ScheduleType.Leisure,
            _ => ScheduleType.Work,
        };

        villager.overrideSchedule = true;
        villager.scheduleOverride = st;
        // Force the value the FSM actually reads, so the change takes effect immediately rather than
        // on the next in-game hour tick.
        if (villager.CurrentBehaviorType != st)
            villager.CurrentBehaviorType = st;
    }

    // Only the simulation authority (host in co-op, or the local player solo) should drive villager
    // behavior; doing it on a non-authority client would fight network sync.
    private static bool CanControl(VillagerSurvival surv)
    {
        try { return surv._hasAuthority; }
        catch { return true; }
    }
}
