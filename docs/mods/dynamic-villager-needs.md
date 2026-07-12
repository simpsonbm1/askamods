# Mod 5: DynamicVillagerNeedsMod — COMPLETE (v1.9.7)

**Goal:** Replace ASKA's clock-based villager schedule (manually assigning Sleep/Work/Leisure hours)
with **needs-based** behavior so villagers self-manage: tired → sleep, low happiness or a real
food/water need → leisure, otherwise work. Overarching principle: **stop wasting time without
reducing happiness.** Villager-only; the player is never touched.

**Game subsystem:** [Villager Schedule / Needs / Happiness System](../architecture.md#villager-schedule--needs--happiness-system)
— survival/happiness/schedule APIs, the eating FSM, the game-mechanic facts (night-sleep race, sleep
being a rate problem, warmth left to the game), and the subsystem dead-ends all live there. Read it
first. This file describes the mod as it exists at v1.9.7; the version history is compressed into
the appendix at the bottom.

## Architecture

- `NeedsController` (registered `MonoBehaviour`, `Update()` polling — the standard `DayTracker`
  pattern). `VillagerSurvivalSpawnedPatch` Postfix on `VillagerSurvival.Spawned()` registers each
  villager. All writes gated on `surv._hasAuthority` (host/solo).
- **Throttling:** the villager loop runs at 4 Hz (every 4th frame) with `Time.deltaTime` accumulated
  across skipped frames and fed as dt so rate math stays correct. `RefreshCohorts` (cohort grouping)
  is further gated to every 10 s via `_cohortTimer`. `[Perf]` stopwatches instrument `tick.cohorts`
  and `tick.villagers` sub-phases (log only when > 2 ms). Measured 2026-07-12: tick avg 5.8 /
  max ~44–48 ms; `tick.cohorts` avg 4.7 / max 28 ms; `tick.villagers` avg 8.6 / max 21.6 ms.
- **Live config reload:** `Plugin.Cfg?.Reload()` every 30 s at the top of `Update()`, BEFORE the
  Enabled gate (so `Enabled` edits apply too). Exception: `BuilderTestKey` parses once at startup.
- **Drives the REAL per-hour schedule**, never the override fields: mode changes pack via
  `_ScheduleToNetworkSchedule(arr)` → `Rpc_ChangeSchedule(packed)`, idempotent (only on change).
  Original schedules snapshot on first touch; `RestoreAll` (OnDestroy/OnApplicationQuit) restores
  the player's painted hours (`_scheduleHours`) for every villager the mod ever wrote to.
- Boost rates scale with `WeatherSystem.TimeSpeedMultiplier` so hours-to-full settings hold in
  game-hours under TimeWarp acceleration. (The rest boost is Sleep-MODE-gated, not in-bed-gated —
  under high acceleration a top-up can complete before the villager reaches bed; accepted, because
  in-bed gating risks a stuck-Sleep state when no bed is reachable.)

## Needs-based mode (default) — `Decide`

Priority order, with hysteresis:
1. **Sleep** when `rest <= SleepWhenRestBelowHours` (default 8), stay asleep until
   `rest >= WakeWhenRestAboveHours` (default 23). **Adopt-net (critical):** also enter Sleep when
   `surv.IsSleeping` && `rest < WakeWhenRestAboveHours` — the game forces villagers to bed at
   nightfall regardless of schedule, so the mod ADOPTS the game-forced sleep, applies the rest
   boost, and wakes them at the threshold. This makes the nightfall race irrelevant and lets the
   sleep-entry threshold stay low ("nap only when truly tired").
2. **Leisure** when food/water critically low (`LeisureWhenNeedBelow`, hold to
   `LeisureUntilNeedAbove`) or happiness below trigger (`LeisureWhenHappinessBelow`, hold to
   `LeisureUntilHappinessAbove`).
3. Else **Work**.

**Direct VariableAttribute writes (the core trick), rates derived from in-game hour length:**
- *Happiness:* while in mod-Leisure, add to `_happinessVAttr` so empty→full takes
  `LeisureHoursToFullHappiness` in-game hours (vanilla rate is ~2 in-game days for 0.4→0.75).
  Already-happy villagers never enter mod-Leisure, so they keep vanilla happiness by design.
- *Rest:* while asleep, add to `_restVariableAttribute` so 0→24 takes `SleepHoursToFullRest`
  in-game hours → shorter sleeps. (Lowering the wake threshold alone does NOT reduce total sleep;
  sleep *fraction* = drain/(drain+gain) — you must raise the gain rate.)
- *Warmth:* `FireWarmthMultiplier` adds extra warmth **only while warmth is already rising** (the
  game has them at a heat source) → shorter warm-up trips; never overrides the game's *when*.
- **Plateau safety:** if happiness can't rise under the boost (capped below the exit threshold by
  housing etc.), a windowed plateau check sends the villager back to Work with a retry cooldown so
  they don't oscillate Work↔Leisure. (All tested villagers had cap=100, so it rarely fires.)
- **Cold is deliberately NOT a leisure trigger** — the game's own `warmUpBehaviour` warms villagers
  fully during work; a forced leisure trip warms only partway and causes fire↔job thrash.

**Stuck-eater fix:** each tick, for any owned villager below `FoodRecheckWhenNeedBelow` (or
starving/dehydrated), call `SurvivalObjectiveQuest.SetDirty()` via
`GetReplenishFoodQuest()`/`GetReplenishWaterQuest()` (throttled by `FoodRecheckIntervalSeconds`).
This re-arms the game's OWN eat path so a villager parked at an empty store re-evaluates and eats
real food. The threshold gates WHO gets re-poked, not when they eat — the eat decision stays the
game's `_foodObjectiveLow` objective. No thrashing observed at 15 s cadence.

**Drain-rate controls:** `HungerRateMultiplier`/`ThirstRateMultiplier` scale how fast food/water
fall (observe-the-delta-and-amplify, drops only, so eating is never scaled; both ends clamped).
1.0 = vanilla = zero overhead.

## Manual-schedule mode (opt-in) — `RespectManualSchedule` + `DecideManual`

Scope: villagers in a **2+ same-workstation cohort** (grouped by `GetNonVikingWorkstation()` native
pointer every 10 s) whose captured painted schedule is a **real mixed paint** (≥1 Work hour AND ≥1
off-hour — uniform all-S/all-L saved schedules are legacy junk from old collapse writes and fall
back to pure needs mode). The **Buildstation family is excluded** from cohorts/qualification/lending
(native class-name check — managed `is` lies): unassigned and newly-summoned villagers are
auto-parked there and their mixed DEFAULT schedules would wrongly qualify.

**Baseline = follow the villager's own painted schedule, write-silent while held** (the game
natively executes painted schedules). `DecideManual`, in order:
1. **Emergency food/water** (`IsStarving`/`IsDehydrated` or `minNeed <= CriticalNeedOffPostBelow`)
   → Leisure, pierces any window (holds to `LeisureUntilNeedAbove` only off-window).
2. **Hold an in-progress sleep** to `WakeWhenRestAboveHours`; re-adopt game-initiated sleeps only
   below `wake − ResleepMarginHours` (2 h margin — kills tick-flap at the wake threshold).
3. **Asleep-but-rested during the on-window** → one all-Work write as a wake shove, then back to
   baseline.
4. **Off-window** → **ONE back-loaded top-up sleep per off-window** (once-per-window flag; leisure
   drains rest ~1 h/game-h so unlimited re-entry naps on a ~2.5 h limit cycle), **timed to end at
   shift start** (lead = `(wake − rest) × SleepHoursToFullRest/24 + 1.5 h buffer`; an exhausted
   villager still sleeps immediately). Remaining off-window time = the **fill policy** (below).
   Happiness never pulls a manual villager off-shift — the off-window fill (+ its boost) is where
   mood recovers; base-mode happiness thresholds are honored during Work/Builder fill as a mood
   safety valve.
5. Else **man the post** (follow the paint).

**Fill policy — global `OffWindowFill` (Leisure | Work | Builder) + per-station overrides:**
- `Leisure` (default): leisure fill + happiness boost.
- `Work`: return to post during off-hours (opt-in over-manning; "cooks fighting over the pot"
  optics are the deliberate trade-off).
- `Builder`: **builder-loan** — the villager does real builder work while every UI surface still
  shows the home job. Recipe (shared `BuilderLoan.cs`, also drives the F9 diagnostic):
  `home.ReleaseTaskAgent(agent)` (home quests leave the QuestRunner; checkboxes/`VillagersInCharge`
  untouched) + `bs.SetTaskAgent(agent)` + `bs.AddToTaskDatas(villager)` + all-Work schedule write;
  exact reverse restores. Lend failure → Leisure + per-window no-retry; restore failure retries
  (10 s throttled warning); `RestoreAll` releases loans at app-quit; world-leave drops loan state
  WITHOUT native calls (stale-wrapper rule). `BuilderReturnLeadHours` (1.0) = walk-back fallback.
  Known cosmetic: lent villagers acquire the Buildstation complain quest → "time to build
  something" barks while idle on loan (accepted; future option: skip lend when
  `buildProjects`/`repairProjects` empty).
- **Three-brush semantic (no config — the brush IS the toggle):** painted Work = on-window; painted
  **Leisure** = fill FORCED to Leisure for that hour (overrides global/per-station fill; top-up
  sleep and emergencies still take precedence; happiness boost applies); painted Sleep = flexible
  off-time (top-up sleep + configured fill).
- **`ManualScheduleStations`** (default "" = any station): comma-separated case-insensitive
  station-name substrings limiting manual mode to matching cohorts. Entries accept `NameSubstring`
  or `NameSubstring:Fill` (Fill ∈ Leisure|Work|Builder; FIRST-MATCH-WINS in list order — put
  specific substrings before broad ones; invalid suffix → one-time warning, entry treated bare).
  Matching checks the display name OR `Structure.DefaultName` (station names are player-editable
  via `Rpc_ChangeName`; `DefaultName` keeps the pristine type name). Beware over-broad terms
  (`house` matches Cooking House AND Warehouse AND Longhouse).

**Station-name discovery + feedback:** generated file `com.askamods.dynamicneeds.stations.txt`
(BepInEx config dir; rewritten only on content change; requires `RespectManualSchedule=true` or
`ManualScheduleDiagnostics=true`): every station with ≥1 assigned villager — name (rendered
`Display (Default)` when renamed), assigned count, whitelist verdict + fill. Two
NON-diagnostics-gated one-time lines: (a) INFO when a 2+ cohort matches no entry while the list is
non-empty, (b) WARNING when an entry matches no station ~60 s after first tracked villager.
Motivation: real user failure — entry "Watchtower" vs actual building "Guard Tower"; names vary per
save and game language, so docs can never list them.

**Player-edit adoption (mode-independent, every controlled villager):** diff the per-hour
`_schedule` ARRAY contents every 5 s; a live array whose pack != `_appliedPacked` (the mod's own
last write) and != the snapshot = player intent → adopt into `_scheduleHours`. Catches edits made
mid-collapse, off-window, or while lent, within 5 s.

**`PreserveScheduleInSaves` (default true):** Harmony prefix/postfix on
`Villager.Serialize(DataObject)` swaps the painted snapshot into `_schedule` for the serialize call.
WHY: quit-to-menu never fires plugin OnDestroy → `RestoreAll` doesn't run → a live collapse would
bake into the save. **Load-bearing guard:** mask ONLY when the live schedule packs to
`_appliedPacked[surv]` — an unadopted fresh player repaint also differs from the snapshot, and
masking it destroys the repaint.

**`ShowIntendedScheduleInUI` (default true):** shared `ScheduleMask.cs` Begin/End core (used by the
save patch too); `SchedulePanelDisplayPatch` masks `VillagerSchedulePanel`
`Set(Villager)`/`Refresh()`/`OnEnable()`/`HasUnappliedChanges()` via Harmony `__state`
(nesting-safe). The apply path `UpdateSchedule` is deliberately unpatched so player writes hit
reality and adoption catches them. WHY: the panel reads live `_schedule`, so during a collapse it
showed all-Work — users read that as data loss (three false bug reports in two days).

## Quest-layer interactions (accepted behavior, not bugs)

- **Off-duty retrieval:** an off-duty cook in Leisure never STARTS new dishes but still gets pulled
  out to retrieve/distribute a finishing dish, then resumes leisure. The work runs on the QUEST
  layer (`WorkstationQuest`/`QuestRunner`, priority ~15 vs idle 0), and the deliver-results step
  stays eligible regardless of schedule. USER DECISION (2026-07-09): accepted — it protects
  finished dishes from overcooking, and the leisure happiness boost keys on the mod's mode so it
  keeps pumping mid-retrieval. Full ground truth: architecture.md → Cooking Station Pipeline.
- Schedule flips notify quest data (`_OnVillagerBehaviorChanged`) but never interrupt/block quests.

## Config reference — `[DynamicNeeds]` (`BepInEx/config/com.askamods.dynamicneeds.cfg`)

Core: `Enabled` (true); `SleepWhenRestBelowHours` (8); `WakeWhenRestAboveHours` (23);
`SleepHoursToFullRest` (3, 0=vanilla — the real "sleep less" lever); `LeisureWhenNeedBelow` (0.15,
food/water only, 0=off) / `LeisureUntilNeedAbove` (0.5); `LeisureWhenHappinessBelow` (0.6, 0=off) /
`LeisureUntilHappinessAbove` (0.78); `LeisureHoursToFullHappiness` (4, 0=vanilla);
`FireWarmthMultiplier` (2.0, 1=vanilla); `DebugLogging` (false).

Eating/drain: `FoodRecheckIntervalSeconds` (15.0, 0=off); `FoodRecheckWhenNeedBelow` (0.2);
`HungerRateMultiplier` (1.0); `ThirstRateMultiplier` (1.0).

Manual mode: `RespectManualSchedule` (false); `CriticalNeedOffPostBelow` (0.05); `OffWindowFill`
(Leisure); `ManualScheduleStations` (""); `PreserveScheduleInSaves` (true);
`ShowIntendedScheduleInUI` (true).

Diagnostics: `ManualScheduleDiagnostics` (false); `BuilderDiagnostics` (false); `BuilderTestKey`
(F9, typing-guarded, parsed once at startup); `BuilderReturnLeadHours` (1.0).

## Tuning facts (confirmed in-game)

- `FoodRecheckWhenNeedBelow` default 0.2 sits just below the game's natural eat trigger. Higher
  (tested 0.5) re-arms mildly-peckish villagers → **mass dash to storage right after loading a
  save**. Too low delays un-sticking until near-starvation.
- Hunger ×50 pins everyone at starving → permanent emergency-tier eating; use ~4–8× for testing.
- 10x+ TimeWarp optics caveat: walking costs game-hours, so even correct behavior looks erratic —
  validate at 1x–2x.

## Dead-ends (don't retry)

- `SetTaskAgent` ALONE for builder-loan — home quests remain in the QuestRunner and outcompete
  builder quests. The full release+set+add recipe is required (v1.6.0 finding).
- Front-loading the off-window top-up sleep — rest drains ~1 h/game-h afterward, so a 12 h-shift
  villager started at rest 11.4 and ended at 0.4. Back-load it (end at shift start).
- Unlimited sleep re-entry in the off-window — naps on a ~2.5 h drain-driven limit cycle. One
  top-up per window.
- Packed-value (`__NetworkedSchedule`) diffing for player-edit detection — it's a transient
  change-transport field: reads 0x0 at rest, mass-resets mid-session, and UI edits never pulse it.
  Diff `_schedule` array contents instead. (Ground truth: architecture.md → Schedule clock.)
- `WeatherSystem.DayNightValue` as a clock — saturates at 0/1 for long stretches; use `TimeOfDay`.
- Assuming `ConfigEntry.Value` reads the edited .cfg live — BepInEx never re-reads the file; a code
  review claimed "reads live each pass" and was wrong. `Cfg.Reload()` polling is required.
- Subsystem-level dead-ends (`overrideSchedule`, get-only happiness rates, warmth-as-trigger, wake
  threshold as "sleep less") live in architecture.md → Villager Schedule / Needs / Happiness.

## Verification status

- Needs-based core, boosted sleeps/happiness, warmth, stuck-eater fix: confirmed in-game across
  many sessions (2026-06-22 onward).
- Manual mode Phase 1 confirmed in-game for the cook cohort 2026-07-09 (user goal 2a);
  guards (first non-cooking cohort) cycled builder-loan lend/restore 2026-07-10.
- Builder-loan final verification 2026-07-10: **227 lend/restore pairs across two cooks at 10x over
  several game-days, zero failures/exceptions**; panel shows painted staggers while on loan;
  mid-collapse repaint adopts within 5 s.
- v1.9.x QoL round (per-station fill, three-brush, discovery file, live reload, rename-proof)
  confirmed in-game 2026-07-10 in one session.
- Unexercised: the happiness safety valve during Work fill (mood never dipped ≤ 0.6 in the test
  session; same logic as base `Decide`, low-risk); the log-only sleep-overlap warning.

## Version history (compressed)

| Version(s) | Date | What changed |
|---|---|---|
| v1.0.x | 2026-06 | Needs-based core: Decide + VAttr boosts + adopt-net for game-forced sleep. |
| v1.1.0 | 2026-06 | Stuck-eater `SetDirty()` fix; hunger/thirst rate multipliers. |
| v1.2.0 | 2026-07-07 | Villager loop throttled to 4 Hz with accumulated dt. |
| v1.3.0–v1.3.3 | 2026-07-09 | idea-11 Phase 0 diagnostics; established the schedule ground truth (TimeOfDay clock, slot==hour, `_schedule` array-diff for edits) now in architecture.md. |
| v1.4.0–v1.4.4 | 2026-07-09 | `RespectManualSchedule` + `DecideManual`; iteration fixed tick-flap (re-entry margin), nap limit cycle (once-per-window), front-load drain (back-load). |
| v1.5.0 | 2026-07-09 | `OffWindowFill` enum (Work fill confirmed in-game). |
| v1.6.0–v1.6.1 | 2026-07-10 | Builder-loan diagnostics (F9); proved the release+set+add loan recipe. |
| v1.7.0–v1.7.2 | 2026-07-10 | Builder fill shipped; `PreserveScheduleInSaves` (+ repaint guard); universal player-edit adoption; Buildstation exclusion; `ManualScheduleStations`. |
| v1.8.0 | 2026-07-10 | `ShowIntendedScheduleInUI` panel masking. |
| v1.9.0–v1.9.4 | 2026-07-10 | Per-station `Name:Fill`; three-brush semantic; stations.txt discovery + mismatch feedback; live cfg reload; rename-proof matching; diag defaults → false. |
| v1.9.5 | 2026-07-10 | Typing guard on the F9 hotkey. |
| v1.9.6–v1.9.7 | 2026-07-12 | `[Perf]` sub-phase stopwatches; cfg reload 5 s→30 s. No behavior change. |
