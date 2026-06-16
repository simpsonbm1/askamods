# AskaMods — Project Context

## Game
**ASKA** — co-op Viking survival/city-builder on Steam.
Install path: `D:\SteamLibrary\steamapps\common\ASKA`

## Mod Loader Stack
- **BepInEx 6.0.0** (IL2CPP build) — installed at `ASKA\BepInEx\`
- **HarmonyX** (0Harmony.dll) — bundled with BepInEx 6, used for runtime method patching
- **Il2CppInterop** — also bundled; generates C# wrapper assemblies from the native IL2CPP binary
- **.NET 10 SDK** — used to compile mods, targeting `net6.0`

## Why IL2CPP Matters
ASKA ships as IL2CPP (not Mono). The game's C# code is compiled to native machine code in `GameAssembly.dll`. BepInEx 6 generates interop wrapper assemblies on first launch at:
`ASKA\BepInEx\interop\` (158 DLLs, including `Assembly-CSharp.dll`)
Mods reference these interop DLLs, not the original game DLLs.

## Project Structure
```
askamods/
  CLAUDE.md                  ← this file
  _explore/                  ← throwaway Mono.Cecil inspector scripts (not a mod)
  BowDamageMod/              ← Mod 1: buff early-game bow damage
  TreeRespawnMod/            ← Mod 2: respawn trees after 3 in-game days if stump remains
```

Each mod is a separate `.csproj` that outputs its own `.dll` to `BepInEx\plugins\<ModName>\`.
The build target `CopyToPlugins` handles this automatically on build.

## Key Game Architecture (from Assembly-CSharp interop inspection)

### Namespaces
- `SSSGame.*` — ASKA's own code (Sand Sailor Studio)
- `Invector.*` / `Invector.vShooter.*` — third-party character controller framework ASKA is built on
- `SandSailorStudio.Inventory.*` — custom inventory system

### Damage Pipeline (Projectiles / Bow)
```
vShooterWeapon.Shot()
  → vBowControl.OnInstantiateProjectile()
  → SSSGame.Combat.Projectile.Shoot(powerLevel, origin, shootRay, aimRay,
                                     speed, drag, gravity, damageMask,
                                     DamageData damageData, Action onDamageDealt)
       └─ DamageData
            .weapon          → WeaponizedItem  (the bow item)
            .baseDamage      → float  ← what we multiply
            .damageMultiplier → float
            .attributeBonus  → float
            .result          → float (computed final)
```

**Best patch point for bow damage:** Prefix `SSSGame.Combat.Projectile.Shoot()` — `DamageData` is passed by reference and can be modified before damage is applied.

Other useful classes:
- `Invector.vShooter.vProjectileControl` — has `minDamage` (int), `maxDamage` (int), `damageByDistance` (bool), `DropOffStart`/`DropOffEnd` (float)
- `Invector.vShooter.vShooterWeapon` — has `chargeDamageMultiplier` (float), `chargeVelocityMultiplier` (float)
- `Invector.vDamage` — has `damageValue` (int), `reducedDamage` (bool)

### Resource / Tree System

**In-game day system — `SSSGame.Weather.WeatherSystem` (Fusion NetworkBehaviour, singleton)**
```
WeatherSystem.Instance           ← singleton accessor
  ._onNewDay  (Action)           ← subscribe here to get a callback each new in-game day
  .GetDaysPassed()  (Int32)      ← total days elapsed since game start
  .DayOfYear  (Int32)            ← current day within the year
  .dayLength  (Single)           ← configurable day length in seconds
```
`_onNewDay` is the reliable hook for "one more day has passed" — subscribe a lambda from the mod plugin.

**Respawn countdown — `SSSGame.BiomeItemAvailabilityData`**
The game has a built-in per-resource countdown system. May be usable directly or as a reference for our approach:
```
BiomeItemAvailabilityData
  .itemDescriptors  (List)       ← the BiomeItemDescriptors this countdown controls
  .remainingDays  (Int32)        ← days remaining before replenish
  .ReplenishOnAvailable  (bool)  ← if true, calls Replenish() when remainingDays hits 0
  ._OnNewDay()                   ← called each day; decrements remainingDays
```

**Vegetation / resource classes**
```
BiomeItemDescriptor              ← represents a vegetation/resource type in the world
  .Replenish()                   ← call this to respawn the item
  .OnReplenish  (Action)         ← event fired when replenishment happens
  ._instances  (List)            ← active BiomeItemInstance list
  .IsHarvestable  (bool)
  .IsAvailable  (bool)

HarvestInteraction               ← attached to harvestable world objects (trees, rocks, etc.)
  .harvestPieces  (List)         ← harvest stages in order (trunk pieces, then stump last)
  ._currentPieceIdx  (int)       ← which piece is currently active
  ._currentVegeData  (uint)      ← bitmask of which pieces have been harvested
  ._worldInstance  (WorldItemInstance) ← link back to the world resource instance
  .OnFullyHarvested  (Action)    ← fires when ALL pieces done INCLUDING stump
  .OnPieceLootPieceHarversted    ← Action<3> fires per trunk piece
  .TakeDamage(DamageData)        ← damage entry point (no ref params — safe to patch)
  .SetHarvestBits(int) / ClearHarvestBits(int) ← directly manipulate vege data

HarvestSpawner                   ← spawns loot; has ref to HarvestInteraction (_hi)
  ._OnFullyHarvested()           ← fires when entire tree incl. stump is done
  ._OnPieceHarvested(DamageData, ResourcePieces, Vector3) ← per piece

WorldItemInstance                ← a specific instance of a resource in the world
  .Destroyed  (bool)
  .Active  (bool)
```

**Planned approach for TreeRespawnMod:**
1. **Detect trunk felled / stump remains**: Subscribe a Postfix to `HarvestInteraction.TakeDamage` (safe — no ref params). After each hit check: is `_currentPieceIdx` now pointing at the last piece (stump)? That means trunk is gone. Alternatively hook `OnPieceLootPieceHarversted` event at startup.
2. **Track pending respawns**: Keep a `Dictionary<int, int>` mapping `WorldItemInstance` hash (or position hash) → day it was felled. Also store the `BiomeItemDescriptor` reference.
3. **Detect stump taken**: Subscribe to `HarvestInteraction.OnFullyHarvested` on the same object. If stump is harvested before 3 days, remove from the dictionary.
4. **Count days**: Subscribe to `WeatherSystem.Instance._onNewDay`. On each new day, iterate dictionary, check if `GetDaysPassed() - dayFelled >= 3`, then call `BiomeItemDescriptor.Replenish()`.
5. **Get `BiomeItemDescriptor` from `HarvestInteraction`**: Path TBD — `HarvestInteraction._worldInstance` → `WorldItemInstance` → need to find link to descriptor. May need to search `BiomeItemDescriptor._instances` list, or find another path. Log the `WorldItemInstance` type at runtime to discover the link.

**Confirmed answers:**
- `HarvestInteraction._worldInstance.TryCast<BiomeItemInstance>()` gives the biome instance directly; `BiomeItemInstance.Replenish()` fully respawns in place
- Last entry in `harvestPieces` is reliably the stump (`harvestPieces` is `List<HarvestInteraction.HarvestPiece>`; element type is a nested class with `pieceId ResourcePieces` enum — no explicit stump flag, position-based)
- `BiomeItemInstance.Replenish()` is sufficient — confirmed working in-game
- `WeatherSystem.NetworkedCurrentGameTime` (float) persists across saves — valid for elapsed-time calculations after a full game restart
- Save/load: implemented — queue written to `com.askamods.treerespawn.save` on each change; survives full restarts

### Item Naming
- Item names live in Unity ScriptableObject assets, not in code — read at runtime via `item.info.Name` (the `ItemInfo.Name` property)
- Known item names: **"Flimsy Shortbow"** (first bow), **"Wood Arrow"** (early arrows)
- In ranged `DamageData`, `weapon` = the **arrow**, not the bow — match on arrow name

## Mod 1: BowDamageMod — COMPLETE
**Goal:** Buff "Flimsy Shortbow" and its arrows without touching mid/late-game weapons.
**Working approach:**
- Prefix patch on `SSSGame.Creature.TakeDamage(DamageData damage)` — the actual HP-reduction call
- `DamageData.weapon` is the arrow ("Wood Arrow"); match via `damage.weapon.info.Name`
- Multiply both `baseDamage` and `result` by `DamageMultiplier` config (default 3.0x)
- Config: `BowDamage/DamageMultiplier` (float), `BowDamage/TargetWeaponNames` (comma list)
- Confirmed working: 4 shots to kill a Wight vs 8 baseline; club damage unaffected

**Dead ends (don't retry):**
- `Projectile.Shoot()` — IL2CPP trampoline crash (mixed ref value-type + nullable object params)
- `RangedManager.OnProjectileDamage()` — post-damage notification; modifying `result` here only changes UI numbers, not actual HP
- `Creature.TakeDamage` / `Character.TakeDamage` via Invector path — never fired
- `vBowControl.OnInstantiateProjectile`, `vProjectileControl.Start()` — ASKA doesn't use these Invector hooks

## Mod 2: TreeRespawnMod — COMPLETE
**Goal:** When a tree is felled, if the stump is NOT harvested, respawn the tree after 3 in-game days.
**Working approach:**
- Postfix patch on `HarvestInteraction.TakeDamage` — after each hit, check `GetCurrentPieceIndex() == harvestPieces.Count - 1` to detect stump stage
- `_worldInstance.TryCast<BiomeItemInstance>()` to get the biome instance; non-biome resources (rocks etc.) return null and are skipped
- Store `WeatherSystem.NetworkedCurrentGameTime` at fell time; compare elapsed via `GetTimeDifferenceFromCurrentGameTimeInSeconds() / dayLength` against threshold
- Key everything by **world position** string (`GetPosition()` rounded to 0.1m, format `x:y:z`) — `UniqueId` is NOT globally unique (see dead ends)
- `BiomeInstancePatch` Postfix on `BiomeItemInstance.Initialize()` populates `ActiveInstances[posKey]` as world loads; `DayTracker` looks up the live instance at check time — no stale pointers stored
- Stump-harvested detection: check `inst.Destroyed` at each tick (no event subscription needed)
- Persistence: queue saved to `com.askamods.treerespawn.save` (posKey + gameTimeFelled per line) on every change; `LoadPending()` restores on next launch
- Day tracking via a registered `MonoBehaviour` (`DayTracker`) with `Update()` polling — avoids IL2CPP delegate subscription issues
- Config: `TreeRespawn/RespawnDays` (float, default 3.0) — supports decimals for testing (e.g. 0.02 ≈ 20 seconds)

**Dead ends (don't retry):**
- `Il2CppSystem.Action(methodRef)` — constructor only accepts `IntPtr` in this interop version; use MonoBehaviour Update() polling instead of subscribing to `_onNewDay`
- Subscribing to `HarvestInteraction.OnFullyHarvested` per-instance — same IL2CPP delegate issue; checking `BiomeItemInstance.Destroyed` at tick time is simpler and equivalent
- `BiomeItemInstance.UniqueId` as a dictionary key — NOT globally unique; it's a per-buffer index that restarts at 0 in every spatial chunk. Use world position (`GetPosition()`) as the key instead — positions are genuinely unique and stable across saves

## Reference Paths
| Purpose | Path |
|---|---|
| BepInEx core DLLs | `ASKA\BepInEx\core\` |
| Game interop assemblies | `ASKA\BepInEx\interop\` |
| Unity engine modules | `ASKA\BepInEx\unity-libs\` |
| Plugin output folder | `ASKA\BepInEx\plugins\` |
| BepInEx log | `ASKA\BepInEx\LogOutput.log` |
