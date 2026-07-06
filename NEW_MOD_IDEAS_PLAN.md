# Five New Mod Ideas — Approaches (research 2026-07-03)

Status legend: everything here is **⚠️ pending in-game verification** unless marked otherwise.
All type/member signatures below were confirmed from the interop binaries via Mono.Cecil
(2026-07-03) — signatures are facts; *runtime behavior* claims are the ⚠️ part.

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

## Cross-cutting notes
- Every new lever above is host-authoritative: gate on authority, prefer the game's own
  networked methods (`ReplenishCharges`, `Revive`, `RemoveObjectFromWorld`).
- All new diagnostics configs default **true** until the mod is verified, then flip to false (project rule).
- Fire-verify every new Harmony patch with a log line (inlining gotcha) before trusting it.
