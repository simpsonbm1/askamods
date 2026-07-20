# New Mod Ideas — Approaches

Research file for candidate mods: per-idea goal, Cecil-confirmed API leads, approach, and risks.
Type/member signatures were confirmed from the interop binaries via Mono.Cecil at research time —
signatures are facts; *runtime behavior* claims are **⚠️ pending in-game verification** unless
marked otherwise. Most ideas originate from Nexus user feedback (credited per idea).

**Open priority (user):** 5) freezing hunters → 3) crafting multiplier.

**Shipped/built ideas — research retired, self-documented in `docs/mods/`:**
- Idea 2, monster/beast den respawning → **SHIPPED** as DenRespawnMod —
  [`docs/mods/den-respawn.md`](docs/mods/den-respawn.md).
- Idea 4, ground-item vacuum → **SHIPPED** as GroundItemVacuumMod —
  [`docs/mods/ground-item-vacuum.md`](docs/mods/ground-item-vacuum.md).
- Idea 6, recipe/fish task unlock → **SHIPPED** as TaskUnlockerMod —
  [`docs/mods/task-unlocker.md`](docs/mods/task-unlocker.md). NOTE: the plan's original model was
  only half right — cooking is IDiscoverableItem/Rpc_AddDiscoverable, but **fishing tasks are
  gated by MARKED fishing grounds, not item discovery** — corrected mechanism in
  [`docs/architecture.md`](docs/architecture.md) → "Task Discovery & Bypassing".
- Idea 8, new workers start with zero tasks → **SHIPPED** as ZeroTaskWorkersMod —
  [`docs/mods/zero-task-workers.md`](docs/mods/zero-task-workers.md).
- Idea 9, mushrooms year-round/rain-independent → **SHIPPED** in TreeRespawnMod v1.4.7+ —
  [`docs/mods/tree-respawn.md`](docs/mods/tree-respawn.md). NOTE: the original research paired
  plain "Mushrooms" with Grey's gates — a conflation; plain Mushrooms is vanilla-ungated
  (corrected ground truth in the mod doc + architecture.md).
- Idea 10, inventory fish fillet (shift-click) → **SHIPPED** as FishFilletMod —
  [`docs/mods/fish-fillet.md`](docs/mods/fish-fillet.md).
- Idea 11, manual-schedule mode for DynamicVillagerNeeds → **SHIPPED** through DVN v1.9.x
  (Phases 0–2 + QoL complete) — [`docs/mods/dynamic-villager-needs.md`](docs/mods/dynamic-villager-needs.md).
  Only Phase 3 remains open — see the idea-11 section below.
- Idea 13, outhouse composter → **SHIPPED** as OuthouseComposterMod v1.0.0 (Phase 1, confirmed
  in-game 2026-07-12) — [`docs/mods/outhouse-composter.md`](docs/mods/outhouse-composter.md).
  Phases 2–3 unbuilt.
- Idea 15, villager ammo refund → **SHIPPED** as VillagerAmmoMod —
  [`docs/mods/villager-ammo.md`](docs/mods/villager-ammo.md). ⚠️ The research's recommended
  "refill-on-remove" approach was a FATAL dead-end — see the idea-15 stub below.

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

## 11. Manual-schedule mode (DynamicVillagerNeedsMod) — SHIPPED except Phase 3

**Shipped:** Phases 0–2 + the QoL round are COMPLETE and confirmed in-game (DVN v1.3.x–v1.9.x,
2026-07-09/10): `RespectManualSchedule`, cohort-scoped off-window confinement, back-loaded top-up
sleep, `OffWindowFill` (Leisure|Work|Builder incl. builder-loan), per-station `Name:Fill` syntax,
three-brush semantic, save/UI schedule masking, universal player-edit adoption. Full recipe:
[`docs/mods/dynamic-villager-needs.md`](docs/mods/dynamic-villager-needs.md); schedule/quest ground
truth: `docs/architecture.md` → Villager Schedule / Needs / Happiness.

**Open — Phase 3, schedule-UI overlap warning (the user's requested next feature; mockup
2026-07-08):** when the player paints Sleep hours that overlap a same-station coworker's sleep
window, highlight the overlapping hours in the schedule panel and show e.g.
`SLEEP OVERLAP WITH COWORKER "ASA"` near Apply.
- Foothold (all runtime-proven): `SSSGame.UI.VillagerSchedulePanel` (`_v` = bound villager;
  `Set(Villager)`/`Refresh()`/`OnEnable()` patchable — Set and OnEnable fire-verified in-game
  2026-07-10; renders via `_schedulePixels`/`_scheduleTexture`); parent
  `ScheduleEditorMenu.SetupVillagers()`; DVN's `RefreshCohorts` already computes cohorts +
  pairwise sleep-overlap (the log-only warning already ships there); TerrainLeveler's build-menu
  work proves UI injection is doable.

**Open — Nexus-derived enhancement candidates (not committed):**
- **Skip builder lend when Buildstation has no pending projects:** lent villagers acquire the
  Buildstation complain quest → idle "time to build something" barks. Readable gates:
  `buildProjects`/`repairProjects` non-empty; skip the lend when both are empty.
- **Idle→leisure default with per-station leisure concurrency cap — FLAGGED GOOD:** workers idle
  because no work exists (nothing queued, full inventories, completed bills) default to leisure
  instead of loitering; cap concurrent leisure per station (e.g. 4 assigned → max 3 on leisure) so
  a sudden job always has someone present. Echoes DVN's existing same-station cohort grouping, so
  cheaper to build than it sounds.
- **Runestone recreation:** DVN-boosted villagers leisure so rarely they stop meditating at
  runestones (shaman-XP loop unused). Existing knobs already tune this
  (`LeisureHoursToFullHappiness` toward 0 = vanilla rate → longer leisure). Candidate: minimum
  leisure duration or "prefer recreation structures" — NOT forced visits.
- **Outhouse disuse:** probably the same efficiency root cause, but WHICH need drives outhouse
  trips is unknown — investigate before designing.

---

## 12. Demand-driven supply-chain autopilot — new mod (planned; design v2 2026-07-12; end-goal refined 2026-07-14)

**End-goal (user-decided 2026-07-14):** a steady-state flow autopilot. "Player creates buildings,
assigns workers, sets basic material floors; the mod works out the rest." Steady phases: Phase 2c
(episodic closed loop with rails), Phase 2d (continuous rate-based quota calibration), Phase 3
(player-declared floors + mod-owned continuous priorities + persistent auto-clear capacity verdict).

**Goal:** keep a settlement's whole supply chain running without micromanagement. The player's
manually-set tasks/quotas are the demand declaration ("keep this many on hand") and the player owns
total storage CAPACITY (villagers never build storage — hard non-goal); within that cap the mod
manages THROUGHPUT: adjusts crafting-task priorities to meet downstream demand, creates missing
crafting tasks for items that are called for but not being made, manages storage quotas/priorities
so materials flow without upstream clogs, and adjusts gathering tiers to feed it all. The
supply-chain health chain being serviced: (1) right materials collected in the right quantity →
(2) enough storage available for the needed materials → (3) tasks exist for everything that needs
producing → (4) in-demand products outrank low-demand ones → (5) finished products have storage
room to reach their consumers. Each question decomposes into sub-checks at implementation time —
handled structurally by the actuation-preconditions rule in F4, not enumerated upfront.

**Source:** Nexus discussion on DynamicVillagerNeedsMod (2026-07-09; user suggested a SEPARATE mod,
not baked into DVN). Design sessions: 2026-07-09 (F1–F3 machinery research + a boost-only design)
and 2026-07-12 (scope expanded to full supply-chain management after two in-game storage-flow
failures — design v2 below supersedes the boost-only synthesis).

**Verdict:** actuation is plausible — three levers (priority, quota, task-existence), each against
Cecil-confirmed machinery. The real risk is the CONTROLLER: closed-loop control over a
winner-take-all scheduler with four interacting actuators WILL oscillate unless the discipline
rules below are load-bearing.

**Motivating failure modes (confirmed in-game 2026-07-12).** A station stalls three ways:
- **missing input** — native complaints cover this (F1);
- **blocked output/byproduct** — hunting hut saturated with bone fragments while the warehouse's
  fragment allotment was also full → haulers stopped draining → hunters stalled → food shortage;
- **dead inventory** — cooking house filled with raw vegetables no cooking task consumed; foods
  WITH tasks were eaten first, then intake clogged. ⚠️ unverified whether ANY native complaint
  fires here (noticed via the shortage, not an alert).
Full detail: `docs/architecture.md` → Settlement Hauling → "Storage-flow failure modes".

**Core machinery (research 2026-07-09):**

**F1 — Villager complaint system [Cecil, 2026-07-09].** `SSSGame.AI.Complaint`
(Il2CppSystem.Object-derived) is a typed, structured system behind villager
"barks"/exclamation-point statuses. Per-villager: `VillagerSocial.complaints : List<Complaint>`,
`AddComplaint`/`_AddComplaintInternal` (candidate Harmony postfix capture point),
`Rpc_AddComplaint`/`Rpc_RemoveComplaint`, `OnComplainChange : Action` (do NOT subscribe — IL2CPP
Action gotcha; poll or patch instead), counts
`ImportantComplaintsCount`/`NotificationComplaintsCount`/`MenuNotificationComplaintsCount`.
Per-complaint flags `important`/`notification`/`forcedMenuNotification` = UI alert tiers. Typed
payload subclasses carry the missing item directly: `ItemComplaint.itemInfo : ItemInfo`,
`ItemManifestComplaint.itemManifest : ItemManifest` (+maxItemCount),
`ItemCategoryComplaint.itemCategory`, `ComplexCategoryComplaint` (itemA/itemB/category),
`LoadoutsComplaint.missingItems : List`, `StructureComplaint._structure`. ~120 named format-string
keys on Complaint cover every station (`c_defender_needAmmo_format`,
`c_buildstation_noSupplies_format(Single)`, `c_crafting_noPartsFound_format`,
`c_farming_noSeeds_format`, `c_item_needItem_format`, etc.). Settlement-wide aggregation exists too:
`SettlementIssueTrackerWidget` (maps for
marker/storage-full/mine-exhausted/no-crafting-station/no-production/farm complaints) +
`SettlementIssuesTabPage`. Every station type owns a typed complain quest (`CrafterComplainQuest`,
`BuildstationComplainQuest`, `CookingComplainQuest`, `ForesterComplaintQuest`, …) +
`complainBehaviour : vFSMBehaviour`; `QuestPriority` has
`c_ComplainHigh`/`c_WorkstationComplain`/`c_ComplainLow` tiers. For subclass identification use
`TryCast` (works on Il2CppSystem.Object-derived types per the QuestData precedent).

**F2 — Workstation task priority machinery [Cecil, 2026-07-09].** `SSSGame.AI.WorkstationTaskData`:
`itemInfoQuantity : ItemInfoQuantity` (the task's item + QUOTA), `priority : Int32` +
`SetPriority(Int32)`, `pinnedTask : Boolean`, `VillagersInCharge : List`, `onDataChanged : Action`,
`GetQuantityRange()`. Priority is SERIALIZED into saves (keys `c_taskDataPriority` on
CraftingStation/CookingStation/AnimalPen; `c_taskPriority` on Buildstation/ResourceStorage;
`c_taskPriorityKey` on FarmingStation) — a modified priority PERSISTS across save/reload. Native
co-op path: UI `WorkstationMenu.IncreaseTaskPriority/DecreaseTaskPriority(TaskDataPanel)` →
`TaskDataPanel._SetPriority(WorkstationTaskData)` →
**`NetworkWorkstation<T>.Rpc_ChangeTaskPriority(Int32, Int32)`** +
`OnTaskPriorityChanged(WorkstationTaskData)` callback (args unverified ⚠️). A separate
`WorkstationTaskPriority` type exists (seen on `StorageSupply.defaultTaskPriority`) — [likely] the
High/Medium/Low tier enum used by gathering stations. Item→producer lookup:
`CraftingStation.KnowsBlueprintForItem(ItemInfo, out CraftBlueprint)`; also
`_outsourcedQuests`/`_outsourcedBlueprints` dictionaries and `GetPersonalFetchDepth(Villager,
ItemInfo, out minDepth, out topPriority)` exist on CraftingStation (fetch-chain plumbing,
unexplored). `BuildstationQuest.c_TaskPriorityCoefficient : Single` hints priority feeds
quest-priority math.

**F3 — Two priority mechanisms + the starvation loop [in-game, confirmed in-game (2026-07-09)].**
(a) CRAFTING stations use an absolute RANKED LIST with a per-task QUOTA measured against the LOCAL
station inventory: worker fills rank 1's quota locally, moves to rank 2, etc., and snaps BACK to an
earlier rank whenever its local count drops below quota. (b) GATHERING stations (woodcutter,
forager, stonecutter, …) use High/Medium/Low TIERS per eligible resource + quota: all highs
satisfied locally → mediums → lows. (c) Priority is effectively WINNER-TAKE-ALL, not a soft weight:
one task set higher monopolizes the worker (a woodcutter with only long-hardwood-stick=high did
nothing else; a warehouse with one raised collect-priority let everything else run dry). (d) The
persistent-monopoly mechanism is the QUOTA-vs-LOCAL-INVENTORY + WAREHOUSE-HAULER-DRAIN loop: haulers
carrying output away keep the quota unmet, so the top task never completes (intended vanilla
design). Type mapping [Cecil-corroborated]: rank = `WorkstationTaskData.priority : Int32` (one-step
UI moves); quota = `itemInfoQuantity`; tier = [likely] `WorkstationTaskPriority`.

**F4 — Design v2 (2026-07-12; no code, nothing built).**

*Scope contract (replaces the original "never create tasks" guard):*
- **Pinned tasks (`WorkstationTaskData.pinnedTask` — [likely] the native "don't touch" flag) and
  player-created tasks are SACROSANCT** — never edited, never deleted. The player's task list and
  quotas ARE the demand declaration the mod services.
- The mod only (a) temporarily boosts priorities/quotas with guaranteed revert, and (b) creates
  its own tasks — through the native add-task path only, and only for blueprints the station
  already knows (`CraftingStation.KnowsBlueprintForItem` ⇒ tech gating for free). Everything it
  does is recorded in the ledger (below). Dropping the old guard is justified because the game
  itself computes "item needed but no task exists" (the no-production / no-crafting-station maps
  in `SettlementIssueTrackerWidget`, F1) — the mod completes a loop the game already surfaces.
- Player owns capacity; mod owns throughput within it.
- **Inter-settlement market automation is OUT of scope** — transport latency wrecks the control
  loop (a boost whose effect lands minutes later, after a courier walk, invites oscillation and
  double-ordering). Stretch goal instead: a REPORT-only cross-settlement surplus/deficit pairing
  ("A has surplus X, B has a standing X deficit") — nearly free once per-settlement controllers
  exist, and the player sets the market task manually.

*Controller architecture:*
- **Per-settlement controllers, created LAZILY.** Every captured complaint/task/station event
  arrives on an instance that knows its owning settlement — look up or spin up that settlement's
  controller on first contact. Never enumerate settlements (`SettlementManager.settlements` stays
  null — universal gotcha); lazy creation also handles settlements founded mid-session. One shared
  scheduler round-robins all controllers (never N independent loops). All state keyed by stable
  IDs, never interop wrapper refs; everything drops on world-leave (`NoteWorldLeft` pattern).
- **Two planes** (the load-bearing structural decision):
  - **Reactive plane — push-based.** Harmony postfix on `AddComplaint` (+ task-change events)
    queues work; the controller tick drains it. Handles ACUTE failures. A storage-full complaint
    dirty-flags the station for a composition scan: the event says WHERE to look, the scan says
    WHAT is wrong (e.g. "full, but full of bones").
  - **Metabolic plane — a rolling composition sweep,** ~1 station per tick, full settlement
    coverage every 2–5 min, snapshotting per-item stack counts. The ONLY source for chronic
    problems: silent saturation (dead inventory fires no event) and ratio/rebalance decisions
    ("hunter hut should be 20% bones / 80% meat"), which need DERIVATIVES — fill/consumption
    rates from successive snapshots; no event can yield a trend. Polling is also FORCED here:
    patching methods with inventory-family params is a fatal native crash (universal gotcha,
    VillagerAmmo), so no event-driven path to item movement exists at all.
  - Snapshot store: ring buffer of the last K sweeps per station, stable-ID-keyed, dropped on
    world-leave (bounded memory).
- **Anti-oscillation discipline:** ONE arbiter per settlement — no independent per-feature logic
  (else: boost arrows → arrows flood storage → drain-boost → arrow complaint returns → re-boost,
  forever). Every actuator duty-cycled: boost N minutes → restore → cooldown → re-boost only if
  the complaint persists (revert is BOTH the safety core and the normal termination — hauler
  drain means a boost never self-terminates at the station). Hysteresis on all triggers.
  Same-station conflicts: ONE active boost per station, queue ordered by `Complaint.important`
  then FIFO, time-slice rotation until clear; optional later: shortest-job-first via demand
  quantities (manifest/loadout complaints carry counts).
- **Ledger (safety-critical — build BEFORE any actuation ships):** priorities persist in saves
  and mod-created tasks presumably do too, so a crash mid-management strands them. Per-world
  store of every original priority/quota plus every mod-created task (tagged as ours), restored
  or swept at world load, plus a hard max-boost-duration.

*Actuation levers (all through the game's own RPC paths, host/solo-gated):*
- **Priority**: rank-move on crafting stations (`Rpc_ChangeTaskPriority`) and tier-set on
  gathering stations (High/Med/Low — RPC unknown, Phase 0 target). Two flavors, per F3.
- **Quota** (`itemInfoQuantity`): first-class, and PRIMARY for warehouse allotments — the
  bone-fragment clog resolves by boosting the WAREHOUSE's collect task/quota for the clogging
  item. Demand chains run backwards through storage: the right boost target is often the hauler
  draining a clog, not any producer. Demand-derived temporary quotas also make boosts
  self-limiting.
- **Task creation** (native add path only): from no-production complaints; possibly also from
  derivable dead-inventory ("vegetables present + soup blueprint known + no soup task") even if
  the game never complains — Phase 0 tests whether the native alert fires there. Detection→alert
  ships before auto-create.
- Fallback if native-quota drain-boosting proves insufficient: the Mod 6 haul gate
  (`ResourceStorage.CanCreateStorageTaskForItemInfo`, `DYNAMIC_HAULING_HANDOFF.md`) is a
  confirmed chokepoint on the same pipeline — native-RPC route first.

*Actuation preconditions (generative rule — every lever fires only after its preconditions pass):*
each of the five chain questions decomposes into sub-checks at implementation time; rather than
enumerating them all upfront, the rule is: **before firing any actuator, verify the action can
take effect**, evaluated against the metabolic plane's snapshots (already on hand — no extra
scanning). Winner-take-all (F3c) makes this SAFETY-critical, not cosmetic: a precondition-blocked
boost actively harms — the monopolized worker idles on an impossible task instead of doing
anything useful. A failed precondition either REDIRECTS to the blocking problem or ALERTS when the
blocker is player-owned. Examples:
- boost a gatherer tier for item X → precondition: the station has intake room for X. Blocked by a
  byproduct → redirect to the drain lever (warehouse collect quota for the clogger), fire the
  original boost only once room exists. Blocked by genuine capacity → player-owned, alert.
- bump a crafting task → precondition: its inputs are present locally or haulable from somewhere
  in the settlement; missing inputs mean the real problem is upstream (redirect), not priority.
- create a task → station knows the blueprint AND has output room AND ingredients are obtainable.
Redirect chains are shallow by construction (~depth 2 — they bottom out at capacity, which the
scope contract assigns to the player); a redirect that can't resolve becomes an alert. Never
escalate or re-fire a boost whose precondition still fails.

*Case study — genuine gatherer starvation (co-op save, Phase 0 run 4, confirmed in-game
2026-07-12):* two dedicated ore miners cannot keep pace with iron consumption — Improved
Bloomery 4 sat at Coal x75 / Iron Ore x6 (composition sweep) while a bloomery worker complained
`ItemManifestComplaint: Iron Ore x4 + Metal Scraps x4`. No lever fixes this: the bottleneck is
real gathering capacity, not misallocation. Design requirements derived:
1. **Distinguish misallocation from capacity deficit.** Detection is metabolic-plane derivatives:
   sustained negative net rate (consumption > intake) for the item across N sweeps while the
   upstream gatherer lever is already at max — or a would-be gatherer boost fails preconditions.
2. **In capacity deficit, boost at most ONCE, measure, then stop and ALERT.** If the net rate
   doesn't improve after one boost cycle, mark the chain capacity-limited, revert, alert the
   player ("iron consumption exceeds intake; miners already maxed — add miners/mine or reduce
   consumers"), and cool down before re-evaluating. Winner-take-all (F3c) makes repeat-boosting
   an ineffective lever actively harmful — it monopolizes the miner on an unmeetable quota.
   Alert-only is the v1 behavior; demand-side throttling (deprioritizing the starved consumer)
   is a possible later refinement, not in scope initially.
3. **Complaint identity must be villager+type+payload, not the complaint id** — the game issues
   a FRESH id per re-complaint (observed: Heavy Pelt/Pelt complainants re-complained every ~3 s,
   ids incrementing 4→28), so id-keyed tracking makes a chronic need look like hundreds of
   transients. Hold-time accumulates across re-issues of the same identity.
4. **Controller ignore-list (non-supply keys, from field data):** feelUnsafe*, villager_homeless
   (sub-second churn blips), villager_jobless (≈ the builder pool — unassigned villagers park on
   the Buildstation), napTime/foodSafe/waterSafe/warmthSafe/restSafe transients, AttackTarget
   noise; `complaint.alarm` (held exactly 60 s) is the actuation-lockout signal, not demand.

*Performance contract (the stack is already CPU-bound — architecture.md "Mod-side frame hitches"):*
nothing per-frame. Controller tick every 5–10 s under a stopwatch budget (~2 ms), yielding
leftover work to the next tick (DVN cohort pattern). Static data cached ONCE at world load
(blueprint→station map via `KnowsBlueprintForItem`, recipe ingredient lists, station rosters),
invalidated on build/unlock events + world-leave. Composition scans read stack counts, never
per-item enumeration; dirty-flagged stations scanned promptly, everything else only via the slow
sweep. `[Perf]` instrumentation from Phase 0 so the cost is a measured number, not a suspicion.
Sizing reference: reactive + metabolic is an order of magnitude lighter than DVN's 10 s
all-villager ticks (worst cohort 28 ms).

*Phasing (each phase ships alone and burns down one unknown):*
- **Phase 0 — flow diagnostics (read-only), SHIPPED as SupplyChainMod v0.1.3** — all Phase 0
  unknowns answered in-game 2026-07-12; findings absorbed into `docs/architecture.md`
  (`docs/mods/supply-chain.md` for the diagnostic mod itself).
- **Phase 1 — crafting priority bumps — BUILT as SupplyChainMod v0.3.5** (1a spike + 1b
  complaint-driven controller), core in-game-verified 2026-07-12; see `docs/mods/supply-chain.md`.
- **Phase 2 — storage drain management** (warehouse allotment quota/priority; fixes the byproduct
  clog; the metabolic plane comes online here). Design refinements (user-confirmed mechanics,
  2026-07-13):
  - **Quota is an intake throttle, NOT an eviction lever.** Lowering a warehouse quota below the
    stored count removes nothing — intake just stops until consumption draws the count under quota.
    Consequences: (a) revert is always item-safe (nothing stranded/destroyed), but a boost's
    physical effect outlives the revert — the extra stock occupies warehouse capacity until
    consumed; (b) therefore CAP the boost delta to the clog size read from the composition scan,
    never "boost to lots"; (c) the mod cannot free warehouse space — physically-full warehouse =
    capacity boundary = alert, never actuate.
  - **Quota-raise effectiveness is priority-conditional (PROVEN INSUFFICIENT, 2026-07-14).**
    Raising a met quota re-opens the intake valve, but haulers act on it only if that collect
    task's priority beats other unfilled-quota tasks. Precondition check: log/inspect the collect
    task's rank vs. the set of other unfilled-quota tasks. Test run 3 exposed the real binding
    constraint: priority TIER (newly-built coal rows Med, shadowed by High-tier rows on the same
    warehouse). Quota-raise-only proved insufficient; **tier lever promoted per user decision**
    (design pivot v2, 2026-07-14) — soft quota budgeting + ground-drop eviction now core, tier
    lever for priority-shadow diagnosis + deliberate surge boosts. Warehouse PRIORITY moves stay a
    dangerous secondary lever (winner-take-all), but budgeting mitigates shadowing so tier becomes
    rare corrective not primary actuator.
  - **Test strategy (contrived clogs are expensive — planned around):** (1) the 2a detector is
    read-only and rides along passively in every session, validating against ORGANIC clogs;
    (2) the actuator micro-test needs no clog — any healthy settlement has (item X at a source
    station + warehouse quota for X met) pairs; the spike auto-selects one, raises the quota,
    and self-reports whether/when haulers respond (response latency sizes the controller's
    measure window); (3) full closed loop via a "clog forge" harness: clamp a byproduct's
    warehouse quota down to its current count (shuts the drain — the failure mode's exact cause),
    let production build the clog under TimeWarpMod, verify detect→drain-boost→recover→revert in
    one session. The forge doubles as a permanent regression test for controller changes.
  - Sub-phasing mirrors Phase 0→1a→1b: **2a** read-only warehouse dump (ResourceStorage task
    structure, quotas, supply state) + dry-run clog detector logging verdicts — **SHIPPED as
    SupplyChainMod v0.4.3, in-game-verified 2026-07-13** (three-way diagnosis working: drain
    candidate / capacity-blocked / priority-shadowed; facts absorbed into architecture.md +
    docs/mods/supply-chain.md); **2b quota-raise drain actuator + clog-forge harness —
    SHIPPED as SupplyChainMod v0.5.2, in-game-verified 2026-07-14** — quota write path proven
    (write `quantity`, call `_RefreshQuantityRange()`, then `HostUpdateTasks()`), hauler response
    latency 13 s (single datum), player deposits bypass quota, clog-forge end-to-end verified
    (clamp → storage-full → revert → target met). See `docs/mods/supply-chain.md` for Phase 2b
    recipe/config/findings. **2c** metabolic plane (snapshot ring buffer + net-rate derivatives)
    + clog state machine in the existing arbiter — **SHIPPED as SupplyChainMod v0.6.1,
    in-game-verified 2026-07-14** (runs 1–3; armed cycle fired run 3 mechanically but exposed
    wrong-lever finding; quota-raise retired as core lever; design pivot to soft budgeting +
    eviction + tier lever, see `docs/mods/supply-chain.md` Phase 2c findings + design direction);
    2d uses 2c's metabolic data for quota calibration.
- **Phase 2d — continuous SOFT quota budgeting (promoted to CORE of Phase 2, user-decided 2026-07-14):**
  Vanilla's default (every item's quota = unit physical max) means any single item crowds out whole
  storage. Mod counters by sizing per-item quotas from measured need (metabolic-plane rates) +
  headroom for boosts, with per-item caps below unit max; sum MAY exceed capacity (soft, keeps
  flexibility). Common failure: bone-fragment shape (low/no-demand item maxes storage). Remedy =
  quota REDUCE + NEW down-to-quota EVICTION routine: `ItemContainer.DropItem(...)` ground-drop
  (recipe runtime-confirmed 2026-07-15, v0.7.0 spike) (player X-press analog; GroundItemVacuumMod
  picks up); direct deletion deferred. Tier lever stays critical: diagnose/rebalance priority-shadow
  starvation + deliberate surge boosts (lever = direct SetPriority + HostUpdateTasks, runtime-confirmed
  2026-07-15; tier RPC inert). Budgeting mitigates shadowing: with sane quotas, High rows reach met
  and haulers proceed tier-down, so tier becomes rare corrective not primary. Bounds via
  `ItemCollection.GetMaximumStorageCapacity(ItemInfo)` (per-item physical max warehouse-wide).
  Cecil-confirmed primitives recorded in architecture.md (2026-07-14). Distinct from Phase 5
  (player-DECLARED stock targets): 2d derives targets from observed usage. Conceptually: shift from
  episodic reaction (Phase 2c) to continuous proactive flow balancing (2d). Builds on 2c's metabolic
  data. Fire-verify spike SHIPPED as SupplyChainMod v0.7.0, in-game-verified 2026-07-15 — both
  levers proven; core budgeting build started: dry-run brain (BudgetPlane) in v0.8.0, built
  2026-07-15, ⚠️ pending in-game confirmation.
- **Phase 2e — producer-side priority lever (research complete for mechanism, 2026-07-14):** a
  producer gathering bulky low-urgency items at equal priority with demanded items starves the
  demanded item via winner-take-all priority (e.g., mine hut Iron Ore / Large Rocks). No in-game
  lever exists — mod solution requires identifying competing items + reaching producer priority.
  **Mechanism ANSWERED (Cecil 2026-07-14):** CaveResourceStorage rows carry WorkstationTaskPriority
  TIER values (High=0, Med=1, Low=2, None=3), NOT rank-index. Station self-storages (Woodcutter's,
  Gatherer's, Outhouse, Stonecutter's) use the same ResourceStorage row machinery as warehouses.
  **Remaining opens:** reliable demand signal + lever design. Research before design.
- **Phase 3 — gathering tiers** (the second bump flavor).
- **Phase 4 — task creation** (requires the ledger hardened + the add-task RPC verified;
  dead-inventory detection→alert at minimum, auto-create where derivable).
- **Phase 5 — declared stock targets + BOM planner (optional endgame):** recursive ingredient
  decomposition from player-declared "keep N on hand" targets. Only if reactive mode leaves gaps —
  the complaint system already propagates demand implicitly (a missing downstream item eventually
  surfaces upstream complaints on its own). Recompute on slow sweep / config change only.
- **Stretch (post-Phase 2):** cross-settlement surplus/deficit report (see scope contract).
- Deferred idea from v1, unchanged status: "priority softening" (patch task-selection into
  weighted/round-robin) — deeper + riskier; duty-cycling achieves most of it externally.

*Open unknowns (Phase 0 + Phase 1 ANSWERED in-game 2026-07-12; Phase 2 additions 2026-07-14):*
**Answered:** `Rpc_ChangeTaskPriority` args (taskIndex, newPriority); gathering tier mechanism
(FiltersTaskData filters + Rpc_ChangeTaskFilters); native add-task path exists (Rpc_AddTask +
WorkstationMenu.AddTaskDataToWorkstation); no-production complaint payload (SettlementIssueTrackerWidget
map ItemInfo→needed-by string); storage-full fires (typed StorageFullComplaint + widget map);
complaint cadence (level signal, re-add ~50–100 ms, fresh id ~3 s, chronic 40–160 s vs transient
<2 s, alarm 60 s); AddComplaint not inlined; complaint TryCast works; CraftBlueprint ingredient
access (via _localBlueprints + CreateFromBlueprint; KnowsBlueprintForItem is a dead-end); sweep
cost measured (6–20 ms per 31 stations, 10.8 ms per 61 stations); priority Int32 rank-vs-index
semantics (= 0-based rank index mirroring list position, confirmed 2026-07-12); `NetworkWorkstation<T>`
reachability (GetComponent works; property lies, confirmed 2026-07-12); **storage priority
semantics (storage-family = VALUE tier, Cecil + in-game 2026-07-14); tier enum values
(High=0, Med=1, Low=2, None=3, confirmed in-game 2026-07-14); per-item capacity read
(GetMaximumStorageCapacity + per-container APIs, Cecil 2026-07-14); accepted-item enumeration
(GetAcceptedItemTypes, CanStoreItemType, container matching, Cecil 2026-07-14); drop-to-ground
eviction (Cecil 2026-07-14; runtime-confirmed in-game 2026-07-15, ⚠️ co-op client replication
unverified).** taskMaxQuota [likely per-spawned-task carry count per item-trip, irrelevant to
budgeting, Cecil 2026-07-14; supersedes "unknown"]; **tier lever runtime-confirmed (direct
SetPriority + HostUpdateTasks, synchronous, in-game 2026-07-15; Rpc_ChangeTaskPriority host-side
call INERT = dead-end); ground-drop eviction runtime-confirmed (spawn + exact decrement, solo
host, in-game 2026-07-15).**

**Still open:** whether mod-created tasks serialize like player ones; dead-inventory native alert
(untested; sweep-only detection assumed per design); research-task discriminator (no assigned
workers + TaskDataPanel `_bgStudyTaskColor` are markers; Phase 1b controller excluded research tasks
per user decision); untested Phase 1b edges (militia-alarm lockout no raid/test, StationBusy rotation
no same-station contention observed, capacity verdict via 2 ineffective cycles all real boosts
self-cleared early); ⚠️ ground-drop co-op CLIENT replication.

*Sequencing dependency:* do NOT start building while the stack's hitching investigation is open —
land the TreeRespawn v1.6.1 verdict and the `bisect-plugins.ps1 -DisableAll` FPS baseline first,
so this mod starts against a known-clean performance baseline.

Phase 2d demand-model redesign plan (approved 2026-07-15, supersedes earlier Phase 2d classifier
notes here): `SupplyChainMod/DEMAND_MODEL_PLAN.md`.

---

## 13. Outhouse composter — BUILT as OuthouseComposterMod v0.2.0 (⚠️ pending in-game confirmation); Phases 2–3 unbuilt

**Goal:** throw vegetables/seeds (and other food) into the Outhouse's storage; they convert to
**compost** usable on farm plots — the compost-bin fantasy without new assets. Source: Nexus
discussion (rondi112 + author, 2026-07-10).

**Status (2026-07-11):** Phase 0 diagnostics confirmed in-game; Phase 1 built as
**OuthouseComposterMod v0.2.0**, ⚠️ pending its in-game test — per-category independent repeating
REAL-TIME timers (food ratio 1:1 @ 5 min, seeds 20:1 @ 10 min defaults, full-ratio-or-skip,
output item 'Compost' added to the same grid, host/solo-gated, timers reset on load). Conversion
timers, acceptance patches, and the villager-raiding risk are all unverified. Container/acceptance
ground truth (unique containerType 'Storage_SmallItems_Outhouse', per-item acceptance beyond
storage class, capacity=20, no Fusion backing observed, the ItemContainer API surface, the
`GameObject.GetComponents(Type)` dead-end) is recorded in `docs/architecture.md` → "Storage
acceptance / the Outhouse container".

**Unbuilt Phase 2 (bigger grid, config-gated, host/solo only):** bump `ItemContainer.capacity` for
the outhouse instance. ⚠️ verify: the UI grid panel copes with extra slots; serialization
round-trips them. No Fusion backing was observed on the Outhouse container, but the general rule
stands: the networked storage family is
`SSSGame.Network.NetworkSimpleResourceStorage_2/_3/_4/_5/_8/_12/_22/_42/_45`
(+ `NetworkCompositeResourceStorage`) — slot counts baked into compile-time Fusion state types, so
grid growth past a struct cannot sync ⇒ host/solo-only by construction; stack-size scaling is the
co-op-safe axis.

**Unbuilt Phase 3 (native-decay flavor):** let the game's own rot do the conversion instead of the
mod timer. Native decay API (Cecil 2026-07-10, unused so far — also relevant to any future
rot/spoilage mod):
- `SSSGame.ItemDurablilityProcess : ItemProcess` — `category : DecayMode { INVENTORY_AND_WORLD,
  ONLY_WORLD, EQUIPPED }`, `decayMultiplier`, `badWeatherMultiplier`, `Run(Item, Single&)`,
  `_BreakItem(Item)`, `fallbackJunkItem : ItemInfoQuantity`; subclass
  `ContainerItemDurabilityProcess` (items INSIDE containers decay too — own `_BreakItem`).
- **Per-item conversion table: `SSSGame.ResourceInfo.junk : ItemInfoQuantity[]`** (also
  `EquipmentItemInfo.junk`) — a fully-decayed item transmutes into its junk item(s)+quantity
  natively.
- Decay-pace levers: `ItemInfo._decayRateAttrData : AttributeData`; property ids
  `ItemsConstants.c_Decay / c_DecayRate / c_DecayProtection / c_DecayMultiplier`;
  `ItemDurablilityProcess.checkDecayRateMultiplierOnItem`; `Item.SyncDecay()` (+ network
  `ItemDecaySyncProcess`); `CrockpotInteraction.junkRecipe` (overcooking precedent).
- Farm-side compost API (output side, confirmed from binary): `FarmGridCellData`
  `_currentCompostVolume`/`MaxCompostVolume`/`HasEnoughCompost()` (save key
  `c_CurrentCompostVolumeKey`); `FarmCrop.AddCompostVolumeToCell(Vector3, Single)` /
  `AddCompostVolumeToAllCells(Single)` + `Rpc_AddCompostToCell(Int32, Single)` /
  `Rpc_AddCompostToAllCells(Single)`; `FarmingStation.composts` /
  `FarmCropInteraction.compatibleComposts` (confirmed in-game: ['Spoiled Food', 'Compost',
  'Crawler Slime', 'Slag']) + `CheckCompost(ItemInfo)`; `Complaint.c_farming_needCompost`;
  debug helper `SettlementDebugAddon._CompostAllCrops()`.
- Caveats: `ResourceInfo.junk` edits are global (food rotting ANYWHERE would yield compost — a
  design decision, arguably a feature); `_BreakItem` is a small method — fire-verify against
  inlining. Only pursue if the timer version feels artificial.

---

## 14. Rocks-only remover — hotkey + radius around the player (research 2026-07-10)

**Goal:** remove ONLY rocks within a configurable radius of the player, on a hotkey — trees, terrain
height, buildings, and living things all untouched. For cleaning up rocks inside an already-built base,
where dragging a bulldozer field is impractical/destructive.

**Source:** Nexus comment on TerrainLevelerMod (ButtKoWitz, 2026-07-10): using the bulldozer
single-plot to pick off rocks in his main base, ">50% of the rocks that I try to remove can't be
removed without selecting a much larger area"; explicitly suggests "a hotkey based on the player's
position, search for rocks w/in nn meters." (Plausible cause of the >50% failure: the fixed rock's
data-layer instance origin sits outside a small plot's footprint even when the visible mesh overlaps
it — a radius query around the player sidesteps footprint games entirely.)

**Verdict: highly plausible — the core rocks-only behavior has ALREADY been observed in-game.**
During bulldozer development (confirmed in-game 2026-07-01, documented as the `skipAllCollisionChecks`
gotcha in `docs/architecture.md` → Terrain/Terraforming and `docs/mods/terrain-leveler.md`): an
`AOESpell` cast with `skipAllCollisionChecks = true` skips `CollisionCheck()` — the overlap pass that
gathers harvestable damage targets — so only the `clearItemInstances = true` data-layer pass fired.
Observed result: **fixed rocks cleared, trees survived.** For the bulldozer that was the bug to fix;
for this idea it IS the feature. No new Cecil dump needed — `SpellsManager` registry (Awake postfix),
`CastSpellOnPos(aoe, ref pos)`, and the `AOESpell` field recipe are all runtime-proven in shipped
TerrainLevelerMod code.

**Two rock kinds, two levers (per the bulldozer's confirmed clearing model):**
1. **Fixed "invincible" `WorldItemInstance` rocks** (the ones the vanilla level field can't remove —
   likely most of the complaint) → an `AOESpell` sized to the radius, centered on the player, with
   `clearItemInstances = true` and **no damage** (`dealDamageToHarvestables = false`, `baseDamage = 0`;
   `skipAllCollisionChecks = true` is the belt-and-braces confirmed-in-game variant of the same
   outcome). Dealing zero damage makes it inherently safe for the player/villagers/buildings/trees —
   none of the bulldozer's `BuildLivingExclusionMask` machinery is even needed.
2. **Harvestable stone clumps** (mineable rocks, e.g. `Harvest_Stone4` — Fusion scene objects) → NOT
   touched by `clearItemInstances`; they die to damage, but a damage AOE would hit trees too. Instead
   target per-object: OverlapSphere the radius → per-hit `GetComponent<HarvestInteraction>` (singular —
   plural generic is the documented dead-end) → classify rock vs tree by GameObject name
   (`Harvest_Stone*` vs `Harvest_Wood_*`) and/or `_worldInstance.TryCast<BiomeItemInstance>() == null`
   (non-biome = rocks per the Resource/Tree section) → call `HarvestInteraction.TakeDamage(DamageData)`
   (documented safe entry point, no ref params) with lethal damage. Per-object targeting means trees
   and buildings *cannot* be collateral, regardless of layer layout. ⚠️ constructing a valid
   `DamageData` for a direct call is unverified; fallback if it's awkward — a second, damage-dealing
   `AOESpell` with `damageMask` limited to the rock layer, IF Phase 0 shows rocks and trees live on
   different layers (the exact open question from the author's own Nexus reply).

**Approach (phased):**
- **Phase 0 (read-only diagnostics, default true):** hotkey logs everything in the radius — GameObject
  names, layers, `HarvestInteraction`/`WorldItemInstance` presence, biome vs non-biome. Answers: (a)
  are rocks/trees on distinct layers; (b) which rock varieties are actually in a base area (fixed
  instances vs harvestable clumps vs `"Small Stone"` gather pickups); (c) **what else
  `clearItemInstances` would sweep** — the top risk below.
- **Phase 1 (the likely 90% win):** hotkey (with the standard typing guard, per the 2026-07-10
  cross-mod convention) + `RadiusMeters` config → cast the zero-damage, `clearItemInstances`-only AOE.
  If the problem rocks are all fixed instances, this alone satisfies the request.
- **Phase 2:** harvestable clumps via per-object `TakeDamage` (or the layer-masked damage AOE if
  Phase 0 blessed it). Damage-killed clumps drop their normal stone loot — arguably a feature
  (and GroundItemVacuumMod picks it up).

**⚠️ verify / risks (Phase 0 resolves most):**
- **`clearItemInstances` selectivity is the big one:** trees surviving is confirmed, but trees are
  *biome* instances killed via damage — what the data-layer pass does to OTHER non-biome
  `WorldItemInstance`s in the radius (loose `Item_Wood_*` logs/debris, dropped items) is unknown. In
  the bulldozer everything got cleared anyway, so nobody could tell. If it sweeps loose items, either
  accept+document it, shrink via any filter fields `AOESpell` exposes (re-check its field list in
  Cecil), or pre-snapshot + re-drop. Also confirm gather nodes (berry bushes etc. — biome instances,
  so *expected* to survive like trees) really do survive.
- Whether a rock partially under a placed structure clears cleanly (fine — that's the use case) vs.
  destabilizes anything.
- Co-op: AOE/data destruction only replicates from the state authority — gate the hotkey action on
  host authority (Phase 1 = solo/host only). Unlike the bulldozer there's no networked structure whose
  replication a client press rides in on (a bare hotkey has no `TerraformingGridState` to postfix), so
  a client-usable version would need custom signaling — out of scope; document host-only.
- **Home:** either a TerrainLevelerMod feature (all plumbing already lives there, same Nexus audience
  asking) or a tiny standalone RockRemoverMod (for users who want rock cleanup without the bulldozer).
  Default lean: TerrainLeveler extension reusing the shipped `SpellsManager`/AOE code; user decides.

---

## 15. Unlimited villager ammo — SHIPPED as VillagerAmmoMod (confirmed in-game 2026-07-11)

Research retired. The shipped recipe (parameterless `RangedManager.Awake` capture +
`RealAmmoCount` baseline polling + `ItemContainer.AddItems` refund + recent-shooting grace
window), the stuck-arrow ground-item cull, and the target-helper census facts live in
[`docs/mods/villager-ammo.md`](docs/mods/villager-ammo.md) and `docs/architecture.md` → Villager
Ranged Combat / Ammo System.

**⚠️ Fatal dead-end, recorded so it is never re-tried:** this plan originally RECOMMENDED a
"refill-on-remove" Harmony postfix on `RangedManager._OnAmmoRemoved`. ANY Harmony patch on a
method whose signature contains inventory-family types (`Item`, `ItemCollection`,
`ItemEventContext`) hard-crashes the game at plugin load via too-early il2cpp class-init
(`coreclr.dll+0x1d1fdd`, no managed exception; bisect-proven 3×). Polling is the working
alternative — full evidence in the mod doc's Dead-end section and the universal gotchas.

---

## 16. Enemy-corpse vacuum — GroundItemVacuumMod extension (v1.2.1, ⚠️ pending in-game)

**Goal:** let the vacuum clear dead enemy bodies near the base. Killed enemies leave corpses that
must be "harvested" repeatedly to despawn; the user doesn't want the loot, just the pile gone.

**Verdict: feasible — enemy corpse = dead `Creature` instance, NOT `Character`
(Cecil-confirmed 2026-07-18; mechanism deployed in GroundItemVacuumMod v1.2.1).**
- Enemy corpse = the dead `Monster` (a `Creature` subclass; `Creature` is not a
  `Character`), lingering until despawn. No separate remains object.
- Player corpses use `SSSGame.CharacterRemains` (player/villager path); enemy
  corpses do NOT use this system.
- Identity via `Creature.GetTargetName()` for `ExcludeCorpseNames` substring
  matching; removal via `DespawnImmediatelyIfStateAuthority()`.

**Config-only path ruled out (in-game 2026-07-18):** user added `wight` to
`OnlyItems` near a wight-corpse pile — nothing swept. Reason: corpses are NOT
tracked as `DynamicItemObject`s. The code-based tracking (below) is the robust
path.

**In-game facts (user-confirmed 2026-07-18):**
- Enemy/animal corpses have **no map/objective marker**; **only player corpses**
  get one (the `CharacterRemains._marker` system is player-only).

**Mechanism (built as GroundItemVacuumMod v1.2.1):** track enemy corpses via
Harmony postfix on `Monster.Spawned()` + `Monster.Despawned()` prefix into an
own `HashSet` (world-leave clear; null-prune at sweep); sweep only `IsDead ==
true`; identity via `GetTargetName()` for `ExcludeCorpseNames`; removal via
`DespawnImmediatelyIfStateAuthority()`; `Den`/`Pet` excluded by construction
(never patched) — den despawn would permanently destroy the spawner.
`CharacterRemains`-based sweeping was removed (it could only ever match player
corpses, or villager corpses which must never be deleted). Same host gate,
DryRun handling, and world-leave clear as the existing item set. Corpses are
exempt from `OnlyItems`/`ExcludeItems` (debris filters). New config section
`Corpses/`: `IncludeCorpses` (default true during dev; flip to false before
Nexus ship — users shouldn't silently lose loot) + `ExcludeCorpseNames`
(substring list, empty default).

**Status:** built as GroundItemVacuumMod v1.2.1, ⚠️ pending in-game
confirmation (2026-07-18). Open ⚠️ items: does `DespawnImmediatelyIfStateAuthority()`
visually remove the body cleanly; are deer/boar `Monster` too.

---

## 17. Craft from settlement storage — new mod (research 2026-07-20)

**Goal:** crafting at a station table (armorer, metalworker, carpenter, …) draws on materials
sitting in ANY non-blacklisted settlement storage — counted as available AND actually consumed —
instead of requiring them to already be in the station's own bin or the crafter's inventory. Nobody
should have to go find materials and hand-carry them to the station first. **Applies to the player
and to villager crafters, via one independent config toggle each** (see Scope).

**Source:** Nexus comment (rondi112, 2026-07-20): "a mod that pulls resources from all storages?
Like crafting from chests."

**Scope: BOTH agents, one independent config toggle each** — **the player** at a station table, and
**villager crafting tasks** (so the player no longer manually stocks each station's material box).
Either toggle can be enabled alone. The villager half is core scope, not a stretch goal: manually
stocking station boxes is the tedium this mod exists to remove.

**Vanilla baseline — a villager crafter can only use its OWN station's storage** (user-confirmed
from gameplay 2026-07-20): the small/medium materials box associated with the armorsmith is what
gets used to make armor, not settlement-wide storage. ⚠️ Mechanism unconfirmed — candidates are
`CraftingStation.GetFetchDepth` / `GetPersonalFetchDepth(Villager, …)` (a bounded fetch reach) and
the vanilla per-building storage-access whitelist (architecture.md → settlement hauling), either of
which would keep a crafter out of warehouses.
**Dead-end guard:** the presence of `CrafterFetchQuest` / `FSM_FetchCraftingSupplies` in the type
list is NOT evidence that villager fetching reaches settlement-wide. Observed behavior is the
authority; do not conclude otherwise from the type surface alone.

**The two halves need different mechanisms.** For the player, an availability flip plus a
just-in-time transfer works: the player is at the table and the craft click is the trigger. A
villager consumes from its station bin, so the villager half must **physically move materials into
that bin** — an availability flip alone is actively harmful there, leaving the crafter believing it
can craft with an empty bin. Build order: the player half lands and is confirmed in-game before the
villager half turns on.

**Verdict: feasible — every needed primitive is either Cecil-confirmed or already in-game-proven
by other mods.** (Cecil pass 2026-07-20, `_explore/cecil_craft_from_storage*.ps1` + out files.)

**Cecil-confirmed ground truth (2026-07-20; runtime behavior ⚠️ pending unless noted):**
- **Family tree — one base class covers all craft tables:** `SSSGame.CraftInteraction :
  Interaction`; `AnvilInteraction : CraftInteraction`; `CarpenterInteraction : AnvilInteraction`;
  `DyeingInteraction : CraftInteraction`. (`ForgeInteraction : CookingInteraction` is
  cooking-family — out of v1 scope.)
- **Gate candidates on `CraftInteraction`** (sole declarations; no other type redeclares the
  first two):
  - `Boolean CheckOwnedRequirements(Blueprint bp, IInteractionAgent agent)` — public,
    NON-virtual → inlining risk, must fire-verify. Safe params.
  - `Boolean _CheckOwnedBlueprintManifest(ItemManifest, IInteractionAgent)` — protected
    VIRTUAL (vtable-safe). ⚠️ `ItemManifest` param is inventory-adjacent; not in the confirmed
    patch-crash family (`Item`/`ItemCollection`/`ItemEventContext`) but treat as the riskiest
    patch — if plugin-load hard-crashes, drop this one patch first.
  - `Void BeginCraftingSequence(InteractionSession)` — virtual; overridden by
    `AnvilInteraction` + `DyeingInteraction` (patch all three impls).
  - `Void _OnCraftingSuccess(IInteractionAgent)` — virtual; overridden by `DyeingInteraction`.
    Likely consumption site (see primitives below). `craftingEvents.onCraftSuccess : UnityEvent`.
- **Station-side state:** `inventory`/`blueprintInventory : InventoryComponent`,
  `useAgentInventory : Boolean` (some tables use the agent's inventory!), `ItemInventory :
  ItemCollection` (property), `ActiveBlueprint : CraftBlueprint` / `ActivateBlueprint(bp)`,
  static scratch `s_bpManifest : ItemManifest`.
- **UI chain:** `PlayerCraftInteractionSession` (`_OpenMenu`, `_BeginCraftInput(CallbackContext)`)
  → `SSSGame.UI.CraftMenu : ContextMenu` (`GetCraftStationInventory()`, `GetCraftInteraction()`)
  → `CreateItemsTabPage : TabPage` — recipe list built by async `_CreateItems()`; partitions into
  `_availableBlueprints`/`_unavailableBlueprints`, but its `_blueprintConditionsDatabase` +
  `_knowledgeManager` fields suggest that split is knowledge/unlock-based, with ingredient
  graying done per-button (`itemButton : ItemThumbnailPanel` — the class FishFilletMod already
  patches successfully) or in `ItemDetailsPanel`/`ItemManifestDisplayer`. ⚠️ which check drives
  the gray-out needs the Phase 0 live trace.
- **Inventory primitives (`SandSailorStudio.Inventory`):**
  - `ItemCollection.ContainsItemManifest(ItemManifest) : Int32` — how many times a manifest fits
    (= craftable count); `RemoveOwnedItemManifest(ItemManifest)` — manifest-shaped removal,
    the likely vanilla consumption call ("Owned" = clamped to what's present → **dupe risk**, see
    risks); `FillOwnedItemManifest(...)`.
  - `ItemManifest.Transfer(ItemCollection source, ItemCollection destination, Boolean copy)` —
    the game's own manifest-shaped bulk mover: THE just-in-time transfer primitive.
  - Settlement-wide counts + storage enumeration: the `Settlement.GetStructures()` walk with
    per-structure `GetComponent<ItemContainerComponent>()` (in-game-proven here and by
    OuthouseComposter/SupplyChain). **Not** `QuerySettlementResources()` — it hangs the game
    (see below) — and not `GetStorageSites()`, whose stub forces a scan-until-it-throws pattern.
  - Fallback movers if `Transfer` misbehaves: per-container `RemoveItem` + `AddItems`
    (in-game-proven by OuthouseComposter, host/solo).

**Runtime ground truth (Phase 0 spike, CraftFromStorageMod v0.1.1/v0.1.2, confirmed in-game
2026-07-20):**
- `CheckOwnedRequirements` **fires — it is not inlined** — and is the OUTER gate, calling
  `_CheckOwnedBlueprintManifest` internally. It is THE single availability lever.
- **Villagers ride that same gate** (`agent=Villager` observed on it). This is what makes the
  per-agent toggles implementable, and what makes an agent-blind patch dangerous: with the
  villager toggle off, every change must still test the agent.
- `useAgentInventory=True` on a workshop table — the station consults the AGENT's inventory.
- **The UI available/unavailable split is NOT ingredient-based** (`Available=96, Unavailable=0`
  while clicking unaffordable recipes): it is knowledge/unlock based. UI work must target the
  per-button gray-out, not this list.
- **`_OnCraftingSuccess` is not where ingredients leave `CraftInteraction.ItemInventory`**
  (PRE==POST across a real rope craft), so it cannot anchor transfer timing.
- **`Settlement.QuerySettlementResources()` HANGS the game** (AppHangB1) — a dead-end, no managed
  rescue possible. Use the `GetStructures()` walk. Full evidence: architecture.md → Player
  crafting pipeline.
- **Census (v0.1.2):** 417 structures → 141 containers, 13,630 items — the read half works.
  Container-type frequencies, biggest holders, and the never-drain set are recorded in
  architecture.md → Player crafting pipeline → "Settlement storage census ground truth".
- **Two census limitations to design around:** (1) first-container-per-structure MISSES real
  crafting-station storage (stations reported CharacterFlask / `?`) — enumerate all containers, or
  use `Workstation.GetInventory()`; (2) structure display names repeat across distinct buildings
  (141 containers → 91 distinct names) — key by world position, never name.

**Blacklist rule: structural, not name-based.** Skip containers reachable from an
`EquipPoint`/`EquipmentManager` rather than matching a hardcoded container-type-name list — a name
list built from one settlement would silently drain armor racks in worlds with buildings we never
saw. `EquipPoint` is **not** a MonoBehaviour (base `Il2CppSystem.Object`), so it cannot be reached
by `GetComponent`: walk `EquipmentManager.equipPoints : List<EquipPoint>` → `.ItemContainer`.
⚠️ Open: is `CharacterBuilder` (capacity=9999, on Improved Armorsmith 2) the armorer mannequin?
See architecture.md → Player crafting pipeline.

**❗ BLOCKER — the ingredient-consumption site is unidentified.** No Phase 1 code until it is
found: `RemoveOwnedItemManifest` consumes only what is present, so a mistimed transfer duplicates
items in a live save.
- **Ruled out:** `_OnCraftingSuccess` (above); `CraftInteractionDisplay._CraftRoutine` and its
  state machine (Cecil pass 5 — the type is purely cosmetic: `tableDisplaySlot`,
  `_leftHandDisplayObj`, `_ClearDisplayObjects`); `CraftingStation` (the villager-side
  fetch/craft/study quest manager, not the player craft path).
- **Leading candidate:** the agent's own collection via `IInteractionAgent.GetInventory() :
  ItemCollection` — consistent with `useAgentInventory=True`, and never sampled by v0.1.1, which
  watched only `CraftInteraction.ItemInventory`.
- **Cecil cannot settle this** — interop method bodies are trampolines, so there is no IL to read
  and no caller analysis available. It needs an in-game delta watcher over all candidate
  collections, or a Cpp2IL body dump.
- Note that Phase 1 does not strictly need the method's *name*: it needs to know the collection and
  whether consumption happens at or after `BeginCraftingSequence`. If it does, transfer there, then
  re-run vanilla's gate and abort on failure — fail-closed without identifying the caller.

**Approach (phased):**
- **Phase 0 — read-only diagnostic spike (CraftFromStorageMod v0.1.x): COMPLETE**, results above.
  **v0.2.0 (built 2026-07-20, ⚠️ unrun)** extends it to close the blocker: a craft **delta
  watcher** that arms on `BeginCraftingSequence` and samples six candidate collections (agent
  `GetInventory()`, station `ItemInventory`/`inventory`/`blueprintInventory`,
  `Workstation.GetInventory()`, `CraftingAgent.ItemInventory`) at 10 Hz for 20 s, logging only
  deltas plus a per-collection first-change/net-delta summary; census v2 (all containers per
  structure, structural `EquipPoint` probe, per-station stock +
  `GetItemsNeededFromSettlement()`); and a `CheckOwnedRequirements` call-frequency rollup that
  sizes the Phase 1 availability-postfix budget.

  **v0.2.0 test protocol (one session, ~5 min — run on either machine):**
  1. `git pull` + `.\sync-plugins.ps1` if this machine hasn't run v0.2.0 before. Launch, load the
     save, confirm `CraftFromStorageMod v0.2.0` in the log (or `.\check-loaded.ps1`).
  2. Craft one item you HAVE materials for at a normal crafting table; **stand still until the
     craft finishes AND ~20 s have elapsed**, so the `WATCH SUMMARY` block prints.
  3. Repeat at a DIFFERENT station family (anvil/carpenter/dyeing), ideally one a villager also
     uses — one station is not enough to tell whether `useAgentInventory` varies.
  4. Open a crafting menu and let it sit ~2 s without clicking (clean `GATE rollup` line).
  5. Press **F12** once in the settlement (census v2). Send `LogOutput.log`; triage via log-analyst.

  **Triage questions:** (a) did `WATCH ARM` resolve slots at all; (b) **which slot shows the
  ingredient decrease and at what `+Nms`** — the blocker's answer; (c) does it land before/at/after
  the `_OnCraftingSuccess` / `_OnCraftingFinished` markers; (d) `GATE rollup` calls per menu-open
  and per second — the Phase 1 cost budget; (e) do `STATION PROFILE` lines differ in
  `useAgentInventory`; (f) does the census EquipPoint probe mark `CharacterBuilder` as
  `yes-equipPoint`; (g) do stations report real stock via `Workstation.GetInventory()`; (h) what
  does `GetItemsNeededFromSettlement()` return (Phase 2 lever evidence).

  **Known limits (not defects):** the watch window is 20 s from craft start
  (`Watch/WatchWindowSeconds`) — a longer craft disarms before `_OnCraftingSuccess`, so raise it
  and retest if the SUMMARY prints early; `GetItemsNeededFromSettlement()`'s element type is
  unresolvable from Cecil so item names may log as `?` (the count is still meaningful); a slot
  logging as `unresolved` at ARM means that accessor returned null on that station — itself a
  finding worth recording.
- **Phase 1 — the PLAYER half** (build and confirm in-game before Phase 2 turns villagers on):
  (a) availability: postfix the Phase-0-confirmed gate — when vanilla says false AND the agent is
  the local player AND `EnableForPlayer` is on AND station stock + settlement-wide non-blacklisted
  stock covers the manifest → flip true. (b) just-in-time transfer: host-gated prefix on the three
  `BeginCraftingSequence` impls — compute shortfall (bp manifest minus the collection vanilla
  actually reads), pull it from non-blacklisted settlement storages (`ItemManifest.Transfer`), so
  vanilla validation + consumption then run untouched against local stock. Fail closed: re-run
  vanilla's own gate after the transfer and abort the craft if it still fails, so a partial pull can
  never reach consumption. ⚠️ Two hard constraints (Fable audit 2026-07-20): the re-run must
  BYPASS this mod's own availability postfix — a reentrancy guard (static in-verification flag) —
  because otherwise the postfix re-flips false→true off stock still sitting in settlement storage
  and the abort can never fire; and the prefix must be agent-gated as well as host-gated
  (villagers ride the same methods). (c) UI gray-out: per Phase 0 — if buttons stay gray despite
  (a), patch the per-button count source (`ItemThumbnailPanel`-level).
- **Phase 2 — the VILLAGER half** (`EnableForVillagers`, independent toggle): make the materials
  physically arrive at the station bin rather than flipping any gate — see the scope note. ⚠️ The
  trigger point is unresolved and needs its own evidence pass (crafting-quest creation? fetch-quest
  failure? per-station poll?). **Probe the cheap lever first** (Fable audit 2026-07-20): if the
  vanilla one-station limit turns out to be `GetFetchDepth`/`GetPersonalFetchDepth` or the
  storage-access whitelist, widening vanilla's own fetch may let `CrafterFetchQuest` haul
  settlement-wide itself — potentially the entire Phase 2 implementation, riding the game's own
  machinery. Must not fight `CrafterFetchQuest` or re-create the fetch-loop /
  complaint pathologies documented for SupplyChainMod.
- **Phase 3 (optional):** cooking-family tables (`ForgeInteraction`), config polish (blacklist
  list, prefer-player-inventory-first toggle, max-pull-distance).

**⚠️ verify / risks:**
- **Dupe risk is structural:** `RemoveOwnedItemManifest` removes only what's present, so a craft
  that passes a faked availability check without a completed transfer would consume partial
  ingredients and still produce output. The transfer prefix must run BEFORE vanilla's craft-time
  validation, and availability must never report true unless the pull can actually complete.
- **Availability-check cost:** if `CheckOwnedRequirements` drives the per-button gray-out it can
  fire roughly once per blueprint per menu open (~96 on the Phase-0 table) — never walk the
  settlement inside the postfix; compute against a cached settlement-stock snapshot (short TTL,
  invalidated on craft/transfer). The v0.2.0 spike should log the gate's call frequency while a
  crafting menu sits open to size this.
- **`useAgentInventory` has one data point** (True, one workshop table) yet decides the transfer
  DESTINATION per station. The v0.2.0 spike should record it per station family (craft/anvil/
  dyeing — craft at ≥2 families in one run). Also unknown: `ItemManifest.Transfer` behavior on a
  full destination (player inventory slots/weight) — Phase 1 testing must force the abort path
  once, and verify what state a skipped `BeginCraftingSequence` leaves the session/UI in.
- **Villager half must MOVE materials, never just flip the gate.** A villager crafter consumes from
  its station bin; an availability flip with no transfer leaves it believing materials exist with
  nothing local to consume — the fetch-loop / complaint pathologies SupplyChainMod already
  documented. ⚠️ Open: what the villager-side transfer trigger should be (on the crafting quest
  being created? on the fetch quest failing? polled per station?) — needs its own in-game evidence
  pass, since unlike the player case there is no "player clicked craft" moment to hang it on.
- **Inlining: resolved.** `CheckOwnedRequirements` is non-virtual (normally an inlining risk) but
  was fire-verified in-game — the patch fires. Fallback lever if that ever regresses: the virtual
  `_CheckOwnedBlueprintManifest`.
- **Co-op:** all writes host-gated; whether `ItemManifest.Transfer`/container writes replicate to
  clients is the standing unknown — v1 ships host/solo like the rest of the roster.
- **Blacklist semantics — station input bins, and the severity depends on the villager toggle.**
  A villager crafter's station bin is (vanilla) its **sole** supply. So with the villager toggle
  **OFF**, a player craft that drains the armorsmith's material box to feed the carpenter simply
  stops the armorsmith working — a production stall with no visible cause. With the villager toggle
  **ON**, that station can re-pull settlement-wide, so the same drain is self-healing rather than
  fatal — but it still causes pointless item churn between bins.
  **Default both ways: exclude every crafting station's input bin as a SOURCE.** Warehouses /
  generic storage are the intended sources. Also default-exclude the Outhouse
  (OuthouseComposterMod's input pool). An opt-in override should be loud about the OFF-toggle
  consequence above.

---

## Cross-cutting notes
- Every new lever above is host-authoritative: gate on authority, prefer the game's own
  networked methods (`ReplenishCharges`, `Revive`, `RemoveObjectFromWorld`).
- All new diagnostics configs default **true** until the mod is verified, then flip to false (project rule).
- Fire-verify every new Harmony patch with a log line (inlining gotcha) before trusting it.
