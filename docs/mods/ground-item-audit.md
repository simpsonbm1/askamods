# GroundItemAuditMod (Mod 31)

**Goal:** answer whether a data-layer sweep for loose world items would reach further than
GroundItemVacuumMod's current object-layer (spawned-GameObject) sweep, by counting how many
loose-item data records the per-cell inventory buffers hold, independent of whether each record
currently has a spawned GameObject.

DEV TOOL, NOT for Nexus. Read-only: never deletes, modifies, activates or deactivates anything.
Patches nothing (no Harmony patches at all).

## What it reads (v0.2)

`handler._instanceList` (the v0.1 target) is confirmed NOT the world's item store — see Results
below. v0.2 instead walks the per-cell inventory buffers that actually hold the records:

On hotkey press it dumps to the BepInEx log:
- the player's position and the `WorldDataManager` header (`TileSize`, `CellSize`,
  `CellResolution`, `_dataMap.Count`)
- the `WorldDataConfiguration` activation-range fields (`interactionObjectsRange`,
  `closeRangeScale`, `nearRangeScale`, `farRangeScale`)
- the resolved `InventoryItemDataHandler` (via `GetDataHandler<T>`, falling back to a native
  class-pointer walk of `_dataHandlers` if that returns null), plus `handler._instanceList.Count`
  logged once for contrast with the totals below (not walked further)
- `handler.GetSlotId()`, then every tile from `manager.FetchAllData()` (183 tiles, `_dataMap.Count`
  unchanged, in the v0.1 run)
- for each tile, its cells via `tile.QueryAllCells(DataAccessMode.FETCH, …)` (FETCH only, never
  CREATE, so the walk cannot create records)
- for each cell, `cell.GetDataContainer(slotId)`, identified as an `InventoryCellDataContainer` by
  native class pointer (managed `as`/`is` lies here — see architecture.md's interop gotchas)
- for each container, every buffer in `inv.itemBuffers.Values`, and for each buffer every record by
  index via `GetSize()` / `GetPosAt(ref i)` / `GetItemAt(ref i)`
- aggregate counts only (tiles seen, cells seen, cells with an inventory container, buffers seen,
  total records), a horizontal distance-from-player histogram
  (`0-30`/`30-60`/`60-120`/`120-256`/`256-512`/`512+` metres), position bounds, largest distance
  found, and a per-item-name count breakdown
- three skipped-item counters (tiles/cells/buffers that threw during the walk)
- a single `HEADLINE:` log line with total records, `_instanceList.Count` for contrast, and the
  largest horizontal distance from the player
- a hard safety cap at 250,000 records: if hit, a warning line marks every count above as a floor,
  not a total

v0.2 has no active/inactive split — that data isn't available per record from this walk (it was
available from `_instanceList`, which v0.1 showed is the wrong list).

## Hotkey and config

- Hotkey: `F8` (config key `DumpHotkey`, `GroundItemAudit` section). F8 is the only function key
  not already claimed by another mod/probe in this repo (F4, F5, F6, F7, F9, F10, F11, F12 are all
  taken) — do not rebind this to a taken key.
- `MaxNamesLogged` (int, default `25`) — how many distinct item names to print

`IncludeFetchAllData` (v0.1 only) is removed in v0.2: `FetchAllData()` is now an unconditional step
of the per-cell walk, not an optional extra.

## Results

v0.2 ran in-game on 2026-08-10 with two dumps in one session. Zero exceptions. The
per-cell buffer route works. Total records: 4829 and 4831 against
`_instanceList.Count` of 0 and 105 respectively, across 183 tiles and 1978 cells.
Furthest record 1009.9802 m from the player. The probe has answered its question.
Detailed findings live in `docs/architecture.md` → "World item DATA vs. spawned
GameObjects".
