# Seed Harvester — Handoff & Learnings

## Current Status

**Parked state (2026-06-28):** the Harmony patch is commented out AND the installed plugin is renamed `SeedHarvesterMod.dll.off` (same convention as CookingStationFixMod) so BepInEx does not load it at all. A 1-frame `yield` does NOT force cave `AreaInstance` instantiation — dead-end confirmed 2026-06-28. Reading positions would require dumping `GameAssembly.dll` to parse the raw data buffers.

We have successfully bypassed the slow loading screen parts and built a "Fast Harvest" coroutine (`v0.5.0`). By directly invoking `WorldGenerator.GenerateWorldMapAsync`, `BiomesManager.UpdateDataAsync`, and `CavesManager.UpdateDataAsync` in memory, we can iterate through and generate 200 seeds in a matter of seconds.

**However**, there is a new blocker: every single seed currently scores `-9999`. 

The scoring algorithm fails because `wg.GetDataMap()._areaInstances` is completely empty. The `UpdateDataAsync` methods on the generation managers do not actually instantiate the `CaveAreaInstance` GameObjects and register them to the `_areaInstances` dictionary as we originally thought. This means we have the underlying map topology generated, but we cannot query the cave `Vector2` positions because the GameObjects haven't been spawned yet.

## Next Steps for Next Session

1. **Find Cave Positions Without AreaInstances**
   - The map generation creates `AreaDataElement`s in the `AreaDataBuffer`. If we can extract the cave `Vector2` positions directly from these elements (or a related buffer like `CaveAreaData`) *before* the GameObjects are instantiated, we can evaluate the map instantly.
   
2. **Force AreaInstance Instantiation**
   - *Dead end confirmed (2026-06-28):* Adding a 1-frame `yield return null` after `CavesManager.UpdateDataAsync` does NOT cause the engine to instantiate the `CaveAreaInstance` GameObjects. The `_areaInstances` dictionary remains empty.
   - This means we cannot use the GameObjects to read cave positions. We MUST extract the positions from the raw data buffers (like `CavesManager._dataMap` or `BiomesManager._biomeAreaDataHandler`).
   - Because we only have Il2CppInterop trampolines and not the actual C# method bodies, locating and parsing these raw buffers likely requires dumping `GameAssembly.dll` and reverse-engineering the native code using Ghidra or Cpp2IL.

3. **TreeRespawnMod Coop Crash (Sidenote)**
   - The user noted that `TreeRespawnMod` does not work in co-op and crashes the game. This was investigated previously and documented in `TREERESPAWN_HANDOFF.md` (Issue A). The crash is caused by a `System.InvalidOperationException: Handle is not initialized` resulting from a Harmony hook on `HarvestInteraction._OnWorldInstanceDataChanged`, which violates IL2CPP Rule #5 (patching lifecycle/initialization methods before GC handles are ready). This fix will need to be reworked using a safer network sync polling mechanism.
