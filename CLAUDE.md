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
  DynamicVillagerNeedsMod/   ← Mod 5: needs-based villager behavior (auto sleep/leisure/work, no manual schedule)
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
- Persistence: `com.askamods.treerespawn.save` — sections `# tree` and `# gather`; tree format `posKey,gameTime`; gather format `posKey,gameTime,itemName`. Old saves without section headers load as tree entries (backward compatible); a legacy `# mining` section is silently skipped on load.
- Day tracking via registered `MonoBehaviour` (`DayTracker`) with `Update()` polling — avoids IL2CPP delegate subscription issues

### Mining / stone-clump respawn — INVESTIGATED & ABANDONED (don't re-attempt)
Stone clumps (`Harvest_Stone4`) are **Fusion scene objects** baked into the streamed world/chunk
data — not `WorldItemInstance`s, not in the 364-entry Fusion prefab table, not spawned via
`NetworkSpawner`, and they invoke **no** managed `HarvestInteraction` method while alive (only a
post-despawn `TakeDamage` with everything nulled). Fusion has no runtime "respawn scene object"
API, and the binary can't be dumped for deeper RE (Unity 6 → IL2CPP **metadata v39**, unsupported
by Il2CppDumper). Full write-up + the three confirming tests: **`STONE_RESPAWN_HANDOFF.md`**.
The only achievable-but-unbuilt alternative is respawning the loose `Item_Stone_Raw`/`Item_Wood_*`
nodes (those ARE `InventoryItemInstance` and managed-visible). All mining code was removed from the
mod 2026-06-18.

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
- "Out of combat" detection: track `LastDamageTime`; if it hasn't changed since the previous frame, accumulate `Time.deltaTime` into a seconds-since-last-hit counter; once that counter clears `OutOfCombatSeconds`, regen kicks in
- Regen is **discrete tick** based: a separate `_secondsSinceLastTick` accumulator adds `HealPerTick` HP every `SecondsPerTick` seconds (a `while` loop drains the accumulator so it catches up if a frame is long), clamped to `MaxHealth`. Tick timer resets on hit / death / player change so the first tick can't fire early. `SecondsPerTick = 0` falls back to smooth/continuous regen (`HealPerTick` treated as HP/sec, the original pre-2026-06-20 behavior)
- Config: `HealthRegen/HealPerTick` (float, default 1.0), `HealthRegen/SecondsPerTick` (float, default 1.0; 0 = continuous), `HealthRegen/OutOfCombatSeconds` (float, default 10.0). Default 1 HP/1s matches the old average rate, just in 1-HP steps
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

### Villager Schedule / Needs / Happiness System

**Survival components**
```
SSSGame.CharacterSurvival (base) → SSSGame.VillagerSurvival
  GetNormalizedFood() / GetNormalizedWater() / GetNormalizedWarmth()  (Single 0..1, lower = more urgent)
  IsStarving / IsDehydrated / IsFreezing / IsSuffering  (bool — the game's own critical-need flags)
  _foodVAttr / _waterVAttr / _warmthVAttr / _energyVAttr / _healthVAttr  (VariableAttribute — writable)
VillagerSurvival
  _restVariableAttribute  (VariableAttribute, range 0..24 "hours of rest"; drains awake, fills asleep)
  GetVillager() → Villager
  _hasAuthority  (bool, inherited from CharacterSurvival) — gate all writes on this (host-authoritative)
  Spawned()  — Postfix here to register each villager into a tracked list
```

**Happiness — it's a writable VariableAttribute (key finding)**
```
Villager._happinessVAttr  (VariableAttribute, writable) ← the REAL happiness store; SetValue() sticks
Villager.NormalizedHappiness (Single 0..1, GET-ONLY) / Villager.HappinessCap (GET-ONLY) — derived from it
VillagerHappiness (via Villager.GetHappinessManager()): SleepRate/WorkRate/LeisureRate are GET-ONLY —
  you CANNOT scale the leisure happiness rate; instead write _happinessVAttr directly each tick.
```
`VariableAttribute` API: `GetValue()`, `SetValue(Single)`, `GetNormalizedValue()`, `min`, `max`. Boost a need by adding `addNormalized * (max - min)` to the value, clamped to `max`; the game re-clamps happiness to `HappinessCap` (so a housing-capped villager shows up as a plateau — happiness stops rising despite the write).

**Schedule (the thing that actually dispatches tasks)**
```
enum SSSGame.UI.ScheduleType { Sleep, Work, Leisure }   (use the enum values; never hardcode the ints)
Villager.scheduleMaxHourCount  (STATIC — access via the type, CS0176 if via an instance)
Villager._ScheduleToNetworkSchedule(Il2CppStructArray<int>) → long   (array holds ScheduleType underlying ints)
Villager.Rpc_ChangeSchedule(long packed)   (network-safe; host/authority only)
Villager.__NetworkedSchedule  (Int64) — current packed schedule; snapshot for restore-on-shutdown
Villager.overrideSchedule (bool), scheduleOverride (ScheduleType), CurrentBehaviorType (ScheduleType)
```

**Day length** — `WeatherSystem.Instance.dayLength` (Single, seconds/day) → in-game hour = `dayLength / 24`; also `IsNight` (bool), `DayNightValue` (Single, ~1=midday ~0=deep night).

## Mod 5: DynamicVillagerNeedsMod — COMPLETE
**Goal:** Replace ASKA's clock-based villager schedule (manually assigning Sleep/Work/Leisure hours) with **needs-based** behavior so villagers self-manage: tired → sleep, low happiness or a real food/water need → leisure, otherwise work. Overarching principle: **stop wasting time without reducing happiness.** Villager-only; the player is never touched.

**Working approach:**
- `NeedsController` (registered `MonoBehaviour`, `Update()` polling, same pattern as `DayTracker`/`RegenTracker`/`TorchFuelTracker`); `VillagerSurvivalSpawnedPatch` Postfix on `VillagerSurvival.Spawned()` registers each villager. Everything gated on `surv._hasAuthority` (host/solo).
- **Drives the REAL per-hour schedule**, not the override fields: each mode change collapses the villager's whole schedule to one activity via `_ScheduleToNetworkSchedule(arr)` → `Rpc_ChangeSchedule(packed)` (idempotent — only on change). The original packed schedule is snapshotted on first touch and restored on `OnDestroy`/`OnApplicationQuit`.
- **Decision (`Decide`, with hysteresis):** Sleep (top priority) when `rest <= SleepWhenRestBelowHours`, stay asleep until `rest >= WakeWhenRestAboveHours`; else Leisure if food/water critically low **or** happiness below its trigger; else Work.
- **Direct VAttr writes (the core trick), rate derived from in-game hour length:**
  - *Happiness:* while in Leisure, add to `_happinessVAttr` so an empty→full restore takes `LeisureHoursToFullHappiness` in-game hours. Makes leisure actually worth taking (vanilla rate was painfully slow — ~2 in-game days 0.4→0.75).
  - *Rest:* while asleep, add to `_restVariableAttribute` so a 0→24 restore takes `SleepHoursToFullRest` in-game hours → shorter sleeps. (Lowering the wake threshold alone does NOT reduce total sleep; sleep *fraction* = drain-rate/(drain+gain), so you must raise the gain rate.)
  - *Warmth:* `FireWarmthMultiplier` adds extra warmth **only when warmth is already rising** (i.e. the game has them at a heat source) → shorter warm-up trips, never warms them out in the cold, never overrides the game's *when*-to-warm decision.
- **Happiness cap / plateau safety:** if happiness can't rise under the boost (capped below the exit threshold by housing etc.), a windowed plateau check sends the villager back to Work and starts a cooldown so they don't oscillate Work↔Leisure. (In practice all tested villagers had `cap=100`, so this rarely fires, but it's the guard against the original "stuck in leisure forever" trap.)
- **Cold is deliberately NOT a leisure trigger.** The game's own `warmUpBehaviour`/`_warmUpQuest` warms villagers *fully* during work; forcing a leisure trip for warmth only warmed them partway and made them thrash fire↔work. Warmth is read for logging only; `FireWarmthMultiplier` tunes the game's warm-up speed without taking over the behavior.
- **Config `[DynamicNeeds]`** (`BepInEx/config/com.askamods.dynamicneeds.cfg`): `Enabled` (true); `SleepWhenRestBelowHours` (8); `WakeWhenRestAboveHours` (23); `SleepHoursToFullRest` (3, 0=vanilla); `LeisureWhenNeedBelow` (0.15, food/water only, 0=off) / `LeisureUntilNeedAbove` (0.5); `LeisureWhenHappinessBelow` (0.6, 0=off) / `LeisureUntilHappinessAbove` (0.78); `LeisureHoursToFullHappiness` (4, 0=vanilla); `FireWarmthMultiplier` (2.0, 1=vanilla); `DebugLogging` (false).
- Confirmed in-game across iterative tests: villagers perform real work tasks; happiness boost visibly moves `_happinessVAttr`/`NormalizedHappiness`; sleep wakes exactly at the threshold (no oversleep) and `SleepHoursToFullRest=3` shortens it; warmth thrash eliminated; food/water self-manage and the leisure net rarely fires.

**Dead ends (don't retry):**
- `Villager.overrideSchedule` + `scheduleOverride` + `CurrentBehaviorType` — drives the FSM behavior **label** (and suppresses sleep) but **bypasses the task-dispatch pipeline**: villagers stand idle (`TaskRunner` active-task null) even at midday with a valid workstation. Use `Rpc_ChangeSchedule` (the real schedule) instead — that's the only thing that dispatches work.
- `VillagerHappiness.LeisureRate`/`WorkRate`/`SleepRate` — **get-only**, can't scale the rate. Write `_happinessVAttr` directly instead.
- Warmth/cold as a leisure trigger (incl. the `IsFreezing` flag) — caused villagers to thrash between a fire and their job (each warmth-leisure trip only warmed to the exit threshold, then they immediately got cold again). Hand cold back to the game.
- Lowering `WakeWhenRestAboveHours` to "sleep less" — only shortens each sleep's cycle, not the total sleep *fraction*; raise the rest-gain rate (`SleepHoursToFullRest`) instead.
- `Il2CppSystem.Action` event subscriptions (`_onNewDay`, `OnBehaviorTypeChanged`, etc.) — same interop delegate issues noted for the other mods; use `Update()` polling.
- The `sched=match/REVERTED` readback diagnostic shows `REVERTED` for all-Sleep/all-Leisure but `match` for all-Work — this is a packing/representation quirk of comparing `__NetworkedSchedule` to our packed value, **not** a functional revert: behavior always followed our schedule. Don't chase it.

## Reference Paths
| Purpose | Path |
|---|---|
| BepInEx core DLLs | `ASKA\BepInEx\core\` |
| Game interop assemblies | `ASKA\BepInEx\interop\` |
| Unity engine modules | `ASKA\BepInEx\unity-libs\` |
| Plugin output folder | `ASKA\BepInEx\plugins\` |
| BepInEx log | `ASKA\BepInEx\LogOutput.log` |
