# ASKA Game Architecture & Interop Knowledge Base

The single reference for **how ASKA's internals work** and **what approaches don't work**, from
Assembly-CSharp interop inspection. Organized by subsystem; each subsystem carries its own
"Dead ends (don't retry)" list. **Before touching any subsystem in a new mod, read its section here
first** — most failed approaches were game/interop facts, not mod-specific quirks, so they will bite
again if a new mod re-treads them.

The universal interop gotchas that bite *every* mod are pinned in `CLAUDE.md` (always loaded); their
full detail is under [IL2CPP interop gotchas](#il2cpp-interop-gotchas-universal) below.

---

## Namespaces
- `SSSGame.*` — ASKA's own code (Sand Sailor Studio)
- `Invector.*` / `Invector.vShooter.*` — third-party character controller framework ASKA is built on
- `SandSailorStudio.Inventory.*` — custom inventory system

---

## IL2CPP interop gotchas (universal)
These apply regardless of subsystem — they're properties of the BepInEx 6 / Il2CppInterop layer, not
of any one mod. Condensed copy lives in `CLAUDE.md`.

- **`Il2CppSystem.Action(methodRef)` event subscriptions don't work** — the constructor only accepts
  an `IntPtr` in this interop version, so you cannot subscribe a managed lambda to game `Action`
  events (`_onNewDay`, `OnFullyHarvested`, `OnBehaviorTypeChanged`, `OnTakeDamage`, etc.). **Use a
  registered `MonoBehaviour` with `Update()` polling instead** (the `DayTracker`/`RegenTracker`/
  `TorchFuelTracker`/`NeedsController` pattern). This is the single most-repeated dead end across mods.
- **`UnityEngine.Object.FindObjectsByType<T>()` (generic overload) throws `MissingMethodException`**
  through the IL2CPP-to-managed trampoline on every call (AOT generic instantiation isn't supported).
  Called from `Update()` it logs thousands of times/sec. Get instances from a Harmony patch's
  `__instance` instead of searching. (`FindAnyObjectByType<T>()` — singular — works: shipped in
  TreeRespawn's `PollWorldId` for `StorageManager` and its `WellRefill` for `SettlementManager`.)
- **The generic PLURAL `GetComponentsInChildren<T>(bool)` is missing through the trampoline too** —
  `MissingMethodException: '!!0[] UnityEngine.Component.GetComponentsInChildren(Boolean)'` on every
  call (confirmed in-game 2026-07-03, TreeRespawn v1.4.0). The **singular**
  `GetComponentInChildren<T>(bool)` works fine (shipped in DataSyncPatch / TerrainLeveler / WarpTour).
  To collect ALL matching components under a root, walk the hierarchy manually: per-object
  `GetComponent<T>()` + recurse over `transform.GetChild(i)` (MineRefreshMod's
  `GetComponentsInChildrenRecursive` and TreeRespawn `WellRefill.CollectGatherInteractions`).
- **Never Harmony-patch an IL2CPP method that takes a by-ref primitive parameter** (`Single&`, `Int32&`,
  a `bool`/`Boolean&` out-param, etc.). Marshaling the by-ref value type through the patch trampoline
  throws a `NullReferenceException` **on every call** — logged as `During invoking native->managed
  trampoline … at (il2cpp -> managed) Method(IntPtr, Single&, Byte, …)` — and since the original body
  never runs, whatever calls that method breaks hard (Mod 7 v1.0.12 patched
  `QuestRunner.FindNextBestQuest(Single& topPriority, …)` and every villager went totally inert, thousands
  of NREs/frame). The exception is **outside** your `try/catch` (it's in the glue), so you won't catch it.
  Patch a sibling method with a safe signature instead — e.g. the void/no-arg `Update()` — and call the
  game's own `Void M(Quest)`-style methods to do the work.
- **`.TryCast<T>()` is NOT available on `UnityEngine.Object`** — its wrapper's base chain is just
  `System.Object`, not `Il2CppObjectBase` (unlike normal game types e.g.
  `WorldItemInstance : Il2CppSystem.Object : Il2CppObjectBase`). Avoid by obtaining an
  already-correctly-typed instance from a patch parameter rather than from a `UnityEngine.Object[]`.
  **ROOT CAUSE + runtime escape hatch (confirmed in-game 2026-07-04, SeedScout):** this is a
  *compile-time* artifact — our csprojs reference the `unity-libs\UnityEngine.CoreModule.dll` STUB
  (where `UnityEngine.Object : System.Object`), but the RUNTIME loads the `interop\` CoreModule whose
  chain is `UnityEngine.Object → Il2CppSystem.Object → Il2CppObjectBase`. So a conversion the compiler
  rejects works at runtime via box-cast: `if ((object)unityObj is Il2CppObjectBase b)` → then
  `b.Pointer` is usable for native class checks / `new T(IntPtr)` rewraps.
- **BepInEx interpolated-string logging THROWS on Unity struct args** — passing a `Vector3`/`Vector4`
  (etc.) directly into `Logger.LogInfo($"… {someVector3} …")` picks BepInEx's
  `BepInExLogInterpolatedStringHandler` overload, whose `AppendFormatted<T>` constraint rejects the type
  at IL-verification: `VerificationException`, thrown AT THE LOG SITE inside your try/catch's scope —
  it silently killed SeedScout v1.2.0's home-island resolver. `.ToString()` struct args first, or route
  through a `(string)`-typed wrapper method (a plain `LogInfo(string msg)` compiles to `string.Format`,
  which is safe for anything).
- **Don't Harmony-patch `Initialize`/lifecycle methods on `MonoBehaviour`-based `Interaction` types**
  (e.g. `KilnInteraction`/`BellowsInteraction`/`ForgeInteraction`). They fire during early-boot
  prefab/pool init on instances whose **managed GC handle isn't set up yet**, so the il2cpp→managed
  trampoline throws `Handle is not initialized` (`GetMonoObjectFromIl2CppPointer`) while marshalling
  `__instance` — **in the glue, before your patch body** — and the original method never completes, so
  the game **fails to finish opening** (CONFIRMED 2026-06-25, TorchFuelMod v1.2.0/1.2.1). NetworkBehaviour
  types are fine here (`FireStructure.Initialize` ships safely; prefer Fusion `Spawned()` for capture). See
  the [Torch / Fire-Fuel section](#torch--fire-fuel-system) coal-buildings dead-end for the full write-up.
- **`UniqueId`-style indices are not globally unique** — e.g. `BiomeItemInstance.UniqueId` is a
  per-buffer index that restarts at 0 in every spatial chunk. Key dictionaries by **world position**
  (`GetPosition()` rounded to 0.1m, `x:y:z`) — positions are genuinely unique and stable across saves.
- **Gate all state writes on authority** — `HasAuthority` (Character/PlayerCharacter) or `_hasAuthority`
  (CharacterSurvival/VillagerSurvival). True only for the avatar/instance this client owns; other
  players' objects are visible locally with authority `false`. Writing without the gate desyncs co-op.
- **Prefer the game's own RPCs over direct networked-state writes.** Whether a direct field/inventory
  write on a networked object replicates in co-op is unverified; the game's RPCs (e.g.
  `Rpc_AddFuel`, `Rpc_ChangeSchedule`) are the network-safe path used by the game itself.
- **Never Harmony-patch a `NetworkBehaviour`'s `CopyBackingFieldsToState`/`CopyStateToBackingFields`**
  (Fusion's state-sync methods, run every network tick in the simulation path) — this **hangs the game
  at load**, no exception, just a stuck boot (confirmed the hard way on `SpellsManager`, but the rule is
  general to any `NetworkBehaviour`). If you need to capture/observe an instance of a networked type,
  hook its plain Unity lifecycle instead (`Awake`/`Spawned` are safe) and register it into a static list.
- **Managed `as`/`is` casts LIE for interop objects materialized under a base declared type**
  (list elements, base-typed patch parameters, etc.): the wrapper's managed class IS the declared type,
  so `info as DynamicDimensionTemplate` returns `null` even when the native object really is one
  (confirmed in-game 2026-07-01 — a scan over `List<ItemInfo>` found zero templates that demonstrably
  existed). Identify by asset `name`/`id`, or read the native class via
  `IL2CPP.il2cpp_object_get_class(obj.Pointer)` + `il2cpp_class_get_name`, then construct the derived
  wrapper with the generated `new T(IntPtr)` ctor. This **supersedes** the old belief that plain C#
  casts work on interop types.
- **The IL2CPP AOT compiler inlines small/single-caller methods constantly — a Harmony patch on an
  inlined method silently never fires.** Confirmed victims: `TerraformingGrid.TryLevelTile`/
  `MarkCellInteracted`, `BuildItemsTabPage.ShowPage` (private, 1 caller), `InventoryComponent.
  GetItemCollection` + `ItemCollection.GetAllItems` at specific callsites, `TabButtonCategory._OnSelect`
  (despite being declared virtual). Prefer patching **virtual/vtable-dispatched methods** (`Init`,
  `Show`, `Use`) or multi-caller publics, and **ALWAYS fire-verify a new patch with a log line** before
  trusting it.
- **`Fusion.SimulationBehaviour` exposes public properties for authority checks.** Any `NetworkBehaviour`
  inherits from `Fusion.SimulationBehaviour`, which exposes `Runner` (the Fusion runner), `Object` (this
  networked object), `HasStateAuthority`, and `HasInputAuthority` as public properties. Authority-check
  any networked object directly via `x.Runner.IsServer || x.Runner.IsSharedModeMasterClient` (build +
  runtime verified 2026-07-09, TimeWarpMod).

---

## Per-frame cost & throttling (universal)

**Finding (confirmed in-game 2026-07-07):** ~16 mods each doing per-frame work summed to a **cumulative ~22–25 ms/frame main-thread stall**, halving FPS (23–27 fps loaded vs. 53–67 plugins-off). No single mod dominated — only disabling ALL recovered the full FPS. Throttling each mod's per-frame work to a few Hz recovered ~full FPS (45–60 fps post-throttle ≈ plugin overhead eliminated).

**Anti-patterns hit (and fixes shipped):**

- **`FindAnyObjectByType<T>()` every frame in `Update()` (a full-scene search).** Gate to ~1 Hz: cache the result, set a timer, re-search only every ~1 second. (ZeroTaskWorkersMod v1.0.1, TaskUnlockerMod v1.2.2 — both were doing this on every frame to check if the world was loaded; moved the world gate outside the per-frame loop.)
- **Unthrottled per-frame loop over a settlement-sized collection** (villagers, pending respawns). Gate to a few Hz; process one (or a batched slice) per frame if you must scan. (DynamicVillagerNeedsMod v1.2.0 — was looping all villagers every frame; now gated to every 4 frames = 15 Hz at 60 FPS.)
- **Harmony patch on a per-frame-per-entity method** (e.g. `QuestRunner.Update`, ticked per villager every frame). A throttled patch BODY still pays the trampoline + entry cost on every invocation. Add a cheap `if ((Time.frameCount & N) != 0) return;` gate as the **FIRST line** to skip most invocations before any interop. (VillagerFightBackMod v1.0.29 — frame-gated the QuestRunner.Update postfix to 4 Hz, recovering ~75% of its per-frame cost; the v1.0.28 per-villager throttle via dictionary caching halved it further.)
- **Correctness pattern: throttled `Update()` doing rate/time math.** Do NOT early-return on skipped frames — that starves internal `+= Time.deltaTime` timers and breaks time accounting. Instead, accumulate the real elapsed time (`Time.deltaTime * skipped_count`) and feed THAT as `dt` on the frames you do process. (DynamicVillagerNeedsMod v1.2.0, SeedScoutMod v1.3.1 — both accumulate across skipped ticks and apply only on processing ticks.)

**Diagnostic:** use `bisect-plugins.ps1` to bisect a live-plugin performance/crash regression (disable all, re-enable one by one). Baseline vanilla framerate check: `doorstop_config.ini enabled=false`.

## Configuration & Live Reload (universal)

**BepInEx does NOT re-read edited .cfg files (confirmed in-game 2026-07-10):** `ConfigEntry.Value` reflects only in-memory state; when the user edits `BepInEx/config/com.modname.cfg` in a text editor mid-session, the changes are visible to the OS but not to the running process. To pick up file edits at runtime, call `ConfigFile.Reload()` (or `Plugin.Cfg?.Reload()`) periodically in `Update()` — the GroundItemVacuum/SeedScout/DynamicVillagerNeedsMod 5 s pattern does this at the top of `Update()` BEFORE the Enabled gate, so even toggling `Enabled=true` from the config file takes effect immediately. Single-run config parses (e.g. key bindings read once at startup) do NOT need live reload — only values that change behavior per-session need the polling.

**Typed-hotkey leak (FIXED, confirmed in-game 2026-07-10):** typing in the game's structure-rename and other text fields delivered keystrokes to `Input.GetKeyDown`, firing every mod hotkey bound to a letter while the player typed, creating unwanted mod actions mid-rename (observed 2026-07-10, pre-fix). **Recipe:** each affected mod gained a self-contained `IsTextInputFocused()` helper (interop: `UnityEngine.EventSystems.EventSystem` + `UnityEngine.UI.InputField` + `TMPro.TMP_InputField` live in `interop\UnityEngine.UI.dll` + `interop\Unity.TextMeshPro.dll` respectively; pair with `unity-libs\UnityEngine.UIModule.dll` + `UnityEngine.TextRenderingModule.dll` in csproj if needed) — checks `EventSystem.current?.currentSelectedGameObject` for a `TMPro.TMP_InputField` or legacy `UnityEngine.UI.InputField` component — and gates ONLY the hotkey block on it (config reloads/auto-timers untouched). Confirmed: rename field registers as the EventSystem's selected object; hovering other UI controls moves selection off it and re-enables hotkeys; keyboard mashing in a rename window triggers nothing; hotkeys work again after the window closes.

---

## Damage Pipeline (Projectiles / Bow)
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

**Best patch point for bow damage:** the **actual HP-reduction call** is
`SSSGame.Creature.TakeDamage(DamageData damage)` (Prefix). `DamageData` is passed by reference and can
be modified before damage is applied. (`Creature.TakeDamage` only fires for monsters — see
[Player Character vs. Creature](#player-character-vs-creature).)

In ranged `DamageData`, `.weapon` = the **arrow** (e.g. `"Wood Arrow"`), **not the bow** — match on
arrow name via `damage.weapon.info.Name`.

Other useful classes:
- `Invector.vShooter.vProjectileControl` — `minDamage` (int), `maxDamage` (int), `damageByDistance` (bool), `DropOffStart`/`DropOffEnd` (float)
- `Invector.vShooter.vShooterWeapon` — `chargeDamageMultiplier` (float), `chargeVelocityMultiplier` (float)
- `Invector.vDamage` — `damageValue` (int), `reducedDamage` (bool)

### Dead ends (don't retry)
- `SSSGame.Combat.Projectile.Shoot()` — IL2CPP trampoline crash (mixed ref value-type + nullable object params). Don't patch it; patch `Creature.TakeDamage` instead.
- `RangedManager.OnProjectileDamage()` — post-damage *notification*; modifying `result` here only changes the UI damage numbers, not actual HP.
- `Creature.TakeDamage` / `Character.TakeDamage` via the **Invector** path — never fired. The live path is `SSSGame.Creature.TakeDamage`.
- `vBowControl.OnInstantiateProjectile`, `vProjectileControl.Start()` — ASKA doesn't use these Invector hooks.

---

## Player Character vs. Creature
`SSSGame.Character` (base `Fusion.NetworkBehaviour`) and `SSSGame.Creature` (also base
`Fusion.NetworkBehaviour`) are **independent class hierarchies**, not subtype/supertype.
`PlayerCharacter : Character` is used for player avatars; `Creature` is used for monsters/NPCs. Both
implement the same `IDamageReceiver`-style contract (`TakeDamage(DamageData)`,
`CurrentHealth`/`MaxHealth`, `GetDamageTakenHistory()`, `IsPlayer()`, `IsAlive()`) but as separate,
non-overlapping implementations.

**Consequence:** a Harmony patch on `Creature.TakeDamage`/`Creature.X` only ever fires for monsters —
it never sees player damage. To affect the player, patch `SSSGame.PlayerCharacter`. `PlayerCharacter`
overrides `TakeDamage`/`Spawned`/`Despawned`/`IsPlayer` itself, so a patch on the **base** `Character`
method does **not** intercept calls dispatched to the `PlayerCharacter` override — **patch the
most-derived type.**

Useful `Character`/`PlayerCharacter` members:
```
CurrentHealth / MaxHealth   (float, public get+set)
HasAuthority                (bool, get-only) — true only for the avatar this client controls;
                             other players' characters in co-op are visible locally but HasAuthority == false
GetDamageTakenHistory()     → DamageHistory
  .LastDamageTime  (float)  — updated internally on every hit; poll this instead of subscribing
                               to OnTakeDamage (Action-typed IL2CPP delegate — see interop gotchas)
IsInCombat()                — PlayerCharacter's own built-in combat-timeout check (fixed window,
                               not configurable — implement your own timer via LastDamageTime instead)
```

`PlayerManager.LocalPlayer` (property on the scene's `PlayerManager` singleton) is the game's own
"get the local player" accessor, but no static `PlayerManager.Instance` was found — a Harmony Postfix
on `PlayerCharacter.Spawned()`/`Despawned()` (gated on `HasAuthority`) is the simpler way to capture
the local avatar.

**Villagers (soldiers/warriors — binary-confirmed via Cecil, 2026-07-08):**
- `SSSGame.Villager : SSSGame.Character` — villagers are on the Character hierarchy, NOT Creature. They share the exact health surface the player uses: `CurrentHealth` (get+set), `MaxHealth`, `IsDead`, `HasAuthority`, `GetDamageTakenHistory().LastDamageTime`.
- The ONLY subclasses of `Character` are `PlayerCharacter` and `Villager`; `Villager` has no subclasses and there is no Soldier/Guard class — warriors/soldiers are gear-flagged `Villager`s (`Villager.IsWarrior`).
- `Villager` overrides `Spawned()`/`Despawned(NetworkRunner, bool)` — patch those (most-derived type) to capture villager instances.
- ⚠️ pending in-game: whether `Character.CurrentHealth` writes stick on villagers as they do on the player (villagers also have the survival-side `CharacterSurvival._healthVAttr`, the fallback lever if not).

### Dead ends (don't retry)
- `SSSGame.Creature.TakeDamage`/`CurrentHealth` for **player** health — `Creature` only covers monsters/NPCs. Use `Character`/`PlayerCharacter`.

---

## Resource / Tree System

**In-game day / time system — `SSSGame.Weather.WeatherSystem` (Fusion NetworkBehaviour, singleton)**
```
WeatherSystem.Instance           ← singleton accessor
  ._onNewDay  (Action)           ← new-day callback (Action — can't subscribe; poll instead)
  .GetDaysPassed()  (Int32)      ← total days elapsed since game start
  .DayOfYear  (Int32)            ← current day within the year
  .dayLength  (Single)           ← configurable day length in seconds
  .NetworkedCurrentGameTime (Single) ← persists across saves; valid for elapsed-time math after restart
  .GetTimeDifferenceFromCurrentGameTimeInSeconds()  ← elapsed-since helper
  .IsNight (bool) / .DayNightValue (Single, ~1=midday ~0=deep night)
  .Rpc_ToggleFastForward()       ← dev RPC: cycles TimeSpeedMultiplier 1→10→36→75→162→1 (confirmed 2026-07-09, TimeWarpMod)
  .Rpc_SetDayOfYear(Int32)       ← dev RPC: set day-of-year, also advances GetDaysPassed() (confirmed 2026-07-09)
  .Rpc_SetGameTime(Single)       ← dev RPC: set absolute game time (untested)
  .Rpc_ToggleTimePassing()       ← dev RPC: pause/resume time (untested)
  .TimeRunningEnabled            ← writable field controlling whether time advances (untested)
```
In-game hour = `dayLength / 24`.

**Time-control oddities (confirmed 2026-07-09):** `dayOfYear` can exceed `daysInOneYear` (e.g. 74 vs 57 read in the same session) — year-wrap is not enforced the way field names suggest. Right after world load, `GetDaysPassed()` can briefly read 0 before WeatherSystem deserializes (transient).

**Respawn countdown — `SSSGame.BiomeItemAvailabilityData`** (the game's own built-in per-resource countdown; reference)
```
BiomeItemAvailabilityData
  .itemDescriptors  (List)       ← the BiomeItemDescriptors this countdown controls
  .remainingDays  (Int32)        ← days remaining before replenish; arms to Lifespan on availability evaluation,
                                    decrements per day, parks at −1; does NOT re-arm on instance consumption
  .ReplenishOnAvailable  (bool)  ← if true, calls Replenish() when remainingDays hits 0
  ._OnNewDay()                   ← called each day; decrements remainingDays (confirmed in-game 2026-07-11)
```

**Seasonal/weather availability gating (WeatherManager) — confirmed in-game 2026-07-10/11**
- **Structure:** `WeatherManager._descriptors : Dictionary<ItemInfo, BiomeItemAvailabilityData>` holds ~22 entries per world (mushrooms, crops, berries, driftwood, nests, etc.). Each entry carries an `AvailabilityProcess` ScriptableObject (process-global, NOT per-world — edit persists across world reloads). The process ANDs four condition lists: Season/Time/Other/ProgressingWeather. Evaluated on weather/season changes.
- **Vanilla mushroom gating (confirmed deterministically in-game 2026-07-10/11):** Grey Mushrooms (id 16793609): Season[Spring,Summer,Autumn] + Other[IsRaining mandatory]; Yellow Mushrooms (id 16793610): Season[Autumn] + Other[IsRaining mandatory]; plain Mushrooms (id 16793608): ungated (both Season and Other condition lists empty). The v1.4.7 TreeRespawnMod mushroom feature clears these gates to remove seasonal/rain culling.
- **Grey↔Gray plant/yield pairing:** the weather table registers the PLANT ItemInfo named "Grey Mushrooms"; world gather nodes' YIELD is a distinct ItemInfo named "Gray Mushrooms" (both spellings are literal `ItemInfo.Name` strings, NOT localization). Name-based substring filters are locale-safe as far as observed, but plant vs yield items can differ in id and gating.
- **Census recipe (v1.5.8):** read `BiomeItemAvailabilityData.itemDescriptors` (interop List via Count+indexer) → per descriptor read `IsAvailable`/`IsHarvestable`/`._instances` → per `BiomeItemInstance` read `.Active`/`.Destroyed`/`.GetQuantity()`. Instance lists reflect the currently-streamed region.
- **Dormant replenish cycle:** with gates cleared, `remainingDays` parks at −1 and the game's own replenish countdown goes dormant (no availability transitions). Un-gating must be paired with a node-respawn mechanism (TreeRespawnMod's `[GatherRespawn]` config provides the restore engine).
- **`AvailabilityProcess.CanRun` observation (2026-07-11):** reads False when all condition lists are empty (seen on both cleared and vanilla-empty processes) — semantics unconfirmed, observation only.

**Vegetation / resource classes**
```
BiomeItemDescriptor              ← represents a vegetation/resource type in the world
  .Replenish()                   ← call this to respawn the item
  .OnReplenish  (Action)         ← event fired when replenishment happens
  ._instances  (List)            ← active BiomeItemInstance list
  .IsHarvestable  (bool) / .IsAvailable  (bool)

HarvestInteraction               ← attached to harvestable world objects (trees, rocks, etc.)
  .harvestPieces  (List)         ← harvest stages in order (trunk pieces, then stump LAST)
  ._currentPieceIdx  (int)       ← which piece is currently active (GetCurrentPieceIndex())
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
  .Destroyed  (bool) / .Active  (bool)
BiomeItemInstance                ← .Initialize() fires per instance as the world loads;
  .Replenish()                   ← fully respawns the resource in place (confirmed working)
  .GetPosition()                 ← world position; use as the stable dictionary key
```

**Confirmed facts (HarvestInteraction / trees):**
- `HarvestInteraction._worldInstance.TryCast<BiomeItemInstance>()` gives the biome instance directly; non-biome resources (rocks etc.) return null and are skipped. `BiomeItemInstance.Replenish()` fully respawns in place — confirmed in-game.
- The **last** entry in `harvestPieces` is reliably the **stump** (`List<HarvestInteraction.HarvestPiece>`; element is a nested class with a `pieceId ResourcePieces` enum — no explicit stump flag, position-based). Detect stump stage via `GetCurrentPieceIndex() == harvestPieces.Count - 1`.
- `WeatherSystem.NetworkedCurrentGameTime` persists across saves — valid for elapsed-time calculations after a full game restart.

### Dead ends (don't retry)
- Subscribing to `HarvestInteraction.OnFullyHarvested` per-instance — IL2CPP `Action` delegate issue (see interop gotchas). Checking `BiomeItemInstance.Destroyed` at tick time is simpler and equivalent.
- `BiomeItemInstance.UniqueId` as a dictionary key — NOT globally unique (per-chunk index restarting at 0). Key by world position instead.

### Mining / stone-clump respawn — INVESTIGATED & ABANDONED (don't re-attempt)
Stone clumps (`Harvest_Stone4`) are **Fusion scene objects** baked into the streamed world/chunk data
— not `WorldItemInstance`s, not in the 364-entry Fusion prefab table, not spawned via `NetworkSpawner`,
and they invoke **no** managed `HarvestInteraction` method while alive (only a post-despawn
`TakeDamage` with everything nulled). Fusion has no runtime "respawn scene object" API, and the binary
can't be dumped for deeper RE (Unity 6 → IL2CPP **metadata v39**, unsupported by Il2CppDumper). Full
write-up + the three confirming tests: **`TreeRespawnMod/STONE_RESPAWN_HANDOFF.md`**. The only
achievable-but-unbuilt alternative is respawning the loose `Item_Stone_Raw`/`Item_Wood_*` nodes (those
ARE `InventoryItemInstance` and managed-visible). All mining code was removed from TreeRespawnMod 2026-06-18.

### Woodcutter / harvest-AI — and stump protection (forester ≠ woodcutter)
**Forester and woodcutter are different jobs:** the **forester** (`SSSGame.ForestryStation`, a
`FarmingStation`) *plants* trees (`ForesterPlantQuest`); the **woodcutter** *fells* trees for wood.
Don't conflate them.

Villager harvest AI runs through **`SSSGame.AI.GatherAndHarvestQuest` / `SSSGame.AI.FSM.FSM_GatherAndHarvest`**
(FSM node bodies are ScriptableObjects — **not dumpable** in this IL2CPP build, only signatures). Each
job's work zone is a **`SSSGame.HarvestMarker`** typed by `HarvestMarkerAreaType`
(`FORESTRY`/`WOOD`/`STONE`/`FORAGING`/`HUNTING`/`PATROL`/`SETTLEMENT`). ASKA already ships native player
control over *where* the woodcutter cuts: a forestry **painter UI** (`SSSGame.UI.ForestryPainterPanel` +
`ForestryCellMap`/`ForestryDataHandler`), per-station villager **whitelisting**, and a forester
`Rpc_ChangeNoNewHoles` toggle.

**The AI resource search is patchable.** `SSSGame.ResourceManager` is a plain **MonoBehaviour** (unlike the
Fusion scene-object wall that killed stone respawn). `FindResource(…)` / `CheckResources(…)` return the
chosen `WorldItemInstance` and take an `IWorldItemInstanceFilter` + `IItemFilter` + `searchDepth`. Each
candidate `BiomeItemInstance` is scored by **`GetNonExhaustedDepth(IItemFilter)`** ("how much harvestable
matches my filter") — it also exposes `IsExhausted()` / `IsAvailable()`. Name of a harvestable:
`BiomeItemInstance.Descriptor` (`BiomeItemDescriptor : WorldItemDescriptor`) → `GetFormalName()` / `.itemInfo`.

**Stumps are valid woodcutter targets (confirmed in-game 2026-06-26):** with TreeRespawnMod installed but
no manual stump-clearing, the user saw slow deforestation over ~30 in-game days — woodcutters occasionally
harvest the leftover stump for **firewood** (stumps drop firewood — that's the draw), which sets the instance
`Destroyed`, which `DayTracker` reads as "stump cleared → cancel respawn", so each pick permanently loses a tree.

**What a "stump" actually IS (CONFIRMED in-game 2026-06-26, TreeRespawnMod v1.1.3 identity diagnostic).** The
in-game "Tree Stump" label is a *display name only* — it is **not** a separate object. A tree is one
`Harvest_Wood_<species><n>` GameObject backed by one **`BiomeItemInstance`** with multiple `harvestPieces`
(a Fir has `pieces=2`: trunk, then stump). The **stump is that same instance at its LAST piece**; a standing
tree is the same instance at an earlier piece. The fallen trunk and branches are **separate `Item_Wood_*`
non-biome `WorldItemInstance`s** (loose logs/debris the woodcutter hauls). So:
- `Harvest_Wood_Fir5`, `worldInst=Biome`, `pieces=2`, at last piece → **the stump** (`CanProvideItem → 6/8` firewood).
- `Item_Wood_Fir5` / `Item_Wood_Bark` / `Item_Wood_RawLongStick`, `non-Biome` → fallen logs/debris (leave these alone — they're the haul).
- ⚠️ **The HarvestInteraction's `transform.position` ≠ `BiomeItemInstance.GetPosition()`** (observed `225.9:40.7:583.0`
  vs `225.8:38.0:582.9` — notably different Y). The biome-instance position is the canonical key
  (`PendingRespawns`/`ActiveInstances` use it); never key a stump by the HarvestInteraction transform.

**Which gate the woodcutter uses + the fix (CONFIRMED working in-game 2026-06-26, v1.1.6).** A v1.1.1 diagnostic instrumented
every candidate gate for a stump: `GetNonExhaustedDepth` → **-1**, `IsExhausted()` → **True**, `IsAvailable()`
→ **True** ("instance active", not "harvestable"), `Check(ItemInfo)` → **False** — all already read depleted,
yet the woodcutter still picks the stump because **`HarvestInteraction.CanProvideItem(ItemInfo)` → 6/8** (it
leaks the stump's firewood yield). **Fix:** Postfix `CanProvideItem` → `__result = 0` when the instance is a
**multi-piece `BiomeItemInstance` (`pieces >= 2`) at its last piece** (i.e. a tree stump). This is a
**structural** gate — it deliberately does NOT touch standing trees (same object, earlier piece → still
felled) or loose `Item_Wood_*` logs (non-biome → still hauled). The player clears stumps via axe **damage
(TakeDamage), not this query**, so manual clearing — and "cleared stump = permanent" — still works.
- **Work-priority interaction (confirmed in-game):** if firewood is prioritized and the only firewood left is
  protected stumps, woodcutters idle via `GatherAndHarvestData.ComplainNoResourcesFound` — vanilla priority
  starvation, NOT a mod softlock. Keep logs/long-sticks at equal priority (or fell a tree near them) and they
  resume. A toggle-gated diagnostic (`EnableDiagnostics`, `WorkerIdleDiagPatch`) Postfixes
  `ComplainNoResourcesFound`/`ComplainNoGatherTask` to confirm this in the log.
- **Dead ends (don't retry):** (1) zeroing/forcing `GetNonExhaustedDepth`/`IsExhausted`/`IsAvailable`/`Check`
  does nothing — a stump already reports depleted/false through all of them. (2) Gating the `CanProvideItem`
  fix on `PendingRespawns` membership (v1.1.2) **failed** — the `CanProvideItem` query races the fell-time
  registration, so the position isn't in the set yet; gate structurally on piece index instead. (3) Do **not**
  block damage to the stump — the AI would believe wood remains and swing forever; remove it from *selection*.

---

## Gather / Press-to-Collect System
```
GatherInteraction (SSSGame.GatherInteraction : SSSGame.Interaction)
  .GatherItemsCharge(IInteractionAgent, Int32 charges, ItemContainer) → Int32
                                   ← safe Postfix patch point; no ref params; fires when items actually collected
  .CheckAvailableItemCount()  → Int32   ← returns 0 when resource is fully exhausted (register respawn here)
  ._worldInstance  (WorldItemInstance)  ← same path as HarvestInteraction; TryCast<BiomeItemInstance>() works
  .GetGatherableItemInfo()    → ItemInfo ← yields the ITEM name (not the node/resource name — see below)
```

**Critical distinction:** `GetGatherableItemInfo().Name` is the **yielded item**, not the world node name.
- All names confirmed in-game against the inventory/storage UI (2026-06-17):
  - Reeds → `"Thatch"`, Berry Bush → `"Berries"`, Dwarf Spruce → `"Stick"`, Flax Bush → `"Fibers"` (PLURAL — verified live in the respawn log 2026-06-25; the config key `Fiber` still matches it via the case-insensitive `itemName.Contains(key)` substring test, so the override applies regardless)
  - `"Small Stone"` (ground pickup), `"Mussels"`, `"Feathers"` (Bird's Nest), `"Water"` (Natural Water Collector)
  - Vegetables: `"Carrot"`, `"Cabbage"`, `"Onion"`, `"Garlic"`, `"Beetroot"`
  - Mushrooms: `"Mushroom"` substring matches Gray/Grey/Yellow Mushrooms
- **Not their own GatherInteraction** (bonus drops bundled with parent gather — can't trigger respawn): `"Seeds"` (plants), `"Wild Egg"` (Bird's Nest)
- **No stump-equivalent visual state (confirmed in-game 2026-06-28):** unlike a felled tree (which leaves
  a visible stump — see Resource/Tree System), an exhausted gather resource (reed/flax/berry bush etc.)
  just **disappears entirely** with no depleted-but-present model left behind. This means you can't tell
  by looking whether a bare spot still has a live `BiomeItemInstance` waiting on a pending respawn, or
  whether the underlying object was destroyed/removed some other way — there's no observational test that
  distinguishes the two. Relevant if investigating "a gathered resource never came back": don't expect a
  stump-like visual cue the way tree respawn debugging can rely on.
- **`IsExhausted()` does NOT flag an empty gather node (confirmed in-game 2026-07-03).** It's
  stump/harvest semantics only (a stump reads `true` — see Resource/Tree, v1.1.1 gates); a gather node
  picked to zero stock keeps reading `false` (`IsAvailable()` also stays `true` at qty 0 — it means
  "instance active", not "has stock"). Gather depletion is a **quantity** condition: `GetQuantity() <= 0`
  (pure data read, works even while the chunk is deactivated). **Dead-end (don't retry):** TreeRespawn
  v1.3.1 gated its network data-sync catch on `IsExhausted()` alone, assuming an empty gather node reads
  it true like a stump — result was `registered gather=0` in every diagnostic window for an entire co-op
  session while every depleted gather node was silently binned as "healthy". Evidence it was the gate:
  widening it to `IsExhausted() || GetQuantity() <= 0` (v1.3.2) made the same detection fire steadily
  under identical conditions. Never gate a gather-depletion check on `IsExhausted()`.
- **Co-op client gathers ARE catchable host-side from data alone (confirmed in-game 2026-07-03).** A
  client's harvests replicate to the host as `WorldItemInstance` data changes (`_OnDataChanged` fires);
  classify the node via a bind-time posKey→kind/item map (`SetWorldInstance` postfixes — the one moment
  interaction and instance are both in hand) and register on pure data conditions (`GetQuantity() <= 0`
  gather / `IsExhausted() && !Destroyed` tree). Detects client gathers and chops **both near and far
  from the host**, no GameObject/GetComponent needed (TreeRespawn `DataSyncPatch` + `Registration.cs`).

### Constructed water structures — Well / Rain Collector (confirmed in-game 2026-07-04)
Built water buildings are **`Structure`s carrying a plain charge-based `GatherInteraction`**, NOT
biome nodes — none of the BiomeItemInstance/Replenish machinery applies. Confirmed live:
`'Water Well'` (max 50), `'Rain Collector'` (max 10); display names via `Structure.GetName()`.
- **Find them:** `FindAnyObjectByType<SettlementManager>()` → settlement **getter methods** (the
  `.settlements` list is a dead end — see Settlement Queries) → `Settlement.GetStructures()` →
  hierarchy-walk each structure for `GatherInteraction`s (plural generic GetComponentsInChildren is
  a dead end — see universal gotchas) → keep those whose `GetGatherableItemInfo().Name == "Water"`.
  A structure-hosted water gather can only be a built well/collector — the wild Natural Water
  Collector is a biome node and never appears in `GetStructures()`.
- **Refill lever: `GatherInteraction.ReplenishCharges(int)`** host-side — the count verifiably
  rises (`CheckAvailableItemCount()`), the in-game capacity readout follows, and villagers drink
  the added stock (real game state, not cosmetic). Clamp with `CheckMaximumItemCount()`; the
  `*Client` variants exist for the client side (untested). Co-op client-side behavior unverified ⚠️.
- **Time base:** anchor `WeatherSystem.NetworkedCurrentGameTime` and measure ONLY via
  `GetTimeDifferenceFromCurrentGameTimeInSeconds(anchor)` — the value's internal unit is opaque;
  never do seconds-arithmetic directly on it (TreeRespawn WellRefill's anchor+carry pattern).
- Vanilla's own refill wiring was NOT identified (no well-specific type exists; prefab-wired
  `Timer`/`WeatherConditionsHelper` + `ReplenishOneChargeIfTrue(bool)` is the ⚠️ unverified
  hypothesis) — irrelevant for modding: the mod's additive `ReplenishCharges` rides on top.

---

## Item Naming
- Item names live in Unity ScriptableObject assets, not in code — read at runtime via `item.info.Name` (the `ItemInfo.Name` property)
- Known item names: **"Flimsy Shortbow"** (first bow), **"Wood Arrow"** (early arrows)
- In ranged `DamageData`, `weapon` = the **arrow**, not the bow — match on arrow name

---

## Structures, Workstations & the AI Quest/Task system (general)
- **`SSSGame.Structure`** is the base building (a `NetworkBehaviour`). Useful members: `StructureName` / `DefaultName` / `GetName()` (display name, same role as `ItemInfo.Name` — use for substring filtering), `storageSupplies` (array of the structure's `StorageSupply` dispatchers), `Settlement`, `Parent`/`Root`, `OnSpawned`/`OnStructureDeath` actions.
- **Get a typed component off a structure:** `structure.TryGetStructureComponent<T>(out T comp)` (bool) or `GetStructureComponent<T>()` / `FindStructureComponent(predicate)`. This is how you ask "is this building a `CraftingStation`/`ResourceStorage`/…".
- **Workstations** (`ResourceStorage`, `CraftingStation`, `CookingStation`, etc.) are `Workstation : NetworkBehaviour` structure-components. Each owns a set of AI **Quests** (`SSSGame.AI.*Quest`, e.g. `CrafterFetchQuest`, `SupplyPatrolQuest`, `GatherAndHarvestQuest`) exposed as `vFSMBehaviour` fields, and runs them through **FSM nodes** under `SSSGame.AI.FSM.*` (ScriptableObjects — their method bodies are NOT dumpable in IL2CPP, only signatures). Active work is tracked by `SSSGame.AI.TaskRunner`.
- **Per-villager whitelisting** exists on workstations: `IsWhitelisted(Villager)`, `Rpc_ChangeWhitelistedVillager(id, state)`, `WhitelistNewVillagers` — the game's built-in "which villagers may use this station."

### `GatherAndHarvestQuest` — fiber gather can get permanently stuck (confirmed in-game 2026-06-28)
`GatherAndHarvestQuest.GatherAndHarvestData.ComplainNoResourcesFound(bool value, ItemManifest
finderManifest)` — the `finderManifest` parameter (previously unread by any mod) names what the worker
was searching for. Confirmed in **two independent worlds** with **two different villagers**: both
permanently stuck wanting exactly `Fibers x 15` (`ItemManifest.GetItems()` → `ItemInfoQuantity[]`,
`.itemInfo.Name`/`.quantity`), even with flax visibly present and reachable — the complaint just repeats
forever. The identical "15" across unrelated worlds/villagers means it's a fixed constant (most likely a
workstation/recipe stockpile target — same shape as `CookingStockpileQuestData`'s `NeededSpecificSupplies`/
`NeededFillerSupplies` fixed-target manifests, see Cooking Station Pipeline below), not a per-world
computed deficit. **Leading hypothesis, not fully confirmed:** the resource-search needs a single source
able to satisfy the whole manifest in one shot; since one flax harvest yields only 6-7 fiber, no source
can ever satisfy a 15-unit request, so it fails forever regardless of flax abundance/proximity. **Can't
be confirmed further without an IL decompile** — `SSSGame.AI.FSM.*` quest-behavior nodes are
ScriptableObjects whose method bodies Cecil can't dump (signatures only). Full writeup, evidence, and the
cheap in-game verification (check the stuck villager's job for a workstation needing exactly 15 fiber):
[`../TREERESPAWN_HANDOFF.md`](../TREERESPAWN_HANDOFF.md) Issue F. **Not a TreeRespawnMod bug** —
discovered through its diagnostics, but the mod doesn't touch quest targets or resource search.

### Workstation task assignment (Mod 18 groundwork)
- **`_CanAddVillagerToTaskData(Villager, WorkstationTaskData)` has FOUR implementations:** base `SSSGame.Workstation` plus overrides in `Buildstation`, `Marketplace`, `ResourceStorage`. Harmony patches concrete methods, so blocking task inheritance requires patching all four. Derived overrides call base (paired log lines confirmed).
- **`AddToTaskDatas(Villager)` has TWO implementations:** `Workstation` + `HarboringStation` override. It fires at WORLD LOAD for every villager (per-station re-add burst), BEFORE `DeserializeTaskData` — vanilla load order is Add-then-Deserialize; the later Deserialize overwrites `VillagersInCharge` from the save (this is also how vanilla preserves unchecked task boxes).
- **The task-assignment UI does NOT read `_CanAddVillagerToTaskData`:** menu-open and checkbox toggles never call it (the checkbox path is RPC-only), so a prefix-block on `_CanAdd` cannot break manual checkbox assignment. `_CanAdd` is called ONLY from inside `AddToTaskDatas`.
- **Unassigning a villager from a station auto-transfers them to the `Buildstation`** (Remove → Add(Buildstation)) — any inheritance-blocking mod must exempt the Buildstation family or unassigned villagers lose build/firekeep tasks.
- **Load/upgrade deserialize paths:** `Workstation.DeserializeTaskData(DataObject)` (+ `Buildstation` override), `Workstation.DeserializeTaskDataForRecreation(DataObject)`. `Workstation._structure : Structure` is the back-reference (may be '?'/unlinked during load-path and on Buildstation).
- **Interop chain:** `SSSGame.Workstation` → `NetworkComponent` → `Fusion.NetworkBehaviour` → `Fusion.SimulationBehaviour`, so `HasStateAuthority` compiles directly on `Workstation` (Cecil cross-assembly chain walk, build-verified).

### Workstation task priority machinery (idea-12 groundwork)
**Task data + priority (confirmed in-game 2026-07-09):**
- **`WorkstationTaskData` structure:** `itemInfoQuantity : ItemInfoQuantity` (item + quota), `priority : Int32`, `pinnedTask : Boolean`, `VillagersInCharge : List`, `onDataChanged : Action`, `GetQuantityRange()`. Priority is SERIALIZED into saves (key names per station type: `c_taskDataPriority` on CraftingStation/CookingStation/AnimalPen; `c_taskPriority` on Buildstation/ResourceStorage; `c_taskPriorityKey` on FarmingStation) — a modified priority PERSISTS across save/reload.
- **Native co-op priority update path:** UI `WorkstationMenu.IncreaseTaskPriority/DecreaseTaskPriority(TaskDataPanel)` → `TaskDataPanel._SetPriority(WorkstationTaskData)` → **`NetworkWorkstation<T>.Rpc_ChangeTaskPriority(Int32, Int32)`** + `OnTaskPriorityChanged(WorkstationTaskData)` callback (exact arg signatures ⚠️ unverified, but the RPC exists and fires).
- **Task-priority tiers/types:** `WorkstationTaskPriority` enum exists (seen on `StorageSupply.defaultTaskPriority`) — [likely] the High/Medium/Low tier used by gathering stations (woodcutter, forager, stonecutter). Crafting stations use an integer `priority : Int32` for a ranked list.
- **Item → producer lookup:** `CraftingStation.KnowsBlueprintForItem(ItemInfo, out CraftBlueprint)` — check if a station can craft an item. Also exists: `_outsourcedQuests`/`_outsourcedBlueprints` dictionaries and `GetPersonalFetchDepth(Villager, ItemInfo, out minDepth, out topPriority)` on CraftingStation (fetch-chain plumbing, unexplored). `BuildstationQuest.c_TaskPriorityCoefficient : Single` hints priority feeds quest-priority math.

**Two priority mechanisms + the vanilla starvation loop (confirmed in-game 2026-07-09):**
1. **Crafting stations:** use an absolute RANKED LIST with per-task QUOTA measured against LOCAL inventory. Worker fills rank 1's quota locally, moves to rank 2, snaps BACK to earlier ranks if their quota drops below the local count.
2. **Gathering stations** (woodcutter, forager, stonecutter, …): use High/Medium/Low TIERS per eligible resource + quota — all Highs satisfied → Mediums → Lows.
3. **Priority is effectively WINNER-TAKE-ALL**, not a soft weight: one task set higher monopolizes the worker (a woodcutter with only long-hardwood-stick=High did nothing else; a warehouse with one raised Collect-priority let everything else run dry).
4. **The persistent-monopoly mechanism:** QUOTA-vs-LOCAL-INVENTORY + WAREHOUSE-HAULER-DRAIN loop — haulers carrying output away keep the quota unmet, so the top task never self-completes (intended vanilla design; creates the user-observed starvation loop).

### Builder-loan recipe via agent registry (idea-11 Phase 2, confirmed in-game 2026-07-10)
**Loan plumbing — off-duty villagers temporarily assigned to the builder pool:**
- Villager assignment = single `Villager._workstation` pointer (networked `__WorkstationID`, `Rpc_ChangeWorkstation`); the UI job icon/marker and schedule-panel binding read it. `_GetDefaultBuildstationToAssign()` resolves the unassigned pool; "builders" ARE villagers assigned to the Buildstation.
- Station-side agent registry (`Workstation._taskAgents`, `SetTaskAgent`/`ReleaseTaskAgent` — base-class methods) is SEPARATE from the villager pointer. **The builder-loan recipe (confirmed in-game 2026-07-10):** `home.ReleaseTaskAgent(agent)` removes home quests from the villager's QuestRunner without touching `_workstation` or task checkboxes; `bs.SetTaskAgent(agent)` + `bs.AddToTaskDatas(villager)` + an all-Work schedule write = the villager does real builder work (build/haul/terraform/lightkeep) while every UI surface still shows the home job; exact reverse restores cleanly. `SetTaskAgent` ALONE is insufficient — home quests outcompete (dead-end, v1.6.0). Registry membership is sticky (no reconciler eviction observed across sessions).
- Observed quest priorities: `BuilderPrepareForWorkQuest` 16.0, `BuildAndSupplyQuest` 15.1, cooking quests ~15.5.
- `ITaskAgent`/`IWorkstation` interop wrapper classes are in `SSSGame.AI` (not SSSGame), `.ctor(IntPtr)`; interop `Villager` does NOT managed-implement `ITaskAgent` — rewrap via pointer (boxing pattern for `.Pointer`).
- **Buildstation cohort trap:** unassigned (incl. newly-summoned) villagers are auto-parked on the Buildstation → any station-pointer cohort grouping must exclude the Buildstation family (native class-name check; managed `is` lies) or the "builder pool" masquerades as a schedulable cohort.
- Lent villagers also receive the Buildstation complain quest → builder barks while idle on loan.

### Reusable structure-footprint spatial query — "is (x,z) inside a player-built structure?" (TreeRespawnMod v1.5.x, confirmed in-game 2026-07-07)
Generic helper `TreeRespawnMod/StructureQuery.cs` — `IsBlockedByStructure(x, z, margin)` — for any "don't do X
where the player built something" rule (trees regrowing through houses, item/creature spawns, terrain edits…).
Mechanism:
- **Building list:** `SettlementManager` (`FindAnyObjectByType`) → settlements via the getter methods
  (`GetPlayerSettlement()` / `GetCurrentSettlement()` / `worldSettlement`; the `settlements` list accessor stays
  null even when loaded) → `Settlement.GetStructures()`. Same authoritative walk WellRefill uses (returns every
  placed building), and it reads **host-side data present regardless of where the player stands or what terrain
  is streamed in** — so it answers correctly even for a spot far from the player.
- **Footprint per building:** union of the structure's **non-trigger `Collider` bounds** as a horizontal AABB
  (min/max on X and Z). Collect colliders by a **manual hierarchy walk** doing per-node `GetComponent<Collider>()`
  + child recursion — the plural `GetComponentsInChildren<Collider>(true)` is trampoline-broken
  (MissingMethodException, the universal gotcha). Base-typed `GetComponent<Collider>()` **does** surface derived
  colliders through interop (confirmed in-game 2026-07-07). Falls back to a degenerate point-rect at
  `structure.GetPosition()` if a building yields no colliders.
- **Why footprint, not center-distance:** a fixed radius from each building's ORIGIN is inconsistent by size — it
  blocks a point next to a small hut but misses one at a longhouse corner (confirmed in-game 2026-07-07). Test
  point-in-rect ± `margin` instead. AABB is axis-aligned, so a rotated building gets a slightly generous box at
  the corners — harmless for a keep-out check, and `margin` is a deliberate extra buffer.
- **Caching + load-race:** footprints cached ~15 s (buildings are static, rarely placed); **only a NON-EMPTY
  snapshot is cached** — an early-load walk returns empty (the settlement resolves a beat after the manager) and
  caching that would blind the query for a full window. Expose `DataReady` (true once ≥1 building is walked) so a
  caller can **HOLD** an action until footprints load, paired with a **time cap** so a genuinely building-less
  world still proceeds (DayTracker's 45 s `StructureDataStillLoading` grace — a prior-session tree comes due the
  instant the world loads, before the cache is built).
- **Fail-open** (any resolve/read error → not blocked — a backstop must never suppress a legitimate action just
  because the settlement wasn't resolvable) and **cache MUST be cleared on world switch** (positions are
  world-specific) — `StructureQuery.ClearCache()` from the world-leave/switch handlers.
- Related members: `Structure.GetPosition()`, `Settlement.GetStructures()`; see also WellRefill's identical
  settlement walk (docs/mods/tree-respawn.md → Well water refill).

### Villager Complaint / Issue System (idea-12 groundwork)
**Structured complaint events for "villager needs X" (confirmed via Cecil dump 2026-07-09):**
- **`SSSGame.AI.Complaint`** (Il2CppSystem.Object-derived) is the typed system behind villager barks/exclamation-point statuses — **NOT text parsing**.
- **Per-villager complaint tracking:** `VillagerSocial.complaints : List<Complaint>`, `AddComplaint(Complaint)`/`_AddComplaintInternal(Complaint)` (candidate Harmony postfix for capture), `Rpc_AddComplaint(Complaint)`/`Rpc_RemoveComplaint(Complaint)`, `OnComplainChange : Action` (do NOT subscribe — IL2CPP Action gotcha; patch the add method or poll instead), `ImportantComplaintsCount`/`NotificationComplaintsCount`/`MenuNotificationComplaintsCount` counters.
- **Per-complaint alert tiers:** boolean flags `important`/`notification`/`forcedMenuNotification` control UI alert level.
- **Typed complaint payloads carry the missing resource/item directly:** `ItemComplaint.itemInfo : ItemInfo`, `ItemManifestComplaint.itemManifest : ItemManifest` (+ `maxItemCount`), `ItemCategoryComplaint.itemCategory`, `ComplexCategoryComplaint` (itemA, itemB, category), `LoadoutsComplaint.missingItems : List<ItemInfo>`, `StructureComplaint._structure`. **No text parsing needed — every missing item is a strong-typed object.**
- **~120 named complaint format keys** cover every station type: `c_defender_needAmmo_format`, `c_buildstation_noSupplies_format(Single)`, `c_crafting_noPartsFound_format`, `c_farming_noSeeds_format`, `c_item_needItem_format`, etc.
- **Settlement-wide issue aggregation:** `SettlementIssueTrackerWidget` (UI widget tracking maps of marker/storage-full/mine-exhausted/no-crafting-station/no-production/farm complaints across all villagers) + `SettlementIssuesTabPage`.
- **Per-station complaint quests:** every workstation type owns a typed complain quest (`CrafterComplainQuest`, `BuildstationComplainQuest`, `CookingComplainQuest`, `ForesterComplaintQuest`, …) + `complainBehaviour : vFSMBehaviour`; quest priority tier `QuestPriority` has `c_ComplainHigh`/`c_WorkstationComplain`/`c_ComplainLow`.
- **Subclass identification:** `TryCast<T>(Complaint)` works on Il2CppSystem.Object-derived complaint types (confirmed pattern from QuestData precedent).

---

## Settlement Hauling / Storage / Task-Dispatcher System (Mod 6 groundwork)
How resources get moved into the central **warehouse** (a `ResourceStorage` building) — the system
behind the "warehouse worker steals crafter materials" loop:
```
SSSGame.AI.TaskDispatcher (MonoBehaviour)
  → SSSGame.AI.StructureTaskDispatcher
       .OwnerStructure (Structure)   ← the building this dispatcher belongs to (key back-reference)
       .Initialize(Structure) / _LinkToStructure(Structure)
       → SSSGame.StorageSupply        ← "here are items available to be hauled from this structure"
            .IsAvailable() (bool), .interaction (StorageInteraction), .taskMaxQuota
       → SSSGame.DependendTaskStorageSupply : StorageSupply  (gather-dependent variant)

SSSGame.ResourceStorage (the warehouse/storage building; a Workstation)
  .CreateOrUpdateTasksFromStorageSupply(StorageSupply, List taskAgentsToVillagers, bool addForChildren)
                                        ← builds the "haul these items to me" tasks from a supply
  .CanCreateStorageTaskForItemInfo(StorageSupply, ItemInfo) → bool   ← clean per-item GATE (prefix here)
  .GetGatheredItemQuantity / GetGatheredItemPriority / FillResourceStorageTaskDatas
  .excludedResourcesList (ItemInfoList), onlyGatherNativeItems / _nativeItemsFilter — built-in exclusions
```
- **The warehouse pulls from every `StorageSupply` in the settlement.** A `CraftingStation` registers supplies for BOTH its **input** material bins (`stationInventory` / `_storageSupplies`) and its **output**; the output is additionally wrapped in **`CraftingStation/OutputStorageWrapper`** (`._cs`, `._ss` = the output `StorageSupply`). The warehouse hauling the *input* supplies is the theft loop (crafter re-fetches → warehouse re-hauls).
- **To filter what the warehouse hauls:** prefix `ResourceStorage.CanCreateStorageTaskForItemInfo(StorageSupply ss, ItemInfo)` and inspect `ss.OwnerStructure`: skip (`__result=false`) when the owner is a `CraftingStation` (protect its inputs) and/or its `StructureName`/`DefaultName` matches a user ignore-list. This only suppresses the warehouse's *outbound* haul tasks — the crafter's own fetch is untouched, so the loop simply stops. (See `DYNAMIC_HAULING_HANDOFF.md` for the Mod 6 plan.)

---

## Inventory, Settlement Queries & Recipes (general — storage/crafting toolkit)
Confirmed via interop dump 2026-06-21 while scoping a (cancelled) fisher-bait mod. None of this is
mod-specific — reuse it for any storage / crafting / needs idea.

**Settlement-wide access & enumeration**
```
SSSGame.SettlementManager (MonoBehaviour)
  .GetPlayerSettlement() / .GetCurrentSettlement() / .GetClosestSettlement(Vector3) → Settlement
  .settlements (List) / .worldSettlement / .OnSettlementLoaded (Action)  ← wait for load before querying
  .IsLoading (bool)
```
⚠️ **`.settlements` stays `null` even in a fully-loaded, busy village** (confirmed in-game
2026-07-03/04, TreeRespawn WellRefill: `list=null` on every scan of a whole session while
`GetPlayerSettlement()`/`GetCurrentSettlement()`/`worldSettlement` all returned live settlements
and `GetStructures()` walked 239 buildings). Resolve settlements via the **getter methods**, never
the list. `FindAnyObjectByType<SettlementManager>()` works for getting the manager itself.
```
SSSGame.Settlement (MonoBehaviour)
  .QuerySettlementResources() → ItemManifest   ← ONE call = total item counts across the WHOLE village
                                                 (the clean "does the settlement have X, and how many?" query)
  .GetStructures() (List) / .FindStructures(pred) / .FindStructure(pred)        ← enumerate buildings
  .FindStructuresInRange(pos, range, result, pred) / .FindClosestStructure(pos, range, pred)
  .GetStorageSites() (IReadOnlyList) / .FindItemStorage(filter, …) → Interaction (storage holding an item)
  .FindStorageToDeposit(ItemInfo, pos, range, pred, ignoreLimits) → Interaction (where to drop an item)
  .OnAddStructure / .OnRemoveStructure / .OnActivateStructure (Action<Structure>) ← track buildings live
```
No static `Settlement.Instance` — reach it via a `SettlementManager` instance, or via any `Structure.Settlement` back-ref.

**Inventory read / write — `SandSailorStudio.Inventory.ItemCollection` / `ItemContainer`**
A workstation's own stock: `Workstation.GetInventory() → ItemCollection`; what a station is asking the village to deliver: `Workstation.GetItemsNeededFromSettlement() → List`.
```
ItemCollection (a logical inventory = a set of ItemContainers)
  .GetItemQuantity(ItemInfo) / .HasItem(ItemInfo) / .GetFirstItem(ItemInfo)
  .GetCategoryItems(ItemCategoryInfo) / .GetCategoryItemsCount(...) / .HasCategoryItems(...)   ← by category
  .AddItems(ItemInfo, count) / .RemoveItems(ItemInfo, qty)  → int actually added/removed
  .GetTotalRemainingCapacity(ItemInfo) / .FindLargestMatchingContainer(ItemInfo)
  .OnItemAdded / .OnItemRemoved (ItemEventHandler)
ItemContainer (one physical bin)  .AddItems / .RemoveItem / .HasSpace(ItemInfo, qty) / .GetItemCount(...)
```
⚠️ Whether a direct `AddItems`/`RemoveItems` on a **networked** `ResourceStorage` replicates in co-op is UNVERIFIED. TorchFuelMod deliberately used the game's own RPC (`Rpc_AddFuel`) to stay network-safe — look for an analogous item RPC before writing storage state directly. Host/solo is unaffected either way.

**`SandSailorStudio.Inventory.ItemManifest` — the counts / requirements struct**
```
.GetQuantity(ItemInfo) / .GetItems() / .Check(ItemInfo) / .GetTotalQuantity()
.Contains(ItemManifest) / .Overlaps(...) / .AddItem / .RemoveItem
static .CreateFromBlueprint(BlueprintInfo)        ← READ A RECIPE'S REQUIRED INGREDIENTS as a manifest
static .CreateFromContainer(ItemContainer) / .CreateFromFilter(ItemCollection, filter)
.Transfer(ItemContainer↔ItemCollection / collection→collection)   ← bulk move helpers
```

**Recipes / blueprints**
```
SandSailorStudio.Inventory.BlueprintInfo (base "recipe") — ingredients via ItemManifest.CreateFromBlueprint(bp)
  SSSGame.CraftBlueprintInfo : BlueprintInfo   .GetItemInfo() → produced ItemInfo, .craftVolume, .interaction (CraftInteraction)
  SSSGame.CookingRecipeInfo : BlueprintInfo  /  SSSGame.CrockpotRecipeInfo : CookingRecipeInfo
     .cookingRequirements : CookingRecipeRequirement[] { ItemInfo acceptedInfo; int count; type; ItemTableConfig tableConfig }
  Forging / Dyeing / Painting / Workshop / Structure variants also derive from Blueprint(Info)
```
`ItemInfo`s are ScriptableObjects (not constructable) — get a reference by name (`item.info.Name`) off a live item, by category via `ItemCategoryInfo`, or from a blueprint's `GetItemInfo()`.

**Fishing/bait (the trigger for the above — mod cancelled, game does it natively)**
`SSSGame.FishingStation : ResourceStorage : Workstation`; bait = `SSSGame.BaitItem : EquipmentItem` (category `VillagerFishingInteractionConfig.BaitCategInfo`); the "no bait" complaint is `FishingComplainQuest : CrafterComplainQuest`. **Villagers self-craft bait from warehouse ingredients with no mod**; the "no bait" bark actually means "no *ingredients* to make bait." Don't build a fisher-bait mod — confirmed in-game 2026-06-21 (also recorded in session memory `fisher-bait-native`).

**Storage acceptance / the Outhouse container (OuthouseComposter groundwork, confirmed in-game 2026-07-11)**
**Container acceptance is NOT storage-class-based alone.** Pointer-verified in-game: every SmallItem-class item (Fibers, Jotun Blood, Feathers, Gray Mushrooms, Raw Red Meat, Compost) carries the IDENTICAL `ItemStorageClass` asset (same native pointer) that the Outhouse containerType lists — yet the Outhouse container accepts ONLY 'Compost' (`CanStoreItemType`/`Check` true, `GetStackSize`=10). `ItemContainer.CanStoreItemType(ItemInfo)` applies a per-item condition beyond class membership (mechanism unidentified; OuthouseComposterMod v0.2.0 overrides it with scoped postfixes rather than reverse-engineering it). `SSSGame.StorageInteraction.Check(ItemInfo)` (the UI-level gate) always mirrors `container.Check` — patching the container level is sufficient.

**`ItemStorageClass` is an EMPTY ScriptableObject** — a pure name tag; `ItemContainerType.storageClasses` entries are `StorageClassData { storageClass, stackSize }`. ⚠️ `stackSize` is NOT the visible stack cap (Outhouse entry has stackSize=1 yet Compost stacks to 10 there) — semantics unresolved.

**Outhouse ground truth:** a generic `Structure`, display name AND `DefaultName` both exactly 'Outhouse'. Its `ItemContainerComponent` (`.container : ItemContainer`) sits on child node **'crapContainer'**; `StorageInteraction` on node 'ActiveInteractions'. containerType **'Storage_SmallItems_Outhouse' is UNIQUE to the Outhouse** (52 containerTypes scanned across 199 structures). capacity=20 (4×5 UI grid), `IsHidden`=False, `NoActionTargets`=False, `canAddItems`=True. NO `NetworkSimpleResourceStorage_N` Fusion backing observed on it (only `NetworkPositionYRotation` at the root).

**Compost ground truth:** `FarmingStation.composts` == `FarmCropInteraction.compatibleComposts` == ['Spoiled Food', 'Compost', 'Crawler Slime', 'Slag']. The vanilla Outhouse stores 'Spoiled Food' — which is itself a valid compost item, i.e. vanilla already implements a rot→outhouse→farm loop; the mod adds fresh food/seed → 'Compost' conversion.

**Useful ItemContainer API surface** (Cecil 2026-07-11, exercised in-game): `CanStoreItemType(ItemInfo)`, `Check(ItemInfo)`, `HasSpace(ItemInfo, Int32)`, `GetStackSize(ItemInfo)`, `GetEmptySlots()`, `AddItems(ItemInfo, Int32) : Int32`, `RemoveItem(Item, Int32, ItemEventContext)` — **`ItemEventContext` is an ENUM** (pass `ItemEventContext.Default`), `GetItems() : IReadOnlyList<Item>` (through the compile-time reference only the indexer is visible — bound iteration by `capacity`), `canAddItems`, `containerType`, `GetAcceptedItemTypes()`.

**Player inventory recipe (confirmed in-game):** capture the local player via `PlayerCharacter.Spawned` postfix gated on `HasAuthority` (GroundItemVacuum pattern), then `Character.Inventory : InventoryComponent` → `.GetItemCollection() : ItemCollection` → `.GetItemInfos() : Il2Cpp List<ItemInfo>`. Item classification: `ItemInfo.storageClass` (asset name, e.g. seeds = 'SeedItem') and `ItemInfo.category : ItemCategoryInfo` with display name property **`.Name` (capital N)**.

**Dead-end:** non-generic `GameObject.GetComponents(System.Type)` throws MissingMethodException through the interop trampoline (same family as plural generics / FindObjectsByType) — AND when it threw it escaped an enclosing per-call try/catch and aborted the whole calling dump method, so don't assume a caught probe: just never call it. Confirmed in-game 2026-07-11.

---

## Cooking Station Pipeline

How a cook turns raw ingredients into meals, and why a cook can stand at the hut doing nothing. Structure is
confirmed from the binary; runtime facts marked **confirmed in-game (2026-06-23)** were verified live with the
`CookingStationFixMod` diagnostic — a read-only FSM/priority logger, parked at `CookingStationFixMod.dll.off`
(rename back to `.dll` to revive it if cooking ever needs re-investigating).

**Building + project state — `SSSGame.CookingStation : Workstation`**
```
CookingStation
  .cookingProjects (List<CookingProject>)      ← active cook JOBS; 0 = nothing is being cooked right now
  .CookingTargetManifest (ItemManifest)        ← what the hut is trying to cook (qty); 0 = no cook target
  .GlobalInventory / .GlobalInventoryManifest  ← raw ingredients stockpiled at the hut
  .cookingBehaviour/.stockpileBehaviour/.returnBehaviour/.complainBehaviour/.idleCrafterBehaviour/
   .firekeeperBehaviour/.overcookingBehaviour (vFSMBehaviour)   ← the per-quest FSMs
  .GetAllKnownCookingRecipes() / .IsWhitelisted(Villager) / Rpc_ChangeWhitelistedVillager(id, state)
CookingStation/CookingProject  ← one active cook job: .cooks (List), .mainQuest (CookingQuest), .taskData
CookingInteraction : StorageInteraction  ← the physical cooker; ._activeCooksList, ._cookingContainers,
   ._activeRecipeContainer, KnowsRecipe(...), IsCookCooking(agent), _GetOverallCookingProgress()
```

**Quests (all `WorkstationQuest : Quest`; the QuestRunner selects by priority — scale below)**
```
CookingStockpileQuest          ← gather RAW ingredients into the hut stockpile (the "Fetch Supplies" job)
  CookingStockpileQuestData : WorkstationQuestData
    .NeedsMoreSpecificSupplies / .NeedsMoreFillerSupplies / .NeedsMoreGatheredSupplies (bool)
    .GatheredStockpileSupplies / .NeededSpecificSupplies / .NeededFillerSupplies (ItemManifest)
    ._nextRecipeInfo (CrockpotRecipeInfo)   ← null when no recipe is queued
    ._RecheckSupplies(Item)                 ← its own re-eval, fired by station inventory add/remove events
CookingQuest : CookingPlaceQuest            ← the ACTUAL cook; exists ONLY while a CookingProject does
  CookingQuestData : CookingPlaceQuestData : WorkstationQuestData
    .RecipeInfo (CrockpotRecipeInfo), .CookingStation, .LocalPromiseManifest, ._noFillers
  CookingPlaceQuestData stall latches: ._noSupplies/._noFuel/._noTool/._noStorageSpace/._badWeather + .IsWorking
CookingComplainQuest, WorkstationIdleQuest/CookingStationIdleQuestData, OvercookingQuest, CookingReturnQuest
```

**The FSM chain a WORKING cook runs (states in `SSSGame.AI.FSM`, all take `IFSMBehaviourController`):**
```
FSM_FetchCookingStockpile (stockpile raw)  →  [a CookingProject/recipe exists]  →
FSM_FetchCookingSupplies (fetch recipe-specific)  →  FSM_SetCookingPlacePosition (place each ingredient in
its oven slot — the "which ingredient goes where" step)  →  FSM_CanStartCooking.Decide (gate)  →
cook (SandSailorStudio.Inventory.CookingProcess.Run, on a timer)  →  FSM_ReturnCookingResults (deliver food)
```
- Every cooking FSM state derives from `FSM_QuestDecision`/`FSM_QuestAction`, which expose
  `GetQuestData(fsmBehaviour)` / `GetQuest` / `GetQuestRunner` — the clean way to reach the cook's quest data
  from an FSM patch (then `QuestData.TryCast<CookingQuestData/CookingStockpileQuestData>()`; TryCast works,
  QuestData is `Il2CppSystem.Object`-derived). `IFSMBehaviourController` gives `.gameObject`,
  `.NameOfCurrentState`, `.AiTargeting`.
- **Quest priority scale (confirmed in-game):** `CookingStockpileQuest` = **15** (`c_WorkstationWork`);
  `CookingQuest` ≈ **15.5–15.6** (just above stockpile, so a real cook job out-ranks gathering); `GetPriority`
  returns **-1** when the quest isn't currently eligible; idle = 0.

### Cook stands at the hut / loops sit-down-stand-up and won't make food — CONFIRMED CAUSE (2026-06-23)
**Almost always: no recipe is designated for that station** (recipe selection is buried in a nested submenu,
easy to miss). Diagnostic signature: `cookingProjects=0`, `CookingTargetManifest=0`, `_nextRecipeInfo=null`,
and **`CookingQuest.GetPriority` never fires** (no cook quest exists). With no cook target, the only runnable
job is `CookingStockpileQuest` at priority 15 (beats idle 0), so the cook walks to the hut, enters
`FSM_FetchCookingStockpile` ("Fetch Supplies"), finds nothing needed (`NeedsMore*`=false), bails, and re-fires
every ~3.5s — the visible sit/stand loop. A **full stockpile with `target=0`** (e.g. 127 raw items, nothing to
cook) is the tell. **Fix = designate recipes in-game**: the project/target then exists, `CookingQuest` appears
(~15.6, out-ranking stockpile), and the cook runs the full chain — `FSM_ReturnCookingResults` fires once per
completed dish. This is a UI-discoverability issue, **not a code or mod bug.** (A Nexus user reported the same;
"cooks mixing up which ingredients go where" is the same class — nothing designated.)

### Dead ends (don't retry)
- **`WorkstationQuest.SetDirty()` / `_RecheckSupplies()` as the cook-loop fix** — the eating-stall fix (force
  re-eval of a *parked* quest) does NOT apply: a looping cook is already re-evaluating every ~3.5s, so SetDirty
  just makes it re-loop faster. The loop is "no target," not "parked, won't re-check."
- **Suspecting DynamicVillagerNeedsMod (Mod 5) of breaking cooking** — ruled out twice: the loop reproduces with
  Mod 5 `Enabled=false`, and with Mod 5 `Enabled=true` cooks complete dishes fine (9 dishes, 0 errors,
  2026-06-23). `WorkstationQuestData._OnVillagerBehaviorChanged(ScheduleType)` means Mod 5's schedule flips DO
  notify the cook's quest, but in practice this does not interrupt cooking. **Confirmed in-game (2026-07-09):**
  quest-layer eligibility ignores schedule/behavior mode — an off-duty cook in Leisure (probe: mode/behavior=Leisure,
  TaskRunner act/run empty) can still be pulled out to deliver finished dishes (the `FSM_ReturnCookingResults` /
  `CookingReturnQuest` step), then returns to leisure after. New-dish starts do NOT happen off-schedule; only
  retrieval of already-cooking results fires, protected by the delivered-dish safety logic (vanilla `OvercookingQuest`).

---

## Torch / Fire-Fuel System
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
    FireStructure too, so filtering by **structure name** keeps non-torch fires unaffected
LightOutlet (StructureTaskDispatcher) .fireStructure / .fireInteraction / .Initialize(Structure)
  - the light-duty dispatcher; attached to EVERY fire that exists to give off light — free-standing
    torches AND fires built into buildings (tavern campfire, braziers). Cooking stations/forges/kilns
    use CookingOutlet/WarmthOutlet and have NO LightOutlet, so patching LightOutlet.Initialize and
    grabbing __instance.fireStructure is a name-free way to catch all *lighting* fires while leaving
    crafting fires alone (TorchFuelMod's KeepAllLightSources path).
```
**Confirmed:** `Structure.DefaultName` / `Structure.StructureName` give the structure's display name (e.g. "Flimsy Torch") for substring matching, same pattern as `ItemInfo.Name`.

**Rename-proof name matching (confirmed in-game 2026-07-10):** station/structure display names are player-editable at runtime via `Structure.Rpc_ChangeName(String)` (networked `__NetworkedStructureName`, `OnNameChanged`). **`Structure.DefaultName` keeps returning the pristine type name after a rename** — any name-keyed matching/config should check BOTH `GetName()` (display name) and `DefaultName` (type name). When both names differ, display them as `Display (Default)` for clarity (DynamicVillagerNeedsMod v1.9.4 pattern).

**Built-in / composite-building fires (e.g. tavern campfire):** these ARE `FireStructure`s and DO fire `Initialize`, but their owning `Structure` is the *building* ("Tavern"), so a name filter of `"Torch"` misses them. Two ways to catch them: add the building/fire name to the name list, or use the `LightOutlet.Initialize` patch above (catches any light-emitting fire by component, no name needed).

**⚠️ DEAD-END — cave wall sconces are NOT FireStructures.** Sticking a torch into a cave wall sconce goes through `SSSGame.CaveTorchOutlet` (a plain `MonoBehaviour`, *not* a NetworkBehaviour and *not* a FireStructure). It has **no fuel volume and no `Rpc_AddFuel`** — `IsLit()`/`IsAvailable()` are get-only, the actual light is a torch **equipment item** (`CaveTorchOutlet.torchReplacements[] : {GameObject replacementPrefab, EquipmentItemInfo torchInfo}`) held in an `EquipmentDisplaySlot` via `EquipmentManager`, that burns down by **item durability** and is *replaced* by a villager `LightkeepingQuest` (see `FSM_BuilderLightkeep`, `CaveLightingFilter`). Keeping sconces perpetually lit therefore is a *different* problem from fuel top-off — it needs blocking the equipped torch's durability decay or auto-re-equipping, not `Rpc_AddFuel`. The fuel-volume TorchFuelMod cannot touch them. (confirmed via interop dump 2026-06-25; in-game behavior pending)

**⚠️ DEAD-END — coal-burning buildings (Kiln, etc.) do NOT use the FireStructure fuel model.** TorchFuelMod's name filter cannot reach them, and there is **no structure named "Furnace"/"Smelter"** in the binary at all (searched the whole interop surface 2026-06-25 — only `KilnInteraction`, `ForgeInteraction`, `CharcoalStation` exist). The smelting building is the **Kiln** (`SSSGame.KilnInteraction : CookingInteraction : StorageInteraction : Interaction : MonoBehaviour`) — it is **not a `FireStructure` and not even a `NetworkBehaviour`**, so it never comes through the `FireStructure.Initialize` patch and has **no `CurrentFuelVolume`/`Rpc_AddFuel`**. Its fuel is a fundamentally different mechanic:
- `_fuelVAttr` (`VariableAttribute`, writable — same type as `_happinessVAttr`/survival attrs, so `SetValue()` is available) = the current fuel/heat reservoir, drained by `_fuelBurnRateAttr` (`Attribute`) × `_burnRateMultiplier` (`CustomOperationAttributeModifier`).
- Fuel arrives as **coal items** dropped into an `ItemContainer`, gated by `static Boolean _IsFuelPredicate(Item)`; `GetFuelCount()` = count of fuel items present; energy per item from `_fuelCaloricMass`/`_fuelCaloricPower`; `fuelRatioToPower` (AnimationCurve) maps fuel ratio → smelting power. (`_oreCaloricMass`/`GetOreCount`/`GetBakedOreCount` are the ore side.)

So "keep coal buildings fueled" is a **separate feature** from TorchFuelMod, with two candidate levers (both untested in-game / co-op): **(a)** pin `_fuelVAttr` to `max` each tick via `SetValue()` (the torch-equivalent "never runs dry"), or **(b)** keep the coal `ItemContainer` topped with coal items. **Co-op replication is the open risk** — there is **no `Rpc_AddFuel`-style network-safe call** on the kiln and it's a plain MonoBehaviour, so writes are guaranteed to work solo/host but co-op-client replication is unverified (same caveat class as direct inventory writes).

**🛑 DEAD-END (CONFIRMED in-game 2026-06-25) — do NOT Harmony-patch `Initialize(Structure)` on these smithing/interaction types to capture instances.** TorchFuelMod v1.2.0–1.2.1 tried postfixing `Initialize(Structure)` on `KilnInteraction`, `BellowsInteraction`, `ForgeInteraction`, `CharcoalStation`, and `Bloomstation` (read-only diagnostic). Result: **v1.2.0 hard-froze the game during chainloader**; **v1.2.1 (added a name guard + dropped `GetFuelCount()`) stopped the freeze but the game still would not finish opening**, logging `[Il2CppInterop] During invoking native->managed trampoline / System.InvalidOperationException: Handle is not initialized … GetMonoObjectFromIl2CppPointer … at (il2cpp -> managed) Initialize(IntPtr, IntPtr, Il2CppMethodInfo*)`. **Why:** these `Initialize` methods fire during early-boot prefab/pool init on instances whose **managed GC handle isn't set up yet**; the il2cpp→managed trampoline fails to marshal `__instance` **in the glue, before your patch body runs** — so a name guard / `try`-catch can't save it (same class as the by-ref-param trampoline hazard), and because the patched `Initialize` never completes, the game object stays half-built and boot stalls. Arg-count attribution: the failing trampoline is a **1-arg** `Initialize` = `Initialize(Structure)`; TreeRespawn's `BiomeItemInstance.Initialize` takes **5 args** so it's not the culprit, and the shipped `FireStructure.Initialize`/`LightOutlet.Initialize` (NetworkBehaviour / dispatcher, also 1-arg) have always been fine — so it's specifically these **MonoBehaviour `Interaction`-based** types that break. **Capture them another way:** for the bloomery, prefer the NetworkBehaviour `Bloomstation.Spawned()` (Fusion lifecycle, the safe pattern other mods use) and reach its `.kiln`/`.bellows` from there; never patch the `Interaction.Initialize`. **For DISCOVERY (which building is which), no new patch is needed at all** — the existing `LogAllFireStructures` flag (which patches the safe `FireStructure.Initialize`) already logs the forge fire, the bloomery (via its bellows `FireStructure`), and the charcoal pyre with each owner Structure's display name.

**Related coal buildings (for reference):** `ForgeInteraction` *does* wrap a `FireStructure fireStructure` (secondary heat — the FireStructure path could see it by name), and `CharcoalStation : Workstation` owns a `PyreStructure pyre` where `PyreStructure : FireStructure` — but the pyre's job is converting wood→charcoal (`woodInputContainer`→`coalOutputContainer`, `_InputFuelToCoalConversion`, `fuelToCoalConversionBaseline`), i.e. it *produces* coal rather than consuming it, so topping its fuel volume isn't "keep it supplied with coal." (confirmed via interop dump 2026-06-25; in-game behavior pending)

---

## Dens & Population Spawners (DenRespawnMod evidence, confirmed in-game 2026-07-09)

**Den defeat is recorded at the SPAWNER level, not on `den.isActive` (confirmed in-game 2026-07-08,
controlled experiment):** clearing a Wulfar Den's nodes + boss made vanilla set `ignoreRespawning=true`
on its node spawner (`SSSGame.Den.affectedSpawners : PopulationSpawner[]`); a depleted-but-never-attacked
cemetery likewise showed a node spawner with `ignoreRespawning=true` + `HasNoAliveCreatures()=true` while
its `den.isActive` stayed True. **`den.isActive` semantics are murky — don't key logic on it**: two far
wolf dens sat `isActive=False` all (day-time) session while a near one read True; possibly day/night
related (`Den.isDay`, `Den_OnWeatherChanged` exist) but ⚠️ unconfirmed.

**Working revive recipe (the full set is confirmed SUFFICIENT in-game 2026-07-08; per-lever necessity
NOT isolated):**
1. `Den.Revive()` — observed to bump `ReviveCooldown : Int32` 0→1 and NOT flip `isActive` (12
   observations); **called alone it produced no visible respawn** (v1.0.0 falsification).
2. `Den.IgnorePopulationSpawnersRespawning(false)` — after the full recipe, node spawners read
   `ignoreRespawning=False`.
3. `Den.SetDenActive(false, true)` when `!isActive` (signature = `(bool fromDeserialize, bool forced)`).
4. Per `affectedSpawners[i]` with `HasNoAliveCreatures()`: `SetActiveSpawner(true, true)` →
   `RespawnAllPopulations(false)` — repopulation is **INSTANT** (`noAlive=False` immediately) and fires
   the game's own "The monsters from <Den> are back!" toast, i.e. this drives the native revive pipeline.
   Refreshes hold within a session (refreshed dens read healthy on later checks); ⚠️ save/reload
   persistence untested.

**Never touch `alphaSpawner : PopulationSpawner`** — it's the boss spawner; empty/inactive is its normal
pre-boss state (boss spawns after the nodes are cleared — see the den table in `SEED_SCOUT_HANDOFF.md`).

**Structure blocking (⚠️ hypothesis, effect untested):** vanilla has a blocked-by-structures mechanism
(`Den.IsBlockedByStructures()`; `PopulationSpawner.RespawningBlockedByStructures`, recomputed by
`_UpdateBlockedByStructures()`). The ideas-plan hypothesis that this explains "depleted dens near a base"
is **unconfirmed** — in the only test world so far every read was False and the depleted dens were
`ignoreRespawning` cases instead. Harmony postfixes on both members DO fire (fire-verified in-game — not
AOT-inlined). (DenRespawnMod, [`docs/mods/den-respawn.md`](mods/den-respawn.md))

**Den capture pattern (IL2CPP interop):** `Den`'s compile-time base chain passes through a `unity-libs`
stub, so `.Pointer` isn't visible on the declared type — use the boxing pattern
`(object)den is Il2CppObjectBase b` → `b.Pointer` (another instance of the existing universal gotcha).
Capture via `Den.Start()` + `Den.Spawned()` postfixes into a static list, dedupe by pointer; `Den.Start`
was already runtime-proven by SeedScout.

**Per-world state and `ActiveSessionID` timing:** dens are captured during world load BEFORE
`StorageManager.ActiveSessionID` becomes readable. So clear per-world den state when the id goes
non-empty→empty (quit to menu) or changes between two non-empty values — but **KEEP captures when the id
first becomes readable** (null→non-empty), else the load-time captures are wiped. Same first-resolve
special case as TreeRespawn's `OnWorldChanged`. (DenRespawnMod v1.0.1 fix.)

### Map Pins & Revision (DenRespawnMod v1.1.x confirmed in-game 2026-07-09)

**DEAD-END: `MapMenu._OnMarkersLeftClick(WorldObjectiveMarker marker)` is AOT-inlined** — Harmony patches never fire on this method. `MapMenu.OnPointerClick(PointerEventData)` is the patchable click entry point, but ONLY receives clicks on empty map space (pin widgets swallow clicks on themselves; confirmed in-game 2026-07-09).

**Map pins: UI components and state**
- UI widget for a pin = `CompassObjectiveMarker` (MonoBehaviour)
- Pins SELECT ON HOVER (`OnSelect` fires on mouse-over, no click required; confirmed in-game 2026-07-09)
- Pins carry NO `Button` component (confirmed 2026-07-09)
- Vanilla pin left-click toggles the compass-eyeball via the pin's own event handler (not routed through `MapMenu`)
- Reliable pin-click recipe: OnSelect/OnDeselect postfixes to TRACK the hovered pin (clear tracked pin only when OnDeselect fires on a matching widget pointer) + per-frame `Input.GetMouseButtonDown(0)` poll in a tracker's Update method (DenRespawnMod v1.1.4 pattern, confirmed working 2026-07-09)

**Widget-to-marker mapping (UI state sync)**
- `MapMenu._objectiveContainer._icons` is `List<ObjectiveIcon>` where each icon pairs a UI widget with a game-world marker
- `ObjectiveIcon.widget` = the `CompassObjectiveMarker` (UI)
- `ObjectiveIcon._wMarker` = the `WorldObjectiveMarker` (game-state marker link)
- Match via Il2CppObjectBase.Pointer equality (boxing escape hatch: `(object)x is Il2CppObjectBase b` → `b.Pointer`)
- Capture the `MapMenu` instance via an `OnActivate` postfix (fires on every map open); never cache it across world sessions (map-instance is per-world)

**Screen-to-world coordinate transform on the map**
- `RectTransformUtility.ScreenPointToLocalPointInRectangle(MapMenu.Content, screenPos, eventData.pressEventCamera, out local)` → converts screen click to local map-rect coords
- `MapMenu.GetWorldPosition(local)` → converts map-rect coords to world position
- Confirmed accurate in-game 2026-07-09 (matched den's position to within ~19 m of click point)

**Pin state refresh (map-pin recolor on revive)**
- `MarkerObject.RefreshSpawnersHostileStatus(bool noNotification)` — re-evaluates den-spawner hostility and recolors the pin (confirmed in-game 2026-07-09; pins visibly red→yellow on revive)
- Reach the `MarkerObject` via `area.areaInstanceMarkerHandler.GetMarkerObject()` — returns it DECLARED as `SandSailorStudio.Inventory.ItemComponent`; managed casts lie (universal gotcha) — use native-class-name check + rewrap `new MarkerObject(IntPtr)`
- GOTCHA: some markers (long-defeated enemy dens) have `_biomePopulation == null` and calling RefreshSpawnersHostileStatus on them throws a managed NRE inside the native call (caught by try/catch but noisy in logs) — null-check before calling
- With `noNotification=false`, the game fires its native "monsters are back" notification (toast wording ⚠️ unconfirmed)

**Spatial data**
- `AreaInstance.position` is `Vector2Int` (world X,Z)
- `AreaInstance` lives in `SandSailorStudio.WorldGen` namespace
- `AreaInstanceMarkerHandler`/`IAreaInstanceMarkerHandler` are in GLOBAL namespace

**Den defeat semantics (confirmed in-game 2026-07-09)**
- `spawner.ignoreRespawning=true` is the DURABLE vanilla defeat flag (user-confirmed on dens cleared long ago that stayed flagged; natural respawn ~1 in-game year)
- `den.isActive=false` does NOT mean defeated (nocturnal wolf dens inactive by design)
- `PopulationSpawner.HasNoAliveCreatures()` is TRANSIENT (creatures can be roaming/streaming-culled) — false-positive as "needs revive" signal alone
- Reliable defeat selection: `needsWork = (anyIgnore || outOfCombatBeingDecided)` where `anyIgnore = any(affectedSpawner.ignoreRespawning)` (confirmed in-game 2026-07-09; narrower filter than v1.1.2's `anyEmpty || anyIgnore`)

**Vanilla natural den respawn = LOAD-TIME check (working model, confirmed-by-correlation 2026-07-09):** At world load, dens whose TRUE defeat age exceeds a threshold (~1 in-game year per community lore) receive a foreign `Den.Revive()` attempt; mid-session elapsed time triggers NOTHING (74 fast-forwarded in-game days produced zero attempts, confirmed in-game 2026-07-09 via TimeWarpMod). Evidence: only the same 3 long-ago-cleared Baby Crawler dens ever received foreign Revive() calls (at every load); a freshly-defeated Skeleton Den with reviveCooldown=0 was NOT attempted (kills a cooldown-only selection theory); mod-revived dens carry ReviveCooldown=1 and re-defeating a den does NOT reset it (observed on the Wulfar den across defeat cycles); Revive() bumps ReviveCooldown 0→1 (previously known). ⚠️ Caveat: threshold-crossing was INFERRED from defeat-age correlation, never watched live; ReviveCooldown's role in driver selection cannot be isolated from defeat-freshness with current data.

**Map pin labels ≠ den type names**
- "Small Cemetery"/"Large Cemetery" pins = Skeleton Den / Skeleton Den Cluster dens internally

---

## Villager Summoning (Eye of Odin) — SummonTimerMod evidence

**Structure & state:** `SSSGame.VillagerOutlet` is a `NetworkBehaviour` (one instance per Eye of Odin structure, captures via `Spawned()` postfix). Handles the entire summon flow: `_CheckSummonAvailability(ItemContainer)` gates the 5-jotun-blood requirement; `OnStorageMenuConfirmationPressed(ItemContainer)` initiates the wait; `__NetworkedVillagerTimerEnd : float` + `_SpawnPending : NetworkBool` track the timer state; `SpawnVillager()` fires when timer expires. Additional features: `GenerateNewVillagerChoices()` / `Rpc_SetUpcomingVillagerData(DescriptionData, SByte)` handle the villager-choice UI; instance fields for `_pm : PopulationManager`, `_wm : WeatherManager`, militia management (`GetCurrentMilitiaCount`/`GetMaxMilitiaCount`/`CanAssignNewMilitia`), and demo-mode fields (`trialPopulation`, `trialGametimeToSpawnVillager` — ignore).

**Delay data structure:** `_spawnTimeline : VillagerSpawnCooldownsConfig` (shared `ScriptableObject`, one per Eye of Odin blueprint) contains `cooldowns : SandSailorStudio.Types.IntThresholdList<float>` — a struct (`ValueType`) whose `thresholds` property is `Il2CppSystem.Collections.Generic.List<IntThreshold<float>>`. Each threshold entry is `IntThreshold<float>{threshold : int, value : float}`. **IL2CPP interop gotcha:** reading `cooldowns` returns a struct COPY, but its inner `thresholds` List is a shared native reference — to mutate entries, read the list in place with copy-back round-trip (`var e = list[i]; e.value = x; list[i] = e;`). Accessed via `GetValue(int)` lookup in game logic. Per-instance timer lives in `gametimeToSpawnVillager : float`.

**⚠️ Cooldown-table semantics UNKNOWN (confirmed reads in-game 2026-07-09):** All 9 vanilla threshold entries (villager counts: 3, 8, 12, 20, 40, 70, 110, 150, 200) hold the SAME negative value `-2.5980988`, and `gametimeToSpawnVillager = 10`. The per-count escalation is NOT a direct per-threshold duration lookup; the value may be a rate/curve input, and actual growth math may involve `gametimeCustomizationData : GametimeCustomizationData` (world-customization slots; role unconfirmed). **Consequence:** removing the wait entirely (multiplier 0) is confirmed SUFFICIENT; fractional multipliers (0 < m < 1) are UNTESTED and may not scale linearly. Harmless degenerate observed: scaling to 0 produces `__NetworkedVillagerTimerEnd = -2147483.8` (underflow) — game handles gracefully (no crash, timer expires immediately). (SummonTimerMod v0.1.0, confirmed in-game 2026-07-09.)

---

## World Generation (Headless / Main Menu)
- `WorldGenerator` is a `MonoBehaviour` but can be attached to a dummy GameObject in the Main Menu and successfully run `Setup(size...)`. It does not require a live 3D scene to generate the mathematical map!
- `GenerateWorldMapAsync(filterTag)` executes the procedural generation logic, producing a `WorldDataMap` (with `_areaInstances` containing caves/lakes/dens).
- **CRITICAL BLOCKER:** `WorldGenerator` relies on `RandomGeneratorManager`. Instantiating `RandomGeneratorManager` directly via `AddComponent` throws an NRE in `OnEnable()` and `_PadSeedPhrase()` because it requires serialized IL2CPP struct arrays (like `generatorNames`, `seedKeyLength`) that are normally provided by its prefab. You must find an existing instance or initialize these arrays manually before it can process a seed string.

### Identifying the loaded world / save (for per-world mod state)
- **Use `SandSailorStudio.Storage.StorageManager.ActiveSessionID` (String).** `StorageManager` is a
  `MonoBehaviour` — get it via `FindAnyObjectByType<StorageManager>()` — and `ActiveSessionID` is a unique
  per-save id that **is populated for LOADED saves** (also `_activeSessionName` for a friendly name, plus
  `LoadActiveSession`, `SaveCurrentSession`, `ScanSaveGames`, `GetStoragePath`, `CreateNewSessionID`). This is
  the stable key for any mod that must keep per-world state (TreeRespawnMod v1.2.1 keys its respawn save on it).
  Confirmed in-game 2026-06-28: singleplayer and co-op each produced their own distinct per-world file.
  **CRITICAL:** Quit-to-menu → reload of the SAME world does NOT change `ActiveSessionID` — it remains the
  same. The session ID becomes empty (falsy) ONLY when the main menu is active and no world is loaded. Any
  per-world state (cached managers, handlers, instance registries) must be flushed when `ActiveSessionID`
  transitions to empty, else reads through stale interop wrappers will AV in native code beneath the managed
  frame — resulting in a CLR fatal error (`coreclr.dll+0x1d1fdd` WER signature, confirmed in-game 2026-07-06
  TreeRespawnMod reload crashes). See also the IL2CPP gotcha: "Never cache interop wrappers of per-world
  native objects across world sessions" (CLAUDE.md) and the Native Crash Diagnosis section above.
- **❌ Dead-end — the world SEED is unusable as a world key (confirmed in-game 2026-06-28).** On a *loaded*
  save the seed is empty/absent: `RandomGeneratorManager.SetSeedPhrase` only fires during fresh world
  generation, and `RandomGeneratorManager.seedPhrase` / `SSSGame.Network.NetworkSession.Parameters.seed` read
  back null/empty once a world is loaded (SeedScout's dump shows `Seed = <rng-null>` for the same reason).
  TreeRespawnMod v1.2.0 tried this and silently did nothing — don't retry it.

---

## Villager Schedule / Needs / Happiness System

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
  IsSleeping  (bool) — true while the game has the villager in bed (incl. the forced night sleep)
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

**Schedule clock + packed-field ground truth (confirmed in-game 2026-07-09, DynamicVillagerNeeds v1.3.1–v1.3.3 diagnostic runs, TimeWarp-accelerated 10x/36x):**
- **`WeatherSystem.Instance.TimeOfDay` is the wall clock** — linear `float` 0..24, wraps cleanly at midnight, tracks TimeWarp's independent readout. Current schedule hour = `Mathf.FloorToInt(TimeOfDay)`, and **schedule slot k == clock hour k, zero offset** (a painted Sleep block at slots 10–13 executed as behavior=Sleep 10:00–13:59, flipping to Work at 14:00, on two consecutive observed days).
- **DEAD-END: `WeatherSystem.DayNightValue` is NOT a clock** — it saturates at exactly 0.0/1.0 for long stretches (a day/night blend factor) with transitions at odd times; `floor(DayNightValue*24)` pins at 0/23. Never use it for schedule-hour math. `WeatherSystem.IsNight` flips ~20:00 (False→True) and ~07:00 (True→False).
- **`Villager.__NetworkedSchedule` is a transient change-transport field, NOT an at-rest schedule mirror**: it reads 0x0 at world load even when villagers' `_schedule` arrays differ; the game mass-resets it to 0x0 mid-session (seen for all 45 tracked villagers at once); and **player UI schedule edits never pulse it at all** — an apply in the schedule panel changes `villager._schedule` (per-hour int array) without touching the packed field. **Detect player edits by diffing `_schedule` array contents on a slow cadence (5 s is ample; catches edits within one tick)** — packed-value comparison misses real edits and false-positives on the 0x0 resets. (Extends the older "sched=match/REVERTED readback quirk" note below.)
- ⚠️ Tentative (2026-07-09, one villager, two accelerated nights): a UI-painted **Work schedule kept a villager awake working straight through the night** (20:00–02:00, behavior=Work, sleeping=False) — the long-documented "nightfall forces sleep regardless of schedule" did not occur. Possibly rest-depletion-driven (this villager had a painted midday sleep block keeping rest topped up) or specific to Rpc-written all-W schedules. Re-verify during idea-11 Phase 1 before relying on it.
- **Rest drains ~1 h per in-game hour while awake, regardless of activity** — leisure drains it the
  same as work (confirmed from transition telemetry 2026-07-09: 23→20.9 over ~2 h of leisure;
  11.4→0.4 over an 11 h shift). Sleep placement inside an off-window therefore matters: rest peaks at
  sleep end and decays linearly after.

**Quit-to-menu does NOT fire plugin OnDestroy (confirmed in-game 2026-07-10):** shutdown-restore patterns (e.g., `RestoreAll`) never run before the save; any live schedule write bakes into the save. Mitigation proven: Harmony prefix/postfix mask on `Villager.Serialize(DataObject)` (patchable, fires; `DataObject` = `SandSailorStudio.Storage.DataObject`; saves serialize the schedule from `_schedule`). `Villager.GetSurvival()` bridges Villager→VillagerSurvival.

**Player UI schedule edits land in `_schedule` (per-hour int array), discriminable from a mod's own writes:** array-diff detectable; discriminate player edits from a mod's own writes by comparing the packed live array against the mod's recorded last write — an "anything not bit-for-bit mine is player intent" rule (DynamicVillagerNeedsMod v1.7.1/v1.7.2 pattern).

**Schedule UI surface (confirmed in-game 2026-07-10):** `SSSGame.UI.VillagerSchedulePanel` (MonoBehaviour; `_v` property = bound villager; populate/read = `Set(Villager)`/`Refresh()`/`OnEnable()`; `HasUnappliedChanges()` diffs panel vs live; apply = `UpdateSchedule()`; renders via `_schedulePixels`/`_scheduleTexture`); parent `SSSGame.UI.ScheduleEditorMenu` (ContextMenu, `SetupVillagers()` builds per-villager panels). Set/Refresh/OnEnable/HasUnappliedChanges are Harmony-patchable; Set and OnEnable fire-verified in-game 2026-07-10.

**Day length** — `WeatherSystem.Instance.dayLength` (Single, seconds/day) → in-game hour = `dayLength / 24`; also `IsNight` (bool), `DayNightValue` (Single, ~1=midday ~0=deep night).

**Eating / drinking — the survival-quest FSM (separate from the schedule pipeline)**
A villager's eat/drink behavior is NOT driven by the schedule a mod rewrites — it's a survival-quest
FSM that runs on top of whatever schedule mode is active. This is why eating happens during Work mode.
```
SSSGame.VillagerSurvival
  GetReplenishFoodQuest() / GetReplenishWaterQuest()  → SSSGame.AI.SurvivalObjectiveQuest
  _replenishFoodQuest / _replenishWaterQuest / _replenishHealthQuest  (same objects, as fields)
  _foodObjectiveLow / _foodObjectiveSafe  (VariableAttributeValueObjective) — the GAME's own eat thresholds
  foodItems / emergencyFoodItems / drinkItems / emergencyDrinkItems  (ItemTableConfig) — TWO-TIER food
SSSGame.AI.SurvivalObjectiveQuest
  SetDirty()                       ← re-evaluate primitive (the same call the AI's FSM_QuestSetDirty state uses)
  HasItemsToConsume(SatisfyObjectiveQuestData) → bool   ← the "is there food" check
  consumableItems / emergencyConsumableItems  (ItemTableConfig)
  priorityHigh / priorityLow ; highPriorityObjective / lowPriorityObjective
  autoRetryTimeHighPriority / autoRetryTimeLowPriority  (float) — vanilla's OWN retry timers (too slow/unreliable)
  _TryNotifySurvivalObjectivesChanged() / RefreshComplaints()   ← alternatives to SetDirty if it's ever insufficient
SSSGame.AI.FSM.FSM_SurvivalConsume       ← the FSM state that walks to the store and consumes
  ConsumeStatus { IDLE, WAIT, COMPLETE, ERROR_NO_ITEM }   ← ERROR_NO_ITEM = arrived, store empty
```
- **Two food tiers:** normal (`foodItems` — cooked meals) and emergency (`emergencyFoodItems` — raw/poisonous). A starving villager (high-priority objective) eats the emergency tier; a mildly-hungry one only normal food. Remove all cooked food and villagers fall *down* the tiers — raw-but-safe (eggs) when low, poison only when truly starving. **This is vanilla, not a mod effect** (confirmed by testing with hunger ×50 → constant emergency-tier eating).
- **The "stuck at empty store" bug:** a villager commits to a food target and stops re-evaluating across tiers; if the cooked store is empty when they arrive (`ERROR_NO_ITEM`) they park there and ignore *available raw food*, until something forces a re-evaluation (hand-feeding, or `SetDirty()`). Vanilla's `autoRetryTime*` did not recover them in practice. DynamicVillagerNeedsMod v1.1.0 fixes this by calling `SetDirty()` — see that mod's doc.

### Game-mechanic facts worth knowing before re-modeling villager behavior
- **The night-sleep race:** the game forces villagers to bed at **nightfall** regardless of their schedule, catching them at whatever rest they have then. Any mod-driven sleep control that only runs in its *own* Sleep mode will be pre-empted unless it **adopts** the game-forced sleep (detect `IsSleeping` + low rest, take it over). See DynamicVillagerNeedsMod for the adopt fix.
- **Sleep duration is a rate problem, not a threshold problem:** total sleep *fraction* = drain-rate / (drain + gain). Lowering the wake threshold only shortens each cycle, not the fraction — to make villagers sleep *less*, raise the rest-gain rate.
- **Warmth is best left to the game.** The game's own `warmUpBehaviour`/`_warmUpQuest` warms villagers *fully* during work. Forcing a leisure trip for warmth only warms partway → thrash between fire and job. Tune the game's warm-up *speed* (multiply warmth only while it's already rising) rather than taking over the *when*.

### Dead ends (don't retry)
- `Villager.overrideSchedule` + `scheduleOverride` + `CurrentBehaviorType` — drives the FSM behavior **label** (and suppresses sleep) but **bypasses the task-dispatch pipeline**: villagers stand idle (`TaskRunner` active-task null) even at midday with a valid workstation. Use `Rpc_ChangeSchedule` (the real schedule) — the only thing that dispatches work.
- `VillagerHappiness.LeisureRate`/`WorkRate`/`SleepRate` — **get-only**, can't scale the rate. Write `_happinessVAttr` directly.
- Warmth/cold as a leisure trigger (incl. the `IsFreezing` flag) — villagers thrash between a fire and their job (each warmth-leisure trip only warms to the exit threshold, then they immediately get cold again). Hand cold back to the game.
- Lowering `WakeWhenRestAboveHours` to "sleep less" — only shortens each sleep's cycle, not the total sleep *fraction*; raise the rest-gain rate instead (see facts above).
- The `sched=match/REVERTED` readback diagnostic shows `REVERTED` for all-Sleep/all-Leisure but `match` for all-Work — a packing/representation quirk of comparing `__NetworkedSchedule` to the packed value, **not** a functional revert: behavior always followed the schedule. Don't chase it.

---

## Villager Combat / Fight-vs-Flee System

How a villager decides to **run from** vs **fight** an attacker. Used by Mod 7 (VillagerFightBackMod).
Structure below is **confirmed from the binary**; the runtime *behavior* notes flagged ⚠️ are
**pending in-game verification** (FSM state method bodies aren't dumpable in IL2CPP — only signatures).

**Competing combat Quests (all derive from `SSSGame.AI.CombatQuest`, ranked by `TriggerPriority`)**
```
SSSGame.AI.CombatQuest                 ← base; .fsmBehaviour, .GetPriority(QuestData), .SetDirty(), .TriggerPriority
  CombatQuest/CombatQuestData          ← .ShouldFight (GET-only, computed), ._engagedEnemy (IAttackTarget, GET+SET),
                                          ._survival (VillagerSurvival), ._villager, ._targeting (ITargetAI),
                                          ._combatTimeRemaining, ._lowHealth
  SSSGame.AI.FleeCombatQuest           ← what a NON-warrior villager gets: "get spooked & run"
    FleeCombatQuest/FleeCombatQuestData
        .ShouldFight (GET-only)        ← its OWN computed override (home-safety / low-HP / defense-score complaints)
        ._GetSpooked(IAttackTarget)    ← entry point when an attacker spooks the villager (safe to patch; 1 ref param)
        ._homeSafetyAttribute, ._lowHpComplaint, ._low{Defense}AtHome/AtWork complaints, ._fleeingComplaint
  SSSGame.AI.WarriorCombatQuest        ← what an equipped warrior gets: stand & fight
  SSSGame.AI.PartyCombatQuest / HunterCombatQuest / PetCombatQuest   ← other fight variants
SSSGame.AI.DefensiveCombatTask         ← party/villager OnTakeDamage→combat-timer task
```

**Warrior status (the game's own "this villager fights" flag)**
```
Villager.IsWarrior  (bool)             ← set by VillagerSurvival._CheckWarriorStatus() from equipped warrior gear
VillagerSurvival._warriorCombatQuest (WarriorCombatQuest), ._warriorQuestAdded, ._warriorBuffAdded
VillagerDataSheet.warriorCombatTime / warriorDetectionRange / warriorReactRange / warriorStatusEffect
WeaponizedItemInfo.warriorGear / WearableItemInfo.warriorGear  (bool)  ← gear that confers warrior status
Villager.IsOnDefendDuty / ._isOnDefendDuty / .OnDefendDutyChanged   ← separate "assigned to a DefensiveStation" flag
VillagerSocial.SendSpookedNotification(IAttackTarget) / .Rpc_SendSpookedNotification(NetworkId)
```

**Identifying an attacker — `SSSGame.Combat.IAttackTarget` (every attackable thing implements it)**
```
.GetTargetName() → String              ← display name (e.g. match "Wisp"); names live in runtime SOs, not the binary
.Faction → SSSGame.Combat.Faction      ← enum { Critters, Danger, Ignore, Neutral, Structures, Undead, Vikings }
.IsAggressive() / .IsAlive() / .IsViable() / .IsPlayer() / .ThreatScore / .transform
SSSGame.Creature : IAttackTarget       ← monsters; .Faction, .GetName(), .dataSheet (CreatureDataSheet.faction/localizedName)
SSSGame.Combat.Threat                  ← per-target threat record: .ThreatScore, .Damage, .target (IAttackTarget), .Seen
SSSGame.Combat.CombatManager (MonoBehaviour singleton-ish) ← RegisterCombatant / faction context unwinding
```
- **Faction is coarser than name:** wisps and wolves may share `Danger`; draugar are `Undead`. For a precise
  "fight wisps, flee draugar/wolves" split, match by **name substring**, not faction.
- A `Creature`'s name is a runtime ScriptableObject value (same as item names) — **not** in the binary;
  discover it live by logging `IAttackTarget.GetTargetName()` + `.Faction` from a patch.

**The fight-vs-flee override (VillagerFightBackMod's approach):** Postfix
`FleeCombatQuest/FleeCombatQuestData.get_ShouldFight`; when `__result==false` (would flee) and
`__instance._engagedEnemy` matches a name/faction whitelist, set `__result=true`. Read + return-a-bool only
(no networked write → co-op-safe); gate the flip on `__instance._survival._hasAuthority`.

### Confirmed in-game (session 2, 2026-06-22) — supersedes the old "pending" notes
Verified live while building Mod 7 (see [`VILLAGER_FIGHTBACK_HANDOFF.md`](../VILLAGER_FIGHTBACK_HANDOFF.md)
for the full lever-by-lever log). **These are facts now, with dead-ends called out so they aren't re-tried:**
- **Wisp:** `GetTargetName()` == `"Wisp"`, `Faction` == `Undead`. Name-whitelist only (draugar are also `Undead`).
- **`ShouldFight` is a DEAD END for fight-vs-flee.** In a real Wisp encounter it already returns `True`, and
  the villager flees anyway. The flee is **not** gated on `ShouldFight`. Forcing it does nothing.
- **`_engagedEnemy` is usually `null`** when `ShouldFight`/`GetPriority` are read — capture the attacker from
  `_GetSpooked(IAttackTarget)` and remember it per quest-data instance (key by `Pointer`) as a fallback. (Done.)
- **Flee is FSM-driven, in layers:** `_GetSpooked` = *notification only* (suppressing it removes the spook
  icon/fanfare but NOT the flee); `FSM_SetVillagerIsFleeing.OnStateEnter` sets `Villager.IsFleeing`;
  `FSM_RunFromTarget` does the run movement. Blocking each removes its piece but not the overall retreat.
- **`FSM_CheckVillagerWarrior` is NOT the melee-vs-run branch** — forcing its `Decide` true has no effect.
- **A regular (non-warrior) villager CAN reach `FSM_MeleeCombat`** and kill a Wisp — confirmed when **idle**.
- **The real blocker is QUEST ARBITRATION:** a villager **with an active work quest** (e.g. `TerraformingQuest`)
  gets pulled back to her job marker; combat only fully wins once the job resets. `QuestRunner._activeQuestPriority`
  oscillates between the work quest and `FleeCombatQuest`. **Combat time is NOT the issue** (topping
  `_combatTimeRemaining` works but doesn't fix it).
- **Priority-number boosts via getter postfixes DON'T fix it — both tried and failed in-game:**
  - Postfix `CombatQuest.GetPriority` → 41 **fired** yet the runner still re-selected `TerraformingQuest`
    (`activePrio=15`) — impossible for a `GetPriority`-ranked selector, so `GetPriority` (Single) is only the
    *reported* priority of the already-chosen quest, not the selector.
  - Postfix `FleeCombatQuest.get_TriggerPriority` → 40 (v1.0.11): the boost log **never fired** → the getter
    is almost certainly **cached** (a stored field the runner reads, not the property; `CombatQuest.SetDirty()`
    exists) — so postfixing the property is inert. (`FleeCombatQuest` *does* override `TriggerPriority`, and
    it is **per-villager** — `VillagerSurvival._fleeCombatQuest` — but that doesn't help if the value is cached.)
  - **Takeaway: don't fight the arbiter with priority numbers. Patch the selection method.**
- **v1.0.12 tried overriding `QuestRunner.FindNextBestQuest` → CRASHED** (its `Single&` by-ref out-param is
  the IL2CPP trampoline hazard above; villagers went inert). Dead end as a patch target.
- **v1.0.13 suspended the work quest** (`QuestRunner.Update` poll → `RemoveQuest` the active work quest,
  `AddQuest` back later). **Mechanically worked** (`Suspended work quest 'TerraformingQuest'` → active became
  `FleeCombatQuest` → reached `FSM_MeleeCombat`) **but she STILL ran.** Decisive: with the job removed, the
  run can't be job-pathing.
- **CONFIRMED ROOT CAUSE (session 3): she runs because the FleeCombatQuest executes the FLEE combat
  _behaviour_.** `VillagerSurvival` has two combat FSMs: **`fleeCombatBehaviour`** (kite/run) and
  **`naturalCombatBehaviour`** (stand & fight) — plus `avoidDangerBehaviour`, `callToArmsBehaviour`, etc.
  `CombatQuest.GetFSMBehavior(QuestData) → vFSMBehaviour` selects which one runs; for `FleeCombatQuest` it
  returns the flee one. Idle villagers fight Wisps because they use the *natural* behaviour. Every lever that
  poked the flee quest (suppress spook, block flee-flag/run-action, force `ShouldFight`, boost priority,
  remove job) was de-fanging the wrong FSM.
- **The working fix (v1.0.22): postfix `CombatQuest.GetFSMBehavior` → return `_survival.naturalCombatBehaviour`
  for a whitelisted enemy.** `FleeCombatQuest` does **not** override `GetFSMBehavior`, so patching `CombatQuest`'s
  covers it.
  - **TryCast Crash Gotcha:** Calling the native `TryCast<CombatQuest.CombatQuestData>()` on dummy/placeholder
    `QuestData` assets during early startup asset loading causes a native Access Violation (`0xc0000005`).
    **Resolution:** Use standard C# `as` casting (`var cqd = questData as CombatQuest.CombatQuestData`) and
    `Plugin.PtrOf(cqd) == IntPtr.Zero` checks, which executes in managed code without invoking the native casting runtime.
  - **Immediate Combat Drop:** Upon enemy death (`!decisionTarget.IsAlive()`), reset `_combatTimeRemaining = 0f` and
    bridge active combat with a tight `8f` second (rather than `60f` second) top-up. This allows the villager to drop
    out of the combat quest and resume their suspended work quest within seconds of the fight ending.
- **Scope FSM-action patches by target** via `IFSMBehaviourController.AiTargeting.CurrentTarget`.
- **Quest back-refs (confirmed):** `QuestData.Quest` + `QuestData.QuestRunner`, and `CombatQuestData.GetVillager()`
  — so from a `FleeCombatQuestData` patch you can reach the villager's runner/quest without `FindObjectsByType`.
  `QuestRunner` exposes `FindNextBestQuest(out topPriority, excludeActiveQuest)`, `ReevaluateQuest`,
  `AddQuest`/`RemoveQuest`, `StopAndRemoveAllQuests`, `ActiveQuest`, `_activeQuestPriority`, and a
  `_pendingQuest*` set. **Pointer gotcha:** `QuestRunner` is on the UnityEngine.Object chain and the csproj
  references the *stripped* unity-libs CoreModule, so `QuestRunner.Pointer` won't compile — cast through
  `object` to `Il2CppObjectBase` at runtime (Il2CppSystem.Object-derived types like `QuestData`/`Quest` are fine).
- `QuestPriority` scale (runtime): `Flee`=40, `SelfDefense`=42 (warrior combat), `AvoidImminentDanger`=46,
  `WorkstationWork`=15, `ImportantWork`=22, `Idle`=0.
- **Vanilla re-arms `CombatQuestData._combatTimeRemaining` to ~60s mid-fight** (observed
  `combatTime 63.8 -> ...`, plausibly on the villager taking a hit) — a top-up that only ever RAISES the
  timer toward a target (VillagerFightBackMod's original `KeepCombatAlive` logic) can get overridden by
  this re-arm and ride out close to the full ~60s. **Fix: also down-clamp** — if the timer is ever found
  ABOVE the intended cap, clamp it back down in the same patch. (confirmed in-game 2026-07-03, v1.0.27)
- **Death-detection via a polled getter (`ShouldFight`) is not reliable enough alone** — the getter can
  simply stop being polled once the target is actually dead, so a `!IsAlive()` → zero-the-timer branch
  placed only there can silently never run for a given fight. **Fix: also check on a method that runs
  unconditionally every tick** (this mod already patches `QuestRunner.Update` for work-suspension) —
  store the live `CombatQuestData` wrapper per villager and zero the timer / purge engagement state from
  there the instant the remembered enemy reads dead, independent of getter polling. Together with the
  clamp above this closed VillagerFightBackMod's "inconsistent return-to-work (seconds vs. ~a minute)"
  bug. (confirmed in-game 2026-07-03, v1.0.27 — see [`docs/mods/villager-fight-back.md`](mods/villager-fight-back.md))

## Worldgen / World Streaming
How the world loads around the player, and how to make distant tiles stream **without moving the player**.
Used by Mod 9 (SeedScoutMod) — full recipe in [`SEED_SCOUT_HANDOFF.md`](../SEED_SCOUT_HANDOFF.md).

- **Worldgen is deterministic, seed-phrase driven**, and runs at world **LOAD** (not in the create-world UI).
  Caves (mines) are fully known at load (`BiomesManager._worldGenerator.GetDataMap()._areaInstances`, filter
  `AreaInstance.TryCast<CaveAreaInstance>()`); ~3 per world. **Lakes, dens (hostiles), etc. spawn per-tile as
  the world streams** near the tracked actor — that's why they only "fill in" as you walk.
- **`AreaInstance.position` (Vector2Int) = WORLD METERS (x, z)**, not tile coords (verified twice, ~10 m).
- **`SandSailorStudio.Streaming.WorldStreamingManager`** is the streamer (capture via its `Awake`). It follows
  one `IStreamingActor TrackedActor` — which is the player's **`PlayerDrive`** (it implements `Teleport(Vector3)`
  / `IsTeleporting()`, a first-class game teleport, not a raw transform write).
- **Force-stream a tile without the player — CONFIRMED in-game (2026-06-24):**
  `WorldStreamingManager.RequestLoadWorldTile(int requestId, Transform anchor, WorldTileId tileId)`. Each
  `CaveAreaInstance` carries its own `WorldTileId` (no need to compute). Calling this for a **distant** tile
  **does instantiate that tile's biome/den/lake entities** (their spawn hooks — `BiomeLakeStampMask._OnSpawnBiome`,
  `Den.Start` — fire) while the player stays put. Verified: player never moved (`-209.9,33,-13.7`) yet dens +
  a lake spawned at cave tiles 468–1120 m away. So a *resident/requested* tile spawns content; it is **not**
  gated on the tile being *visible* near the player.
- **Coverage:** `RequestLoadWorldTile` loads only the **one tile** containing that position (~128 m square).
  To cover a cave's surroundings, request a **ring of neighbour tiles** — CONFIRMED working (2026-06-24,
  SeedScoutMod radius config). Addressing rules (these took two iterations to get right):
  - **A cave's assigned `WorldTileId` does NOT necessarily contain its entrance `position`.** e.g. a cave at
    `(-426,-722)` was assigned tile grid `(-3,-5)` = `[-384,-256)×[-640,-512)`, which excludes that point. So
    **never derive a cave's tile from its entrance** via floor/round/ceil — it fails for some caves.
  - **Address neighbours off the cave tile's own centre:** `mid = caveTile.GetWorldMidPosition((int)tileSize)`,
    then neighbour tile = `WorldTileId.GetLowest(mid.x + dx·ts, mid.y + dy·ts, ts)`.
  - **Tile grid convention is FLOOR:** tile `g` spans `[g·ts,(g+1)·ts)`, centre `(g+0.5)·ts`; `GetLowest`
    (floor) maps a centre→its tile with no rounding tie. (`GetClosest`=round and `GetHighest`=ceil exist too;
    round is tie-prone at a tile centre, so prefer `GetLowest`. Best practice: probe all three and keep the one
    that round-trips every cave's centre→known id, so a wrong tileSize/convention self-corrects.)
  - **`tileSize`** = `WorldConfiguration.GetActive().tileSize` (Int32, =128).
- **WorldTileId helpers:** `GetClosest/GetHighest/GetLowest(worldX, worldZ, worldTileSize)`, `WorldXY` (grid),
  `GetWorldPosition(tileSize)`. Manager also exposes `GetTile(int x,int y)` / `GetTile(float worldX,float worldZ)`,
  `RegisterWorldTile`, `CancelLoading`, `StopAllStreaming`, `SetTrackTransform(Transform)`.
- **Alternative force-stream primitive:** `SSSGame.StreamingInstigator` (MonoBehaviour, `Request()`) — the game's
  own "stream at this point." Not needed once `RequestLoadWorldTile` was confirmed working.
- **POI "discovery" / native map pins — SOLVED, reveal-without-walking works (confirmed in-game
  2026-07-04, SeedScout v1.x; supersedes the WarpTour teleport-tour for this purpose):**
  - **A map pin is a persistent `MarkerItem` (an `Item`, `MarkerObject : ItemComponent` on its world
    object)** spawned through the item pipeline — this is why real pins survive save/reload and render at
    any distance. The display layer (`WorldObjectiveMarker`, `ObjectiveMarkerFlags`) is stateless; there
    is no "discovered" flag anywhere — discovery IS the existence of the marker item.
  - **Recipe per marker-bearing `AreaInstance`**: set `isExplored = true`, call
    `areaInstanceMarkerHandler.RefreshExploration()` → pin materializes. ~450 handlers exist natively at
    world load (NOT created by streaming). **Caves are the exception (handler null natively)** — construct
    one: `new AreaInstanceMarkerHandler(caveArea, CavesManager.caveMarkerInfo)` (public ctor, also on the
    `BiomeAreaMarkerHandler`/`TerrainAreaMarkerHandler` subclasses), assign via
    `area.areaInstanceMarkerHandler = h.Cast<IAreaInstanceMarkerHandler>()`.
  - **Non-cave pin templates:** `area.area.itemInfo` IS the `MarkerInfo : ItemInfo` for marker-bearing
    areas — but check the NATIVE class name (ends "MarkerInfo") and rewrap `new MarkerInfo(ptr)`; the
    declared type is base ItemInfo and managed casts lie. `MarkerInfo.CreateItem()` exists as a fallback
    lever (never needed).
  - **Tile residency is the coverage limiter:** the marker-object SPAWN (not the handler) needs the POI's
    world tile resident. Residency-independent: caves, islands (Terrain areas). Everything else needs its
    tile streamed → request exactly the unpinned POIs' tiles via `RequestLoadWorldTile` (position-derived,
    FLOOR convention). Only refresh while `GetMarkerObject()` is null (duplicate-pin guard).
  - **Dens/camps/lakes/fishing grounds ARE area pins** — the "den pins" are `BiomeAreaInstance` markers
    (thematic names = area names: Habitat_Wolves→"Wulfar Den", Habitat_SkeletonsCemetaryLarge→"Large
    Cemetery", …). No separate marker path needed. Full name-stem→POI-type table in
    [`docs/mods/seed-scout.md`](mods/seed-scout.md).
  - **Gotchas found on the way:** `AreaInstance.GetWorldTileCoordinates()` returns an EMPTY rect —
    derive tiles from `area.position` (world meters). `WorldStreamingManager` has NO request-release API
    (only `CancelLoading` for in-flight) — force-loaded tiles stay resident for the session.
    **`AreaInstance.GetBounds()` Vector4 layout CONFIRMED (2026-07-05):
    `(xMin, zMin, xMax, zMax)` directly** — e.g. `MainIsland-0` raw `(-975.00, -801.00, 835.00,
    1009.00)` → bounds `(-975,-801)-(835,1009)`. (The earlier "failed under both interpretations"
    note was a false alarm — v1.2.0's home-island resolver crashed on the logging gotcha above
    *before* it could report which interpretation matched, not because the bounds genuinely didn't
    contain spawn; A was correct all along.)
- **Den type/tier:** a `Den : Creature`'s own sheet is empty (`faction=Ignore`, `baseThreatScore=0`); the
  type/tier is what its `alphaSpawner` (PopulationSpawner) spawns — `_populations[i].config`
  (CreaturePopulationConfiguration) `.name` + `.populationInfo.name` (e.g. `WightBoss`, `DraugarAlpha`).
  Full den→creature table in the SeedScout handoff.
- **Dead end (still unresolved):** the runtime seed reads `<rng-null>` —
  `RandomGeneratorManager.seedPhrase`/`SetSeedPhrase` are never the path the seed travels at load. Non-blocking
  for a scorer (you type the seed); required only for an auto-finder. See the SeedScout handoff for the chase.

---

## Caves, Mines & Hallway Excavation (confirmed in-game 2026-06-26)
- **Caves Manager**: `SSSGame.CavesManager` is a persistent MonoBehaviour that tracks all mine/cave instances in the world. It exposes `GetAllCaves()` returning a list of `CaveEntrance` instances (which represent the root nodes of each cave/mine).
- **Cave Node Tree**: A mine is a tree of `CaveNode`s (with `CaveEntrance` inheriting from `CaveNode`) structured using `SandSailorStudio.Procedural.LSystemNode`. You can recursively traverse the entire mine system starting from the entrance node using the `connections` list on `LSystemNode`.
- **Hallway State (`DigData`)**:
  - Each `CaveNode` stores the excavation progress of its mineable walls in a `SSSGame.DigData` object, accessed via `node.PersistentData.GetDigData(node.index, DataAccessMode.FETCH)`.
  - `DigData` contains a list of crack damages (`crackDamagesLeft`, `crackDamagesRight`) and indices for the currently active walls (`wallIndexLeft`, `wallIndexRight`).
  - Calling `digData.ResetCrackData()` and setting `wallIndexLeft = 0` / `wallIndexRight = 0` resets the wall's excavation state to brand new. Calling `digData.SetDirty()` flags the data for saving and replicates the reset state over the network to all clients in co-op.
- **Wall Interaction & Renderers (`CaveWallInteraction`)**:
  - Mineable walls are represented by the `CaveWallInteraction` component on child GameObjects of each node.
  - To apply the reset `DigData` and update the visuals/physics instantly on the host, locate all `CaveWallInteraction` components in children and call `wall.RefreshDigData(digData)`, `wall.RefreshRendererData()`, and `wall.SetRenderersVisible(true)`.
- **Rubble & Cave-In Clearing**:
  - A hallway collapse (cave-in) is represented by `node.IsCollapsed` and `node.open = false`.
  - A collapsed node can be programmatically cleared and reopened by setting `node.open = true`, `node._isCollapsed = false` (the writable backing field in the interop assembly), and calling `node.UpdateCollapsedState()`. This clears the rubble visuals and restores navigation.
- **Resource Spawning**:
  - Inside each hallway, loose items (chests, iron deposits, mushrooms) are spawned by `CaveItemSpawner` components. Calling `spawner.Run()` on all spawners in the node's `caveItemSpawners` list regenerates these resources.
- **Character Proximity Scans**:
  - Standard Unity `UnityEngine.Object.FindObjectsOfType(typeof(Character))` is used to query all active characters in the scene. 
  - To check if a worker or player is inside the mine before refreshing, calculate their distance to *every* node in the traversed list. If anyone is within 25 meters, abort the refresh to prevent trapping them.
- **IL2CPP Interop Quirks**:
  - **UnityEngine.Object Casting**: Do NOT call `.TryCast<T>()` on `UnityEngine.Object`-derived types (like `Component` or `GameObject`) as it throws compile-time errors. Standard C# casting (`obj as Character`, `node as CaveNode`) is fully supported and works perfectly for these mirrored types.
  - **IReadOnlyList Metadata**: `Il2CppSystem.Collections.Generic.IReadOnlyList<T>` lacks metadata showing it inherits from `IReadOnlyCollection<T>` in the interop assembly, making `.Count` and `GetEnumerator` unavailable directly. **Workaround**: Cast the list to `Il2CppSystem.Collections.Generic.IReadOnlyCollection<T>` using `.TryCast<T>()` to safely retrieve `.Count`, and access elements by index `list[i]`.


## Build Menu / Structure Templates & Localization (confirmed in-game 2026-07-01)

Backing feature: TerrainLevelerMod's "Bulldozer Field" square (Mod 15, v1.4.5–v1.4.8) — the first mod
to inject a **new buildable entry** into the build menu. Full recipe in
[`docs/mods/terrain-leveler.md`](mods/terrain-leveler.md); design/evidence chain in
`TerrainLevelerMod/BULLDOZER_UI_PLAN.md`.

*   **Templates are ItemInfos.** Inheritance: `DynamicDimensionTemplate : NodeTemplate :
    StructureTemplate : BlueprintInfo (SSSGame) : SandSailorStudio.Inventory.BlueprintInfo : ItemInfo :
    AssetNode : ScriptableObject`. A build-menu square's identity/skin lives on `ItemInfo`: `int id`,
    `ushort databaseIndex`, `ItemCategoryInfo category`, `Sprite icon`/`previewImage`, plus
    localization plumbing (below). Vanilla terrain-field template: asset
    `Item_Structures_TerrainLevelField`, **id 12664862**, `maxNumberOfTiles` **25**, category
    `Categ_Blueprints_Structures`.
*   **Registry:** `ItemInfoDatabase` holds `ItemInfoList CompleteItemInfoList` (plain
    `List<ItemInfo> itemInfoList`) and builds `_itemsMap` (id→info) + `_categoryMap` (category→infos)
    in `Initialize()`. **`Initialize` re-sorts/re-indexes the list and runs multiple times per
    session** (a `databaseIndex` moved 0→483 between calls) — any injection must be idempotent
    (re-find by id + asset name each call).
*   **Clone-inject recipe for a new buildable:** prefix `ItemInfoDatabase.Initialize()`; find the
    vanilla template in `itemInfoList` **by asset name** (managed casts lie — see universal gotchas),
    wrap via `new DynamicDimensionTemplate(info.Pointer)`, `UnityEngine.Object.Instantiate` it,
    re-skin (`name`, stable custom `id`, `databaseIndex = list position`,
    `showUnlockNotification=false`), append to the list *before* the original builds its maps.
*   **Being in the database/category map is NOT sufficient for a square to appear.** The menu lists
    ITEMS: `BuildItemsTabPage` (its `ShowPage` body is inlined into `Init`) reads
    `ItemCollection.GetAllItems(filter)` from the **player's inventory collection** (component on
    `CharacterRagnar(Clone)`, `startItems='StartItems_Aska_Vanilla'`) — the clone needs an `Item`
    granted there (`AddItems(clone, 1)`, idempotent via `GetFirstItem`). Gate the grant on the vanilla
    item being present so it rides the vanilla unlock for free.
*   **The per-tab square list is an EXPLICIT `additionalInfos` list** on the tab's
    `TabButtonCategory` (an `IItemFilter`; its `Check` combines category ids with
    `additionalInfos`/`excludedInfos`). The hoe tab shows 3 squares out of a 75-item category — the
    clone only appears once appended to the tab's `additionalInfos`. Do it in a **prefix on
    `BuildItemsTabPage.Init(TabButton)`** (before the squares are built → present on first open);
    `Init`/`Show` are virtual and safe from inlining.
*   **Unlocks are computed live, not possessed**: `NodeTemplate.IsUnlocked()` runs
    `BlueprintConditionsRule.CheckWithData()` per call; `OnUnlock` only shows a notification.
    `ItemDatabaseManager.Seed()`/`Save()` are empty stubs.
*   **Items with an unknown `ItemInfo.id` are silently dropped from serialized collections** on load —
    so removing a mod that granted a custom-id blueprint item is save-safe (the item just vanishes).
    A *placed structure* serializing a custom `templateID` fails to resolve without the mod (likely
    silently dropped) — keep custom-template structures short-lived or document the exposure.
*   **Display strings resolve through `LocalizationManager`, NEVER through the raw
    `localizedName`/`localizedDescription` fields** — `Localized = false` does not help; the UI still
    shows the derived KEY. Key derivation (`ItemInfo.GetKey(token)`):
    `item.Blueprints_<assetName-minus-Item_-prefix>_<token>` (e.g.
    `item.Blueprints_TerrainLevelField_Bulldozer_name`). Fix: inject entries into
    `LocalizationManager._localizedStrings` / `_localizedFallbackStrings`
    (`Dictionary<string,string>`) and add the keys to the **static** `LocalizationManager._allKeys`
    (`HashSet<string>`, needs an `Il2CppSystem.Core.dll` reference). Re-inject after `SetLanguage`
    (language switches rebuild the dictionaries); a postfix on `Loc(string, bool)` works as a
    safety net for lookup paths that bypass the dictionaries. (confirmed in-game 2026-07-01, v1.4.7)

## Task Discovery & Bypassing (Fishing/Cooking)

Bypassing discovery to show tasks in workstations requires different strategies depending on the station:

- **Cooking Tasks (CrockpotRecipeInfo)**: Cooking workstation tasks are gated strictly by SSSGame.BlueprintConditionsDatabase. Setting NetworkBlueprintConditionsDatabase.Rpc_AddDiscoverable unlocks them natively (confirmed in-game 2026-07-05, TaskUnlockerMod). requiresDiscovery = false does not work here.
- **Fishing Tasks — the gate is MARKING, not discovery.** Fishing tasks are not item-discovery-gated
  at all; the 2026-07-05 "dead ends everywhere" session was attacking the wrong system. The real chain
  (signature-traced and **confirmed in-game 2026-07-06**, TaskUnlockerMod v1.2.0): player marks a fishing ground (buoy) →
  `FishingGround.Mark(bool)` / `NetworkWorldDataManager.MarkFishingGround(int id)` (+ `Rpc_MarkFishingGround`)
  → `PopulationManager` (`_markedFishingGroundsMap`, `GetMarkedFishingGroundsCountForItem(ItemInfo)`) fires
  `OnFishingGroundsMarkedCountChanged` → `FishingStation._OnFishingGroundMarkChanged(FishingGround, int count)`
  → `_UpdateFishStatus(fish, count)` → task created/removed via `_CreateTaskDataForItem`/`AddTaskData`.
  `FishingGround` has *separate* `Discovered` and `IsMarked` states (plus `UnlockMarking()` — marking may
  itself be tutorial/quest-gated), and `NetworkWorldDataManager` exposes the full official quartet:
  `DiscoverFishingGround` / `MarkFishingGround` / `DisableFishingGround` / `ChangeFishingGroundSize`, each
  with an RPC, plus client-safe request variants `RequestDiscoverFishingGround(id)` / `RequestMarkFishinGround(id,
  bool)` (the "FishinGround" typo is the game's). **Working recipe (confirmed in-game 2026-07-06): enumerate
  `PopulationManager._fishingGrounds`, and per unmarked ground call its `NetworkCommunicator.RequestDiscoverFishingGround(id)`
  + `RequestMarkFishinGround(id, true)`** and let the game build tasks natively (network-replicated) —
  `UnlockMarking()` was NOT needed. Hand-adding tasks can't work end-to-end anyway because
  villager dispatch (`FSM_Fishing`, `PopulationManager.TryGetFishingGroundsForItem`) needs a real marked
  ground to row to, and count-driven `_UpdateFishStatus` would remove tasks whose marked-count is 0.
  - **Dead end**: NetworkBlueprintConditionsDatabase / DebugAllBlueprintsUnlocked — FishingStation ignores them (wrong system).
  - **Dead end**: StorageSupply.hideTasksForUndiscoveredItems = false has no effect (UI-level; the tasks were never created).
  - **Dead end**: Patching WorkstationTaskData.ShouldBeHidden throws System.NullReferenceException inside the IL2CPP
    virtual method trampoline. (Same family: `FishingStation.CheckUndiscoveredTasks(Int32&, String&)` has by-ref
    primitives — never patch it; see universal gotchas.)
  - **Dead end**: Setting FishingGround.Discovered = true doesn't populate the task list — Discovered isn't the gate,
    Marked is. (Also a `Discovered` getter-postfix likely never fired for native callers — tiny property getters
    are prime AOT-inlining candidates.)
  - **Dead end (silent no-op, log-confirmed 2026-07-06)**: `FindAnyObjectByType<FishableItemsConfig>()` returns
    **null** — it's a `ScriptableObject` config asset, not a scene object. TaskUnlockerMod v1.1.5's diagnostic loop
    logged zero fish for this reason, and v1.1.6's `FishingStation.Start` patch (`_CreateTaskDataForItem` +
    `AddTaskData` hand-rolling) uses the same lookup, so it would no-op identically — v1.1.6 was never load-tested
    anyway (log shows 1.1.5 was the last loaded version). Use the station's own `__instance.fishables` field instead.

## Terrain / Terraforming System

Backing mod: TerrainLevelerMod (Mod 15) — see [`docs/mods/terrain-leveler.md`](mods/terrain-leveler.md)
for the full recipe. Key types: `DynamicDimensionsPlacementTool` (drag/placement), `TerraformingGrid` +
`TerraformingFieldInteraction` (the placed field and its E-press leveling), `HeightmapTool` (the actual
heightmap writer), `SSSGame.Network.NetworkDynamicDimensionBuildingState[_256|_512]` (Fusion network
state for the drag preview).

### Confirmed facts
*   **`TerraformingFieldInteraction.Use(IInteractionAgent)` is the real E-press leveling hook.**
    `TryLevelTile`/`MarkCellInteracted` are **inlined by the IL2CPP AOT compiler** — Harmony patches on
    them never fire. Patch `Use`'s postfix instead, and get the grid from
    `interaction.terraformingGrid`. (confirmed in-game 2026-07-01)
*   **`HeightmapTool.RunOnArea(TerraformingToolOperation.LEVEL, width, depth, 0, center, 0)` is a true
    instant flatten** — it sets the whole footprint to `SetReferenceHeight`'s target in one pass and can
    clear vegetation (`clearVegetation`/`setVegetationMask`), unlike `TryLevelTile` (below). Get the tool
    from `TerraformingFieldInteraction.heightmapTool`. (confirmed in-game 2026-07-01)
*   **`TerraformingGridState.Rpc_DebugCompleteField()` finalizes the field (dismisses the marker/UI) but
    does NOT itself deform terrain** — safe to call *after* a `HeightmapTool` flatten as a tidy-up, does
    nothing destructive on its own. (confirmed in-game 2026-07-01)
*   **`GetCellAverageHeight`/`IsCellLeveled` are unreliable convergence signals** — `GetCellAverageHeight`
    reads a fixed baseline (doesn't reflect a `HeightmapTool` deform), and `IsCellLeveled` can report
    `true` while still ~1.5m off the real target. Don't use either to decide whether a flatten worked.
*   **Obstacle clearing needs a separate pass**: `RunOnArea` doesn't remove trees/fixed rocks. Build a
    `SSSGame.Combat.AOESpell` (Box shape sized to the grid footprint) and cast it via the player's
    `SpellsManager.CastSpellOnPos(aoe, ref pos)` — the proper entry point (calling `OnDetonated` directly
    NREs because spell setup is skipped). `clearItemInstances = true` clears fixed `WorldItemInstance`
    rocks via the data layer; `dealDamageToHarvestables = true` + high `baseDamage` kills trees/harvestable
    rocks. **`skipAllCollisionChecks` must be `false`** — `true` skips `CollisionCheck()`, which is the
    overlap that gathers harvestable targets, so trees survive while only data-layer rocks clear.
    (confirmed in-game 2026-07-01)
*   **`SpellsManager` is a standalone Fusion network object**, not under the player hierarchy —
    `GetComponent` searches from the player fail. Register every instance from a Harmony **postfix on
    `SpellsManager.Awake`** (a plain Unity lifecycle method, safe to patch), then at cast time pick
    `IsPlayer && HasAuthority` from the registry. **Do NOT patch its Fusion state-sync methods**
    (`CopyBackingFieldsToState`/`CopyStateToBackingFields`) — this **hangs the game at load** (confirmed
    the hard way). Same rule applies to any `NetworkBehaviour`'s Copy*State methods, not just SpellsManager.
*   **The real drag/resize pipeline** (relevant any time a mod needs to intercept a `DynamicDimensions`-
    style drag): `DynamicDimensionsPlacement.gridSize` (`Vector3Int`) →
    `DynamicDimensionBuilding.SetDimensions(float tileSize, Vector3Int GridSize, bool forceSet)` →
    `NetworkDynamicDimensionBuildingState.ChangeGridSize(Vector3Int gridSize)` (non-virtual, by-value —
    directly Harmony-patchable) → `NetworkDynamicDimensionBuildingState_256/_512.UpdateGridValidityOnNetwork()`.
    `DynamicDimensionsPlacementTool._OnBuildRayChanged(Ray)` is the per-frame driver that moves the
    marker and feeds this pipeline every frame during a drag. (confirmed in-game 2026-07-01)
*   **The drag preview's per-tile validity is a FIXED-SIZE Fusion struct, not a dynamic array**:
    `BitSet256` on `NetworkDynamicDimensionBuildingState_256` (used by this structure),
    `BitSet512` on the `_512` variant (used by other building types). Read the true cap at runtime via
    `NetworkedGridSize` — don't hardcode 256 or 512, since which variant is in play depends on the
    building. Past that many tiles, `UpdateGridValidityOnNetwork` writes validity bits past the struct →
    network-state corruption → a hard, silent native crash (confirmed by mapping WER crash offsets to
    this exact method via Cpp2IL — see [Native Crash Diagnosis](#native-crash-diagnosis-wer--cpp2il)
    below). **This supersedes the old "512-tile NetworkArray" belief below** — 512 is real, but it's the
    *`_512` variant's* cap, not this structure's; the terraforming preview uses the 256 variant.
    (confirmed in-game 2026-07-01)
*   **`DynamicDimensionTemplate.maxNumberOfTiles`, `DynamicDimensionsPlacementTool.maxRange`, and
    `_ValidateFootprint`'s `OverlapBoxNonAlloc` are all real but secondary limits** — they cap the
    *placed* footprint size and validate for obstacle overlap, but none of them are the drag-preview
    crash (that's the BitSet struct above, which corrupts during the drag, before placement).
*   **AOE destruction only replicates when applied by the state authority (host) — a client's own AOE
    damage is a local no-op.** This is the general form of the "only the host can break objects" class
    of co-op bug (reported on Nexus for TerrainLevelerMod's bulldozer bomb pass). The `HeightmapTool`
    flatten (a Fusion-networked call) DOES propagate regardless of who presses E; it's specifically the
    `AOESpell`/damage-based destruction that needs authority. (confirmed in-game 2026-07-03)
*   **Fix pattern: host-side execution triggered by observing the client's action through the game's own
    replication** — the mirror image of TreeRespawnMod's `WorldItemInstance._OnDataChanged` trick (there,
    the host *listens* for a client's data change; here, the host *performs* the action itself). For a
    field structure like `TerraformingGridState` (a `NetworkBehaviour`), patch postfixes on its
    replication-facing callbacks (`_OnInteractionMapChanged`, `_RefreshCellsHeights`) and/or an RPC it
    receives (`Rpc_DebugCompleteField` — Fusion's weave runs an RPC body on the receiving authority, so a
    postfix fires host-side even when a client's mod sent it), gate on `HasStateAuthority`, and dedup per
    grid instance so the host's own local press and this handler never double-process the same grid. Of
    the three trigger candidates tried, **the RPC postfix was the one that actually carried the fix in
    every observed case** — the map-replication callback fired but consistently found the local
    interaction-map mirror not yet updated at that instant, so it never got past its own "any cell
    interacted" gate; the RPC needs no such gate (by the time it arrives the map has already updated).
    Verified both near (~35m) and far (~155m) from the host. (confirmed in-game 2026-07-03, v1.5.0 — see
    [`docs/mods/terrain-leveler.md`](mods/terrain-leveler.md#co-op-host-side-destruction-v150-confirmed-in-game-2026-07-03))
    This pattern generalizes to any other mod hitting the same "client action doesn't destroy/replicate"
    symptom on a Fusion `NetworkBehaviour`-backed structure.

### Dead ends (don't retry)
*   **`DynamicDimensionsPlacementTool.firstMarker` is NOT a reliable drag anchor.** In practice it can sit
    hundreds of meters from the live `_marker` throughout an entire drag (observed ~580m offset, static,
    confirmed via diagnostic logs). Any tether/clamp built by measuring `firstMarker`-to-`_marker`
    distance measures a stale, unrelated value and does not bound real grid growth — this was tried
    across several sessions (a hardcoded 20m clamp, then a parameterized 22.5m clamp) and never actually
    prevented the crash; it just happened to coincide with default configs that stayed under the real
    (unrelated) BitSet256 cap most of the time. (dead end confirmed 2026-07-01)
*   **Skipping `_OnBuildRayChanged` (returning false from a Harmony prefix) to suppress an oversized
    preview breaks the UX**: the original method both moves the marker AND rebuilds the visible preview
    mesh, so skipping it freezes/hides the preview instead of just capping its size — the marker can
    disappear entirely or jump in large increments on frames where a substitute raycast misses. Clamp the
    **tile count** at `ChangeGridSize` instead (see confirmed facts above), not the per-frame ray/marker.
    (dead end confirmed 2026-07-01)
*   **The "steep terrain" crash theory was a red herring.** The BitSet256 overflow crash reproduces on
    flat/gently-curved terrain at the same *tile count* as on steep terrain — steeper terrain just makes
    it easier to drag farther (more apparent distance covered) before noticing the grid is already past
    256 tiles. Don't chase terrain steepness/mesh-NaN theories for a drag crash before first ruling out
    tile count via diagnostic logging.
*   **Mesh generation NaN normal crash:** The native TerraformingGrid mesh generator can crash with NaN
    normals if the height difference between the grid's start and end bounds is extreme (e.g. stretching
    across a vertical cliff face). Still a real, separate risk from the BitSet256 crash — clamp vertical
    stretch (e.g. ~15m) independently. (confirmed in-game 2026-06-30)
*   **Physics overlap array crash:** Natively, `_ValidateFootprint` uses `OverlapBoxNonAlloc` with a
    fixed-size C++ array to check for trees/rocks inside the placement grid. On very large grids placed
    over dense forests, the number of colliders can exceed the array bounds and hard-crash. Bypass by
    prefixing `_ValidateFootprint` to return `true` (skip native validation) — this is crash-prevention,
    not a cheat, so don't gate it behind a *config* toggle. (Since v1.4.6 it IS gated **per template** —
    only bulldozer drags bypass; vanilla grids cap at 25 tiles, far below the overflow point, and keep
    native red-when-obstructed validation. confirmed in-game 2026-06-30/2026-07-01)
*   **Instant flattening is impossible with `TryLevelTile` driven directly in a loop:** it does not snap
    a tile to the target height; it increments/decrements by a small hardcoded amount per call, and
    driving it directly (bypassing the `Use`/`HeightmapTool` path) corrupts the mesh into spikes rather
    than producing a lumpy-but-safe result. Use `HeightmapTool.RunOnArea` instead (confirmed fact above).
    (confirmed in-game 2026-06-30, corrected 2026-07-01 re: mesh corruption)

### Native Crash Diagnosis (WER + Cpp2IL)
A reusable technique for when a mod causes a hard native crash (no managed exception in
`LogOutput.log`) and the cause isn't obvious from reading the patched C# alone — first used to find the
BitSet256 root cause above, but applies to any future native ASKA crash:
1.  **Get the crash offset.** Windows Error Reporting logs every native crash even with no managed
    stack trace. `Get-WinEvent -FilterHashtable @{LogName='Application'; ProviderName='Application
    Error'}`, filter the message for `Aska`, and read `Faulting module` + `Fault offset` (the offset is
    an RVA into that module, usually `GameAssembly.dll`). Recurring identical offsets across multiple
    crashes are your most reliable lead — one-off `unknown module, offset 0x0` crashes are usually
    delayed corruption fallout from an earlier bad write, not the true site. Crash dumps also land in
    `%LOCALAPPDATA%\CrashDumps\Aska.exe.*.dmp` (~100MB each).
2.  **Il2CppDumper does NOT work on this game** (metadata version 39 / Unity 6000.3 unsupported —
    confirmed 2026-07-01). Use **Cpp2IL** instead (a standalone exe from its GitHub releases;
    `2022.1.0-pre-release.21` confirmed working):
    `cpp2il.exe --game-path <ASKA install dir> --use-processor attributeinjector --output-as dummydll`
    — the `attributeinjector` processor is **required**, or the dummy DLLs carry no `AddressAttribute`
    RVAs to map against (whole dump takes ~7s). `--output-as diffable-cs` additionally produces
    per-type C# skeletons, including Fusion `[Networked]` fields and codegen constants — useful for
    understanding a type's real shape beyond what the BepInEx interop assembly exposes.
3.  **Map offset → method name**: binary-search the crash RVA against every dumped method's
    `AddressAttribute.RVA` (sorted). `_explore/map_crash_offsets.ps1` in this repo does this with
    Mono.Cecil — edit its `$targets` array with your crash offsets and re-run.
4.  Unity's own `Player.log` (`%USERPROFILE%\AppData\LocalLow\Sand Sailor Studio\ASKA\`) does **not**
    contain a native stack trace for these crashes — WER events are the reliable source, not this file.

#### When WER blames `coreclr.dll+0x1d1fdd`
`coreclr.dll+0x1d1fdd` (coreclr 6.0.722, exception 0xc0000005) is NOT a real crash site — it is the CLR's fatal-error chokepoint (`EEPolicy::HandleFatalError` on the stack). It means: a NATIVE access violation happened beneath managed (mod) frames — e.g. inside a game method called from a Harmony hook — and the CLR fail-fasted. The BepInEx log always ends clean (no managed exception is possible on this path). Confirmed 2026-07-06 by symbol-resolving the public coreclr PDB from the Microsoft symbol server.

To find the REAL fault: parse the matching minidump in `%LOCALAPPDATA%\CrashDumps\Aska.exe.<pid>.dmp` with `_explore/parse_minidump.ps1` (exception context + faulting-thread stack scan against module ranges), then map GameAssembly RVAs with the Cpp2IL dummy-DLL index (`_explore/map_crash_offsets.ps1` / `map_reload_crash_rvas.ps1`) and coreclr offsets with `_explore/resolve_coreclr_syms.ps1` (downloads the PDB from msdl.microsoft.com, resolves via dbghelp).

### Startup: a one-off `UnityEngine.CoreModule` chainloader abort is transient — NOT a SAC block
Distinct from both the native crashes above and from a Smart App Control block. Symptom (seen
2026-07-08, laptop): the game launches but **no** mods load — `LogOutput.log` ends right at
`Chainloader initialized` with zero plugin lines, and `check-loaded.ps1` shows nothing loaded. The
real error is in **`BepInEx\ErrorLog.log`** (not `LogOutput.log`):
`Unhandled exception. System.IO.FileNotFoundException: Could not load file or assembly 'UnityEngine.CoreModule,
Version=0.0.0.0' … at BepInEx.Unity.IL2CPP.IL2CPPChainloader.OnInvokeMethod`. Because it is
**unhandled**, it aborts the ENTIRE chainloader before any plugin loads — one plugin's failure takes
down all of them.
- **It is transient.** A plain relaunch with **no** changes cleared it, after which all plugins (incl.
  a foreign third-party mod) loaded normally. So: relaunch first; only dig deeper if it recurs
  identically (then bisect with `bisect-plugins.ps1`, suspecting any foreign/Nexus mod first).
- **Do not mistake it for SAC.** A SAC block is a *different*, per-DLL, NON-fatal error logged during
  plugin loading: `System.IO.FileLoadException … An Application Control policy has blocked this file.
  (0x800711C7)` — it skips only that one DLL; the rest still load. Quick discriminator: if
  `check-loaded` shows *some* mods loaded and one missing → SAC (bump that mod's version). If **zero**
  loaded and the log dies at `Chainloader initialized` → this transient chainloader abort, not SAC.

`dotnet-dump analyze` CANNOT read these WER minidumps (DAC init fails — the dump lacks the managed heap); the manual parser is the working path. Worked example: the 2026-07-06 reload crash — WER said coreclr+0x1d1fdd in 4/4 crashes; the real fault in all 4 dumps was `GameAssembly+0x13e89d1` = VegetationStudioPro `InstancesDataArrays.FindIndexOfUniqueId+0xc1`, reached via `SSSGame.BiomeProceduralDataHandler.GetInstance` from TreeRespawnMod's stale cached handler after a same-world reload.

Historical note: the same coreclr+0x1d1fdd signature appears in Application-Error events on 6/18, 6/23, 6/27, 6/29 (2026) — the TreeRespawn reload bug existed for weeks before it was reproduced; recurring WER offsets in coreclr.dll therefore mean "same crash CLASS (native AV under managed frames)", not necessarily same root cause.

