# New Mod Ideas — Approaches (research 2026-07-03; ideas 7–8 added 2026-07-06)

Status legend: everything here is **⚠️ pending in-game verification** unless marked otherwise.
All type/member signatures below were confirmed from the interop binaries via Mono.Cecil
(2026-07-03; ideas 7–8 re-dumped 2026-07-06) — signatures are facts; *runtime behavior* claims
are the ⚠️ part. Ideas 7–8 come from Nexus user feedback on TaskUnlockerMod (rondi112, 2026-07-06).

**Priority order (user):** 5) freezing hunters → 2) den respawn → 4) vacuum → 3) crafting multiplier.
(Idea 6, recipe/fish task unlock → **SHIPPED** as TaskUnlockerMod v1.2.x, confirmed in-game 2026-07-06 —
see [`docs/mods/task-unlocker.md`](docs/mods/task-unlocker.md). NOTE: the plan's original model was only
half right — cooking is IDiscoverableItem/Rpc_AddDiscoverable, but **fishing tasks are gated by MARKED
fishing grounds, not item discovery** — corrected mechanism in
[`docs/architecture.md`](docs/architecture.md) → "Task Discovery & Bypassing".)

---

## 2. Monster/beast den respawning (wulfar/bear dens) — new mod

**Goal:** (a) bring "Defeated" den POIs back to life, ideally remotely from the map;
(b) stop villagers from destroying dens (keep huntable spawns alive).

**Key API (confirmed from binary):**
- `SSSGame.Den : Creature` (a Fusion NetworkBehaviour):
  - `Revive()` / `_ReviveCreatureComponents()` / `SetDenActive(bool, bool)` / `isActive` — the un-defeat levers.
  - `ReviveCooldown : Int32` (networked + serialized) — vanilla already has a den-revive cooldown concept.
  - `affectedSpawners : PopulationSpawner[]`, `alphaSpawner : PopulationSpawner` (the boss).
  - `IsBlockedByStructures()` and `IgnorePopulationSpawnersRespawning(bool)`.
  - ⚠️ NEVER patch its `CopyBackingFieldsToState`/`CopyStateToBackingFields` (load-hang gotcha).
    Capture live dens via `Den.Spawned()`/`Start()` postfix into a static list (documented safe pattern).
- `SSSGame.Combat.PopulationSpawner : CreatureSpawner`:
  - `RespawnAllPopulations(bool)`, `SetActiveSpawner(bool[, bool])`, `Spawn()`, `HasNoAliveCreatures()`.
  - `ignoreRespawning : bool`, `RespawningBlockedByStructures : bool`, `_UpdateBlockedByStructures()`.
- **Likely root cause of "depleted dens I never destroyed": vanilla blocks spawner respawn when
  player structures are built nearby** (`RespawningBlockedByStructures` + `Den.IsBlockedByStructures()`).
  So near-base dens quietly stop repopulating even with no combat. The mod should offer a config
  to neutralize this (postfix `IsBlockedByStructures` → false and/or clear
  `RespawningBlockedByStructures`), which may fix the whole complaint *without* any manual revive.

**Approach:**
1. **Phase 1 (core):** capture dens on `Spawned()`; hotkey (MineRefreshMod on-demand pattern) that
   calls `Revive()` on defeated dens within a config radius — or all tracked dens ("remote"
   without UI work). Config: `AllowRespawnNearStructures` (the blocker bypass), `ReviveHotkey`,
   optional auto-revive after N days (timer like TreeRespawn, using `ReviveCooldown` semantics).
2. **Phase 2 (map-click revive):** reuse WarpTourMod's native-map-pin knowledge (map icon / world
   objective types dumped during that mod) to trigger a revive from the den's POI icon. Riskier UI
   work; hotkey-first ships value sooner.
3. **Villager blacklist (b):** needs a diagnostics session first — unclear whether villagers
   (warriors on defend duty / HunterCombatQuest) actually attack `Den` hitzones, vs. the
   structure-blocking explanation above. If they do: dens are `Creature`s ⇒ `IAttackTarget`;
   blacklist in target selection (log `GetTargetName()`/`Faction` of what warriors engage, then
   gate the acquisition method — the VillagerFightBackMod toolbox: patch the *selection* method,
   not priority getters).

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

## 4. "Vacuum cleaner" for ground clutter — new mod

**Goal:** clear loose ground items (config radius) to protect framerate; optional decay tuning,
including items that never decay (long sticks, logs).

**Key API (confirmed from binary):**
- `SSSGame.DynamicItemObjectManager` — persistent manager holding an intrusive **linked list of every
  dynamic ground item** (`_head` → `DynamicItemObject.NextDynamicObject`). Enumerable without
  FindObjectsByType.
- `DynamicItemObject._itemObject : WorldItemObject`; `WorldItemObject.RemoveObjectFromWorld()` —
  the game's own "delete this ground item" (it's what the confirm-dialogue destroy uses) ⇒ the
  network-safe removal call.
- Decay already exists as item *processes*: `SandSailorStudio.Inventory.ExpirationProcess` +
  `SSSGame.Network.ItemDecaySyncProcess` (per-item-asset lifespans).

**Approach:**
1. **Phase 1 (vacuum):** hotkey → walk the DynamicItemObjectManager list, filter by distance to
   player and a config item-name/category blacklist (default-exclude equipment: Weaponized/Wearable/
   EquipmentItemObject — identify by native class name, NOT managed `is`/`as`, per interop gotcha),
   call `RemoveObjectFromWorld()` host-side. Config: radius, hotkey, exclusions, optional auto-run
   every N minutes. Diagnostics default true: log per-sweep counts by item name.
2. **Phase 2 (decay tuning):** enumerate item assets, scale `ExpirationProcess` lifespans; for
   never-decay items (logs, long sticks) attach/enable an expiration process — riskier (asset
   surgery + network sync), do only after Phase 1 proves the enumeration.

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

## 8. New workers start with ZERO tasks (no task-list inheritance) — SHIPPED as ZeroTaskWorkersMod v1.0.0 (2026-07-06)

**Status: ✅ COMPLETE — confirmed in-game 2026-07-06.** See [`docs/mods/zero-task-workers.md`](docs/mods/zero-task-workers.md) for the shipped recipe, config, and test results.

**Original Goal:** on config-listed buildings, a newly assigned worker gets *no* tasks by default (today he's
auto-activated for all 25 blacksmith tasks and you must hand-uncheck each). The user then opts him
into exactly the tasks wanted (e.g. only Draugr weapon research).

**Key API (confirmed from binary, reconfirmed for ship):**
- Per-task assignees: `SSSGame.AI.WorkstationTaskData.VillagersInCharge : List` (+ `priority`,
  `pinnedTask`, `onDataChanged : Action`).
- The inheritance moment: `Workstation.AddToTaskDatas(Villager)` — called when a villager joins a
  station (assignment chain: `Villager.AssignToWorkstation(IWorkstation)` →
  `_AssignToWorkstationInternal` → `Workstation.SetTaskAgent(ITaskAgent)`); the reverse is
  `RemoveFromTaskDatas(Villager)`.
- **The clean per-(villager, task) gate:** `Workstation._CanAddVillagerToTaskData(Villager,
  WorkstationTaskData) : Boolean` — and it's **overridden by `Buildstation`** ⇒ virtual/vtable-
  dispatched ⇒ safe from the AOT-inlining trap that kills small-method patches.
- Network sync is the game's own: the generic NetworkWorkstation's
  `Rpc_ChangeTaskVillagersInCharge(Int32, Il2CppStructArray)` /
  `OnTaskVillagersInChargeChanged(WorkstationTaskData)` (mask-based,
  `_UpdateTaskVillagersInChargeFromMask`). The UI checkbox path goes through this — so removals done
  via the same calls replicate correctly.
- NOT the same system as station *whitelisting* (`IsWhitelisted` / `WhitelistNewVillagers` /
  `Rpc_ChangeWhitelistedVillager`) — that's "which villagers may use this station", not per-task
  in-charge lists. Don't conflate them.

**Approach:** prefix `Workstation._CanAddVillagerToTaskData` → `__result = false` (skip original)
when the station's `Structure.GetName()`/`DefaultName` matches the config list (TreeRespawnMod
substring-map pattern; option for "all buildings"). Result: `AddToTaskDatas` adds the villager to
nothing — zero tasks, exactly the ask. Fallback if that gate turns out to be consulted elsewhere
(e.g. by the UI to decide which checkboxes to *show*): postfix `AddToTaskDatas(Villager)` and
immediately strip the villager back out of every `WorkstationTaskData.VillagersInCharge` via the
game's RPC path. Config: building-name list, `ApplyToAllBuildings`, and (stretch) `NewTasksStartUnassigned`
— when a new task is added to a station, existing workers likely inherit it the same way
(`WorkstationTaskData` ctors take a villager list) — same gate should cover it.

**⚠️ verify / risks (the diagnostics phase decides the final hook):**
- **When exactly `AddToTaskDatas` fires** — must be hire-time only. If it also runs on world load /
  villager respawn / structure upgrade re-registration, a blanket prefix would strip SAVED
  assignments every load. Vanilla preserves unchecked boxes across save/load, so load-path
  restoration is probably `DeserializeTaskData` / network state — but confirm with a
  diagnostics-first build (log every call with villager name + station + call timing), and if
  needed gate on "world finished loading" (TaskUnlockerMod's `BlueprintConditionsDatabase` world
  gate) before treating a call as a real hire.
- Whether the UI task checkboxes read `_CanAddVillagerToTaskData` (would make unchecked boxes
  un-checkable — then use the postfix-strip fallback instead).
- Fire-verify the prefix actually intercepts calls from `AddToTaskDatas` (virtual dispatch should
  guarantee it, but that's the standing rule).
- Host-only (`NetworkLogic.HasStateAuthority`); client-side assignment presumably routes through
  the host anyway — confirm in co-op.

---

## 9. Mushrooms year-round / rain-independent — TreeRespawnMod extension

**Status: ✅ SHIPPED in TreeRespawnMod v1.4.7 — CONFIRMED in-game 2026-07-07** (rain half Spring-no-rain; winter/season half in a co-op winter save).

**Goal:** remove the seasonal + weather gating on wild mushrooms so they appear year-round (not
culled in winter) and don't wait on rain to grow. Keep their spatial "wooded areas" placement as-is
(that's separate worldgen spawn-location logic — untouched).

**Key API (confirmed from binary via Cecil 2026-07-06):** the game gates seasonal/weather-restricted
resources through ONE universal availability system:
- `SSSGame.WeatherManager` (MonoBehaviour; `FindAnyObjectByType` works) holds
  **`_descriptors : Dictionary<ItemInfo, BiomeItemAvailabilityData>`** — keyed by the produced
  **`ItemInfo`** (so entries are targetable by item name; wild mushrooms surface as
  `"Gray/Grey Mushroom"` / `"Yellow Mushroom"`, substring `"Mushroom"` — the same key TreeRespawn's
  GatherRespawn already uses). `RegisterDescriptor(BiomeItemDescriptor)` populates it;
  `HandleDescriptors()` / `IsAvailable(AvailabilityProcess)` re-evaluate on every weather/season change.
- `SSSGame.BiomeItemAvailabilityData`: `availabilityProcess : AvailabilityProcess`, `remainingDays`,
  `ReplenishOnAvailable`, `LastResult`, `CurrentDescriptorsState`, `_OnNewDay()` (daily countdown).
- **`SandSailorStudio.Inventory.AvailabilityProcess`** (ScriptableObject) is the gate. Master eval
  **`CheckAll(WeatherEventData)`** ANDs four mutable condition lists:
  - `SeasonAvailabilityConditions : List<SSSGame.Weather.SeasonConfig>` — allowed seasons (**winter cull**),
  - `TimeAvailabilityConditions : List<TimeCondition>` (`condition` ∈ `NormalizedSeasonTime`/`DayOfYear`/…, `+subcondition` range),
  - `OtherAvailabilityConditions : List<OtherCondition>` (`condition` ∈ `IsRaining`/`IsWet`/… — **the rain gate**),
  - `ProgressingWeatherConditionAvailabilityConditions`.
  Each condition carries `isMandatory` + `negateCondition`. Sub-checks: `CheckSeasonCondition`,
  `CheckOtherWeatherConditions`, `CheckTimeWeatherConditions`, `GetNextAvailableTime`.
- Same system also gates `FishingGround.FishAvailabilityProcess`, `PlantableItemInfo.PlantAvailabilityProcess`,
  traps, invasions, populations — so **any bypass MUST be scoped to mushroom entries only**, never global.

**Diagnostic (TreeRespawnMod v1.4.6 — `MushroomDiag.cs`, `[MushroomAvailability]` config, read-only)
— MODEL CONFIRMED in-game 2026-07-07.** The dump (22 descriptors; context Spring/day-12/not-raining)
showed all **3 wild mushrooms are registered and gated exactly as expected**:
- `Mushrooms` (id 16793608) & `Grey Mushrooms` (16793609): `Season = Spring,Summer,Autumn` (no Winter);
  `Other = IsRaining mandatory=True`; `process.lifespan=1`.
- `Yellow Mushrooms` (16793610): `Season = Autumn` only; `Other = IsRaining mandatory=True`; `lifespan=1`.
- All three read `CheckAll=False / IsAvailable=False` in Spring-no-rain — and since Spring is IN-season
  for Grey/plain, the **only** failing gate was the mandatory `IsRaining` → decisive proof the rain gate
  is `OtherCondition{IsRaining, mandatory=True}` and the winter cull is `SeasonAvailabilityConditions`
  (omitting Winter). Each mushroom has its **own** process (season lists differ) ⇒ no shared-SO collateral.
- Cross-check: seasonal crops confirm the season-list mechanism (Beetroot=Autumn,**Winter**; Carrot/
  Cabbage=Summer,Autumn — all `False` in Spring), and `mandatory=False` weather conditions do NOT block
  (Natural Water Collector has 3 non-mandatory Others, all currently false, yet `IsAvailable=True`).

**Approach (levers now exact) — data edit at world load, mushroom-keyed only:** on the 3 mushroom
entries in `WeatherManager._descriptors` (name contains "Mushroom"), (1) **rain-independent** = clear
`OtherAvailabilityConditions` (removes the mandatory `IsRaining`); (2) **year-round** = clear
`SeasonAvailabilityConditions` (empty ⇒ no season restriction — evidence: empty-season items read
available). Then the game's own `replenishWhenAvailable`/lifespan loop runs unrestricted. Config:
substring item-name list (default `Mushroom`), plus independent `IgnoreSeason` / `IgnoreRain` toggles.
Alternative if a data edit misbehaves: Harmony prefix on `AvailabilityProcess.CheckAll` → `__result=true`
for a pre-scanned mushroom-process `HashSet` (reversible, no SO mutation).

**✅ Verified in-game 2026-07-07 (v1.4.7 — implemented exactly as "clear both lists" above):**
- Clearing the two lists makes mushrooms spawn/persist without rain (Spring, `raining=False` — where the
  v1.4.6 diagnostic had proven vanilla read `IsAvailable=False`) AND in winter (co-op winter save,
  `season=WeatherSeason_Winter`/`snowing=False` — all 3 mushrooms `Season[0]`/`Other[0]`, `IsAvailable=True`,
  visible on the ground). The `lifespan=1` loop keeps them re-replenishing.
- No separate winter cull remains — mushrooms stay present in winter with the availability gate cleared.
- Co-op: the SO edit is applied locally on **every** peer (un-host-gated), so it worked in the co-op
  session regardless of role. A second peer simultaneously seeing the host's winter mushrooms is a safe
  extrapolation from that design, but was not itself explicitly logged.
- Minor: `IsAvailable` lags False→True by one weather/season eval tick after the clear (the game caches
  the result); self-corrects (`remainingDays` −1→1). The Harmony-prefix alternative was not needed.

---

## Cross-cutting notes
- Every new lever above is host-authoritative: gate on authority, prefer the game's own
  networked methods (`ReplenishCharges`, `Revive`, `RemoveObjectFromWorld`).
- All new diagnostics configs default **true** until the mod is verified, then flip to false (project rule).
- Fire-verify every new Harmony patch with a log line (inlining gotcha) before trusting it.
