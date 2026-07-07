# Mod 19: GroundItemVacuumMod — COMPLETE (v1.0.1)

**Goal:** clear loose ground items (dropped/decayed clutter — sticks, resin, firewood, stones, bark)
on a configurable hotkey (or timer) to reduce ground clutter. Confirmed in-game 2026-07-07: removes
the debris cleanly with only a minor ~2-frame hitch on ~1165 removals, no crash.

> **Important finding (2026-07-07):** this mod does its job, but **ground clutter was NOT the user's
> framerate bottleneck.** Removing ~1165 items barely moved FPS. A vanilla-vs-modded test (disable
> BepInEx via `doorstop_config.ini enabled=false`) showed **vanilla ~80 fps at base / 110-120 outside
> vs. ~30-40 with mods** on an RTX 4090 + i9-13900K @ 3440x1440, with graphics settings/DLSS/sky-look
> making no difference ⇒ the FPS loss is a **main-thread stall from a loaded mod**, not rendering and
> not ground items. Culprit isolation (bisect) is pending — see SESSION_HANDOFF / the prime suspect is
> TreeRespawnMod's per-tick datasync patch (~19k calls/sec over ~90k tree entries in a prior log).

## Game subsystem: Dynamic ground items
- `SSSGame.DynamicItemObjectManager : MonoBehaviour` — a **per-streaming-cell** manager (NOT a
  singleton; ~31 live at once in a loaded world), holding an intrusive doubly-linked list of the
  cell's dynamic items: `_head` → `DynamicItemObject.NextDynamicObject` / `PreviousDynamicObject`.
  `Awake()`/`OnDestroy()`, `RegisterDynamicObject`/`UnregisterDynamicObject`, `FixedUpdate()` (batched).
- `SSSGame.DynamicItemObject : MonoBehaviour` — one per ground item. `_itemObject : WorldItemObject`,
  `transform.position`, `OnEnable()`/`OnDisable()` (registration lifecycle).
- `SSSGame.WorldItemObject : ItemComponent` — `RemoveObjectFromWorld()` = the game's own network-safe
  delete (what the destroy-confirm dialogue uses). `ItemInstance : Item` (inherited from ItemComponent).
- Identity: `WorldItemObject.ItemInstance (Item) → .info (ItemInfo) → .Name` + `.category`
  (`ItemCategoryInfo.Name` + `.parent` chain). All plain managed strings — no interop casting needed.

## Working approach (v1.0.1)
- **Own-set item tracking (NOT list traversal):** maintain a `HashSet<DynamicItemObject>` via Harmony
  patches on **`DynamicItemObject.OnEnable`** (add) / **`OnDisable`** (remove) — Unity-guaranteed
  lifecycle messages, inlining-immune, bracketing exactly the window an item is live/safe-to-touch.
  Cleared on local-`PlayerCharacter.Despawned` (world-leave) so no stale wrapper survives a reload.
- **Sweep:** snapshot the set → for each item read name/category/position (per-step trace-loggable) →
  filter by radius + `OnlyItems` allow-list + `ExcludeItems` + `ExcludeCategories` → in DryRun just
  log+HUD the counts and a per-name + category taxonomy; else call `RemoveObjectFromWorld()` on each.
- **Host-gated** real removal (`NetworkObject.Runner.IsServer/IsSharedModeMasterClient`); DryRun scan
  allowed for anyone (read-only). Cyan OnGUI HUD summary. Optional `AutoVacuumMinutes` timer.

## DEAD-END — do NOT walk the game's linked list (v1.0.0 native crash)
v1.0.0 captured the managers and walked `_head` → `NextDynamicObject`, calling native getters
(`GetInstanceID`, `transform`, `_itemObject`, item-info) on every raw node. In a world with 31
streaming cells full of physics-active sticks this **hard-crashed the game natively** — WER
`coreclr.dll+0x1d1fdd` (CLR fatal-error chokepoint), no managed exception, `try/catch` powerless. The
minidump confirmed a native access violation beneath the walk frames: a node whose native backing was
mid-teardown was dereferenced. This is a concrete instance of the universal gotchas *"never cache/read
per-world native wrappers that may be gone"* and *"query persistent managers / maintain your own list
via lifecycle patches over ephemeral components."* The OnEnable/OnDisable own-set approach fixes it by
never touching the game's list pointers.

## Config (`com.askamods.grounditemvacuum.cfg`)
- `General/VacuumHotkey` (default `v`), `DryRun` (default **true** — scan-only until configured),
  `Radius` (default `60` m) / `VacuumEntireWorld` (default false), `HostOnly` (default true),
  `AutoVacuumMinutes` (default `0` = off), `Diagnostics` (default true), `TraceEachItem` (default true
  — per-item step log so a native crash pinpoints the failing item/step; turn off once stable).
- `Filters/OnlyItems` (allow-list substrings; empty = all), `ExcludeItems`, `ExcludeCategories`
  (default `Weapon,Armor,Clothing,Tool,Equipment` — matched against the category name + parent chain).
- **Recommended "debris only" config** (user's choice, confirmed to keep gear/logs/planks intact):
  `OnlyItems = Stick,Small Stone,Bark,Twig,Young Fir,Resin,Firewood`, `ExcludeItems = Hardwood,Long`.

## Deferred (next session)
Bake the recommended debris config into the code defaults (bump to v1.0.2), flip `TraceEachItem`/
`Diagnostics` code defaults to false, add to `sync-plugins.ps1` awareness + the Nexus upload workflow.
