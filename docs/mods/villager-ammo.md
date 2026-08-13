# Mod 24: VillagerAmmoMod — v1.0.0 on Nexus, storage restock confirmed; v1.1.2 retune ⚠️ pending

**Goal:** villagers in ranged roles (archery training, defenders, hunters) never run out of arrows —
ammo spent while shooting is refunded in place, so the carried arrow stack holds level. The player is
never refunded (`IsPlayer` gate). Companion feature: stuck arrows that accumulate around
archery-range targets (thousands over time — a confirmed FPS killer) are periodically culled.

**Game subsystem:** [Villager Ranged Combat / Ammo System](../architecture.md#villager-ranged-combat--ammo-system-villagerammomod-evidence-confirmed-in-game-2026-07-11)
— `RangedManager`/`RangedAmmo` API surface, `AimState` enum, shooting-driver FSMs.

## Working design

### 1) Ammo refund — own-instance polling (event patches are fatal; see Dead-ends)

- `RangedManager.Awake()` Harmony postfix — **PARAMETERLESS** — captures every shooter into a
  static locked registry (one-time fire-verify log per instance; ~76 managers in a live world).
- `AmmoTracker` (ClassInjector-registered MonoBehaviour, DontDestroyOnLoad) polls every 0.5 s,
  per-manager try/catch (drop from registry on exception):
  - skip `IsPlayer == true` (player never refunded) and `HasAuthority == false` (host/authority
    writes only, co-op safe);
  - record `LastShootingSeen[mgr] = Time.time` whenever `State ∈ {Aim, Fire, Reload}`;
  - compare `CurrentRangedAmmo.RealAmmoCount` against a per-manager baseline: count UP → adopt the
    new baseline (restock detected); count DOWN → **refund iff** within the shooting grace window
    (`RecentShootingWindowSeconds`, default 3.0 s, or `RefundOnlyWhenShooting=false`), else adopt as
    a deliberate withdrawal;
  - refund = `ammo._itemContainer.AddItems(info, deficit)`, `info` from `GetItem(0)?.info` with a
    per-container last-known `ItemInfo` cache keyed by `container.Pointer` (covers the
    last-arrow-empty-stack case).
- The grace window exists because post-aim drops land in `StandBy` — a raw state check misreads
  them as withdrawals and leaks refunds (the v0.1.2 bug; v0.1.3 closed it: 146 refunds, 0 false
  adoptions in the verification session).

### 2) Stuck-arrow cull (persisted ground items)

- Persisted stuck arrows are ordinary `DynamicItemObject` ground items after save/load — they are
  NOT in `ProjectileTargetHelper._stuckObjects` (that registry only fills at hit-time via
  `_RegisterStuckAmmo`), so the game's own `ReleaseAllStuckObjects()` cannot clear them (the v0.2.0
  dead-end).
- Own tracking set via `DynamicItemObject.OnEnable` postfix / `OnDisable` prefix (GroundItemVacuum
  pattern; parameterless targets — safe under the inventory-family patch gotcha).
- Every `CleanupCheckSeconds` (60 s), host-gated: cull tracked items whose category chain contains
  `ArrowCategoryMatch` AND that sit within `TargetArrowRadius` (squared-distance) of a tracked
  target whose GameObject name matches `TargetNameMatch`, once the match count ≥
  `StuckArrowThreshold`. Removal via `WorldItemObject.RemoveObjectFromWorld()`.
- **`TargetNameMatch` scoping is load-bearing** (v0.2.3): a census showed `ProjectileTargetHelper`
  rides on characters, creatures, and harvestables too (112 helpers = 6 ArcheryTarget + 6
  TrainingDummy + 79 villagers + skeletons/boss/animals/nodes), so an unscoped cull is town-wide.
  Scoped cull confirmed in-game 2026-07-12: `culled 74/74 stuck arrows near 12 target(s)` with
  town-wide arrows untouched.
- Arrows the player shoots into range targets are culled too; loose arrows away from targets are
  never touched. The native `ReleaseAllStuckObjects` sweep is retained as a (typically no-op)
  secondary pass.
- World-leave (local `PlayerCharacter.Despawned`) clears ALL per-world state: registries,
  baselines, `ItemInfo` cache, `LastShootingSeen` (stale-wrapper native-AV prevention).

### 3) Storage restock — arrows from settlement storage (confirmed in-game 2026-08-12)

- Origin: a Nexus feature request from user ShaySignyr on 2026-08-12. He wants archers to
  draw arrows from settlement storage rather than receive infinite ones, so crafting and
  the production line still matter. His words for the problem: idiot archers going on shift
  with four arrows in their quiver when there are hundreds in the warehouse.
- The mode is opt-in via `Restock/RestockFromStorage`, default false. When it is true it
  REPLACES the in-place refund rather than supplementing it — `AmmoTracker.ProcessManager`
  adds `&& !Plugin.RestockFromStorage.Value` to its `shouldRefund` test, so a detected ammo
  drop is adopted as a genuine expenditure. `VillagerAmmo/Enabled` remains the master
  switch for both modes.
- A periodic top-up pass, `AmmoTracker.RunRestockPass`, runs every `RestockCheckSeconds`
  (default 10 s) rather than on the 0.5 s ammo poll. A villager whose count is below
  `RestockWhenBelow` (default 5) is refilled toward `RestockTargetCount` (default 20),
  stock permitting.
- Gate order in the pass: host-only first (it writes world state, same reason the arrow
  cull is host-gated), then `IsPlayer` false, then `HasAuthority` true, then the villager
  gate.
- **The villager gate is load-bearing.** The registry captures every non-player
  `RangedManager`, and this mod's own v0.2.2 census (recorded in the "Useful confirmed
  world facts" section of this file) found tracked helpers sitting on skeletons and other
  creatures alongside villagers. Without the gate the mod would hand the player's warehouse
  arrows to hostile archers. `AmmoTracker.ResolveVillager` walks the manager's own
  GameObject then up to 6 ancestors using the singular `GetComponent<Villager>()`; a
  manager with no `Villager` anywhere in that chain is skipped.
- Arrow-type resolution runs in three tiers: the quiver's own `GetItem(0)?.info`; then
  `Plugin.InfoCache` keyed by the quiver's native pointer; then
  `SettlementStock.ResolveArrowInfo`, which tries `RestockArrowPreference` by item name in
  order and falls back to the largest settlement stock whose category chain contains
  `TargetCleanup/ArrowCategoryMatch`. The third tier exists for a quiver that has been
  empty since world load, where the first two have nothing to offer.
- **Locale limit:** `RestockArrowPreference` matches item DISPLAY names, so it will not
  match on a non-English game. The category-chain fallback is what covers those players,
  and it is not itself proven locale-invariant. The config description states this to the
  player.
- Sourcing: `SettlementStock.cs` is a port of CraftFromStorageMod's proven settlement walk
  (`GetStructures()` per structure, then a per-node singular `GetComponent<ItemContainerComponent>()`
  recursion, since the plural generic is missing through the interop trampoline). It
  carries CraftFromStorageMod's container dedupe by native pointer, which that mod
  confirmed in-game on 2026-07-30 was necessary because the same physical container is
  otherwise listed once per structure that reaches it, doubling every count.
  `Settlement.QuerySettlementResources()` is never called — it hangs the game. Snapshot
  TTL is `RestockSnapshotTtlSeconds`, default 10 s.
- Deliberately NOT ported from CraftFromStorageMod: its station-qualified node allow-list,
  which exists to separate a crafting station's protected input bins from its output bins,
  a distinction with no meaning for arrows in a warehouse.
- Moving: `AmmoRestock.TryRestock` walks candidates largest-stockpile-first. Removal uses
  the precise per-slot pattern copied from `CraftFromStorageMod/CraftTransfer.cs`
  `RemoveFromContainer`, because **`ItemContainer` has no bulk `RemoveItems(ItemInfo,int)`
  overload** — only the Item-instance-based `RemoveItem(item, count,
  ItemEventContext.Default)`. Slots are matched by `info.id`, never by a managed cast.
  Anything the quiver refuses is added back to the source container, so items are never
  destroyed.
- After a nonzero move the pass writes `Plugin.Baselines[mgr] = count + moved`, so the
  next 0.5 s poll does not read the top-up as an unexplained increase.
- World-leave: `PlayerDespawnedPatch` calls `SettlementStock.ClearWorldState()`, dropping
  every held `ItemContainer` wrapper. Holding interop wrappers of per-world objects across
  world sessions is the documented cause of a native access violation with no managed
  exception.
- Success marker for the first in-game run: the unconditional line `[VillagerAmmo] restocked
  <moved>/<want> '<item>' for villager '<name>' from <sources>`. Under `EnableDiagnostics`
  a zero-move attempt logs `[VillagerAmmo] restock found nothing for '<name>': wanted <n>
  '<item>', settlement holds <n>`, where the settlement figure separates an empty
  settlement from a refused destination.
- The run showed partial top-ups whose shortfall traces to source exhaustion rather
  than quiver capacity, for example `restocked 18/38 'Iron Tipped Arrow' for villager
  'Asmund' from Improved Warehouse 4 x18`, where the single named source supplied
  everything it had. A villager's quiver holds exactly one stack of arrows, which is 20
  (confirmed in-game 2026-08-13). A `RestockTargetCount` above 20 is therefore
  unreachable, and setting it there creates a hover loop: the mod asks for the shortfall
  to the unreachable target, the quiver accepts only enough to reach 20, the archer fires
  a couple of arrows, and the whole cycle repeats on the next pass with a full settlement
  storage walk each time. Measured at v1.1.1 with the target set to 40, four villagers
  produced 228 of the run's 254 top-ups, one of them logging this same line 61 times:
  `restocked 2/22 'Iron Tipped Arrow' for villager 'Björn' from Improved Warehouse 4 x2`.
  The defaults are now a target of 20 and a trigger of 5, so an archer is filled to a
  full stack, fires fifteen arrows, and only then costs one walk.

**First run (v1.1.0, confirmed in-game 2026-08-12):**

- The mod loaded as `VillagerAmmoMod v1.1.0 loaded (polling mode). Enabled=True, ...
  RestockFromStorage=True, RestockTargetCount=40, RestockWhenBelow=20`.
- 110 `restocked` success lines. Two verbatim examples: `restocked 18/38 'Iron Tipped
  Arrow' for villager 'Asmund' from Improved Warehouse 4 x18` and `restocked 2/22
  'Wood Arrow' for villager 'Bosse' from Workshop House 4 x2`.
- The per-pass summary read `87 manager(s) checked` in steady state, so the villager
  gate admits archers rather than rejecting them. Passes topped up between 0 and 9
  villagers each.
- Zero `[VillagerAmmo]` errors, exceptions or warnings across the whole session, and
  no `FileLoadException` or Smart App Control block.
- The settlement genuinely ran dry, logged as `restock found nothing for 'Greta': wanted
  22 'Wood Arrow', settlement holds 0`, which is the intended economy rather than a
  defect. Two such lines appeared, both for Wood Arrow.
- The snapshot walk reported `SettlementStock rebuilt: 132 distinct item type(s), 371
  container-entr(y/ies), 16 blacklisted, 200 duplicate listing(s) skipped.` The 200
  skipped duplicate listings confirm the ported dedupe is load-bearing here, the same
  way it is in CraftFromStorageMod.

**v1.1.1 run result (confirmed in-game 2026-08-13):**

- The snapshot fix worked. The run logged 71 `restock pass:` lines against 64
  `SettlementStock rebuilt:` lines, so a settlement walk now happens roughly once per
  pass rather than once per villager served.
- The worst pass measured `restock pass took 92.3 ms` and the fastest 57.9 ms, against
  `restock pass took 574.5 ms` at v1.1.0.
- 254 successful top-ups, more than double the previous run's 110, with zero
  `[VillagerAmmo]` errors, exceptions or warnings and no Smart App Control block.
- The walk itself is essentially the whole remaining cost of a pass, measured at up to
  `settlement snapshot rebuild took 83.8 ms` across 371 container entries.

## Config (`com.askamods.villagerammo.cfg`, hot-reloaded every 30 s)

- `[General]` `Enabled` (**true**); `RefundOnlyWhenShooting` (**true**);
  `RecentShootingWindowSeconds` (**3.0**); `EnableDiagnostics` (**false** since v1.0.0 — shipped;
  flip to true when troubleshooting).
- `[TargetCleanup]` `TargetCleanupEnabled` (**true**); `StuckArrowThreshold` (**10**);
  `CleanupCheckSeconds` (**60**); `ArrowCategoryMatch` (**"Arrows"** — category parent-chain
  substring); `TargetArrowRadius` (**15** m); `TargetNameMatch`
  (**"ArcheryTarget,TrainingDummy"** — case-insensitive GameObject-name substrings, parsed each
  cleanup pass).
- `[Restock]` `RestockFromStorage` (**false**); `RestockTargetCount` (**20**);
  `RestockWhenBelow` (**5**); `RestockCheckSeconds` (**10**);
  `RestockArrowPreference` (**"Arrow,Stone Arrow,Iron Arrow"**);
  `RestockSnapshotTtlSeconds` (**10**); `RestockBlacklistContainerTypes`
  (**"CharacterFlask,CharacterBuilder,ArmorRack,ArmorRackSmall,ArmorRackLarge,Storage_Core,Storage_DecorationsTop,Storage_SmallItems_Outhouse"**).

## Log lines

- Always-on: `culled N/M stuck arrows near T target(s)` when a cull removed anything (gated
  diagnostics-or-nonzero since v1.0.0); WARNING `released stuck arrows: X -> Y` on secondary-pass
  hits (rare).
- Diagnostics-gated: per-instance capture lines, `refunded (count=N, state=S)`,
  `drop of N adopted (state=S, lastShooting age=X.Xs)`, one-per-session target census
  (`[census] target: '<name>' path='<chain>' matched=…` — the data `TargetNameMatch` is tuned from).

## Open / resolved

- ⚠️ **Unexercised path:** manual arrow withdrawal from an idle villager (should NOT refund — the
  grace window + state gate should adopt it as a withdrawal; never explicitly tested).
- **RESOLVED as moot (2026-07-12):** the player-on-mount refund risk (mount shooting possibly
  routing through base `RangedManager` without `IsPlayer`) — **there are no mounts in ASKA**;
  `RiderRangedManager` is dead code. On-foot player arrows deplete normally (user play experience
  since 2026-07-11 with the mod active).

## Dead-ends (do not retry)

### Patching `RangedManager._OnAmmoRemoved` — FATAL with any binding

CLAUDE.md's universal IL2CPP gotcha list points here for the full evidence.

**Mechanism (highly confident — reproduced 3×, crash forensics via minidump):** Harmony resolves
the TARGET method's parameter types when building the detour. Patching a method whose signature
contains **inventory-family types** (`Item`, `ItemCollection`, `ItemEventContext`) forces too-early
il2cpp class-init of those types during plugin loading — before the game's own init chain. Native
class constructors run inside the trampoline setup, hit an unready dependency, and the process dies
via fatal CLR exit (`coreclr.dll+0x1d1fdd`) with **no managed exception**.

**Evidence (2026-07-11):**
- v0.1.0 (full bindings) and v0.1.1 (reduced bindings: `__instance` + int) crashed at the exact
  same load point; crash stack shows `SandSailorStudio.Inventory.Item..cctor` (native class init);
  BepInEx log cuts cleanly, pure native AV. Dumps:
  `%LOCALAPPDATA%\CrashDumps\Aska.exe.{11360,42548,30908}.dmp`.
- Crash persisted with ALL other mods disabled — the trigger is the patch attempt itself.
- Contrast: OuthouseComposterMod patches four `ItemContainer` methods with `ItemInfo` bindings and
  loads fine — no `Item`/`ItemCollection`/`ItemEventContext` in those signatures, supporting the
  inventory-family-specific hypothesis.
- Forensics recipe: `_explore/parse_minidump.ps1` + Cpp2IL dummy-DLL RVA mapping (architecture.md
  → Native Crash Diagnosis).

**Workaround (the shipped design):** zero-parameter lifecycle capture (`Awake` postfix) + polling —
the detour never touches a method with inventory-family parameters.

### `ReleaseAllStuckObjects()` as the arrow cleanup (v0.2.0) — ineffective

Only clears arrows registered at hit-time; save/load restores stuck arrows as plain
`DynamicItemObject` world items without re-registering them (in-game 2026-07-11: 2,548 tracked
arrow ground items at the range vs `0 with stuck-registry entries`). Cull ground items instead
(the v0.2.1 design above).

### Invalidating the settlement snapshot per villager — a half-second main-thread freeze

Invalidating the storage snapshot after each villager served makes the next villager in
the same pass re-walk the whole settlement. Measured in-game 2026-08-12 at v1.1.0: 36
`restock pass:` lines against 110 `SettlementStock rebuilt:` lines and 110 `restocked`
lines, so rebuilds tracked successes rather than passes. Each rebuild measured up to
`snapshot rebuild took 108.5 ms` over 371 container entries, and the worst whole pass
measured `restock pass took 574.5 ms`. Since the pass runs every 10 seconds, that is a
repeating half-second stutter. The v1.1.1 fix decrements the snapshot entry in place as
arrows are taken (`candidate.Qty -= added` in `AmmoRestock.TryRestock`) and invalidates
once per pass in `AmmoTracker.RunRestockPassCore`, gated on `toppedUp > 0`.
`SettlementStock.GetCandidates` also filters `Qty > 0` so a container drained within one
pass is not retried. The general rule: a cached settlement walk must be decremented,
never discarded, inside a loop that serves several consumers.

## Useful confirmed world facts

- One settlement archery range tracked **103 `ProjectileTargetHelper` instances** (targets are far
  more numerous than the visible racks).
- First v0.2.1 cull: **2,533/2,533 stuck arrows removed** within ~60 s of world load; framerate
  recovered from ~2 FPS to normal (2026-07-11).
- Perf (2026-07-12): poll ≤20 ms rare spikes, 60 s cleanup avg 7.7 ms (architecture.md → Mod-side
  frame hitches).

## Version history

- **v1.1.2** (2026-08-13): retuned defaults to one stack - RestockTargetCount 40 to 20,
  RestockWhenBelow 20 to 5. Config text only, no logic change. Ends the hover loop caused
  by an unreachable target. ⚠️ Not yet run in-game.
- **v1.1.1** (2026-08-12): snapshot decremented in place instead of invalidated per
  villager; one invalidation per restock pass; `GetCandidates` skips drained containers.
  Fixes the 574.5 ms pass measured at v1.1.0, confirmed in-game 2026-08-13 with worst
  pass down to 92.3 ms. Run exposed the unreachable-target hover loop fixed in v1.1.2.
- **v1.1.0** (2026-08-12): storage-restock mode (`Restock` config section, `SettlementStock.cs` +
  `AmmoRestock.cs`, `RunRestockPass`, villager gate). Replaces the refund when on. Built and
  publish-gate clean; confirmed in-game 2026-08-12. The run exposed a pass-time defect
  fixed in v1.1.1.
- **v0.1.0/v0.1.1** (2026-07-11): event-patch designs via `_OnAmmoRemoved` — hard native crash at
  plugin load (see Dead-ends).
- **v0.1.2** (2026-07-11): polling redesign — worked but leaked refunds on post-aim drops.
- **v0.1.3** (2026-07-11): shooting grace window closed the leak (146 refunds, 0 false adoptions).
  Refund feature done.
- **v0.2.0** (2026-07-11): `ReleaseAllStuckObjects` cleanup — ineffective (see Dead-ends).
- **v0.2.1** (2026-07-11): own `DynamicItemObject` tracking + ground-item cull — confirmed
  2,533-arrow cleanup, FPS recovery.
- **v0.2.2–v0.2.3** (2026-07-12): perf stopwatches; cfg reload 5 s→30 s; target census +
  `TargetNameMatch` scoping (confirmed `culled 74/74 near 12 target(s)`).
- **v1.0.0** (2026-07-12): Nexus ship — `EnableDiagnostics` default false; cull summary gated
  diagnostics-or-nonzero.
