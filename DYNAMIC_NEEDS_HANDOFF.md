# DynamicVillagerNeedsMod — Handoff (IN PROGRESS)

**Status as of 2026-06-21 (UPDATE #2 — read this first):** The **schedule-rewrite approach WORKS** —
villagers now perform real tasks (visually confirmed in-game). The earlier `overrideSchedule` approach is
**dead** (kept below for the record). What remains is *decision-logic design + persistence*, not
mechanism. See "UPDATE #2" immediately below for the current state, the open design decisions, and the
next steps. The "UPDATE (override DEAD)" section after it explains why we pivoted; the original handoff
body documents the dead override approach.

This is Mod 5. Intentionally **not** "COMPLETE" in `CLAUDE.md` yet — keep notes here until the design is
settled and confirmed, then promote confirmed facts into `CLAUDE.md`.

---

## UPDATE #2 2026-06-21 — schedule-rewrite CONFIRMED WORKING; open design issues

### The win
Driving the villager's **real schedule** via `Rpc_ChangeSchedule` (with `overrideSchedule=false`)
dispatches actual work — **confirmed in-game**: "everyone seems to be performing their tasks." The code
(`NeedsController.ApplyScheduleIfNeeded`) collapses a villager's whole 24h schedule to one activity
(all-Work / all-Sleep / all-Leisure) on each mode change, snapshots the original packed schedule, and
restores on shutdown. Build is clean + deployed.

Confirmed working API (now battle-tested):
- `int hours = Villager.scheduleMaxHourCount;` (**static** — CS0176 via instance).
- Schedule array is `Il2CppStructArray<int>` (the ScheduleType **underlying ints**, not the enum type).
- `long packed = villager._ScheduleToNetworkSchedule(arr);` then `villager.Rpc_ChangeSchedule(packed);`
  (host/authority only; we gate on `surv._hasAuthority`).
- Snapshot original = `villager.__NetworkedSchedule` (Int64).

### Open issues / decisions (where we stopped)
1. **`task=idle` was a BROKEN diagnostic, not broken behavior.** `TaskRunner.GetActiveTask()` reads null
   even while villagers visibly work (ASKA work likely runs through *interactions*, not the TaskRunner
   active-task slot). Diagnostic now logs `task=act/run` (GetActiveTask vs `_runningTask` field) to learn
   which reflects "working" — **still unresolved**; may need an interaction-based signal instead.
2. **⚠️ CRITICAL — leisure = basic needs.** The game's own schedule-panel legend states: *"Leisure time
   keeps Villagers happy and also lets them address their basic needs like Hunger, Thirst, and Cold."*
   So collapsing a villager to **all-Work** risks them never eating/drinking/warming up → starve/freeze
   over a full day. The decision logic MUST grant leisure for **basic needs**, not just happiness. **Next
   action (was mid-investigation when cut off):** dump `SSSGame.CharacterSurvival` /
   `SSSGame.VillagerSurvival` for hunger/thirst/cold/temperature `VariableAttribute`s (same shape as
   `_restVariableAttribute`) and add them as leisure triggers.
3. **Leisure safety-net is too aggressive / contradicts the goal.** Two genuinely-unhappy villagers
   (happiness 0.40 < `LeisureWhenHappinessBelow` 0.6) were parked on **all-Leisure until happiness > 0.78**
   — a villager doing nothing but leisure is the ultimate "wasting time," the opposite of the mod's point.
   Also risk: if a villager's max achievable happiness < 0.78 (bad housing etc.) they're stuck on leisure
   forever. **User decision needed** on leisure behavior: keep as-is / time-box it (N hours then back to
   work) / lower the trigger (only the truly miserable) / trigger on basic-needs instead / disable. (Prior
   finding: happiness was naturally **stable 0.78–0.82** with NO intervention, so an aggressive happiness
   safety-net may be unnecessary.)
4. **Schedule UI is visibly clobbered + persistence unverified.** The player can SEE schedules overwritten
   (all-purple for the leisure villagers). `RestoreAll()` runs on `OnDestroy`/`OnApplicationQuit` but
   **whether those fire reliably in IL2CPP is UNVERIFIED.** Schedules are networked/saved, so if restore
   doesn't fire, collapsed schedules bake into the save. Likely need **file-based persistence of originals
   + restore on load** (TreeRespawnMod `com.askamods.*.save` pattern). Test on a reloadable save; don't
   trust the schedule to be clean yet.
5. **Sleep-when-tired NOT yet observed.** In all tests `rest` only fell 24→~13 (never below the `8`
   threshold), so the core sleep behavior is **untested**. Needs a longer run (or temporarily raise
   `SleepWhenRestBelowHours`) to confirm a tired villager actually goes to bed via the all-Sleep schedule.

### Immediate next steps (resume here)
1. Dump survival need-attributes (hunger/thirst/cold) — see issue #2.
2. Get the user's call on leisure behavior — see issue #3.
3. Rework `Decide()`: Sleep (tired) > Leisure (low basic-need OR low happiness, gentle/time-boxed) > Work.
4. Implement file-based schedule persistence + restore-on-load; verify restore actually happens.
5. Longer in-game test: confirm sleep triggers, villagers don't starve, leisure villagers recover to work.
6. Resolve the `task=` diagnostic (find the real "is working" signal).

---

## UPDATE 2026-06-21 — override approach DEAD → schedule-rewrite

### What three in-game tests proved
With `overrideSchedule=true` + `scheduleOverride=<type>` (+ optionally `CurrentBehaviorType`), per the
readback diagnostics (`override=… behavior=… sleeping=… workstation=… task=… night=… dayNight=…`):
- The override **does** drive the FSM's behavior LABEL: the game sets `CurrentBehaviorType` to match
  `scheduleOverride` (seen even after we STOPPED writing `CurrentBehaviorType` ourselves), and sleep is
  suppressed (`sleeping=False` when they'd normally be scheduled asleep).
- **But the task pipeline never runs:** `task=idle` permanently, in **broad daylight** (`night=False`,
  `dayNight=1.00`) with a valid `workstation=yes`. Villagers just huddle in a cottage / pace and re-bark
  "going back to work." So `overrideSchedule` forces the *label* the FSM reads but **bypasses the actual
  task-dispatch pipeline**. Not a night-gate issue — fails at noon too. **The override path is a dead end.**

### Key facts learned (all confirmed via interop dump + runtime)
- `Villager.overrideSchedule` (bool), `scheduleOverride` (ScheduleType), `CurrentBehaviorType`
  (ScheduleType) are **plain public fields** — no get_/set_ accessors. Writing `CurrentBehaviorType`
  does NOT fire `OnBehaviorTypeChanged`; the event is raised elsewhere (the schedule pipeline).
- Villager AI is an **Invector FSM** of ScriptableObject nodes under `SSSGame.AI.FSM.*` (300+ nodes).
  `FSM_CheckSchedule : vStateDecision` has a `ScheduleType scheduleType` field + `Decide(IFSMBehaviourController)`
  — it routes Work/Sleep/Leisure. Work branch then runs `FSM_PrepareForWork` → `FSM_MoveToVillagerWorkstation`
  → task steps; gated by `FSM_CheckDayTime`, `FSM_CheckWorkstation`. Tasks execute via `SSSGame.AI.TaskRunner`
  (`GetActiveTask()`, `_runningTask`) fed by the schedule pipeline — NOT by the override fields.
- `WeatherSystem.IsNight` (bool) and `DayNightValue` (Single, ~1.0 = midday, ~0.0 = deep night) exist.

### New approach (current code): rewrite the REAL schedule
Drive the vanilla schedule (the only thing that dispatches tasks); leave `overrideSchedule=false`.
On each **mode change** (idempotent), collapse the villager's whole schedule to the chosen activity:
```
int hours = Villager.scheduleMaxHourCount;             // STATIC member (CS0176 if accessed via instance)
var arr = new Il2CppStructArray<int>(hours);           // schedule array is INT (ScheduleType underlying vals), not the enum type
for (h) arr[h] = (int)scheduleType;
long packed = villager._ScheduleToNetworkSchedule(arr); // game's own packer → Int64
villager.overrideSchedule = false;
villager.Rpc_ChangeSchedule(packed);                    // network-safe apply (host/authority only)
```
- Snapshot each villager's original `villager.__NetworkedSchedule` (Int64) on first touch; `RestoreAll()`
  on `OnDestroy`/`OnApplicationQuit` puts it back (best-effort — verify it actually fires).
- **Open risk being tested:** does flipping the schedule mid-hour make the FSM re-route AND dispatch a
  real task (`task=active`)? If yes, this is the way. If schedules persist badly or restore doesn't fire,
  add file-based persistence + restore (TreeRespawnMod pattern). Test on a reloadable save; don't trust
  the collapsed schedule to be clean until restore is verified.

---

---

## The goal (the user's actual ask, refined over the conversation)

Replace ASKA's clock-based villager **schedule** (you manually assign Sleep/Work/Leisure hours per
villager) with **needs-based behavior**, so villagers self-manage instead of being micromanaged:

- **tired → sleep**
- **needs leisure → take leisure**
- **otherwise → work**

…plus configurable control over **how much sleep and leisure they actually need**. Overarching
principle: **"stop wasting time" without reducing happiness.**

---

## How we got here (so nobody re-walks the dead ends)

1. Original framing was "villagers sleep ~50% of playtime; make them sleep half as much without
   happiness loss." First mod (`VillagerSleepMod`, now **deleted**) scaled the rest-drain /
   rest-recovery rates via a measure-and-rescale poller.
2. **That approach was abandoned.** Runtime logging proved **sleep is schedule-bound, not
   tiredness-bound**: villagers reach `rest = 24.00/24.0` (100%) and then keep `sleeping = True` for
   ~4+ minutes of real time before waking. They sleep the **whole scheduled night** regardless of how
   rested they are. So scaling drain/recovery cannot shorten the dominant nightly sleep. Don't retry it.
3. Good news from that run: **happiness was rock-stable / rising (0.78 → 0.82)** across the session, so
   the rest system itself isn't a happiness risk.
4. The user then clarified the real goal (above): they dislike the schedule system and want needs-based
   behavior. → pivoted to **`DynamicVillagerNeedsMod`**.

---

## Current approach: `DynamicVillagerNeedsMod`

GUID `com.askamods.dynamicneeds`. Files:
- `Plugin.cs` — config binds + registers the controller MonoBehaviour + Harmony patch.
- `Patches/VillagerSurvivalPatch.cs` — Postfix on `VillagerSurvival.Spawned()` registers each villager
  into `Plugin.TrackedSurvivals`.
- `NeedsController.cs` — the poller (`Update()`), same pattern as HealthRegenMod/TorchFuelMod.

**Per tick, for every villager we own (host/authority only):**
1. Read `rest = VillagerSurvival._restVariableAttribute.GetValue()` (0..24) and
   `happiness = Villager.NormalizedHappiness` (0..1).
2. Pick a `Mode` with hysteresis (`NeedsController.Decide`):
   - `prev==Sleep` and `rest < WakeWhenRestAboveHours` → **Sleep** (keep sleeping until rested)
   - else `rest <= SleepWhenRestBelowHours` → **Sleep**
   - else (not sleeping) leisure enabled & `happiness <= LeisureWhenHappinessBelow` (with `…Above`
     hysteresis) → **Leisure**
   - else → **Work**
3. Apply via `NeedsController.Apply`:
   ```
   villager.overrideSchedule = true;
   villager.scheduleOverride  = <ScheduleType>;
   if (villager.CurrentBehaviorType != st) villager.CurrentBehaviorType = st; // force immediate
   ```

**Config section `[DynamicNeeds]`** (file: `BepInEx/config/com.askamods.dynamicneeds.cfg`):
| Key | Default | Meaning |
|---|---|---|
| `Enabled` | `true` | Master switch. |
| `SleepWhenRestBelowHours` | `8.0` | Sleep when rest (0..24) drops below this. Lower = less sleep. |
| `WakeWhenRestAboveHours` | `23.0` | While sleeping, wake once rest reaches this. Lower = shorter sleeps. |
| `LeisureWhenHappinessBelow` | `0.6` | Take leisure when happiness < this. **0 disables leisure.** Self-correcting net. |
| `LeisureUntilHappinessAbove` | `0.78` | Leave leisure once happiness recovers past this. |
| `DebugLogging` | `true` | Per-villager mode-change logs + 5s summary for villager[0]. |

The leisure rule is the **happiness safety net**: there is no separate "leisure need" stat in the game,
so leisure is derived from happiness. If working more / sleeping less ever lowers happiness, villagers
auto-take leisure until it recovers — happiness becomes a feedback loop instead of a hope.

---

## ⚠️ The linchpin (verify this FIRST)

**Unverified assumption:** that setting `Villager.overrideSchedule = true` + `scheduleOverride` +
`CurrentBehaviorType` actually redirects the villager's FSM behavior (i.e. the FSM's `FSM_CheckSchedule`
reads `CurrentBehaviorType`, and `overrideSchedule` stops the clock from overwriting it).

The interop DLLs are native trampolines into `GameAssembly.dll` (Unity 6 IL2CPP, **metadata v39** —
not dumpable by Il2CppDumper), so this could only be confirmed by running. Everything **compiles**, which
proves the members exist with those signatures — it does **not** prove they drive behavior.

**Test (as HOST — villager sim is host-authoritative):** load a save with several villagers, run a day or
two with `DebugLogging=true`, then read `BepInEx/LogOutput.log` for `[DynamicNeeds]` lines, and watch:
1. **Do villagers stop sleeping the whole night?** With defaults they should sleep only when actually
   tired (`rest<=8`), wake at `rest>=23`, and work the rest of the time — including at night.
2. **Does happiness hold?** Watch `happiness=` in summaries and any `Work -> Leisure` transitions (the
   safety net firing).
3. **Anyone stuck idle?** (forced to Work with no job/workstation.)

### If the linchpin FAILS (override doesn't redirect behavior)
Fallback: end the sleep **interaction** directly to force a wake, instead of (or in addition to) the
schedule override. Confirmed available API:
- `Interaction.Finish(IInteractionAgent agent, InteractionSessionState reason)` and
  `Interaction.FinishAllSessions(InteractionSessionState reason)`
- `VillagerRestInteractionConfig/VillagerRestInteractionSession.End(InteractionSessionState lastResult)`
- The sleeping villager's session is reachable via the sleep FSM (`FSM_GoToBedAndSleep/SleepData.session`),
  or by patching `RestInteraction.Use` to capture the session per agent.
Also consider: does `CurrentBehaviorType`'s setter fire `OnBehaviorTypeChanged`? If not, `VillagerHappiness`
/ FSM listeners may not react to our forced changes — may need to invoke whatever the game uses to raise
that event, or set the backing field `_CurrentBehaviorType_k__BackingField` + manually trigger refresh.

---

## Game mechanics reference (what was reverse-engineered)

### Rest / sleep (CONFIRMED at runtime)
- `VillagerSurvival._restVariableAttribute` : `VariableAttribute`, range **0..24** ("hours of rest"),
  drains awake / fills asleep. `.GetValue()`, `.SetValue()`, `.min`, `.max`.
- `VillagerSurvival.IsSleeping` (bool), `.RestRatio` (0..1), `.GetVillager()`, `._hasAuthority` (bool,
  inherited from `CharacterSurvival`; use for authority gating).
- `VillagerDataSheet : CharacterDataSheet` — `fatigueRate` (was **1** at runtime), `restHoursSafe`=**4**,
  `restHoursLow`=**2**, `restHoursCritical`=**0**. (These are the vanilla sleepiness thresholds; the mod
  does not currently touch them.)
- Sleep is **schedule-driven** (villagers sleep the scheduled night even at 100% rest) — the central
  finding that killed the drain/recovery approach.

### Schedule (API surface CONFIRMED present; behavior effect UNVERIFIED — the linchpin)
- `Villager.overrideSchedule` (bool), `Villager.scheduleOverride` (`ScheduleType`).
- `Villager.CurrentBehaviorType` (`ScheduleType`, get+set; backing `_CurrentBehaviorType_k__BackingField`)
  — the current scheduled activity the FSM acts on. `OnBehaviorTypeChanged` (Action<ScheduleType>).
- `Villager.Schedule` (Il2CppStructArray, per-hour), `scheduleMaxHourCount`, `_bitsPerScheduleType`,
  `Rpc_ChangeSchedule(Int64)`, `_ScheduleToNetworkSchedule` / `_NetworkScheduleToSchedule`,
  `GetScheduleModified(int hour)`, `ResetScheduleToDefault()`. (Persistent path — **not used**; we chose
  the runtime override to avoid baking changes into the save / compounding across reloads.)
- `enum SSSGame.UI.ScheduleType { Sleep, Work, Leisure }` (underlying int order unknown — use the enum
  values directly in code, never hardcode ints).
- FSM decision nodes (ScriptableObjects, can't dump bodies): `FSM_CheckSchedule(scheduleType)`,
  `FSM_CheckVillagerSleepiness(Safe/Low/Critical)`, `FSM_CheckVillagerIsSleeping`, `FSM_GoToBedAndSleep`,
  `FSM_SetIsSleeping`.

### Happiness (API surface; relative rates NOT yet measured — see open questions)
- `Villager.NormalizedHappiness` (0..1), `.GetHappinessManager()` → `VillagerHappiness`.
- `VillagerHappiness`: `SleepRate`, `WorkRate`, `LeisureRate` (Single, per-activity happiness rates);
  `GetCurrentHappinessRate(bool)`, `GetHappinessGainPerHour(ScheduleType)`,
  `GetHappinessGainPerDay(ScheduleType, out int matchingHours)`, `_CalculateWorkRateMultiplier()`,
  `workRateTierMultiplier`, `workAnxietyEffect`, `sleep/work/leisureStatusEffect` (StructureStatusEffect).
- **`GetHappinessSummary(bool addDetails)` and `GetOverallHappiness(bool addDetails)` return human-readable
  breakdown strings** (the villager happiness-tab text). Logging these is the fastest way to learn what
  actually drives happiness — do this next if we need to understand/tune the happiness side.
- No "leisure need" VAttr exists; leisure is purely a schedule activity that grants `LeisureRate` happiness.

### Status-effect / attribute system (context)
- `SandSailorStudio.Attributes`: `VariableAttribute` (modifiers + `Tick`), `AttributeManager`
  (ticks VAttrs at `tickInterval`), `CustomOperationVariableAtributeModifier`, `Attribute`, `AttributeConfig`.
- `StatusEffect.table : StatusEffectModifierTable` with `vattrElements[] {modifier: VariableAttributeModifier
  (.value), targetAttribute: AttributeConfig (.attributeId)}` — how effects modify attributes.

---

## Build / deploy / tooling

- Build: `dotnet build "DynamicVillagerNeedsMod/DynamicVillagerNeedsMod.csproj" -c Debug`. The
  `CopyToPlugins` target auto-copies the DLL to `ASKA/BepInEx/plugins/DynamicVillagerNeedsMod/` and to
  the project folder. Targets `net6.0`; references the standard BepInEx core + interop set.
- ASKA install: `D:\SteamLibrary\steamapps\common\ASKA` (paths in the `.csproj` are absolute — adjust on
  the other machine if the install path differs).
- `_explore/` Mono.Cecil inspector tooling (run with PowerShell):
  - `search.ps1 -TypeKeywords @(...) [-MemberKeywords @(...) -Members]` — find types / members by keyword.
  - `dump.ps1 -Types @("Full.Type.Name", ...)` — dump fields/properties/methods of types.
  - `il.ps1 -Type X -Methods @(...)` — dumps IL, but interop methods are just native trampolines
    (won't show real game logic). Useful only to confirm signatures.
  - These read the interop DLLs under `ASKA\BepInEx\interop|core|unity-libs`.

---

## Open questions / next steps
1. **Verify the linchpin** (above). This gates everything.
2. If working, **tune the four thresholds** for "sleep ~half as much" feel and confirm happiness holds.
3. **Measure happiness rates**: log `WorkRate` / `SleepRate` / `LeisureRate` and
   `GetHappinessSummary(true)` for one villager to understand whether Work-vs-Sleep time helps or hurts,
   and whether a happiness compensation lever is even needed.
4. Decide whether to also expose the vanilla sleepiness thresholds (`restHoursSafe/Low/Critical`) or
   `fatigueRate` as config, if finer control is wanted.
5. Once confirmed: promote the verified facts into `CLAUDE.md` as the Mod 5 section, and mark this
   handoff superseded.
