using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using SSSGame;
using SSSGame.UI;        // ScheduleType
using SSSGame.Weather;   // WeatherSystem (day/night diagnostics)
using UnityEngine;

namespace DynamicVillagerNeedsMod;

// Poller (MonoBehaviour Update) — same pattern as the other AskaMods.
//
// We drive villager behavior through the game's REAL per-hour schedule (Rpc_ChangeSchedule), NOT the
// overrideSchedule fields. Confirmed in-game: setting overrideSchedule/scheduleOverride forces the
// behavior LABEL the FSM reads but bypasses the task-dispatch pipeline entirely — villagers just idle
// (task stays null) in broad daylight. The vanilla schedule path is the only thing that actually
// dispatches work/sleep/leisure tasks, so we rewrite the schedule to our needs-chosen activity and let
// the native pipeline run.
//
// Each tick, for every villager we own (host/authority), pick a behavior from its needs:
//   - rest below SleepWhenRestBelowHours        -> Sleep (until rest reaches WakeWhenRestAboveHours)
//   - else happiness below LeisureWhenHappinessBelow -> Leisure (until it recovers past LeisureUntil)
//   - else                                       -> Work
// Hysteresis (the "until" thresholds) prevents flip-flopping between modes. We only rewrite the
// schedule when the chosen mode changes (the schedule is collapsed to that single activity across all
// hours, decoupling the villager from the clock), so Rpc traffic stays minimal.
public class NeedsController : MonoBehaviour
{
    private enum Mode { Work, Sleep, Leisure }

    private readonly Dictionary<VillagerSurvival, Mode> _mode = new();
    // Last schedule type we wrote per villager — drives idempotency (only re-apply on change).
    private readonly Dictionary<VillagerSurvival, ScheduleType> _applied = new();
    // Each villager's original packed schedule, captured the first time we touch it, so we can put it
    // back on shutdown rather than leaving a collapsed needs-schedule baked in.
    private readonly Dictionary<VillagerSurvival, long> _originalSchedule = new();
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

                ScheduleType st = ToSchedule(next);
                bool applied = ApplyScheduleIfNeeded(surv, villager, st);

                if (debug && (next != prev || applied))
                    Plugin.Logger.LogInfo(
                        $"[DynamicNeeds] villager[{i}] {prev} -> {next} (rest={restHours:F1}/24, happiness={happiness:F2}) | {Diag(villager, surv)}");

                if (summaryNow && i == 0)
                    Plugin.Logger.LogInfo(
                        $"[DynamicNeeds] villager[0] mode={next} rest={restHours:F1}/24 happiness={happiness:F2} | {Diag(villager, surv)}");
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

    // Read-back diagnostics: what did the game actually do with our schedule this tick? behavior/task
    // tell us whether the vanilla pipeline picked it up and dispatched (task=active) or is idling.
    private static string Diag(Villager villager, VillagerSurvival surv)
    {
        string ws = "?", task = "?", dn = "?", night = "?";
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
        try { var w = WeatherSystem.Instance; if (w != null) { dn = w.DayNightValue.ToString("F2"); night = w.IsNight.ToString(); } } catch { }
        return $"override={villager.overrideSchedule} behavior={villager.CurrentBehaviorType} " +
               $"sleeping={surv.IsSleeping} workstation={ws} task={task} night={night} dayNight={dn}";
    }

    // Only the simulation authority (host in co-op, or the local player solo) should drive villager
    // behavior; doing it on a non-authority client would fight network sync.
    private static bool CanControl(VillagerSurvival surv)
    {
        try { return surv._hasAuthority; }
        catch { return true; }
    }
}
