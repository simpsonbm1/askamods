# TerrainLevelerMod

**Goal:** Let a player place a large terraforming grid and level it — including clearing trees/rocks —
with a single hit (E), without the native drag crash.
**Status:** WORKING, **confirmed in-game (2026-07-01)** at v1.3.21/1.3.22. Un-parked after being
PARKED/ABANDONED through v1.1.21 — see [History](#history) for why the original approach was dropped
and what changed.

## The working recipe

### Flatten: `HeightmapTool.RunOnArea`
Hook `TerraformingFieldInteraction.Use(IInteractionAgent)` (postfix) — this is the single method a
leveling hit actually routes through (see dead end below on why `TryLevelTile`/`MarkCellInteracted`
don't work). On a successful hit, grab `interaction.heightmapTool` and drive it directly:
```csharp
tool.SetReferenceHeight(target, true);
tool.RunOnArea(TerraformingToolOperation.LEVEL, width, depth, 0f, center, 0f);
tool.ResetHeightReference();
```
`width`/`depth`/`center` come from the grid footprint (`TerraformingGrid.GetCellPosition(i)` over
`GetCellsCount()`); `target` is `grid.ReferenceLevel`. This sets the whole footprint to a single flat
plane in one pass — instant, not incremental. Also set `tool.clearVegetation` /
`tool.setVegetationMask` = true and disable `validateHeight`/`checkTerrainSlope`/`checkItemInstances`
so the pass isn't rejected.

### Obstacle clearing: a custom `AOESpell` cast through the player's `SpellsManager`
Trees and the fixed "invincible" rocks need a separate pass — `RunOnArea` doesn't clear them.
Build `SSSGame.Combat.AOESpell` (Box shape sized to the grid) and cast it via
`SpellsManager.CastSpellOnPos(aoe, ref pos)` (the proper entry point; calling `OnDetonated` directly
NREs because spell setup is skipped). Key config:
- `clearItemInstances = true` — clears fixed `WorldItemInstance` rocks via the data layer.
- `dealDamageToHarvestables = true`, `baseDamage = 999999f` — kills trees/harvestable rocks.
- **`skipAllCollisionChecks = false`** — critical. `true` skips `CollisionCheck()`, which is the overlap
  that gathers harvestable targets; with it true, only `clearItemInstances` fired (rocks cleared, trees
  survived).
- `damageMask` = all layers **except** those occupied by any `Character` (player/villagers/creatures)
  found via an `OverlapBox` over the blast box first — this is how living things are protected from the
  blast (see `BuildLivingExclusionMask`).
- Cast twice (`BombShots` default 2): first knocks trees to stumps, second clears stumps + rocks.

**Getting the player's `SpellsManager`:** it's a standalone Fusion network object, not under the player
hierarchy (`GetComponent` searches fail). Register every instance from a Harmony postfix on
`SpellsManager.Awake` (a plain Unity lifecycle method, safe to patch), then at cast time pick
`IsPlayer && HasAuthority` from the registry. **Never patch its Fusion state-sync methods**
(`CopyBackingFieldsToState`/`CopyStateToBackingFields`) — that hangs the game at load.

### Grid UI dismiss
After flattening, call `grid._state.Rpc_DebugCompleteField()` to finalize the field (dismisses the
marker/UI). This does **not** itself deform terrain — it's purely a completion signal, safe to call
after `RunOnArea` has already done the real work.

### The drag-crash fix: cap tile count at `ChangeGridSize`, not at the marker/distance layer
The drag preview's per-tile validity lives in a **fixed-size Fusion network struct** — `BitSet256` on
`SSSGame.Network.NetworkDynamicDimensionBuildingState_256` (the variant this structure uses; a `_512`
sibling with `BitSet512` exists for other building types). Past that many tiles,
`UpdateGridValidityOnNetwork` writes validity bits past the struct and corrupts the network state —
a hard, silent native crash. The **real** per-frame pipeline is:
```
DynamicDimensionsPlacement.gridSize (Vector3Int)
  -> DynamicDimensionBuilding.SetDimensions(float tileSize, Vector3Int GridSize, bool forceSet)
  -> NetworkDynamicDimensionBuildingState.ChangeGridSize(Vector3Int gridSize)   <- non-virtual, by-value
  -> NetworkDynamicDimensionBuildingState_256/_512.UpdateGridValidityOnNetwork()
```
The fix is a prefix on `ChangeGridSize(Vector3Int)`: read `NetworkedGridSize` at runtime (256 or 512
depending on variant — don't hardcode), and if the requested grid exceeds it, shrink the dominant axis
until it fits, then re-invoke `ChangeGridSize` with the corrected size (recursion-guarded via a static
bool) and skip the oversized original call. A safety-net prefix on `UpdateGridValidityOnNetwork` for
both `_256` and `_512` additionally skips the write outright if the grid is still oversized by the time
it gets there. See `docs/architecture.md`'s Terrain/Terraforming section for the full pipeline and dead
ends, and `TerrainLevelerMod/DRAG_CRASH_PLAN.md` for the evidence chain (WER crash offsets mapped to
these exact methods via Cpp2IL).

**256 tiles is an engine-hard ceiling** — `NetworkedGridSize` is a compiled Fusion codegen constant on a
weaved network-state type; it cannot be raised by a mod. Town-sized areas mean several adjacent
256-tile placements; each E-press still flattens its whole grid instantly, so this is fast in practice.

## Config (current, `Plugin.cs`)
`General`: `MaxDragRange` (20, soft UX cap on marker follow distance — NOT the crash fix),
`OneHitClear` (true), `MaxHeightDifference` (15, vertical stretch clamp before native mesh-NaN crash),
`PlacementDiagnostics` (false by default — flip true to log which placement guards fire and the grid
size/tile-count they see, if the crash or a variant of it ever needs re-diagnosing).
`Obstructions`: `ClearObstructions` (true), `BombShots` (2), `ClearVerticalRange` (30),
`ClearDiagnostics` (false — flip true for `[Bomb]`/`[Flatten]` logs).

## History
The mod was originally PARKED/ABANDONED at v1.1.21 believing a true instant-flatten bulldozer was
impossible (512-tile array cap, mesh-NaN crash on steep grids, `TryLevelTile` only incrementing height
per hit). A later session found the actual flatten/clear recipe above (v1.2.6–v1.3.8), which resolved
the incremental-height and obstacle problems — but surfaced a **new** drag-to-grow crash. Several
sessions chased that crash at the wrong layer (marker/distance tethers anchored to a stale
`firstMarker`, raycast-skip approaches that broke the visible preview) before mapping two recurring WER
crash offsets to the real cause — the `BitSet256` network-state overflow — and fixing it at the source
(`ChangeGridSize`). See `TerrainLevelerMod/DRAG_CRASH_PLAN.md` for the full investigation and
`TerrainLevelerMod/TERRAIN_DRAG_HANDOFF.md` for the (superseded) earlier theories, kept for the record.
