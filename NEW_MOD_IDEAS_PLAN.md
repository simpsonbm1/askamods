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

## 12. Demand-driven supply-chain autopilot — new mod (planned; design v2 2026-07-12)

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
  - **Quota-raise effectiveness is priority-conditional.** Raising a met quota re-opens the intake
    valve, but haulers act on it only if that collect task's priority beats other unfilled-quota
    tasks. Precondition check: log/inspect the collect task's rank vs. the set of other
    unfilled-quota tasks. Warehouse PRIORITY moves stay the dangerous secondary lever (F3c
    winner-take-all: one raised collect priority starved all other hauling) — quota-raise-only is
    tested first; quota+short-duty-cycled-priority only if that proves insufficient.
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
    docs/mods/supply-chain.md); **2b** hotkey quota-spike (boost/revert through the shared ledger)
    + hauler-response verification, with the room-split precondition (allotment met + room=0 is a
    capacity alert, NOT a boost target — run-2 finding); **2c** metabolic plane (snapshot ring
    buffer + net-rate derivatives) + clog state machine in the existing arbiter (shared ledger/
    BoostedStationKeys/duty-cycling/capacity verdicts).
- **Phase 2d — rate-based stock keeping / quota calibration (user-requested 2026-07-13):** use the
  metabolic plane's per-item derivatives (consumption rate vs intake rate across sweeps) to
  auto-maintain warehouse allotment quotas at levels that keep the base self-sustaining — quotas
  sized from measured flow (steady-state target ≈ consumption × buffer window) instead of static
  player guesses, within the player-owned capacity cap and the quota mechanics above (raise-only
  effectiveness, never evicts, taskMaxQuota semantics still unknown ⚠️). Distinct from Phase 5
  (player-DECLARED stock targets): 2d derives targets from observed usage. Builds on 2c's
  derivatives; ships after 2b's quota actuator is proven.
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

*Open unknowns (Phase 0 + Phase 1 ANSWERED in-game 2026-07-12):*
**Answered:** `Rpc_ChangeTaskPriority` args (taskIndex, newPriority); gathering tier mechanism
(FiltersTaskData filters + Rpc_ChangeTaskFilters); native add-task path exists (Rpc_AddTask +
WorkstationMenu.AddTaskDataToWorkstation); no-production complaint payload (SettlementIssueTrackerWidget
map ItemInfo→needed-by string); storage-full fires (typed StorageFullComplaint + widget map);
complaint cadence (level signal, re-add ~50–100 ms, fresh id ~3 s, chronic 40–160 s vs transient
<2 s, alarm 60 s); AddComplaint not inlined; complaint TryCast works; CraftBlueprint ingredient
access (via _localBlueprints + CreateFromBlueprint; KnowsBlueprintForItem is a dead-end); sweep
cost measured (6–20 ms per 31 stations, 10.8 ms per 61 stations); priority Int32 rank-vs-index
semantics (= 0-based rank index mirroring list position, confirmed 2026-07-12); `NetworkWorkstation<T>`
reachability (GetComponent works; property lies, confirmed 2026-07-12).

**Still open:** whether mod-created tasks serialize like player ones; dead-inventory native alert
(untested; sweep-only detection assumed per design); research-task discriminator (no assigned
workers + TaskDataPanel `_bgStudyTaskColor` are markers; Phase 1b controller excluded research tasks
per user decision); untested Phase 1b edges (militia-alarm lockout no raid/test, StationBusy rotation
no same-station contention observed, capacity verdict via 2 ineffective cycles all real boosts
self-cleared early).

*Sequencing dependency:* do NOT start building while the stack's hitching investigation is open —
land the TreeRespawn v1.6.1 verdict and the `bisect-plugins.ps1 -DisableAll` FPS baseline first,
so this mod starts against a known-clean performance baseline.

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

## Cross-cutting notes
- Every new lever above is host-authoritative: gate on authority, prefer the game's own
  networked methods (`ReplenishCharges`, `Revive`, `RemoveObjectFromWorld`).
- All new diagnostics configs default **true** until the mod is verified, then flip to false (project rule).
- Fire-verify every new Harmony patch with a log line (inlining gotcha) before trusting it.
