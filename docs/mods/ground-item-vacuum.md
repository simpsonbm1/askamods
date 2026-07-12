# Mod 19: GroundItemVacuumMod — COMPLETE (v1.1.3, on Nexus as "Ground Item Vacuum Cleaner")

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
  reviewed), `Radius` (default `60` m) / `VacuumEntireWorld` (default false), `HostOnly` (default
  true), `AutoVacuumMinutes` (default `0` = off), `Diagnostics` (default **false** since v1.1.3 —
  shipped), `TraceEachItem` (default **false** since v1.1.3 — only enable when investigating a
  crash; the last logged line pinpoints the failing item/step).
- `Filters/OnlyItems` (allow-list substrings; empty = all), `ExcludeItems`, `ExcludeCategories`
  (default `Weapon,Armor,Clothing,Tool,Equipment` — matched against the category name + parent
  chain).
- **Recommended "debris only" config** (user's choice, confirmed to keep gear/logs/planks intact):
  `OnlyItems = Stick,Small Stone,Bark,Twig,Young Fir,Resin,Firewood`, `ExcludeItems = Hardwood,Long`.
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
