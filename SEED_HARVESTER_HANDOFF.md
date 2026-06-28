# Seed Harvester — Handoff & Learnings

## Current Status
We have successfully bypassed the slow loading screen parts and built a "Fast Harvest" coroutine (`v0.5.0`). By directly invoking `WorldGenerator.GenerateWorldMapAsync`, `BiomesManager.UpdateDataAsync`, and `CavesManager.UpdateDataAsync` in memory, we can iterate through and generate 200 seeds in a matter of seconds.

**However**, there is a new blocker: every single seed currently scores `-9999`. 

The scoring algorithm fails because `wg.GetDataMap()._areaInstances` is completely empty. The `UpdateDataAsync` methods on the generation managers do not actually instantiate the `CaveAreaInstance` GameObjects and register them to the `_areaInstances` dictionary as we originally thought. This means we have the underlying map topology generated, but we cannot query the cave `Vector2` positions because the GameObjects haven't been spawned yet.

## Next Steps for Next Session

1. **Find Cave Positions Without AreaInstances**
   - The map generation creates `AreaDataElement`s in the `AreaDataBuffer`. If we can extract the cave `Vector2` positions directly from these elements (or a related buffer like `CaveAreaData`) *before* the GameObjects are instantiated, we can evaluate the map instantly.
   
2. **Force AreaInstance Instantiation**
   - If we absolutely need the `AreaInstance` GameObjects to score, we need to find the missing method call in our Fast Harvest loop that actually instantiates them. Look into `BiomeAreaDataHandler.ActivateData()`, `CavesManager.RegisterCaves()`, or check if Unity's Job system simply needs a `yield return null` frame wait for instantiation to complete.

3. **TreeRespawnMod Coop Crash (Sidenote)**
   - The user noted that `TreeRespawnMod` does not work in co-op and crashes the game. This was investigated previously and documented at the bottom of `COOP_RESPAWN_HANDOFF.md`. The crash is caused by a `System.InvalidOperationException: Handle is not initialized` resulting from a Harmony hook on `HarvestInteraction._OnWorldInstanceDataChanged`, which violates IL2CPP Rule #5 (patching lifecycle/initialization methods before GC handles are ready). This fix will need to be reworked using a safer network sync polling mechanism.
