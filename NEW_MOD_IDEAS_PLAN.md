# New Mod Ideas — Approaches (research 2026-07-03; ideas 7–8 added 2026-07-06; idea 10 added 2026-07-07; idea 11 added 2026-07-08)

Status legend: everything here is **⚠️ pending in-game verification** unless marked otherwise.
All type/member signatures below were confirmed from the interop binaries via Mono.Cecil
(2026-07-03; ideas 7–8 re-dumped 2026-07-06; idea 10 dumped 2026-07-07) — signatures are facts;
*runtime behavior* claims are the ⚠️ part. Ideas 7–8 come from Nexus user feedback on TaskUnlockerMod
(rondi112, 2026-07-06); idea 10 from Nexus user kira31374 (2026-07-07). Idea 11 comes from a Nexus
discussion on DynamicVillagerNeedsMod (snakesilver + author, 2026-07-08) and — unusually — needs **no**
new binary dump: every API it relies on is already used by the shipped mod (runtime-proven).

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

## 4. "Vacuum cleaner" for ground clutter — SHIPPED as GroundItemVacuumMod v1.0.1 (2026-07-07)

**Status: ✅ COMPLETE — confirmed in-game 2026-07-07.** See [`docs/mods/ground-item-vacuum.md`](docs/mods/ground-item-vacuum.md)
for the shipped recipe (own-set OnEnable/OnDisable tracking + `RemoveObjectFromWorld`), the v1.0.0
raw-linked-list-walk native-crash dead-end, and the framerate finding (ground clutter was NOT the
bottleneck — a loaded mod is; bisect pending). Original plan below.

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

## 10. Fillet fish directly in inventory (shift-click harvest) — SHIPPED as FishFilletMod v1.1.1 (2026-07-08)

**Status: ✅ COMPLETE — confirmed in-game 2026-07-08.** See [`docs/mods/fish-fillet.md`](docs/mods/fish-fillet.md)
for the shipped recipe (Shift+RMB trigger; OnPointerClick prefix gesture; temp-null tool-req + CommandHarvestItem drive; why Shift+RMB dodges the native Shift+LMB harvest-router toast) and dead-ends (CanHarvestCurrentItem-flip-alone, CommandHarvestItem patch, persistent bare-hand).

**Original Goal:** let the player break a caught fish down into its fillet output (meat / blubber) with the same
**shift-click-in-inventory harvest** the game already gives bark, reeds, fur, straw, etc. — instead of
having to drop the fish on the ground, fillet it with a skinning knife, and pick the results back up.
(Nexus request from kira31374, 2026-07-07: fishing chefs waste a lot of time hand-filleting.)

**Two DISTINCT mechanisms confirmed from binary (2026-07-07):**
1. **The inventory shift-click harvest** (what bark/reeds use — toolless, instant, one item → its yield):
   - `SSSGame.UI.ItemThumbnailPanel.CommandHarvestItem() : Void`, **gated by
     `CanHarvestCurrentItem() : Boolean`** — plain `MonoBehaviour` UI methods (**safe patch surface,
     not inlining-prone**, unlike the item-side `CanBeHarvested()` below).
   - Executes via `SSSGame.CharacterInventory.HarvestSelectedItems() : Boolean` (Public **Virtual Final**,
     implements `SandSailorStudio.Inventory.IInventoryManager.HarvestSelectedItems()`) →
     `_HarvestItemOperation() : Void` (private worker). `PetInventory` has the identical twins.
   - **"Shift" = the harvest modifier**: `SSSGame.UI.ContextMenu._harvestModifierInputPressed`,
     `_onClickModifier1Changed/2Changed(CallbackContext)`, `allowHarvestCursor`, `_RefreshHarvestCursor()`,
     `_GetHarvestEventData() : ContextMenu/HarvestCustomActionEventData` (a `CustomActionEventData` with
     `.Use()` / `.Cancel()`). Mirrored on `ItemDetailsPanel._harvestModifierInputPressed` + `harvestTooltip`.
2. **The fish fillet** (drop + skinning knife): the dropped fish is a world `WorldItemInstance` harvested
   with the knife. The villager equivalent is the **harbor butcher**: `SSSGame.AI.HarborHarvestQuest` →
   `SSSGame.AI.FSM.FSM_HarborHarvest` (`slaughterAction`, `resultName`, `categoryName`,
   `_PrepareWorldItemInstance(HarvestData, WorldItemInstance)`) → `FSM_ReturnHarborButcherResults`. This
   is the **same resource-harvest FSM** run on the dropped fish ⇒ strong evidence the fillet yield is the
   fish's own `ResourceInfo.exhaustableComponents` (see below), i.e. the SAME data both paths consume.

**The shared item-side data type — `SSSGame.ResourceInfo : ItemInfo`** (ScriptableObject; the type behind
bark, fiber, raw meat, and almost certainly caught fish):
- `exhaustableComponents : Il2CppReferenceArray` — **the harvest YIELD** (what you receive; "exhaust the
  resource to get these"). `junk : Il2CppReferenceArray` — low-value extras. `piecesHitpoints`.
- `mainHarvestMoveset : InteractionMoveset` — the harvest animation/tool. Tool requirement lives in
  **`SSSGame.InteractionMoveset.requiredEqippmentCategory : ItemCategoryInfo`** (+ `baseUnarmedDamage`,
  `damageMultiplier`, `weaponDurabilityDamage`) — this is where "needs a skinning knife" is encoded.
- `CanBeHarvested() : Boolean` — the per-item gate. **NOT virtual ⇒ prime IL2CPP inlining candidate ⇒ a
  Harmony patch on it may silently never fire (documented gotcha). Do NOT patch it — patch the UI gate
  `ItemThumbnailPanel.CanHarvestCurrentItem()` instead.** Also `GetNonExhaustedDepth(IItemFilter, UInt32&)`,
  `harvestChanceHasLootRequirements`, `harvestDiscoversComponents`, `requiresDiscovery`.
- `SandSailorStudio.Inventory.ItemsConstants.c_NonHarvestable : Int32` (attribute-id constant) + `c_FishWeight`,
  `c_FishingState` — items carry attributes; fish carry weight/state attrs, and `c_NonHarvestable` marks
  items excluded from harvest. (Attributes live in `ItemInfo.attributes` / `FindAttribute(Int32&)`.)
- Caught fish itself = `SSSGame.Fishable.info : ItemInfo` (from `FishableItemsConfig.FishableItems`) — the
  static type is `ItemInfo`, so the concrete subtype (ResourceInfo?) is the Phase-0 question. **NB: the
  `FishingItemInfo`/`FishingItem` types are the ROD/line (`: WeaponizedItem`), not the catch.**
- World-loot alt-path if the yield ISN'T in exhaustableComponents: `SSSGame.LootSpawner.lootOnHarvest` /
  `GetPieceLoot()` on the dropped-fish prefab.

**Model (⚠️ the runtime half — Phase 0 decides):** bark/reeds are `ResourceInfo` with `CanBeHarvested()=true`
and a bare-hand `mainHarvestMoveset` (empty `requiredEqippmentCategory`) ⇒ the toolless inventory shift-click
grants their `exhaustableComponents` (fiber). Fish is (very likely) also a `ResourceInfo` whose
`exhaustableComponents` ARE the fillets, but whose inventory-harvest is blocked because ONE of:
(a) `CanBeHarvested()`/`CanHarvestCurrentItem()` returns false for it; (b) its `mainHarvestMoveset` requires
the skinning-knife category so the toolless inventory path refuses it; (c) it carries the `c_NonHarvestable`
attribute. **Determining WHICH is the entire point of Phase 0** — it also decides whether the yield is already
present (best case) or must be reconstructed (fallback).

**Approach (phased — diagnostics first, ship the cheap win):**
1. **Phase 0 (diagnostics — decides the hook, default `true`):** for the currently-selected inventory item
   (start with a fish; cross-dump bark/reeds as the working baseline), log: runtime IL2CPP class (is it
   `ResourceInfo`?), `CanBeHarvested()`, `exhaustableComponents` + `junk` (item names + quantities), whether
   it has the `c_NonHarvestable` attribute, `mainHarvestMoveset.requiredEqippmentCategory` (the required tool),
   and whether `ItemThumbnailPanel.CanHarvestCurrentItem()` returns false. This confirms fish-is-ResourceInfo,
   whether the meat/blubber yield already lives in `exhaustableComponents`, and exactly which gate blocks it.
2. **Phase 1 (best case — un-hide + reuse the vanilla yield):** if fish is a `ResourceInfo` carrying the
   fillets as `exhaustableComponents`, Harmony **postfix** `ItemThumbnailPanel.CanHarvestCurrentItem()` →
   `__result = true` for fish (**scoped by category/name substring — never global**, so we don't unlock every
   `c_NonHarvestable` item). Vanilla `CommandHarvestItem()` → `HarvestSelectedItems()` → `_HarvestItemOperation()`
   then consumes the fish and grants meat+blubber through the game's own item-add + networking. If the operation
   internally re-checks `CanBeHarvested()` and still refuses, fall back to patching `HarvestSelectedItems`/
   `_HarvestItemOperation` to force-run for fish. Config: fish name/category list (default the fish category),
   optional `RequireSkinningKnifeInInventory` (only allow if the player holds the knife category — matches the
   theme), optional `ConsumeKnifeDurability`. Fire-verify the postfix with a log line.
3. **Phase 2 (fallback — reconstruct the yield):** only if Phase 0 shows the fillet output is NOT in
   `exhaustableComponents` (it lives on the dropped-fish prefab `LootSpawner.lootOnHarvest`). Add a custom
   context-menu "Fillet" action (the `HarvestCustomActionEventData`/CustomAction path) that consumes one fish
   and grants the loot read from the fish's `spawnObject` LootSpawner (or a config'd meat/blubber map), via the
   game's own add-item RPC. More code — avoid unless forced.

**⚠️ verify / risks:**
- **Fish-is-ResourceInfo and `exhaustableComponents` = the fillets** — the central assumption; Phase 0 confirms
  or routes to Phase 2.
- Whether `CanHarvestCurrentItem`/`_HarvestItemOperation` **enforce the moveset tool requirement** (skinning
  knife). If they do, either the mod supplies the tool virtually or gates on the player actually holding it.
- **Stack behavior:** does one shift-click fillet one fish or the whole stack? Match vanilla; verify the
  meat+blubber-per-fish quantities EQUAL the drop-and-skin result (not an exploit or a nerf).
- **Durability:** the world-fillet spends skinning-knife durability; the inventory path may bypass it — offer
  `ConsumeKnifeDurability` so it isn't a free shortcut if the user wants parity.
- **Host authority / co-op:** `HarvestSelectedItems` is the game's own networked path; gate writes on authority
  and let the vanilla op do the add. Confirm a client filleting routes through the host.
- Fire-verify the `CanHarvestCurrentItem` postfix fires (MonoBehaviour UI method — should be safe from the
  AOT-inlining trap, but standing rule).

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
  ⚠️ verify in-game which write actually ends a forced sleep.
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

---

## Cross-cutting notes
- Every new lever above is host-authoritative: gate on authority, prefer the game's own
  networked methods (`ReplenishCharges`, `Revive`, `RemoveObjectFromWorld`).
- All new diagnostics configs default **true** until the mod is verified, then flip to false (project rule).
- Fire-verify every new Harmony patch with a log line (inlining gotcha) before trusting it.
