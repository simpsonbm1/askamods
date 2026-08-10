# Mod 19: GroundItemVacuumMod — on Nexus as "Ground Item Vacuum Cleaner"

> **Status 2026-08-10.** Repo and Nexus both at **v1.9.2**, published on the user's instruction.
> The version it replaced on Nexus was **1.1.3**, in which `VacuumEntireWorld` skipped the
> `Radius` test against the mod's own tracked-item set only — that set holds items with a spawned
> GameObject, so the option cleared just the loaded area around the player despite its name.
> v1.9.2 replaces it with the whole-map data-layer walk described below. The dropped-item filter
> is confirmed in-game at v1.8.0. `Jotun Blood` is spared by a shipped `ExcludeItems` default
> rather than by code (user ruling 2026-08-10); that default is ⚠️ pending — not yet run in-game.
> The corpse sweep is REMOVED — see the REMOVED section below.

**Goal:** clear loose ground items (dropped/decayed clutter — sticks, resin, firewood, stones, bark)
on a configurable hotkey or timer. Confirmed in-game 2026-07-07: removes debris cleanly with only a
minor ~2-frame hitch on ~1165 removals, no crash.

> Note: ground clutter was NOT the framerate bottleneck it was suspected to be — removing ~1165
> items barely moved FPS. The real cost was mod-side per-frame work, diagnosed and fixed in the
> 2026-07-07 and 2026-07-11/12 perf arcs (see docs/architecture.md → "Mod-side frame hitches").

## Game subsystem: Dynamic ground items

- `SSSGame.DynamicItemObjectManager : MonoBehaviour` — a **per-streaming-cell** manager (NOT a
  singleton; ~31 live at once in a loaded world), holding an intrusive doubly-linked list of the
  cell's dynamic items: `_head` → `DynamicItemObject.NextDynamicObject` / `PreviousDynamicObject`.
- `SSSGame.DynamicItemObject : MonoBehaviour` — one per ground item. `_itemObject : WorldItemObject`,
  `transform.position`, `OnEnable()`/`OnDisable()` (registration lifecycle).
- `SSSGame.WorldItemObject : ItemComponent` — `RemoveObjectFromWorld()` = the game's own
  network-safe delete (what the destroy-confirm dialogue uses).
- Identity: `WorldItemObject.ItemInstance (Item) → .info (ItemInfo) → .Name` + `.category`
  (`ItemCategoryInfo.Name` + `.parent` chain). All plain managed strings — no interop casting.

## Working approach

- **Own-set item tracking (NOT list traversal):** maintain a `HashSet<DynamicItemObject>` via
  Harmony patches on **`DynamicItemObject.OnEnable`** (add) / **`OnDisable`** (remove) —
  Unity-guaranteed lifecycle messages, inlining-immune, bracketing exactly the window an item is
  live/safe-to-touch. Cleared on local-`PlayerCharacter.Despawned` (world-leave) so no stale
  wrapper survives a reload.
- **Sweep:** snapshot the set → for each item read name/category/position (per-step trace-loggable
  via `TraceEachItem`) → filter by radius + `OnlyItems` allow-list + `ExcludeItems` +
  `ExcludeCategories` → in DryRun just log+HUD the counts and a per-name + category taxonomy; else
  call `RemoveObjectFromWorld()` on each.
- **Host-gated** real removal (`NetworkObject.Runner.IsServer/IsSharedModeMasterClient`); DryRun
  scan allowed for anyone (read-only). Cyan OnGUI HUD summary. Optional `AutoVacuumMinutes` timer.
- **Live config hot-reload** (every 30 s): `VacuumTracker.Update()` calls `Plugin.Cfg?.Reload()` —
  BepInEx does NOT re-read an edited config on its own. All sweep settings are read fresh from
  `.Value` at sweep time; the hotkey (the only once-cached value) re-binds via `ApplyHotkey()`
  after each reload, logging `[Vacuum] Hotkey bound to <key>` when it changes. Confirmed in-game
  2026-07-08 (hotkey + DryRun flipped live without a relaunch).
- **Typing guard** (confirmed in-game 2026-07-10): the hotkey is ignored while a game text field
  (structure rename, etc.) is focused.

## `VacuumEntireWorld` — whole-map DATA-layer removal (repo v1.7.0, UNVERIFIED)

`VacuumEntireWorld = true` switches a sweep from the radius-gated object layer to a whole-map
walk of the game's item DATA layer (`WorldDataManager` → `InventoryItemDataHandler` → per-tile
`WorldTileData` → per-cell `InventoryCellDataContainer.itemBuffers`). This reaches items the
object layer never sees at all: tracking there is driven by `DynamicItemObject.OnEnable`, and the
game only gives a world-item record a spawned GameObject within some activation range (live
readings, confirmed in-game 2026-08-10: `interactionObjectsRange=32`, `closeRangeScale=1.5`,
`nearRangeScale=3.5`, `farRangeScale=5.5` — ⚠️ pending whether these exact fields gate spawning,
since the probe read the values without tracing a call site that uses them). Items outside the
activation range exist only as data, so no lifecycle message ever fires for them and the
object-layer sweep cannot count them, even with `VacuumEntireWorld` skipping its `Radius` check.

**It filters records the same way the radius-limited sweep does:** `OnlyItems` as an allow-list
(empty = every record is a candidate), then `ExcludeItems`, then `ExcludeCategories` against the
item's category name and parent-category chain. A live run with an empty `OnlyItems` therefore
removes every non-excluded loose item record on the whole map. A walk logs traversal counts,
per-name match counts (top 40), distinct matched-name count, and the largest horizontal matched
distance from the player — always before any removal happens. When `OnlyItems` is empty and the
run is live (not `DryRun`), the log also carries an explicit warning that no allow-list is set and
every non-excluded record map-wide will be removed, with the matched count.

**`Destroy(ref bool, ref InstanceDestructionLevel)` with `silent=false` removes the record
(confirmed in-game 2026-08-10).** The critical parameter is `silent`. Called with `silent=true`,
the call returns without throwing and removes nothing. Called with `silent=false`, it removes the
record from the cell's item buffer. v1.4.0 used `silent=true` on an instance obtained from
`InventoryItemDataHandler.GetInstance(cell, bufferId, index, false, false)` — a silent no-op.
Evidence from the v1.5.1 run with `silent=false`:
```
[Vacuum] Phase 2 route 1 (Destroy silent=false): removed 2079, failed 0.
[Vacuum] Self-verification: matched before removal=2079, removal calls succeeded=2079, matched remaining after=0.
```
`WorldItemObject.RemoveObjectFromWorld()` (Phase 1, object layer) continues to work: the same
run logged `Phase 1 (object layer, tracked items): removed 524/524.`

**Removal, v1.5.1, confirmed in-game 2026-08-10.** A live (non-`DryRun`) run performs
removal in two phases, in order:
- **Phase 1 — object layer.** Unchanged from v1.4.0: the mod's own tracked-item set
  (`DynamicItemObject`s with a spawned GameObject) is removed via
  `WorldItemObject.RemoveObjectFromWorld()` (confirmed in-game since v1.0.1), with the radius
  test skipped.
- **Phase 2 — data layer, route ladder.** Every record matched by the walk is removed by trying
  up to three routes in order, re-checking `InventoryItemInstancesBufferBase
  .FindIndexOfUniqueId(ref uint)` after each attempt (a negative return means the record is
  gone) and stopping at the first route that works:
  1. `WorldItemInstance.Destroy(ref bool silent, ref InstanceDestructionLevel level)` with
     `silent=false` (confirmed working in-game 2026-08-10).
  2. `InventoryItemDataHandler.RemoveInstanceDataSilent(InventoryItemInstance)` — UNTESTED.
     Route 1 succeeded on every record, so this was never reached. Do not record as working
     or failing.
  3. `InventoryItemInstancesBufferBase.RemoveInstanceData(int index, out int swapIndex)` —
     UNTESTED. Route 1 succeeded on every record, so this was never reached. Do not record
     as working or failing.
  Each route is wrapped in its own try/catch incrementing a per-route failure counter. The log
  reports one line per route (`removed`/`failed`) plus a line for records still present after
  all three routes.
- **Self-verification.** Unchanged from v1.4.0: after Phase 2, the mod re-walks the data layer
  with the same filter and logs the matched count before removal, how many removal calls
  succeeded, and the matched count still remaining.

`DryRun` still short-circuits both phases: a `DryRun` run does the full walk and logs the
would-remove counts for both phases, but removes nothing. `HostOnly` gates real removal the same
way the radius-gated sweep does; a `DryRun` walk stays available to anyone regardless of host
status.

**Scan mode confirmed in-game 2026-08-10** across three independent scans in one session (player at
different positions for each): all three completed with zero exceptions, zero skipped tiles/cells/
buffers, and identical traversal structure (tiles seen 183, cells seen 1978). Cells with inventory
containers read 233, 234, 234; buffers read 590, 592, 595; total records read 4832, 4843, 4851.
Matches against the allow-list read 2606, 2611, 2614 (ten distinct matched names across all three).
Largest horizontal distance from player was 610 m, 625 m, 824 m respectively. Scan 1 per-name
breakdown: Feathers 1000, Small Stone 634, Bark 472, Stick 350, Large Stone 78, Fibers 41, Resin
25, Onion Seeds 3, Carrot Seeds 2, Yellow Mushrooms 1 (counts sum exactly to 2606). Counts moved in
both directions between scans (Bark 472→476→480, Stick 350→350→346, Resin 25→25→24, Feathers
1000→1000→1004; Small Stone and Large Stone unchanged in all three).

**2026-08-10 evidence that motivated the dropped-item filter.** A live whole-map run
(`DryRun=false`, `OnlyItems` empty, `VacuumEntireWorld=true`, `IncludeCorpses=False`) deleted
records the radius-gated sweep never touches: Phase 1 removed 575 tracked items; Phase 2 route 1
matched and removed 3809 data-layer records across 40 distinct names. The user named four kinds
the radius sweep has never deleted, all of which that run removed: `Cave Fingers Growth` (x41),
`Iron Deposit` (x20), `Jotun Blood` (x190) and `Crawler Egg` (x54). The radius sweep only ever
sees `DynamicItemObject` instances (loose dropped items); the data layer's
`WorldDataSlot.INVENTORY` holds more than loose drops, and the filter logic before v1.7.0
targeted all of it. That same run also removed `Wight` corpses despite `IncludeCorpses=False`,
because the corpse setting gates only the object-layer corpse pass.

**The dropped-item filter, confirmed in-game 2026-08-10 (v1.8.0 live run).** It excludes
world-placed creatures and harvestables, with one exception named below. Log evidence:
```
[Vacuum] SCAN dropped-item filter (DynamicItemObject prefab check): excluded 445 record(s), undetermined/fail-closed 0 record(s).
[Vacuum] SCAN dropped-item filter excluded x204: Wight
[Vacuum] SCAN dropped-item filter excluded x54: Crawler Egg
[Vacuum] SCAN dropped-item filter excluded x41: Cave Fingers Growth
[Vacuum] SCAN dropped-item filter excluded x20: Iron Deposit
[Vacuum] SCAN dropped-item filter distinct excluded names: 17.
```
All 17 excluded names are creatures or world harvestables: `Wight` 204, `Crawler Egg` 54,
`Crawler Egg 2` 48, `Cave Fingers Growth` 41, `Crawler Egg 4` 31, `Crawler Egg 3` 22,
`Iron Deposit` 20, `Fenn` 6, `Skeleton Bruiser` 4, `Skeleton Bonker` 4, `Skeleton Berserker` 3,
`Wulfar` 3, and one each of `Skeleton Archer`, `Skeleton Headsman`, `Skeleton Warrior`,
`Fennmar` and `Skeleton Basher`. Those counts sum to exactly 445. **Zero records came back
undetermined**, so the descriptor→prefab→component chain resolved for every one of the 4262
records walked — the fail-closed branch was never exercised on this run and stays untested.

**Corpses are out of reach of the whole-map pass.** `Wight` at 204 is excluded by the dropped-item
filter: corpse creatures do not spawn from a prefab carrying `DynamicItemObject`, so the
data-layer pass cannot touch them. This held even while a separate corpse-sweep feature existed,
and that feature is now removed entirely (see the dead-end below).

**`Jotun Blood` is handled by a shipped default exclusion, not by code (user ruling 2026-08-10:
"keep the jotun blood clear, but default it to being blocked in the config for downloads").**
What was measured: the v1.8.0 run logged `[Vacuum] SCAN match x190: Jotun Blood`, absent from the
excluded list and not undetermined, so its source prefab carries `DynamicItemObject`. **The
prefab test therefore cannot separate `Jotun Blood` from ordinary debris** — that is the whole of
the evidence. Nothing in any run established what those 190 records actually are, and no
world-placed Jotun Blood was ever observed; that framing came from the user naming it on
2026-08-10 alongside `Iron Deposit` and `Crawler Egg`. Rather than build a per-record
discriminator on an unmeasured premise, v1.9.0 ships `ExcludeItems` defaulting to `Jotun Blood`
so a fresh install spares it. The user's own installs clear it, because BepInEx never rewrites an
existing config file and his line reads `ExcludeItems = Hardwood,Long,Ore`.

No per-record discriminator was built, and none should be without a new reason. If one is ever
needed, the untested candidate is `InventoryItemInstancesBufferBase.GetNetworkIdAt(ref int index)`
— a world-placed interactive object may carry an attached network id where loose debris does not
(`InventoryItemInstance` exposes the same value as `_attachedObjectNetworkID` /
`AttachedObjectNetworkID`). ⚠️ pending — never measured.

**The dropped-item filter mechanism.** The data-layer walk
requires a record's item to be a loose ground item before it can match: it resolves the item's
`ItemInfo` → `InventoryItemDataHandler.GetItemDescriptor(ItemInfo)` →
`InventoryItemDescriptor.GetSourceObject()` (the source prefab, inherited from
`WorldItemDescriptor`) → `prefab.GetComponent<DynamicItemObject>()`. A non-null component means
the item spawns as a loose ground item — the same class of object the radius-gated sweep's
own-set tracking is built from — and the record may match. The check is fail-closed: a null
`ItemInfo`/descriptor/prefab, a null component result, or any exception counts the record as
NOT a match and it is never removed; those records are tallied separately as "undetermined" and
logged as a count, distinct from records excluded because the check determined they are not a
dropped item. The result is cached per item name in a `Dictionary<string, bool?>` local to one
`Sweep()` call (shared by the main walk and the self-verification re-walk, never a static field)
since a world has only a few dozen distinct item names. Every walk (scan, live removal, and
self-verification) logs the excluded-record count, the undetermined count, and a per-name
breakdown of what was excluded (top 40 by record count) — this is how a run confirms the filter
excluded `Iron Deposit`/`Cave Fingers Growth`/`Crawler Egg`-type records rather than debris.

**Testing it needs an EMPTY `OnlyItems`.** The filter order is `OnlyItems` → `ExcludeItems` →
`ExcludeCategories` → dropped-item check. A non-empty allow-list stops `Iron Deposit`,
`Jotun Blood`, `Cave Fingers Growth`, `Crawler Egg` and `Wight` by name before they ever reach
the check under test, so they would never appear in the excluded breakdown and the run would
prove nothing about the filter.

**DEAD-END — the buffer class does not discriminate anything (confirmed in-game 2026-08-10).**
v1.8.0 tallies every record and match by the concrete class behind each
`InventoryItemInstancesBufferBase`, resolved natively per buffer. The live run read exactly one
class for every record in the world:
```
[Vacuum] SCAN records by buffer class x4262: InventoryItemInstancesBuffer
[Vacuum] SCAN matches by buffer class x3372: InventoryItemInstancesBuffer
```
`VegetationItemInstancesBuffer` never appeared, and `Cave Fingers Growth` sat in the same
`InventoryItemInstancesBuffer` as `Stick`. Do not reach for the buffer class as a discriminator.
The tallies are kept because they cost one dictionary per walk and would immediately show a second
class appearing in some other world.

Data-layer scale, confirmed in-game 2026-08-10 with an unfiltered probe walk: 4829 records
spanning over a kilometre, versus the object layer's largest recorded reach (four sweeps in one
session: `72`, `426`, `477`, `604` tracked, always equal to in-range; the largest object-layer
sweep on record is the ~1165 removals of 2026-07-07). Object-layer counts depend entirely on where
the player stands. The user reported feathers near a gathering hut that did not clear when he swept
from the mine entrance; the sweep log records no positions, so no individual sweep can be tied to a
place. Call sequence and detailed probe results: `docs/architecture.md` → "World item DATA vs.
spawned GameObjects".

## REMOVED — the corpse sweep (v1.9.2, user ruling 2026-08-10)

`Corpses/IncludeCorpses` and `Corpses/ExcludeCorpseNames`, the `Monster.Spawned`/`Monster.Despawned`
tracking patches and the whole corpse pass in `Sweep()` are **deleted**. Do not reinstate any of it
without a fresh decision from the user.

Why: it reached a public Nexus build (1.9.1) having **never been run in-game** — the project holds
no dated confirmation that it worked and no record that it failed. A config toggle that nobody can
answer questions about is worse than an absent feature. The user's words on the 1.9.1 upload:
"so you shipped a feature that was unfinished behind a config toggle that someone is going to turn
on and wonder why it doesnt work?"

The process failure that let it happen: `docs/nexus-upload.md`'s pre-upload checklist named this
exact setting, the check was read, and the upload went ahead without putting it to the user first.
**Anything flagged unfinished goes in front of the user BEFORE an upload, not into a handoff note.**

What was in it, if it is ever rebuilt: dead enemy/animal corpses are dead `SSSGame.Monster`
instances awaiting despawn (confirmed in-game 2026-07-18 — `CharacterRemains` is the PLAYER-corpse
system, and `Creature` is not a `Character` subclass, so `Character._CreateCharacterRemains` never
runs for enemies). Patch `Monster`'s own lifecycle overrides ONLY — never `Creature`/`Den`/`Pet`,
because a defeated den legitimately reads `IsDead` and despawning one permanently destroys the
spawner. Removal went through `Monster.DespawnImmediatelyIfStateAuthority()`, and unharvested loot
on the corpse was lost. Player and villager corpses are a different system and were never touched.

## DEAD-END — do NOT walk the game's linked list (v1.0.0 native crash)

v1.0.0 captured the managers and walked `_head` → `NextDynamicObject`, calling native getters
(`GetInstanceID`, `transform`, `_itemObject`, item-info) on every raw node. In a world with 31
streaming cells full of physics-active sticks this **hard-crashed the game natively** — WER
`coreclr.dll+0x1d1fdd` (CLR fatal-error chokepoint), no managed exception, `try/catch` powerless.
The minidump confirmed a native access violation beneath the walk frames: a node whose native
backing was mid-teardown was dereferenced. Concrete instance of the universal gotchas *"never
cache/read per-world native wrappers that may be gone"* and *"maintain your own list via lifecycle
patches over ephemeral components."* The OnEnable/OnDisable own-set approach fixes it by never
touching the game's list pointers.

## Config (`com.askamods.grounditemvacuum.cfg`, hot-reloaded every 30 s)

- `General/VacuumHotkey` (default `n` — `v` conflicted with the emote wheel), `DryRun` (default
  **true** — scan-only until configured; deliberate safety default, leave until exclusions are
  reviewed), `Radius` (default `60` m) / `VacuumEntireWorld` (default **false**), `HostOnly`
  (default true), `AutoVacuumMinutes` (default `0` = off), `Diagnostics` (default **false** since
  v1.1.3 — shipped), `TraceEachItem` (default **false** since v1.1.3 — only enable when
  investigating a crash; the last logged line pinpoints the failing item/step).
- `General/VacuumEntireWorld` (v1.3.0 scan, v1.4.0 removal, v1.6.0 filter logic, v1.7.0
  dropped-item filter): when true, a sweep walks the whole map's item DATA layer instead of only
  items spawned near the player. Filters records with the same
  `OnlyItems`/`ExcludeItems`/`ExcludeCategories` logic as the radius-gated sweep, plus a
  dropped-item-prefab check (see the section above) that restricts matches to records whose
  source prefab carries `DynamicItemObject` — an empty `OnlyItems` means every non-excluded
  dropped-item record map-wide is a target. A live run removes matches in two phases (object
  layer then data layer, see the section above) unless `DryRun` is true, in which case it only
  walks and logs. `HostOnly` gates real removal the same as the radius-gated sweep. Scan
  traversal, two-phase removal and the dropped-item filter are all confirmed in-game 2026-08-10.
- `Filters/OnlyItems` (allow-list substrings; empty = all), `ExcludeItems` (default `Jotun Blood`
  since v1.9.0 — see the whole-map section for why), `ExcludeCategories` (default
  `Weapon,Armor,Clothing,Tool,Equipment` — matched against the category name + parent chain).
- There is no `Corpses` section. It was removed in v1.9.2 — see the dead-end below. A config file
  written by an older build still carries the two dead keys; they do nothing and BepInEx leaves
  them in place.
- **The user's own "debris only" config**, read verbatim from his live cfg on 2026-08-10:
  `OnlyItems = Stick,Small Stone,Bark,Twig,Young Fir,Resin,Firewood, Stone, Seed, Yellow, Fiber,Feather`
  with `ExcludeItems = Hardwood,Long,Ore` and
  `ExcludeCategories = Armor,Clothing,Tool,Equipment,Weapon`.
  Deliberately NOT baked into the code defaults — fresh installs stay conservative
  (`DryRun=true`, no allow-list) so new users review a scan first.

## Version history

- **v1.0.0** (2026-07-07): linked-list walk — native crash (see DEAD-END).
- **v1.0.1** (2026-07-07): own-set lifecycle tracking — confirmed in-game (~1165 removals clean).
- **v1.0.2**: hotkey default `v` → `n` (emote-wheel conflict).
- **v1.1.0** (2026-07-08): live config hot-reload (5 s), confirmed in-game.
- **v1.1.1** (2026-07-10): typing guard, confirmed in-game.
- **v1.1.2** (2026-07-12): cfg reload cadence 5 s → 30 s (perf arc; no behavior change).
- **v1.1.3** (2026-07-12): shipped-defaults flip — `Diagnostics` and `TraceEachItem` default false
  (mod was already on Nexus with dev defaults; ship-rule catch-up).
- **v1.3.0** (confirmed in-game 2026-08-10): `VacuumEntireWorld=true` now runs a scan-only
  whole-map item-DATA-layer walk instead of just widening the object-layer sweep's radius.
  Requires a non-empty `OnlyItems` allow-list. Removal is not implemented in this version.
- **v1.4.0** (confirmed in-game 2026-08-10, Phase 2 defect found): `VacuumEntireWorld=true` live
  runs remove matches in two phases — object layer via `RemoveObjectFromWorld()` (confirmed
  working, 29/29), then data layer via per-record `FindIndexOfUniqueId` / `GetInstance` /
  `IsRemovable` / `Destroy(ref bool, ref InstanceDestructionLevel)`. Phase 2's `Destroy` call
  reported success on all 2576 records but removed none — self-verification caught it.
- **v1.5.0** (implemented but not run in-game): Phase 2 replaced with a three-route removal
  ladder per record (`Destroy` with `silent=false`, `RemoveInstanceDataSilent`, raw buffer
  `RemoveInstanceData`), stopping at the first route confirmed by `FindIndexOfUniqueId` to have
  removed the record, with per-route success/failure counters. Phase 1 and self-verification
  unchanged.
- **v1.5.1** (confirmed in-game 2026-08-10): the removal MECHANISM works — records really are
  deleted. This says nothing about WHICH records were targeted; v1.6.0's run showed the targeting
  was wrong. Route 1
  (`Destroy` with `silent=false`) removes records; `silent=true` is a silent no-op. Routes 2
  and 3 were never exercised on the test run (route 1 succeeded on all 2079 records) and remain
  untested. Config description updated to state removal runs map-wide when `DryRun` is false
  (allow-list and `HostOnly` gating unchanged).
- **v1.6.0** (run 2026-08-10 exposed defects): `VacuumEntireWorld`'s data-layer walk no longer
  requires a non-empty `OnlyItems` allow-list — it filters with the same
  `OnlyItems`/`ExcludeItems`/`ExcludeCategories` logic as the radius-gated sweep. A live run
  with empty `OnlyItems` logs an explicit warning with the matched count before removal. The
  2026-08-10 run exposed two defects, both since resolved by the dropped-item filter: the
  whole-map pass removed world-placed harvestables the radius sweep never sees, and it removed
  `Wight` corpses despite `IncludeCorpses=False`. `Corpses/IncludeCorpses` default flipped
  `true` → `false` (unfinished feature, code and config entries kept).
- **v1.7.0** (confirmed in-game 2026-08-10 as part of the v1.8.0 run): whole-map data-layer
  records now require a dropped-item-prefab check
  (`InventoryItemDataHandler.GetItemDescriptor(ItemInfo)` →
  `InventoryItemDescriptor.GetSourceObject()` → `GameObject.GetComponent<DynamicItemObject>()`)
  to match, restricting the whole-map pass to the same class of object the radius-gated sweep
  targets. Fail-closed on any null step or exception (record not removed, tallied as
  "undetermined"); result cached per item name for one `Sweep()` call. Every walk logs the
  excluded count, undetermined count, and a per-name excluded breakdown (top 40). Excluding
  `Wight` also closed the `IncludeCorpses` gap on the data-layer path.
- **v1.8.0** (confirmed in-game 2026-08-10): every data-layer walk additionally tallies records
  and matches by the concrete buffer class behind each `InventoryItemInstancesBufferBase`, and
  logs both tallies. Diagnostic only — no change to which records match or are removed. The run
  confirmed the dropped-item filter excludes creatures and world harvestables (445 records, 17
  names, zero undetermined) with `Jotun Blood` the one open exception, and ruled the buffer class
  out as a discriminator (one class for all 4262 records).
- **v1.9.0** (build-verified, ⚠️ pending in-game): `Filters/ExcludeItems` default changed from
  empty to `Jotun Blood`, so a fresh install spares it. No logic change — the prefab test cannot
  separate `Jotun Blood` from debris, and the user ruled that a shipped default exclusion is the
  answer rather than a per-record discriminator.
- **v1.9.1** (build-verified, ⚠️ pending in-game): `ExcludeItems` config description reworded to
  claim only what was measured. No behavior change. Published to Nexus 2026-08-10, replacing the
  previously published 1.1.3, then superseded the same day by v1.9.2.
- **v1.9.2** (build-verified, ⚠️ pending in-game): corpse sweep deleted in full — both `Corpses`
  config entries, the `Monster` lifecycle patches and the corpse pass in `Sweep()`. See the
  REMOVED section above for why and for what to rebuild from if it is ever wanted. No other
  behavior change; the whole-map pass is untouched.
