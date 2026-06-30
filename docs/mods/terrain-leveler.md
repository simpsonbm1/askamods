# TerrainLevelerMod (PARKED)

**Goal:** Allow users to place massive (town-sized) terraforming grids and level them with a single hit.
**Status:** PARKED / ABANDONED (Engine limitations make a true ""instant flat"" bulldozer impossible).

## Mechanics Implemented
- Increased maxRange of the terraforming tool from 10m to 20m.
- Increased max networked tile cap of DynamicDimensionTemplate from 100 to 512 (to match Fusion NetworkArray limits).
- Added a OneHitClear loop that calls TryLevelTile on every cell in the grid when hit.
- Added a Y-Clamp to forcefully truncate the height differential of the grid bounds to 15m (preventing a mesh generator crash when trying to render a grid spanning a vertical cliff).
- Bypassed the native _ValidateFootprint logic for grids > 100 tiles to prevent an OverlapBoxNonAlloc array overflow crash, allowing placement over rocks/trees.

## The Dead Ends (Why it was parked)
1. **Network Array Limit:** Fusion limits networked arrays to 512 elements. A grid larger than 512 tiles throws errors and fails to sync. This mathematically prevents laying out a ""town-sized"" plot in one drag.
2. **Mesh Normal Calc Crash:** The native TerraformingGrid mesh generation crashes with NaN normals if the height difference between the start and end of the grid is too extreme (e.g., trying to level a steep cliff).
3. **Lumpiness (The fatal flaw):** The native TryLevelTile function does not instantly set a tile to the target height; it merely raises or lowers it by a small increment per hit. Because OneHitClear only loops over the tiles once per hit, trying to level a 10m hill results in the hill being lowered slightly across the board, rather than instantly flattened. Achieving a perfectly flat foundation still requires repeatedly hitting the ground 10-20 times, making it a ""Bigger Hoe"" rather than a ""SimCity Bulldozer"". 
4. **Conclusion:** Achieving true instant flattening would require completely circumventing the native networked terrain interactions and writing a custom voxel manipulation layer, which is too unstable and out of scope.
