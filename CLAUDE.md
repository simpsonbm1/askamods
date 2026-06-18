# AskaMods — Project Context

## Git (false-negative warning)
This folder **IS** a git repository (`master`, remote `origin` → `https://github.com/simpsonbm1/askamods.git`).
The session-startup environment readout reports **"Is a git repository: false"** — that is a **false
negative** (a Windows detection bug, likely the space in the `D:\Claude Projects\...` path). Git works
normally here. Do not refuse or skip git operations because of that line; if unsure, verify with
`git rev-parse --is-inside-work-tree` (returns `true`) and proceed with commits/pushes as usual.

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
  TreeRespawnMod/            ← Mod 2: respawn trees (stump condition) + gather resources (reeds, berries, etc.)
  HealthRegenMod/            ← Mod 3: regenerate player HP after 10s out of combat
  TorchFuelMod/               ← Mod 4: keep torches perpetually fueled (no resin chore)
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

### Player Character vs. Creature — separate hierarchies, not parent/child

`SSSGame.Character` (base `Fusion.NetworkBehaviour`) and `SSSGame.Creature` (also base `Fusion.NetworkBehaviour`) are **independent class hierarchies**, not subtype/supertype. `PlayerCharacter : Character`, used for player avatars; `Creature` is used for monsters/NPCs. Both implement the same `IDamageReceiver`-style contract (`TakeDamage(DamageData)`, `CurrentHealth`/`MaxHealth`, `GetDamageTakenHistory()`, `IsPlayer()`, `IsAlive()`) but as separate, non-overlapping implementations.

**Consequence:** a Harmony patch on `Creature.TakeDamage`/`Creature.X` (e.g. BowDamageMod's patch) only ever fires for monsters — it never sees player damage. To affect the player, patch `SSSGame.PlayerCharacter` (or `Character`, but `PlayerCharacter` overrides `TakeDamage`/`Spawned`/`Despawned`/`IsPlayer` itself, so a patch on the base `Character` method does **not** intercept calls dispatched to the `PlayerCharacter` override — patch the most-derived type).

Useful `Character`/`PlayerCharacter` members:
```
CurrentHealth / MaxHealth   (float, public get+set)
HasAuthority                (bool, get-only) — true only for the avatar this client controls;
                             other players' characters in co-op are visible locally but HasAuthority == false
GetDamageTakenHistory()     → DamageHistory
  .LastDamageTime  (float)  — updated internally on every hit; poll this instead of subscribing
                               to OnTakeDamage (Action-typed IL2CPP delegate, same subscription
                               issues noted under TreeRespawnMod dead ends)
IsInCombat()                — PlayerCharacter's own built-in combat-timeout check (fixed window,
                               not configurable — implement your own timer via LastDamageTime instead)
```

`PlayerManager.LocalPlayer` (property on the scene's `PlayerManager` singleton) is the game's own "get the local player" accessor, but no static `PlayerManager.Instance` was found — the Harmony-Spawned-patch approach below was simpler than locating the manager instance.

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

**Confirmed answers (HarvestInteraction / trees):**
- `HarvestInteraction._worldInstance.TryCast<BiomeItemInstance>()` gives the biome instance directly; `BiomeItemInstance.Replenish()` fully respawns in place
- Last entry in `harvestPieces` is reliably the stump (`harvestPieces` is `List<HarvestInteraction.HarvestPiece>`; element type is a nested class with `pieceId ResourcePieces` enum — no explicit stump flag, position-based)
- `BiomeItemInstance.Replenish()` is sufficient — confirmed working in-game
- `WeatherSystem.NetworkedCurrentGameTime` (float) persists across saves — valid for elapsed-time calculations after a full game restart
- Save/load: implemented — queue written to `com.askamods.treerespawn.save` on each change; survives full restarts

### Gather / Press-to-Collect System

```
GatherInteraction (SSSGame.GatherInteraction : SSSGame.Interaction)
  .GatherItemsCharge(IInteractionAgent, Int32 charges, ItemContainer) → Int32
                                   ← safe Postfix patch point; no ref params; fires when items actually collected
  .CheckAvailableItemCount()  → Int32   ← returns 0 when resource is fully exhausted (register respawn here)
  ._worldInstance  (WorldItemInstance)  ← same path as HarvestInteraction; TryCast<BiomeItemInstance>() works
  .GetGatherableItemInfo()    → ItemInfo ← yields the ITEM name (not the node/resource name — see below)
```

**Critical distinction:** `GetGatherableItemInfo().Name` is the **yielded item**, not the world node name.
- All names below confirmed in-game against the inventory/storage UI (2026-06-17):
  - Reeds → `"Thatch"`, Berry Bush → `"Berries"`, Dwarf Spruce → `"Stick"`, Flax Bush → `"Fiber"` (singular)
  - `"Small Stone"` (ground pickup), `"Mussels"`, `"Feathers"` (Bird's Nest), `"Water"` (Natural Water Collector)
  - Vegetables: `"Carrot"`, `"Cabbage"`, `"Onion"`, `"Garlic"`, `"Beetroot"`
  - Mushrooms: `"Mushroom"` substring matches Gray/Grey/Yellow Mushrooms
- **Not their own GatherInteraction** (bonus drops bundled with parent gather — can't trigger respawn): `"Seeds"` (plants), `"Wild Egg"` (Bird's Nest)

Log line on exhaustion shows the item name: `[TreeRespawnMod] Gather resource "Thatch" exhausted at ...`

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
**Goal:** Respawn felled trees (stump condition) and exhausted gather resources (reeds, berries, etc.) after configurable in-game days.

### Tree respawn
- Postfix patch on `HarvestInteraction.TakeDamage` — after each hit, check `GetCurrentPieceIndex() == harvestPieces.Count - 1` to detect stump stage
- `_worldInstance.TryCast<BiomeItemInstance>()` to get the biome instance; non-biome resources (rocks etc.) return null and are skipped
- Store `WeatherSystem.NetworkedCurrentGameTime` at fell time; compare elapsed via `GetTimeDifferenceFromCurrentGameTimeInSeconds() / dayLength` against threshold
- Key everything by **world position** string (`GetPosition()` rounded to 0.1m, format `x:y:z`) — `UniqueId` is NOT globally unique (see dead ends)
- `BiomeInstancePatch` Postfix on `BiomeItemInstance.Initialize()` populates `ActiveInstances[posKey]` as world loads; `DayTracker` looks up the live instance at check time — no stale pointers stored
- Stump-harvested detection: check `inst.Destroyed` at each tick (no event subscription needed)
- Config: `TreeRespawn/RespawnDays` (float, default 3.0)

### Gather resource respawn
- Postfix patch on `GatherInteraction.GatherItemsCharge` — fires when player/villager collects; check `CheckAvailableItemCount() == 0` to detect full exhaustion
- Same `_worldInstance.TryCast<BiomeItemInstance>()` path; same `ActiveInstances` registry; same `Replenish()` call
- `PendingGatherRespawns` stores `(float gameTime, string itemName)` — no stump-cancel condition; resources always respawn unless config disables them (`threshold <= 0` → drop from queue immediately)
- Config: `[GatherRespawn]` section with one `Config.Bind<float>` per known resource (key = yielded item name, value = days). `Default` entry is the fallback for unlisted resources. `0` = disabled.
- Substring match on item name (case-insensitive) — `"Mushroom"` matches `"Gray Mushroom"`, `"Yellow Mushroom"` etc.

### Shared infrastructure
- Persistence: `com.askamods.treerespawn.save` — sections `# tree` and `# gather`; tree format `posKey,gameTime`; gather format `posKey,gameTime,itemName`. Old saves without section headers load as tree entries (backward compatible).
- Day tracking via registered `MonoBehaviour` (`DayTracker`) with `Update()` polling — avoids IL2CPP delegate subscription issues

**Dead ends (don't retry):**
- `Il2CppSystem.Action(methodRef)` — constructor only accepts `IntPtr` in this interop version; use MonoBehaviour Update() polling instead of subscribing to `_onNewDay`
- Subscribing to `HarvestInteraction.OnFullyHarvested` per-instance — same IL2CPP delegate issue; checking `BiomeItemInstance.Destroyed` at tick time is simpler and equivalent
- `BiomeItemInstance.UniqueId` as a dictionary key — NOT globally unique; it's a per-buffer index that restarts at 0 in every spatial chunk. Use world position (`GetPosition()`) as the key instead — positions are genuinely unique and stable across saves
- `GatherRespawnDays` + `GatherRespawnOverrides` (single comma-separated string config) — replaced by individual `Config.Bind<float>` per resource in `[GatherRespawn]` section; much more user-friendly

## Mod 3: HealthRegenMod — COMPLETE
**Goal:** Regenerate player HP (default 1/sec) after 10s (configurable) without taking damage.
**Working approach:**
- No damage-pipeline patch needed — `PlayerCharacter`/`Character` already maintain `GetDamageTakenHistory().LastDamageTime` internally on every hit; poll it instead of patching `TakeDamage`
- Postfix patches on `PlayerCharacter.Spawned()` and `PlayerCharacter.Despawned()` capture/release `Plugin.LocalPlayer`, gated on `HasAuthority` to pick out this client's own avatar (other players' characters are visible locally too, with `HasAuthority == false`)
- `RegenTracker` (registered `MonoBehaviour`, `Update()` polling, same pattern as `DayTracker`) watches `Plugin.LocalPlayer`; reset tracking whenever the reference changes (covers spawn/respawn)
- "Out of combat" detection: track `LastDamageTime`; if it hasn't changed since the previous frame, accumulate `Time.deltaTime` into a seconds-since-last-hit counter; once that counter clears `OutOfCombatSeconds`, add `RegenPerSecond * Time.deltaTime` to `CurrentHealth`, clamped to `MaxHealth`
- Config: `HealthRegen/RegenPerSecond` (float, default 1.0), `HealthRegen/OutOfCombatSeconds` (float, default 10.0)
- Confirmed working in-game: regen kicked in ~10s after spawning at half health; taking damage mid-regen paused it, then it resumed ~10s after the last hit

**Dead ends (don't retry):**
- `SSSGame.Creature.TakeDamage`/`CurrentHealth` for player health — `Creature` only covers monsters/NPCs; see "Player Character vs. Creature" above. Use `Character`/`PlayerCharacter` instead
- `UnityEngine.Object.FindObjectsByType<T>()` (generic overload) — throws `MissingMethodException` through the IL2CPP-to-managed trampoline on every call (AOT generic instantiation isn't supported here); logged thousands of times per second since it was called from `Update()`
- Downcasting a `UnityEngine.Object` to a game type via `.TryCast<T>()` — `UnityEngine.Object`'s wrapper does **not** inherit `Il2CppObjectBase` (its base chain is just `System.Object`), unlike normal game/interop types (e.g. `WorldItemInstance : Il2CppSystem.Object : Il2CppObjectBase`), so `TryCast` isn't available on it. Avoided entirely by getting an already-correctly-typed instance from a Harmony patch's `__instance` parameter instead of from a `UnityEngine.Object[]` search result

### Torch / Fire-Fuel System
```
FireStructure (Fusion.NetworkBehaviour)   ← the actual networked fuel state, one per fire-capable structure
  .CurrentFuelVolume / .MaxFuelVolume / .CurrentFuelRatio  (Single, public get+set)
  .fuelBurnVolumePerSecond  (Single)       ← configured burn rate; actual decrement happens via the
                                              Attribute/VariableAttribute modifier system, not a visible
                                              per-frame method on FireStructure itself
  .Rpc_AddFuel(Single fuel)                ← the network-safe RPC the game itself calls when a player
                                              manually refuels with an item (e.g. resin) — safe to call
                                              from any client/host, replicates correctly in co-op
  .Initialize(Structure ownerStructure)    ← fires once per instance as it spawns/loads; ownerStructure
                                              gives access to the structure's name for filtering
  .IsSpawned  (bool, get-only)             ← false once despawned/destroyed

RefuelableInteraction (abstract) → FireInteraction → (wrapped by LightOutlet / WarmthOutlet)
  - mirrors CurrentFuelVolume/MaxFuelVolume by forwarding to the wrapped FireStructure
  - LightOutlet and WarmthOutlet are SEPARATE AI-dispatcher components (light vs. warmth duty) but
    both wrap a FireInteraction → FireStructure — campfires/forges/kilns/cooking stations all use
    FireStructure too, so filtering must happen by **structure name**, not by component type, to
    avoid accidentally affecting non-torch fires
```
**Confirmed:** `Structure.DefaultName` / `Structure.StructureName` give the structure's display name (e.g. "Flimsy Torch") for substring matching, same pattern as `ItemInfo.Name` used in BowDamageMod.

## Mod 4: TorchFuelMod — COMPLETE
**Goal:** Torches (placed for base lighting/decoration) should never run out of fuel/resin.
**Working approach:**
- Postfix patch on `FireStructure.Initialize(Structure ownerStructure)` — fires for every fire-capable structure; filter by `ownerStructure.DefaultName`/`StructureName` containing a configured substring (default `"Torch"`) so campfires/forges/kilns/cooking stations (which also use `FireStructure`) are untouched
- Matched instances are added to `Plugin.TrackedFireStructures` (plain `List<FireStructure>`)
- `TorchFuelTracker` (registered `MonoBehaviour`, `Update()` polling, same pattern as `DayTracker`/`RegenTracker`) checks the list every `CheckIntervalSeconds` (default 5.0); for any tracked structure with `CurrentFuelVolume < MaxFuelVolume`, calls `Rpc_AddFuel(MaxFuelVolume - CurrentFuelVolume)` to top it off exactly to max (never overfuels, so it can't trigger the "overfire/blazing" state)
- Using the existing `Rpc_AddFuel` RPC (rather than setting `CurrentFuelVolume` directly) was a deliberate choice — it's the same network-safe path the game uses for manual resin refueling, so it works correctly in co-op regardless of which client/host has the mod installed
- Dead/despawned entries (`!fire.IsSpawned` or null) are pruned from the tracked list each tick
- Config: `TorchFuel/TargetStructureNames` (string, default `"Torch"`, comma list, case-insensitive substring match), `TorchFuel/CheckIntervalSeconds` (float, default 5.0)
- Confirmed working in-game: fuel visibly ticks down for a few seconds then jumps back to max on the next check interval; tracked 4 "Flimsy Torch" structures correctly on load

## Reference Paths
| Purpose | Path |
|---|---|
| BepInEx core DLLs | `ASKA\BepInEx\core\` |
| Game interop assemblies | `ASKA\BepInEx\interop\` |
| Unity engine modules | `ASKA\BepInEx\unity-libs\` |
| Plugin output folder | `ASKA\BepInEx\plugins\` |
| BepInEx log | `ASKA\BepInEx\LogOutput.log` |
