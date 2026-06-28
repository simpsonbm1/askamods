# Seed Harvester — Handoff & Learnings

## Current Status
We have proven that we **can** instantiate the game's native `WorldGenerator` headlessly from the Main Menu. The component attaches and successfully runs `Setup()` without requiring a live 3D scene. 

However, we hit a blocker when trying to run the actual generation (`GenerateWorldMapAsync`): it requires `RandomGeneratorManager`. When we dynamically instantiate `RandomGeneratorManager` using `AddComponent`, it throws a `NullReferenceException` in `OnEnable()` and `_PadSeedPhrase()`. 

## The Blocker: `RandomGeneratorManager` initialization
`RandomGeneratorManager` expects certain serialized fields (like `generatorNames`, `seedKeyLength`, `generatedSeedPhraseLength`) to be populated. Because we create it from scratch, these IL2CPP struct arrays are null.

## Next Steps for Next Session

1. **Find an existing `RandomGeneratorManager` instance/prefab**
   - We must avoid creating `RandomGeneratorManager` from scratch.
   - We should search the Main Menu scene for an existing instance of it, or find the global `GameManager`/`NetworkSession` prefab in `Resources.FindObjectsOfTypeAll()` and clone it/steal its `RandomGeneratorManager`.
   - Alternatively, we might manually initialize those missing arrays via reflection before calling `SetSeedPhrase` (though this might be tricky with IL2CPP arrays).

2. **Intercept normal WorldGen instead?**
   - If we can't reliably spin up `RandomGeneratorManager` from scratch, an alternative approach is to hook into the **actual** single-player world creation flow (`Create Game` -> `Generate`).
   - We could intercept the end of `WorldGenerator.GenerateWorldMapAsync`, score the generated `AreaInstances`, and if the score is too low, **automatically trigger a new seed generation** without ever leaving the UI loading screen. Once a good seed is found, we allow the game to proceed to load the 3D scene. 

3. **Complete the Headless Loop**
   - Once `RandomGeneratorManager` is successfully providing a seed to `WorldGenerator`, run the `GenerateWorldMapAsync` coroutine.
   - Read the generated `AreaInstances` from `wg.GetDataMap()._areaInstances` (which contains caves, lakes, dens).
   - Pass this data to our `Scout` scoring algorithm (already refined in `SeedScoutMod`).
   - Repeat the loop until the score meets the user's threshold.
