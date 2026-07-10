# New Mod Ideas — Approaches (research 2026-07-03; ideas 7–8 added 2026-07-06; idea 10 added 2026-07-07; idea 11 added 2026-07-08; idea 12 added 2026-07-09; idea 13 added 2026-07-10)

Status legend: everything here is **⚠️ pending in-game verification** unless marked otherwise.
All type/member signatures below were confirmed from the interop binaries via Mono.Cecil
(2026-07-03; ideas 7–8 re-dumped 2026-07-06; idea 10 dumped 2026-07-07; idea 13 dumped 2026-07-10) —
signatures are facts; *runtime behavior* claims are the ⚠️ part. Ideas 7–8 come from Nexus user feedback
on TaskUnlockerMod (rondi112, 2026-07-06); idea 10 from Nexus user kira31374 (2026-07-07). Idea 11 comes
from a Nexus discussion on DynamicVillagerNeedsMod (snakesilver + author, 2026-07-08) and — unusually —
needs **no** new binary dump: every API it relies on is already used by the shipped mod (runtime-proven).
Idea 13 comes from a Nexus discussion (rondi112 + author, 2026-07-10).

**Priority order (user, historical):** 5) freezing hunters → 2) den respawn → 4) vacuum → 3) crafting
multiplier. Vacuum (4) has since shipped; den respawn (2) is **IN PROGRESS** as DenRespawnMod (WIP
v1.0.2, NOT shipped — core hotkey refresh confirmed in-game up close 2026-07-08; see the idea-2
section status note below); remaining open priority: 5) freezing hunters → 3) crafting multiplier.

**Shipped ideas — full research retired, self-documented in `docs/mods/`:**
- Idea 2, monster/beast den respawning → **SHIPPED** as DenRespawnMod v1.2.2 — [`docs/mods/den-respawn.md`](docs/mods/den-respawn.md).
- Idea 4, ground-item vacuum → **SHIPPED** as GroundItemVacuumMod v1.1.0 — [`docs/mods/ground-item-vacuum.md`](docs/mods/ground-item-vacuum.md).
- Idea 6, recipe/fish task unlock → **SHIPPED** as TaskUnlockerMod v1.2.x, confirmed in-game 2026-07-06 —
  [`docs/mods/task-unlocker.md`](docs/mods/task-unlocker.md). NOTE: the plan's original model was only
  half right — cooking is IDiscoverableItem/Rpc_AddDiscoverable, but **fishing tasks are gated by MARKED
  fishing grounds, not item discovery** — corrected mechanism in
  [`docs/architecture.md`](docs/architecture.md) → "Task Discovery & Bypassing".
- Idea 8, new workers start with zero tasks → **SHIPPED** as ZeroTaskWorkersMod v1.0.0 — [`docs/mods/zero-task-workers.md`](docs/mods/zero-task-workers.md).
- Idea 9, mushrooms year-round/rain-independent → **SHIPPED** in TreeRespawnMod v1.4.7 — [`docs/mods/tree-respawn.md`](docs/mods/tree-respawn.md).
- Idea 10, inventory fish fillet (shift-click) → **SHIPPED** as FishFilletMod v1.1.1 — [`docs/mods/fish-fillet.md`](docs/mods/fish-fillet.md).

---


## 3. Crafting output multiplier — new mod

**Goal:** per-item configurable N× output for every recipe (crafting stations + "materials" build menu).
An existing Nexus mod claims this but users report it broken.

**Key API (confirmed from binary):**
- `SandSailorStudio.Inventory.BlueprintInfo : ItemInfo` (ScriptableObject asset) carries
  **`result : ItemInfo` + `quantity : Int32`** (output), `cost : ItemInfoQuantity` (input — separate,
  so scaling output doesn't touch cost). Subtypes: `CraftBlueprintInfo`, `WorkshopBlueprintInfo`,
  `ForgingBlueprintInfo`, `CookingRecipeInfo`, etc.
- `SandSailorStudio.Inventory.Blueprint.GetResultQuantity() : Int32` — the read choke point, **but
  it's tiny and single-purpose ⇒ prime IL2CPP inlining candidate; a Harmony patch on it may
  silently never fire** (documented gotcha — plausibly exactly why the Nexus mod "doesn't work").
- `SSSGame.AI.ItemCraftStep` (`quota`, `_craftedItemCount`) drives villager crafting; player path
  goes through `CraftInteraction._OnCraftingSuccess(IInteractionAgent)`.

**Approach — edit the DATA, not the method:** at world/database load, enumerate blueprint assets
(via `ItemInfoDatabase` — TerrainLevelerMod already proved read+inject access to it) and multiply
`BlueprintInfo.quantity` by the configured per-item (or global) multiplier. Asset-level writes are
immune to inlining and automatically cover every consumer (player craft, villager craft, materials
menu, UI previews). Config: global multiplier + per-item-name overrides (TreeRespawnMod's
substring-map pattern). Guard against double-applying (idempotence flag per session; re-apply on
world reload). ⚠️ verify: UI shows scaled quantity; villager quota logic stays sane; co-op client
sees host-consistent results (data edit is local — client without the mod may display vanilla
numbers even if items granted are host-authoritative — needs a test).

---

## 5. Hunters/soldiers freezing to death — DynamicVillagerNeedsMod extension

**Goal:** far-roaming workers (hunters, archers, guards) start seeking warmth *earlier* so they
don't die returning home in winter. Vanilla's warm-up trigger fires too late for them.

**Key API (confirmed from binary):**
- `VillagerSurvival._warmUpQuest : SurvivalObjectiveQuest` + `warmUpBehaviour` — the game's OWN
  warm-up pipeline (exactly what the architecture doc says to use: tune the game's trigger, don't
  force leisure trips — that thrash is a documented dead-end).
- `VillagerSurvival._warmthObjectiveLow / _warmthObjectiveSafe : VariableAttributeValueObjective
  : PropertyValueObjective` with **writable `_targetValue : Single`** (+ `_normalized : bool`) —
  the actual trigger/exit thresholds, per villager.
- `SurvivalObjectiveQuest.SetDirty()` — force re-evaluation (proven in-game by DynamicVillagerNeedsMod).
- Distance-to-warmth is computable: `Settlement.FindWarmthOutlet(pred)` / `Settlement._warmthSites`
  / `WarmthOutlet.GetClosestWarmthSource(Villager)`.
- Job identification: hunter = villager whose task agent is bound to `SSSGame.HuntingStation`
  (`SetTaskAgent`); defend duty = `Villager.IsOnDefendDuty`. (DynamicVillagerNeedsMod already
  tracks villagers via `VillagerSurvival.Spawned()` postfix.)

**Approach (in DynamicVillagerNeedsMod):** in the existing NeedsController poll, for villagers
matching config'd roles (hunters/defenders — or simply *all* villagers beyond X meters from the
nearest warmth outlet), raise `_warmthObjectiveLow._targetValue` by a distance-scaled margin
(config: base threshold %, per-100 m bonus, role filter), then `SetDirty()` on the warm-up quest
when crossing. Far villagers head home while they still have warmth budget; villagers at base keep
vanilla behavior (no thrash — the raised trigger only binds when far away). Restore vanilla values
on shutdown/despawn (snapshot originals). All writes gated on `_hasAuthority`.
⚠️ verify: whether `_targetValue` is normalized (0..1) or absolute (`_normalized` flag says which);
winter warmth drain rate vs. travel time — tune default margin in-game.

---

## 7. Assign workers/tasks to a building while it's still under construction — new mod

**Goal:** let the player set up a building's workflow (assigned workers, task list, priorities,
limits) as soon as the plot is placed, instead of camping by the buildsite to configure it the
moment it finishes (worst case: a fresh warehouse instantly crammed with random hauls).

**How construction actually works (confirmed from binary):**
- The full `Structure` (with all its `Workstation` structure-components) already EXISTS during
  construction — `SSSGame.BuildSite : MonoBehaviour` carries `Structure` / `Buildstation` /
  `supplyInventory` / `layers`, and `Structure.buildSite` points back. Finishing is
  `BuildSite.FinishBuild(Int32)` → `_FinalizeBuild()` → `Structure.Activate()` (networked twin
  `Rpc_Activate()`, event `Structure.OnActivate : Action`) → `_DestroyBuildSite()`. The
  finished/unfinished flip is `Structure._isActive` / `IsActive` — nothing is instantiated at
  finish; things are *enabled* (`activityComponents` / `activityObjects`).
- **The T-menu is already buildsite-aware:** the unfinished and finished building open the SAME
  `SSSGame.UI.WorkstationMenu` (matches the screenshots — "Open Fisherman's House Menu" appears in
  both states). The menu has parallel tab-name fields (`structureTab*` vs `buildsiteStructureTab*`)
  and gates its pages per state: `RefreshAssignPageVisibility()`, `_HandleInvalidOverlay()` +
  `taskPageInvalidOverlay` / `goToAssignPageButton`, `SetupPages()`. So the "UI implications" are
  mostly *un-hiding tabs in one menu*, not building new UI.
- **One interaction component serves both states:** `SSSGame.WorkstationInteraction : Interaction`
  holds `buildSite`, `workstation`, `structure`, plus a `hideAssignPage : Boolean` and
  `TaskTabOrder : InteractionOrder` — the R/Q hotkey entries ("Manage workers" / "View Tasks") are
  this component's pages into `WorkstationMenu`; on an unfinished building the R slot is instead the
  BuildSite's supply interaction.
- Assignment plumbing (shared with idea 8): `Villager.AssignToWorkstation(IWorkstation)` →
  `Workstation.SetTaskAgent(ITaskAgent)` → `AddToTaskDatas(Villager)`; per-task assignees live in
  `WorkstationTaskData.VillagersInCharge`, synced by the generic NetworkWorkstation's
  `Rpc_ChangeTaskVillagersInCharge(Int32, Il2CppStructArray)`.
- **The game already has task presets:** `WorkstationMenu.SaveCurrentTaskPreset(Structure)` /
  `LoadTaskPreset(Structure, Boolean)` (UI buttons `saveTaskPresetButton`/`loadTaskPresetButton`),
  backed by `Workstation.SerializeTasksPreset(Structure)` / `DeserializeTasksPresetData`.

**Approach (phased — ship the cheap win first):**
1. **Phase 0 (diagnostics, decides everything):** on aiming at a buildsite, log the underlying
   `Structure`'s workstation state pre-activation: does `Spawned()` run, are `_taskDatas`
   populated, does `CanCreateTasks()` return true, does `SetTaskAgent` succeed? This tells us
   whether pre-assignment is a UI unlock or a simulation problem.
2. **Phase 1 ("activate paused" — solves the warehouse pain with NO new UI):** config-listed
   building types start idle when construction finishes: postfix `Structure.Activate()` → clear
   every task's `VillagersInCharge` (via the game's own `Rpc_ChangeTaskVillagersInCharge` path)
   and/or suppress warehouse haul-task creation (prefix
   `ResourceStorage.CanCreateStorageTaskForItemInfo` → false, the documented Mod-6 gate) until the
   player opens that structure's `WorkstationMenu` once. No camping: the building waits for you.
   **Support-burden guardrails (user concern 2026-07-06: a silently-paused building reads as
   "I built this and nobody works it" → Nexus bug reports):**
   - the pause list ships **EMPTY** (feature fully opt-in per building name — never a default list);
   - a paused building raises the game's OWN issue flag — `Structure.IssueMarker : GameObject` +
     `ActivateIssueMarker(Boolean)` (confirmed from binary) — plus a log/HUD line at activation,
     so the state is discoverable in-game, not only in the cfg;
   - optional `AutoResumeAfterMinutes` timeout so a forgotten paused building reverts to vanilla
     instead of sitting dead forever.
   If the issue-marker visibility can't be made to work, demote Phase 1 to an internal mechanism
   behind Phase 2 (pause only as the deferred-apply step for pre-configured assignments) rather
   than shipping it as a standalone user-facing mode.
3. **Phase 2 (pre-configure UI):** un-gate the existing menu on buildsites — patch
   `RefreshAssignPageVisibility()` / `_HandleInvalidOverlay()` (and respect
   `WorkstationInteraction.hideAssignPage`) so the Assign + Tasks tabs show for a structure whose
   `buildSite != null`. Assignments made pre-completion are stored in the live
   `WorkstationTaskData`s / task agents, which survive activation (same objects — nothing is
   recreated at finish). If villager AI misbehaves on an inactive station (⚠️ below), defer the
   *effect*: record the choices, replay them via the game's own calls in the `Activate()` postfix.
4. **Phase 3 (optional):** per-template default task presets — auto-`LoadTaskPreset` a user-saved
   preset when a structure of that template activates ("every new warehouse starts with MY config").

**⚠️ verify / risks:**
- Whether an assigned villager pathfinds to the unbuilt station and idles/complains (the FSM may
  need `IsActive`) — if so, Phase 2 must defer effects to activation (the Phase 3 replay shape).
- Whether task datas exist pre-activation for stations whose tasks come from discovery/recipes
  (CraftingStation) vs. built-ins (Buildstation `AddBuiltinTaskDatas()`).
- Upgrades (`Structure.Upgrade`/`Rebuild` + `SerializeTaskDataForRecreation`) already carry tasks
  across — don't double-apply "activate paused" on an upgrade activation (`_isUpgrade` flag).
- All writes host-authoritative; the villagers-in-charge RPC lives on the GENERIC
  NetworkWorkstation base — patching generic NetworkBehaviour methods is untested territory here
  (prefer calling the RPCs, never patching `CopyBackingFieldsToState` — load-hang gotcha).

---

## 11. Optional "manual schedule" mode — respect player-set shifts (DynamicVillagerNeedsMod extension)

**Source:** Nexus discussion on DynamicVillagerNeedsMod (snakesilver + the author, 2026-07-08). Some
users love the needs-based automation but still want vanilla schedule control for **coverage
staggering** — watchtowers, archer towers, kitchens, the coal depot — where pure needs-based behavior
sends several workers to sleep at once and leaves a post unmanned (independent workers — lumberjacks,
masons, gatherers — aren't affected because their stations don't overlap). Compromise proposed by
snakesilver (his "Option 2"), endorsed by the author as a **"manual schedule" toggle**: still let the
player set a schedule, but the villager **breaks out of sleep/leisure early when its needs are already
satisfied** — keeps the mod's "no wasted time" magic while restoring shift control. (snakesilver's
"Option 1" — villagers auto-negotiating non-overlapping shifts among themselves — is noted but is a far
harder, downstream-effect-heavy algorithm; the toggle below is the tractable one both parties preferred.)

**Framing — the real dual goal (made explicit; it's an undercurrent in every comment, not stated
outright):** *primary* = guarantee **24/7 coverage** of manned posts (towers, kitchens, coal depot);
*secondary* = **no villager standing idle**. The manual schedule IS the player's coverage plan, and the
one invariant that must never break is **phase separation between the guards' rest cycles** — pure
needs-based silently violates it (two similar guards drift into synchronized tiredness → both sleep at
once → gap). **The invariant is scoped PER SHARED STATION, not globally:** nobody cares if a carpenter and
a cook sleep at the same time, but if both cooks sleep at once no food is made. So the mod's job in this
mode is three things: (a) keep each villager present during its assigned on-window; (b) **for villagers
sharing a coverage-critical station (a cohort of 2+ at the same workstation — two cooks, two tower
guards), confine each one's discretionary sleep/leisure to its OWN staggered off-window**, front-loaded, so
their cycles can never re-collide (the coverage-preserving core — see approach) — villagers on
independent/solo stations (carpenter, lumberjack, mason, gatherer — snakesilver's own examples of
non-overlapping roles) need NO confinement and keep pure needs-based efficiency; (c) fill leftover
off-window time productively rather than idle or over-man.

**Why it's plausible — everything needed is ALREADY in the shipped mod (runtime-proven, not just
Cecil-confirmed):**
- The mod ALREADY snapshots each villager's original player-set schedule
  (`_originalSchedule[surv] = villager.__NetworkedSchedule`) and ALREADY writes it back verbatim on
  shutdown (`RestoreAll` → `Rpc_ChangeSchedule(original)`). So "baseline = the manual schedule" is a
  state that already works in-game — it just needs to become a *live* mode, not only a shutdown restore.
- The per-mode write path (`Villager._ScheduleToNetworkSchedule(arr)` → `Rpc_ChangeSchedule(packed)`,
  made idempotent via `_appliedPacked`) already handles BOTH uniform collapses (all-Sleep/Work/Leisure)
  AND an arbitrary packed schedule (the snapshot restore), so following the manual schedule and
  collapsing to a single activity coexist with zero new plumbing.
- Current in-game hour is already read for the `Diag` line: `WeatherSystem.Instance.DayNightValue`
  (0..1) → `hourIndex = floor(DayNightValue * Villager.scheduleMaxHourCount)`. The player's activity for
  that hour comes from the per-hour `villager._schedule` array (already referenced in
  `ApplyScheduleIfNeeded`) — capture its contents at snapshot time so no schedule-unpack method is needed.
- Already host-authority-gated (`_hasAuthority`), so co-op safety is unchanged.

**Approach (opt-in; default OFF so existing users keep pure needs-based):** add
`[DynamicNeeds] RespectManualSchedule` (default `false`). When true the mod stops using all-Work as the
"needs are fine" baseline and works off each villager's snapshotted player schedule, split into its
**on-window** (player-set Work hours) and **off-window** (player-set Sleep/Leisure hours):
1. **On-window → man the post (Work).** Don't pull the villager off for a need unless it's truly critical —
   the off-window top-up (item 2) is what keeps mid-shift needs from arising. **User-confirmed default
   (2026-07-08): a worker must never die of hunger/thirst/cold at his post.** Nuance by need type:
   critical **hunger/thirst** → allow a brief off-post trip to eat/drink, then return; **cold** is handled
   the way this mod already handles warmth — via the game's OWN warm-up during work (`warmUpBehaviour`,
   accelerated by `FireWarmthMultiplier`), NEVER a forced leisure trip (the documented warmth-thrash
   dead-end), so a guard won't freeze at post without ever leaving it. Optional `ManualWorkIsInviolable`
   sub-toggle suppresses only the *discretionary* off-post cases, never the life-threatening emergency.
2. **Off-window + the coverage-preserving CORE — confine rest to the off-window, front-loaded, SCOPED TO
   SAME-STATION COHORTS.** Confinement only matters between villagers assigned to the SAME coverage-critical
   station — so group tracked villagers by their assigned workstation (`Villager.GetNonVikingWorkstation()`,
   already read in `Diag`) and apply it only to cohorts of **2+**. For a villager in such a cohort: it
   sleeps/recovers ONLY inside its own off-window, biased **early** (top up via the existing
   `SleepHoursToFullRest` boost so it's fully rested for its next on-window) — never floating by raw need
   across its on-window. Because the player staggered the cohort's off-windows, confined sleep can't
   collide → the station is always manned. This is precisely what makes "returns to work early" SAFE: the
   sleep already happened inside the off-window, so resuming activity can't re-sync it onto a cohort-mate's
   sleep (the failure the whole feature guards against). Villagers NOT in a 2+ cohort (solo/independent
   stations) get no confinement — pure needs-based, maximal efficiency, exactly as the mod behaves today.
3. **Off-window SURPLUS → fill it; don't idle, don't over-man.** Once rested/fed/happy the villager has
   leftover off-hours. Sending it back to its OWN post is wrong twice over — it over-mans a post the other
   guard already holds (the "standing around doing nothing" users dislike) AND re-syncs the cycles. Instead:
   - *Ships first (schedule-only, zero coverage risk):* surplus → Leisure/idle — trivial with the existing
     machinery; safe but not yet "productive".
   - *Stretch — the popular "builder mode" request:* divert the villager's task dispatch to the
     construction/haul pool during the surplus **without dropping its `AssignToWorkstation(post)` binding**,
     then restore the post agent when its on-window returns — the same snapshot-and-restore shape the mod
     already uses for schedules, but applied to the task-agent system (`SetTaskAgent` / `WorkstationTaskData`,
     the ideas 7/8 plumbing). ⚠️ needs its OWN diagnostics phase first: ASKA models a "builder" as
     *unassigned* labor, so confirm an assigned villager can be lent to the build pool and cleanly restored
     (no walking home, no dropped station/task state) before promising it; if it isn't clean, degrade to
     the Leisure/idle fill.
4. **`Decide` change (small, same-shape):** the existing Sleep/Leisure branches already gate on genuine
   need and hysteresis-exit — that IS "only sleep/recover as needed." The manual-mode change is only:
   (a) the "needs fine" fall-through returns a `Manual` state (follow the snapshot's activity for the
   current `hourIndex`) instead of all-Work; (b) discretionary Sleep is *permitted only when the current
   hour is in the villager's off-window* (a critical need still overrides anywhere).

**⚠️ verify / risks (diagnostics first — note the phase-collision problem is now handled BY DESIGN via the
off-window confinement in item 2, so it's the mechanism, not an open risk):**
- **Shift longer than one full rest tank → unavoidable mid-shift sleep.** If the player gives 2 guards a
  12h-on/12h-off split but a full rest tank lasts < 12h, a guard WILL tire mid-shift; the mod shortens that
  sleep (boost) and guarantees he starts topped up, but it cannot conjure a third guard — gapless coverage
  with too few workers is the player's schedule responsibility. Document the limit; optionally warn when a
  configured on-window exceeds the achievable rest duration.
- **`DayNightValue` → schedule hour-0 phase alignment** must be calibrated so `hourIndex` lines up with the
  player's blocks (log `DayNightValue` + game clock + live `_schedule[hourIndex]` together).
- **The mod preserves a stagger; it doesn't invent one.** Manual mode relies on the player having given a
  same-station cohort staggered schedules; the job is to stop pure-needs behavior from DESTROYING that
  stagger, not to auto-generate one (auto-negotiating non-overlapping shifts is snakesilver's harder
  "Option 1" — out of scope here). If a 2+ cohort is left with identical off-windows, confinement can't
  separate them — surface that (warn when same-station villagers share an off-window) rather than silently
  failing coverage. **Cohort membership is dynamic** (re-assignment, hiring, firing), so recompute the
  station→villagers grouping periodically, not once.
- **Builder-fill feasibility** (item 3 stretch) — the one genuinely uncertain mechanism; gate it behind its
  own diagnostics phase and ship the Leisure/idle fill first so coverage is never at risk.
- **Critical-need override of a Work hour is DECIDED, not open (user, 2026-07-08):** default allows a brief
  off-post trip for critical hunger/thirst, and relies on the game's warm-up for cold (see item 1) — a
  worker must never die at his post. `ManualWorkIsInviolable` gates only discretionary off-post, never the
  emergency. What still needs tuning in-game is the *threshold* at which "critical" trips the off-post trip
  (should be near-death, not mild hunger, so it rarely fires).
- **Snapshot freshness if the player edits a schedule mid-session:** re-read `villager._schedule` while the
  mod is in the `Manual` baseline (it isn't overwriting then, so that array is the player's live intent).
- All writes stay host-authoritative and go through the game's own RPCs (`Rpc_ChangeSchedule`; and for the
  builder stretch the `SetTaskAgent`/villagers-in-charge RPC path) — unchanged safety posture.

**Implementation mapping (session decision 2026-07-08 — refines the items above where they differ):**
- **The snapshot is chiefly a read-only window map.** Capture a per-hour `int[]` copy of
  `villager._schedule` at the same pre-first-write moment `_originalSchedule` is captured. `Decide` gains
  one gate — "is the current hour in this villager's off-window?" — plus two rule changes: discretionary
  Sleep/Leisure entry requires off-window, and the needs-fine fall-through during off-window is **Leisure
  fill** (surplus), never Work (over-manning) and never snapshot-Sleep (a rested villager napping wastes
  happiness-gain time).
- **On-window baseline = apply the snapshot packed schedule (a `Manual` apply state), per item 4a — NOT an
  all-Work collapse.** Behaviorally identical during Work hours (the game reads only the current hour), but:
  the player's REAL schedule stays visible/editable in the schedule UI mid-session (a collapse would show
  all-Work — fatal to the coverage-staggering UX and to the Phase-3 UI warning below); mid-session edit
  detection falls out naturally (`__NetworkedSchedule` ≠ applied packed while in Manual ⇒ player edit ⇒
  re-read `_schedule`, re-snapshot); and the NATIVE scheduler dispatches the off-window transition itself
  (the mod then adopts/shortens the sleep). `RestoreAll` already proves the arbitrary-packed write path.
- **Scope:** window-gating only for 2+ same-station cohorts — group by `GetNonVikingWorkstation()`, keyed
  by native `Pointer` via the `(object)ws is Il2CppObjectBase b` boxing pattern (Workstation's compile-time
  base chain is unity-libs-stubbed); recompute every ~10 s, never cache the wrappers. Solo/stationless
  villagers: today's pure-needs `Decide`, unchanged. **Degenerate off-window (all-Work snapshot) in a
  cohort → fall back to pure needs** — otherwise the gate forbids discretionary sleep forever.
- **Overrides:** critical hunger/thirst (`IsStarving`/`IsDehydrated` or below `CriticalNeedOffPostBelow`,
  ~0.05) pierce the on-window anywhere; `ManualWorkIsInviolable` suppresses only discretionary cases. Cold:
  unchanged (`FireWarmthMultiplier` is already mode-independent). **Game-forced night sleep during an
  on-window** (night guards): adopt+boost when rest is genuinely depleted (the unavoidable mid-shift sleep);
  otherwise the mod must WAKE them — but the existing wake trick relies on a schedule CHANGE, and `_applied`
  idempotency would skip the re-write ⇒ needs a forced re-fire (clear `_applied` / pulse Sleep→Work).
  ⚠️ verify in-game which write actually ends a forced sleep. (Phase 0 note 2026-07-09: painted-W nights were worked straight through by a rest-topped villager — forced sleep may be rest-depletion-only, making this moot for adequately-scheduled guards; still verify.)
- **Phasing:** **v1.3.0 = Phase 0, read-only diagnostics** (`ManualScheduleDiagnostics`, default true per
  project rule): per-hour snapshot capture + logging, hourIndex calibration (`DayNightValue` → hourIndex vs
  snapshot activity vs live behavior), cohort grouping + pairwise sleep-overlap report, player-edit
  detection (re-read `_schedule` on packed mismatch, log whether it reflects the new hours). **v1.4.0 =
  Phase 1, the feature** (`RespectManualSchedule` default false; log-only overlap warning). **Phase 2
  stretch = builder-fill** (own diagnostics phase, per item 3). **Phase 3 stretch = schedule-UI overlap
  warning (user mockup 2026-07-08):** when the player paints Sleep hours that overlap a same-station
  coworker's sleep window, highlight the overlapping hours in the schedule panel and show e.g.
  `SLEEP OVERLAP WITH COWORKER "ASA"` near Apply. Needs the `SSSGame.UI` schedule-panel type + its
  paint/apply handlers + text injection (TerrainLeveler build-menu precedent proves UI injection is
  doable); Phase 0's cohort+overlap computation is the exact data source, and Phase 1's log warning ships
  the same detection first.
- **Phase 0 RESULTS (2026-07-09, v1.3.1–v1.3.3, COMPLETE — all exit criteria met):** hour source is
  `floor(WeatherSystem.Instance.TimeOfDay)` (linear 0..24 wall clock; slot k == clock hour k, zero
  offset — painted midday Sleep block 10–13 executed 10:00–13:59 exactly). `DayNightValue` is a
  saturating blend, NOT a clock — dead-end. `__NetworkedSchedule` is a transient: 0x0 at rest,
  mass-reset to 0x0 by the game mid-session, and NEVER pulsed by player UI edits ⇒ **snapshot
  freshness / edit detection must diff `_schedule` array contents** (5 s cadence caught 3/3 live
  edits) — this replaces the packed-mismatch mechanism items above assume. Cohort grouping +
  sleep-overlap math validated on real painted staggers. Design refinements for Phase 1: while in
  the Manual baseline the mod should write NOTHING (the game executes painted schedules natively —
  confirmed); write only on mode exits (adopt-sleep collapse, emergency/fill Leisure) and write the
  painted array back on return; shutdown RestoreAll should pack the stored `_scheduleHours` array
  (packed `_originalSchedule` snapshots are 0x0 and restore nothing — this is also why worlds
  played under the ACTIVE mod have uniform saved schedules). ⚠️ softened risk: a painted-W night
  was worked straight through (villager had a midday sleep block) — the "wake night guards from
  game-forced sleep" problem may only exist for rest-depleted villagers; verify during Phase 1.
- **Phase 1 COMPLETE — confirmed in-game 2026-07-09 (cook cohort; other workstation types assumed to track):**
  v1.4.0–v1.4.4 shipped as designed with four deltas: (a) `ManualWorkIsInviolable` dropped — cohort on-windows already forbid
  discretionary off-post, only the critical emergency pierces; (b) snapshot freshness = 5 s array-diff
  (per Phase 0), moved out of the diag gate; (c) the top-up sleep is BACK-loaded (timed via the boost's
  refill rate to end at shift start), not front-loaded — off-windows are disjoint per cohort so
  placement is coverage-neutral, and leisure drains rest ~1 h/game-h (front-load left a 12 h shift
  ending at rest 0.4); (d) the qualifier requires a real MIXED paint (uniform all-S saved schedules are
  legacy collapse junk, not player intent). Boost rates scale by `WeatherSystem.TimeSpeedMultiplier`.
  Builder-fill (Phase 2) and the schedule-UI overlap warning (Phase 3) remain open; Phase 1's log-only
  overlap warning ships in `RefreshCohorts`.

- **Phase 1.5 (first slice of Phase 2) — v1.5.0 shipped (2026-07-09), `[DynamicNeeds] OffWindowFill` enum:**
  new Leisure | Work | Builder config option (default Leisure) controls off-window surplus fill for 2+-cohort villagers.
  `OffWindowFill=Work` (opt-in over-manning) confirmed in-game (2026-07-09): after the one back-loaded top-up sleep,
  qualifying villagers resume their post during off-hours when rested, bounded by the mode's base happiness-leisure safety
  valve.

- **Phase 2 COMPLETE — v1.6.0–v1.8.0 shipped (2026-07-10), `OffWindowFill=Builder` + schedule preservation + UI display:**
  `OffWindowFill=Builder` (unassigned-labor lend/restore via agent registry) confirmed in-game (2026-07-10): the recipe (v1.6.0 research, v1.7.0 implementation) is `home.ReleaseTaskAgent(agent)` + `bs.SetTaskAgent` + `bs.AddToTaskDatas` + all-Work schedule; 227 lend/restore cycles verified, zero failures. v1.7.0–v1.7.1 adds `PreserveScheduleInSaves` Harmony mask on `Villager.Serialize` to prevent quit-to-menu collapse-bake (confirmed root cause of schedule destruction). v1.7.2 adds universal mode-independent player-edit adoption + Buildstation exclusion from cohorts. v1.8.0 adds `ShowIntendedScheduleInUI` Harmony masks on `VillagerSchedulePanel` Set/Refresh/OnEnable/HasUnappliedChanges so the UI always shows painted schedules, not the mod's temporary collapses (three false bug reports fixed). Phase 3 (schedule-UI overlap warning) remains open — the user's requested next feature (see Phase 3 details below).

- **Roadmap (2026-07-10 update):**
  Goal ledger: (1) automate everything = the base mode ✅; (2) no-overlap split schedules: 2a off-duty leisure ✅ (Phase 1), 2b off-duty productive/builder ✅ (Phase 2, v1.8.0), 2c off-duty back-to-work-after-needs ✅ (v1.5.0 `OffWindowFill=Work`); (3) stretch, SEPARATE-MOD idea: demand-driven crafting prioritization (see new idea 12).
  Nexus-derived enhancements (open, not committed):
  - **Skip builder lend when Buildstation has no pending projects:** lent villagers acquire the Buildstation complain quest → idle barks ("It's time to build something") when no work queues exist. Readable gates: `buildProjects`/`repairProjects` non-empty. Candidate: skip the lend entirely when both are empty (or lend only when projects exist) to avoid idle-builder noise.
  - **Idle→leisure default with per-station leisure concurrency cap — FLAGGED GOOD:** workers idle because no work exists
    (builders with nothing queued, full inventories, completed bills) default to leisure instead of loitering; cap concurrent
    leisure per station (e.g. 4 assigned → max 3 on leisure) so a sudden job always has someone present. Machinery echoes DVN's
    existing same-station cohort grouping, so cheaper to build than it sounds. Phase-2-adjacent.
  - **Runestone recreation:** DVN-boosted villagers are so efficient their leisure is short/rare → they stop meditating at
    runestones → runestone shaman-XP loop unused. Existing knobs already tune this (lower `LeisureHoursToFullHappiness` toward
    0 = vanilla rate → longer leisure; thresholds). Candidate: minimum leisure duration or "prefer recreation structures," NOT
    forced visits.
  - **Outhouse disuse:** probably the same efficiency root cause, but WHICH need drives outhouse trips is unknown — investigate
    before designing.

---

---

## 12. Demand-driven crafting prioritization — new mod (planned, fully plausible — research 2026-07-09)

**Goal:** automatically adjust recipe priorities based on downstream demand — e.g. armorsmith missing iron plates → plates jump the metalworker's queue; archers need arrows → arrows jump for craftspeople; conflict resolution TBD.

**Source:** Nexus discussion on DynamicVillagerNeedsMod (2026-07-09), user suggested as a SEPARATE MOD (not baked into DVN). Captures the "optimize villager labor" spirit but via a different lever: instead of schedule/rest/needs automation, tune the recipe *dispatch* priority to match the game's current bottleneck.

**Verdict:** fully plausible — every link confirmed native and structured (no guessing needed).

**Core machinery:**

**F1 — Villager complaint system [Cecil, 2026-07-09].** `SSSGame.AI.Complaint` (Il2CppSystem.Object-derived) is a typed, structured system behind villager "barks"/exclamation-point statuses. Per-villager: `VillagerSocial.complaints : List<Complaint>`, `AddComplaint`/`_AddComplaintInternal` (candidate Harmony postfix capture point), `Rpc_AddComplaint`/`Rpc_RemoveComplaint`, `OnComplainChange : Action` (do NOT subscribe — IL2CPP Action gotcha; poll or patch instead), counts `ImportantComplaintsCount`/`NotificationComplaintsCount`/`MenuNotificationComplaintsCount`. Per-complaint flags `important`/`notification`/`forcedMenuNotification` = UI alert tiers. Typed payload subclasses carry the missing item directly: `ItemComplaint.itemInfo : ItemInfo`, `ItemManifestComplaint.itemManifest : ItemManifest` (+maxItemCount), `ItemCategoryComplaint.itemCategory`, `ComplexCategoryComplaint` (itemA/itemB/category), `LoadoutsComplaint.missingItems : List`, `StructureComplaint._structure`. ~120 named format-string keys on Complaint cover every station (`c_defender_needAmmo_format`, `c_buildstation_noSupplies_format(Single)`, `c_crafting_noPartsFound_format`, `c_farming_noSeeds_format`, `c_item_needItem_format`, etc.). Settlement-wide aggregation exists too: `SettlementIssueTrackerWidget` (maps for marker/storage-full/mine-exhausted/no-crafting-station/no-production/farm complaints) + `SettlementIssuesTabPage`. Every station type owns a typed complain quest (`CrafterComplainQuest`, `BuildstationComplainQuest`, `CookingComplainQuest`, `ForesterComplaintQuest`, …) + `complainBehaviour : vFSMBehaviour`; `QuestPriority` has `c_ComplainHigh`/`c_WorkstationComplain`/`c_ComplainLow` tiers. For subclass identification use `TryCast` (works on Il2CppSystem.Object-derived types per the QuestData precedent).

**F2 — Workstation task priority machinery [Cecil, 2026-07-09].** `SSSGame.AI.WorkstationTaskData`: `itemInfoQuantity : ItemInfoQuantity` (the task's item + QUOTA), `priority : Int32` + `SetPriority(Int32)`, `pinnedTask : Boolean`, `VillagersInCharge : List`, `onDataChanged : Action`, `GetQuantityRange()`. Priority is SERIALIZED into saves (keys `c_taskDataPriority` on CraftingStation/CookingStation/AnimalPen; `c_taskPriority` on Buildstation/ResourceStorage; `c_taskPriorityKey` on FarmingStation) — a modified priority PERSISTS across save/reload. Native co-op path: UI `WorkstationMenu.IncreaseTaskPriority/DecreaseTaskPriority(TaskDataPanel)` → `TaskDataPanel._SetPriority(WorkstationTaskData)` → **`NetworkWorkstation<T>.Rpc_ChangeTaskPriority(Int32, Int32)`** + `OnTaskPriorityChanged(WorkstationTaskData)` callback (args unverified ⚠️). A separate `WorkstationTaskPriority` type exists (seen on `StorageSupply.defaultTaskPriority`) — [likely] the High/Medium/Low tier enum used by gathering stations. Item→producer lookup: `CraftingStation.KnowsBlueprintForItem(ItemInfo, out CraftBlueprint)`; also `_outsourcedQuests`/`_outsourcedBlueprints` dictionaries and `GetPersonalFetchDepth(Villager, ItemInfo, out minDepth, out topPriority)` exist on CraftingStation (fetch-chain plumbing, unexplored). `BuildstationQuest.c_TaskPriorityCoefficient : Single` hints priority feeds quest-priority math.

**F3 — Two priority mechanisms + the starvation loop [in-game, confirmed in-game (2026-07-09)].** (a) CRAFTING stations use an absolute RANKED LIST with a per-task QUOTA measured against the LOCAL station inventory: worker fills rank 1's quota locally, moves to rank 2, etc., and snaps BACK to an earlier rank whenever its local count drops below quota. (b) GATHERING stations (woodcutter, forager, stonecutter, …) use High/Medium/Low TIERS per eligible resource + quota: all highs satisfied locally → mediums → lows. (c) Priority is effectively WINNER-TAKE-ALL, not a soft weight: one task set higher monopolizes the worker (a woodcutter with only long-hardwood-stick=high did nothing else; a warehouse with one raised collect-priority let everything else run dry). (d) The persistent-monopoly mechanism is the QUOTA-vs-LOCAL-INVENTORY + WAREHOUSE-HAULER-DRAIN loop: haulers carrying output away keep the quota unmet, so the top task never completes (intended vanilla design). Type mapping [Cecil-corroborated]: rank = `WorkstationTaskData.priority : Int32` (one-step UI moves); quota = `itemInfoQuantity`; tier = [likely] `WorkstationTaskPriority`.

**F4 — Idea-12 approach design (session synthesis, 2026-07-09; no code, nothing built).** Chain: complaint event → missing ItemInfo → find station(s) with an EXISTING matching task (never create tasks — user scope guard) → temporary priority boost through the game's own RPC → revert. Design decisions: (1) transient boost is the feature (winner-take-all makes "drop everything briefly" work); REVERT is the safety-critical core AND the normal termination path (hauler drain means a boost never self-terminates at the station; only complaint-clear or window-expiry end it); (2) stranded-boost risk: since priorities persist in saves, Phase 1 needs a mod-side per-world store of original priorities restored at world load + a hard max-boost-duration; (3) duty-cycling (boost N minutes → restore → cooldown → re-boost if complaint persists) = soft-priority behavior without patching game code; (4) same-station conflicts (e.g. archers need arrows + farmer needs hoes from one workshop): per-station micro-scheduler — ONE active boost, queue ordered by `Complaint.important` then FIFO, time-slice rotation until all clear; optional later: shortest-job-first via demand quantities (manifest/loadout complaints carry counts); (5) TWO bump/revert flavors needed: rank-move (crafting) vs tier-set (gathering) — Phase 0 checks whether Rpc_ChangeTaskPriority serves both; (6) QUOTA is a second candidate lever — a demand-derived temporary quota could make boosts self-limiting; (7) stretch/maybe-separate-mod: "priority softening" — patch the task-selection logic into weighted/round-robin — deeper+riskier, duty-cycle achieves most of it externally. Phasing: Phase 0 read-only diagnostics (log complaints as they arrive w/ payloads; log task datas incl. priority ints/tiers/quotas on both station kinds; locate the task-selection method; verify AddComplaint patch fires, TryCast subclasses, Rpc args) → Phase 1 auto-bump w/ duty-cycle + revert + per-world originals store (solo/host) → Phase 2 config polish (which complaint classes act, window/cooldown, magnitude). Open unknowns list: priority Int32 rank-vs-index semantics; Rpc_ChangeTaskPriority args; complaint add/remove cadence (hysteresis tuning); AddComplaint AOT-inlining; NetworkWorkstation<T> base-chain reachability from station wrappers; TryCast in practice.

---

## 13. Outhouse composter — repurpose the Outhouse as a compost bin (new mod, research 2026-07-10)

**Goal:** throw vegetables/seeds (and other food) into the Outhouse's storage; after they rot they
become **compost** usable on farm plots. The compost-bin fantasy WITHOUT new assets (author
constraint: logic/code changes only, no asset generation) — reuse the existing Outhouse structure as
the container shell. Per the Nexus thread: reserve the outhouse's own function, let the other storage
capacity accept food/seeds, optionally speed their spoilage inside (but the resulting compost's own
rotting speed stays default), and make the storage bigger where safe.

**Source:** Nexus discussion (rondi112 + author, 2026-07-10). rondi112's original ask was a new
compost-bin structure; the author steered to repurposing an existing structure. rondi112 proposed the
Outhouse specifically (always built near the farm anyway). Author's scoping in-thread, now
Cecil-corroborated (see MP caveat below): stack-size increases are doable and "safe to a value";
adding storage-grid squares is problematic for multiplayer + villager interaction ⇒ treat
grid growth as **singleplayer/host-only**, stack scaling as the safe axis.

**Verdict: fully plausible, and cheaper than it looked — BOTH halves of the loop are native systems:**
the game already has a complete item-decay pipeline (food rots into per-item "junk" outputs) AND a
complete farm-compost pipeline (compost items, per-cell compost volume, villager + player application).
The mod is plumbing between two existing systems, not a new mechanic.

**Key API (confirmed from binary, 2026-07-10):**
- **Native compost system (output side — the "fertilizer" already exists):**
  `SSSGame.FarmGridCellData` — `_currentCompostVolume`/`MaxCompostVolume`/`CurrentCompostRatio`,
  `HasEnoughCompost()`, save key `c_CurrentCompostVolumeKey` (per-cell compost volume, persisted).
  `SSSGame.FarmCrop.AddCompostVolumeToCell(Vector3, Single)` / `AddCompostVolumeToAllCells(Single)` +
  networked `Rpc_AddCompostToCell(Int32, Single)` / `Rpc_AddCompostToAllCells(Single)`.
  **`FarmingStation.composts : ItemInfoList`** (villager-side: which items ARE compost) and
  **`FarmCropInteraction.compatibleComposts : ItemInfoList`** + `CheckCompost(ItemInfo)` (player-side
  manual application). `Complaint.c_farming_needCompost` (ties into the idea-12 complaint system);
  debug helper `SettlementDebugAddon._CompostAllCrops()`. ⇒ The output item needs only to be an entry
  of those lists — **no new item asset**; Phase 0 logs the lists' contents to learn its name.
- **Native decay/spoil system (input side):** `SSSGame.ItemDurablilityProcess : ItemProcess` —
  `category : DecayMode { INVENTORY_AND_WORLD, ONLY_WORLD, EQUIPPED }`, `decayMultiplier`,
  `badWeatherMultiplier`, `Run(Item, Single&)`, `_BreakItem(Item)`, `fallbackJunkItem : ItemInfoQuantity`;
  subclass `ContainerItemDurabilityProcess` (items INSIDE containers decay too — own `_BreakItem`).
  **Per-item conversion table: `SSSGame.ResourceInfo.junk : ItemInfoQuantity[]`** (also
  `EquipmentItemInfo.junk`) — a fully-decayed item transmutes into its junk item(s)+quantity natively.
  Decay-pace levers: `ItemInfo._decayRateAttrData : AttributeData`, property ids
  `ItemsConstants.c_Decay / c_DecayRate / c_DecayProtection / c_DecayMultiplier`,
  `ItemDurablilityProcess.checkDecayRateMultiplierOnItem`, `Item.SyncDecay()` (+ network
  `ItemDecaySyncProcess`). `CrockpotInteraction.junkRecipe` (overcooking precedent).
- **Storage/container machinery:** `SandSailorStudio.Inventory.ItemContainer` — **`capacity : Int32`**
  (slot count, plain int on the instance), `containerType : ItemContainerType`, `CanStoreItemType(ItemInfo)`,
  `Check(ItemInfo)`, `GetStackSize(ItemInfo)`, `AddItems(ItemInfo, Int32)`, `RemoveItem`, `GetItems()`,
  `IsHidden` / `NoActionTargets` flags (candidate way to keep settlement AI's hands off the contents).
  `ItemContainerType : ScriptableObject` — `storageClasses : StorageClassData[]` where
  **`StorageClassData { storageClass : ItemStorageClass; stackSize : Int32 }`**, plus
  `overrideStackSizes` / `overrideWithMaxSize` and an `attributes` property array (candidate carrier of a
  container-level decay-rate/protection property — semantics ⚠️). **`ItemInfo.storageClass : ItemStorageClass`**
  (one tag SO per item) — acceptance = does the container type list your class ⇒ both an asset-edit lever
  (add food/seed classes to the outhouse's type, TerrainLeveler/idea-3 asset-edit precedent) and a patch
  lever (`CanStoreItemType`/`Check` prefix scoped to the outhouse's container instance).
  `ItemContainerComponent : MonoBehaviour` bridges container↔prefab (`DepositItems`, `TryAddOneItemOrDropNearby`).
- **The multiplayer caveat is visible in the binary:** the networked storage family is
  `SSSGame.Network.NetworkSimpleResourceStorage_2/_3/_4/_5/_8/_12/_22/_42/_45` (+ `NetworkCompositeResourceStorage`)
  — **slot counts baked into compile-time Fusion network-state types**. Growing a grid past its network
  struct cannot sync ⇒ capacity growth is host/solo-only by construction; stack-size scaling doesn't
  change slot count and is the co-op-safe axis. (Corroborates the author's in-thread assessment.)
- **No `Outhouse` type exists** — the outhouse is a generic `Structure` template (name confirmed via
  `ReligionOutlet.c_bless_Outhouse`); its villager-visit function is some interaction component on the
  prefab (which need drives visits is still unknown — same open question as the DVN "outhouse disuse"
  note in idea 11's roadmap). The mod doesn't need to touch that function — only the storage side.

**Approach (phased — transmute by mod timer first, native decay as a flavor later):**
1. **Phase 0 (read-only diagnostics, default true):** aim-at/interact logging for the Outhouse
   structure: walk its components and log whether it has an `ItemContainer`/`ItemContainerComponent`
   at all (⚠️ biggest unknown — decides the container strategy), its `containerType` name +
   uniqueness (is the ScriptableObject shared with other structures? decides asset-edit vs
   instance-patch), `capacity`, storage classes + stack sizes, and any Network*ResourceStorage backing.
   Also log: contents of `FarmingStation.composts` + `FarmCropInteraction.compatibleComposts` (what IS
   compost — item name(s) for the config default); `ResourceInfo.junk` + decay attribute for a sample
   of vegetables/seeds/fish/meat (do foods already rot into something compost-compatible natively? do
   seeds decay at all?); and whether items sitting in a structure container visibly tick decay
   (`ContainerItemDurabilityProcess` reachability).
2. **Phase 1 (the feature, host/solo-authoritative):** make the outhouse container accept food+seeds
   (asset-edit its `ItemContainerType.storageClasses` if unique, else patch `CanStoreItemType`/`Check`
   per-instance) with a configurable stack size (`StorageClassData.stackSize`, "safe to a value" —
   default modest). **Conversion = mod-side timer, not native decay:** a throttled poller (existing
   DayTracker/world-clock pattern — `WeatherSystem.TimeOfDay`/day count, runtime-proven in DVN/TimeWarp)
   snapshots the outhouse container's stacks and, once a stack has sat N in-game days (config
   `DaysToCompost`, the "adjust the spoil rate" knob without touching global decay), replaces it via
   `RemoveItem` + `AddItems(compostItem, n × yieldRatio)`. Compost output item = first entry of
   `compatibleComposts` (config override by name). The compost item itself is untouched — its rotting
   speed stays vanilla, exactly as requested. Timer approach also covers **seeds** even if they have no
   native decay attribute. Guardrails: host-authority gate; set/verify `NoActionTargets`/`IsHidden` (or
   equivalent) so cooks/haulers/hungry villagers don't raid or stock the rot bin (⚠️ verify villagers
   actually ignore it — the author's "villager interaction" worry); persistence of "sat N days" across
   save/reload via re-derivation (age unknown after load ⇒ restart the clock — document; a per-slot
   day-stamp store in the mod's own per-world file is the upgrade if that annoys).
3. **Phase 2 (bigger grid, config-gated, host/solo only):** bump `ItemContainer.capacity` for the
   outhouse instance. ⚠️ verify: UI grid panel copes with extra slots; serialization round-trips them;
   any Fusion storage backing tolerates it (if the outhouse container turns out network-backed, cap
   capacity at the baked slot count and rely on stack scaling instead). Hide/disable in co-op sessions.
4. **Phase 3 (optional native-decay flavor):** let the game's own rot do the work instead of the
   timer — edit accepted foods' `ResourceInfo.junk` to a compost-compatible output and/or patch
   `_BreakItem` scoped to items inside the outhouse container. NOTE: `junk` edits are global (food
   rotting ANYWHERE would yield compost — arguably a feature, definitely a design decision), and
   `_BreakItem` on a possibly-inlined small method needs fire-verification. Only pursue if the timer
   version feels artificial.

**⚠️ verify / risks (Phase 0 resolves most):**
- Does the vanilla Outhouse actually have an item container/storage grid? (The thread assumes yes;
  nothing in the binary proves it.) If not: nearest fallback shells with confirmed storage are any
  small `ResourceStorage` structure — or attach our own `ItemContainer` to the outhouse structure
  (heavier, new serialization surface — prefer a shell that already has one).
- What the native compost item(s) are, and whether food already rots into one natively (if vanilla
  `junk` already yields compost-compatible output, Phase 1 shrinks further).
- Villager/AI interaction with a food-holding outhouse container (raiding cooks, haulers stocking it,
  outhouse-visit function confused by contents) — the `NoActionTargets`/`IsHidden` flags are the
  candidate mitigation, semantics unverified.
- Stack-size "safe value" ceiling (author's own caveat): villager carry/haul math and UI stack display
  with big stacks — start small (e.g. 2–4× vanilla), test up.
- Decay/property semantics (`c_DecayProtection` vs `c_DecayRate` vs `c_DecayMultiplier`, container
  `attributes`) only matter for Phase 3 — the Phase 1 timer sidesteps them entirely.
- Co-op: all writes host-gated; stack scaling is the only axis exposed in co-op; capacity growth
  hidden/disabled there (network-struct evidence above).

---

## Cross-cutting notes
- Every new lever above is host-authoritative: gate on authority, prefer the game's own
  networked methods (`ReplenishCharges`, `Revive`, `RemoveObjectFromWorld`).
- All new diagnostics configs default **true** until the mod is verified, then flip to false (project rule).
- Fire-verify every new Harmony patch with a log line (inlining gotcha) before trusting it.
