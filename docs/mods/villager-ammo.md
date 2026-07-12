# Mod 24: VillagerAmmoMod — COMPLETE (v0.2.3)

**Goal:** villagers assigned to the archery range and other ranged combat roles (defenders, hunters)
never run out of arrows. Ammo consumed during shooting is refunded in place, so the carried arrow
stack holds level. The player's own arrows are unaffected (villager-only gate).

## v0.2.2–v0.2.3 — [Perf] stopwatches + target filtering + cfg reload cadence (2026-07-12)
v0.2.2 added Stopwatch instrumentation on poll and cleanup passes; cfg reload cadence 5s→30s (perf
arc coordination across mods); one-time-per-world-session target-helper census (logs
`[VillagerAmmo][census] target: '<name>' path='<4-ancestor chain>'`, diagnostics-gated, reset on
world-leave). Census finding: 112 captured ProjectileTargetHelpers = 6 ArcheryTarget + 6
TrainingDummy + **79 villagers** + 5 skeletons + 1 boss (CharacterRagnar) + wild/tame animals +
harvest nodes — ProjectileTargetHelper component rides on characters/creatures/harvestables too
(arrows stick in anything), so v0.2.1's cull was effectively town-wide.

v0.2.3 adds target **filtering** via new `[TargetCleanup] TargetNameMatch` config (default
"ArcheryTarget,TrainingDummy"; case-insensitive substrings matched against GameObject name; parsed
each cleanup pass so config edits apply next tick); census lines now append `matched=` so the user
can verify filtering. The native ReleaseAllStuckObjects sweep remains unfiltered as a secondary pass
(per-helper, currently no-op). Cleanup pass now logs `culled N/M stuck arrows near T target(s)`
(fire-verification, always-on). Confirmed in-game 2026-07-12: `culled 74/74 stuck arrows near 12
target(s)` — range stays clean (~70–90 arrows/min accumulate) while town-wide arrows untouched. Perf
data: poll ≤20 ms rare, 60s cleanup avg 7.7 ms (see docs/architecture.md → Mod-side frame hitches).

## v0.2.1 — Ground-item cull for persisted stuck arrows (confirmed in-game 2026-07-11)

**Status: confirmed in-game 2026-07-11.** v0.2.0's `ReleaseAllStuckObjects` cleanup was INEFFECTIVE:
persisted stuck arrows do NOT live in `ProjectileTargetHelper._stuckObjects` (that registry only fills
at hit-time via `_RegisterStuckAmmo`; save/load restores arrows as ordinary `DynamicItemObject` world
items without re-registering). In-game diagnosis (2026-07-11): GroundItemVacuum dry-run census at
the archery range showed **2,548 tracked `DynamicItemObject` ground items** with category chain
`Arrows/Weapons`, while v0.2.0's diagnostic sweep reported `103 target(s), 0 with stuck-registry entries`.

v0.2.1 fixes this: **own `DynamicItemObject` tracking set** (OnEnable postfix/OnDisable prefix
capture — GroundItemVacuum pattern; parameterless targets, safe under inventory-family patch gotcha)
+ every `CleanupCheckSeconds` (60 s), host-gated (`Runner.IsServer || IsSharedModeMasterClient`),
cull ground items whose category chain contains `ArrowCategoryMatch` (default "Arrows") AND within
`TargetArrowRadius` (default 15 m, squared-distance) of any tracked `ProjectileTargetHelper` position
once the count ≥ `StuckArrowThreshold` (10). Culled via `WorldItemObject.RemoveObjectFromWorld()`
(proven by user's manual GroundItemVacuum vacuum before v0.2.1 existed).

**In-game results (2026-07-11):** v0.2.1 **culled 2,533/2,533 stuck arrows near 103 target(s)** within
~60 s of world load; arrows visibly disappeared and framerate recovered from ~2 FPS to normal
(user-observed).

**Mechanism:**
- `ProjectileTargetHelper.Awake` postfix (parameterless) still captures targets into a static registry
  (cleared on world-leave).
- New tracking hook: `DynamicItemObject.OnEnable` postfix + `OnDisable` prefix capture/uncapture ground
  items into a static locked `HashSet<DynamicItemObject>` (matching GroundItemVacuum's safe pattern).
- Cleanup every `CleanupCheckSeconds`, host-gated, iterates tracked `DynamicItemObject` set:
  - Check category chain (parent-walk) for `ArrowCategoryMatch` ("Arrows")
  - Check distance-squared to any tracked `ProjectileTargetHelper.position` ≤ `TargetArrowRadius²` (15²)
  - When matches ≥ `StuckArrowThreshold` (10): cull via `WorldItemObject.RemoveObjectFromWorld()`
  - Always-on log: `culled N/M stuck arrows near T target(s)` (fire-verification).
- Old `ReleaseAllStuckObjects` sweep retained as secondary pass with Warning-level errors on each
  release + per-pass summary (now typically logs 0 released since `_stuckObjects` empty after reload).

**New config section `[TargetCleanup]`:**
- `TargetCleanupEnabled`=true
- `StuckArrowThreshold`=10
- `CleanupCheckSeconds`=60
- `ArrowCategoryMatch`="Arrows" (category to match in parent chain; arrows player shoots into range
  targets are also culled; loose arrows away from targets untouched)
- `TargetArrowRadius`=15 (meters from a target center to cull arrows)

**Diagnostic log levels** (always-on, pre-Nexus):
- `[VillagerAmmo] culled N/M stuck arrows near T target(s)` — periodic summary (INFO level)
- `[VillagerAmmo] released stuck arrows: {before} -> {after}` — secondary ReleaseAllStuckObjects pass,
  if any (WARNING level on hits)

**Related finding (useful world fact):** a settlement archery range tracked **103 `ProjectileTargetHelper`
instances** (far more than visible visual racks — targets are numerous per range structure).

## v0.2.0 — Stuck-arrow target cleanup (ineffective — superseded by v0.2.1)

**Status: built + deployed 2026-07-11; INEFFECTIVE — see v0.2.1.** The v0.1.3 arrow-litter risk
materialized in co-op with unlimited villager ammo: ~2000 recoverable arrows accumulated stuck in
archery-range targets and tanked framerate for both players near town. v0.2.0 attempted scheduled
culling via the game's own machinery (`ProjectileTargetHelper.ReleaseAllStuckObjects()`, Cecil-verified
2026-07-11), but this only clears arrows registered at hit-time, not persisted arrows restored from
save/load as DynamicItemObjects.

## v0.1.3 — Polling redesign with grace window (confirmed in-game 2026-07-11)

**Status: shipped.** Poller-based ammo refund (the polling approach from the research plan,
not the initially-attempted event-patch design). Session log tally: 146 refunds, 0 false adoptions,
both 'Wood Arrow' and 'Iron Tipped Arrow' refunded. 76 RangedManagers tracked in a live world.

v0.1.0/v0.1.1 attempted event-patch designs via `RangedManager._OnAmmoRemoved` — both hard-crashed
the game during plugin loading with a native AV at `coreclr.dll+0x1d1fdd` (the fatal CLR-exit choke
point) and a class-init stack trace for `SandSailorStudio.Inventory.Item`. The crash was reproduced
3× on 2026-07-11 regardless of which postfix binding was used, and persisted even when all other mods
were disabled — proving that **any Harmony patch on `RangedManager._OnAmmoRemoved` is a fatal dead-end**
(documented in architecture.md and the universal IL2CPP gotchas list).

v0.1.2 pivoted to polling per the fallback design: `AmmoTracker` MonoBehaviour polls every 0.5 s,
per-manager (try/catch-guarded, removed on exception), checking `CurrentRangedAmmo.RealAmmoCount`
against a baseline. On count increase → adopt (restock). On count drop → read the last-shoot timestamp
from `RangedManager.State` (Aim/Fire/Reload) and refund iff **within a grace window**
(`RecentShootingWindowSeconds`, default 3.0 s) OR if `!RefundOnlyWhenShooting` config gate. Otherwise
adopt as deliberate withdrawal. The grace window solved a leak: v0.1.2's raw drop read was mistaking
post-aim drops (state = StandBy, not shooting) for withdrawals — the grace window closed that race.
v0.1.3 logs show 146 refunds and 0 false adoptions.

## Game subsystem: Ranged combat & ammo

All findings Cecil-verified 2026-07-11; in-game-confirmed where the mod exercises them.

### RangedManager (base shooter class)
- `SSSGame.Combat.RangedManager` (NetworkBehaviour) — base class for all ranged shooters (player
  and villagers). Derived: `PlayerRangedManager`, `RiderRangedManager`. **Villagers likely run the
  base class** (confirmed in census: 76 RangedManager instances in a live world; all carry `IsPlayer=false`).
  - `IsPlayer : bool` — **clean villager-only gate** (player never refunded).
  - `HasAuthority : bool` — network authority check (write-gated).
  - `State : RangedManager.AimState` — nested enum: None=0, StandBy=1, Aim=2, Fire=3, Reload=4.
  - `CurrentRangedAmmo : RangedAmmo` — the equipped-quiver component (see below).
  - `Awake()` patchable as postfix (one-time per instance; PARAMETERLESS capture confirmed in-game).

### RangedAmmo (equipped quiver)
- `SSSGame.Combat.RangedAmmo` (MonoBehaviour) — the equipped-quiver component. Ammo is **a real arrow
  item stack**, not an abstract counter.
  - `_itemContainer : ItemContainer` — the backing inventory (a `SandSailorStudio.Inventory` type).
  - `RealAmmoCount : int` — the actual stack size (read-safe, compared every 0.5 s).
  - `_OnAmmoRemoved(ItemCollection, Item, Int32, ItemEventContext)` / `_OnAmmoAdded(...)` — item-event
    handlers (UNPATCHABLE — documented dead-end).

### ItemContainer (inventory subsystem)
- `SandSailorStudio.Inventory.ItemContainer` — the ammo backing store.
  - `GetItem(int) : Item` — fetch stack at index.
  - `AddItems(ItemInfo, int) : int` — refund path (adds count to same info, returns actual added).
  - `AddItems(Item, int) : int` — overload taking an `Item` (used when info is known from GetItem).

### Villager shooting drivers
- `SSSGame.AI.FSM.FSM_Training` (archery-range FSM) — `timeLimitsToShoot`, complaint gate
  `ComplainLoadoutNotFulfilled` (training requires bow + arrow loadout).
- `SSSGame.TrainingOutlet : StructureTaskDispatcher` — archery-range structure, `assignedVillager`,
  `_trainingQuest`.
- `FSM_RangedCombat` — real combat FSM for defenders/hunters.
- `Complaint.c_defender_needAmmo_format` — the out-of-ammo complaint (triggered when stack empties).

## Working approach (v0.1.3 — poller + grace window)

**Polling design — why this works:**
- Patching `_OnAmmoRemoved` is a fatal dead-end (crashes during plugin load; see IL2CPP gotcha).
- **Own-instance polling** bypasses the detour entirely: walk captured `RangedManager` instances
  from `Awake` postfix (one-time, per-instance), compare `RealAmmoCount` every 0.5 s.
- **Grace window** closes the drop-detection race: drops within 3 seconds of the last recorded shoot
  state (Aim/Fire/Reload) are refunded; older drops are adopted as deliberate withdrawals.

**Mechanism:**
1. Harmony postfix on `RangedManager.Awake()` — **PARAMETERLESS** (no detour of the original body;
   post-return only) — captures each instance into a static locked `HashSet<RangedManager>` registry.
   One-time fire-verify log per instance (confirmed in-game).
2. `AmmoTracker : MonoBehaviour`, ClassInjector-registered, DontDestroyOnLoad, polls every 0.5 s
   in `Update()`:
   - Per tracked manager (guarded per-manager try/catch; remove from registry on exception/null)
   - Skip `IsPlayer==true` (fail-safe: player never refunded)
   - Skip `HasAuthority==false` (co-op safety: only host/authority refunds)
   - Read `State` EVERY poll → record `LastShootingSeen[mgr] = Time.time` when state ∈ {Aim, Fire, Reload}
   - Compare `CurrentRangedAmmo.RealAmmoCount` vs. `Dictionary<RangedManager, int>` baseline (per-manager)
   - Count increase → adopt new baseline (restock detected, item added elsewhere)
   - Count drop → refund iff (`!RefundOnlyWhenShooting` config) OR (`State ∈ {Aim, Fire, Reload}`) OR
     (`Time.time - LastShootingSeen[mgr] <= RecentShootingWindowSeconds`); else adopt as deliberate withdrawal
   - Refund = `ammo._itemContainer.AddItems(info, deficit)` where `info = container.GetItem(0)?.info`
     or fallback to `Dictionary<IntPtr, ItemInfo>` last-known cache (keyed by container.Pointer, covers
     last-arrow-empty-stack case)
   - Baseline = count + added
3. **World-leave safety** (`PlayerCharacter.Spawned`/`Despawned` pair, documented universal gotcha) —
   on local-player Despawned = world-leave, clear ALL per-world state: registry, baselines,
   ItemInfo cache, LastShootingSeen. Prevents stale-wrapper native-AV.
4. Live config reload every 5 s (SeedScout pattern).

## Config (`com.askamods.villagerammo.cfg`)
- `General/Enabled` (default **true**)
- `General/RefundOnlyWhenShooting` (default **true** — refund only during aim/fire/reload cycles,
  not on withdrawal)
- `General/RecentShootingWindowSeconds` (default **3.0** — grace window for post-aim drops)
- `General/EnableDiagnostics` (default **true** — pre-ship default; flip to false before Nexus upload)

**Diagnostic log lines** (when `EnableDiagnostics=true`):
- `[VillagerAmmo] RangedManager.Awake captured…` — once per instance at world load (confirms registry).
- `[VillagerAmmo] refunded (count=N, state=State)` — on each refund (shows state, count delta).
- `[VillagerAmmo] drop of N adopted (state=State, lastShooting age=X.Xs)` — on deliberate withdrawal
  (diagnostic: why it wasn't refunded).

## Pre-ship caveats (⚠️ pending in-game exercise)

**Two code paths remain unexercised in-game (known unknowns):**
1. **Manual arrow withdrawal from an idle villager** (removing arrows from a villager's inventory
   while not shooting) — should NOT refund (villager choosing to drop them). Relies on grace window +
   state gate; NOT tested.
2. **Player's own arrows** (player shooting) — should NOT refund (`IsPlayer==true` gate prevents it).
   Gate is applied; the fallback case (player on a mount) may route through base `RangedManager`
   without setting `IsPlayer` — **requires explicit confirmation** that player's arrows still deplete
   normally.

**Decision pending:** `EnableDiagnostics` currently defaults **true** per project rule (saves config
re-edit cycles pre-launch). Flip to **false** before any Nexus upload to avoid spamming user logs.

## Version history (all 2026-07-11)

- **v0.1.0:** event-patch design via `_OnAmmoRemoved` postfix (bindings: ItemCollection, Item, int,
  ItemEventContext) — **hard-crash at plugin load**, native AV `coreclr.dll+0x1d1fdd`.
- **v0.1.1:** same event-patch, reduced bindings (only `__instance`, int count) — **hard-crash at
  plugin load**, native AV + class-init stack, reproduced 3× (crash dumps:
  `%LOCALAPPDATA%\CrashDumps\Aska.exe.{11360,42548,30908}.dmp`). OuthouseComposterMod disabled for
  cross-mod test — crash persisted, killing the cross-mod-combination hypothesis.
- **v0.1.2:** polling redesign (RangedManager.Awake capture + AmmoTracker.Update poller) — worked
  but **leaked** (v0.1.2 log: refunds interleaved with `drop adopted (state=StandBy)` — post-aim
  drops during state return to StandBy were misread as withdrawals).
- **v0.1.3:** added `LastShootingSeen` grace window (`RecentShootingWindowSeconds`). Closed the leak.
  Final log: 146 refunds, 0 false adoptions. **SHIPPED.**

## Dead-end: Patching `_OnAmmoRemoved` (any binding)

**Mechanism (highly confident hypothesis — reproduced 3×, crash forensics via minidump):**
Harmony resolves the TARGET method's parameter types when building the detour. Patching a method
whose **signature contains types from the inventory family** (`Item`, `ItemCollection`,
`ItemEventContext`) forces too-early il2cpp class-init of those types during plugin loading —
**before the game's own init chain**. Native class constructors run from inside the Harmony trampoline
setup, hit a dependency that isn't ready yet, and crash via fatal CLR exit (`coreclr.dll+0x1d1fdd`)
with no managed exception.

**Evidence:**
- Both v0.1.0 and v0.1.1 crashed at the exact same point: right after WarpTour's tracker registration
  (~2 plugins after the patching mod loaded cleanly).
- The crash stack shows `SandSailorStudio.Inventory.Item..cctor` (native class init).
- BepInEx log cuts cleanly at that point — no managed exception, pure native AV.
- OuthouseComposterMod (confirmed loaded before VillagerAmmoMod) patches four methods taking
  `ItemContainer` params with `ItemInfo` bindings — all load fine (no Item/ItemCollection/ItemEventContext
  in their signatures). This contrasts with the crash and supports the inventory-family-specific hypothesis.
- Disabling all other mods except VillagerAmmoMod (v0.1.1) still crashes — the trigger is purely
  attempting the patch, not a cross-mod conflict.

**Workaround:** zero-parameter lifecycle capture (`Awake`) + polling — the detour never touches a
method with inventory-family parameters.

**Crash forensics:** the crash dumps were analyzed via `_explore/parse_minidump.ps1` + Cpp2IL
dummy-DLL RVA mapping (`_explore/map_crash_offsets.ps1` pattern, detailed in architecture.md
→ Native Crash Diagnosis).
