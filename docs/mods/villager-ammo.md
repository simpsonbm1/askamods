# Mod 24: VillagerAmmoMod — COMPLETE (v1.0.0, on Nexus as "Unlimited Arrows for Villagers")

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

## Config (`com.askamods.villagerammo.cfg`, hot-reloaded every 30 s)

- `[General]` `Enabled` (**true**); `RefundOnlyWhenShooting` (**true**);
  `RecentShootingWindowSeconds` (**3.0**); `EnableDiagnostics` (**false** since v1.0.0 — shipped;
  flip to true when troubleshooting).
- `[TargetCleanup]` `TargetCleanupEnabled` (**true**); `StuckArrowThreshold` (**10**);
  `CleanupCheckSeconds` (**60**); `ArrowCategoryMatch` (**"Arrows"** — category parent-chain
  substring); `TargetArrowRadius` (**15** m); `TargetNameMatch`
  (**"ArcheryTarget,TrainingDummy"** — case-insensitive GameObject-name substrings, parsed each
  cleanup pass).

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

## Useful confirmed world facts

- One settlement archery range tracked **103 `ProjectileTargetHelper` instances** (targets are far
  more numerous than the visible racks).
- First v0.2.1 cull: **2,533/2,533 stuck arrows removed** within ~60 s of world load; framerate
  recovered from ~2 FPS to normal (2026-07-11).
- Perf (2026-07-12): poll ≤20 ms rare spikes, 60 s cleanup avg 7.7 ms (architecture.md → Mod-side
  frame hitches).

## Version history

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
