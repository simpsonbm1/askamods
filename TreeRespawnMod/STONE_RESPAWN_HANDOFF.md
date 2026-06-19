# Stone / Mining-Node Respawn — Handoff Doc

**Status: ABANDONED (not feasible from a mod).** Trees + gather resources respawn fine and
ship in `TreeRespawnMod`. Stone clumps do **not** respawn, and after a full investigation
(2026-06-18) we concluded there is **no mod-reachable path** to respawn them. The mining/stone
code was removed from the mod on 2026-06-18; this doc is kept as the record of why, so nobody
burns time re-deriving it. **Do not re-attempt without new tooling/Unity-6 IL2CPP RE ability.**

### Why it's abandoned (the short version)
- `Harvest_Stone4` clumps are **Fusion SCENE OBJECTS** baked into the streamed world/chunk data —
  NOT runtime-spawned network prefabs. Proven three ways (see §11): no managed `HarvestInteraction`
  method fires for a live clump; they never go through `NetworkSpawner`; and they are absent from
  the complete 364-entry `NetworkProjectConfig.Global.PrefabTable` (confirmed complete in-world).
- Fusion despawns a scene object permanently when destroyed; there is no runtime "respawn scene
  object" API short of reloading the scene/chunk. That is almost certainly *why vanilla mined nodes
  stay mined*.
- The only way to read the actual chunk/world-data logic would be Il2CppDumper/Ghidra, but the game
  is **Unity 6000.3.12f1 → IL2CPP metadata v39**, which Il2CppDumper v6.7.46 does not support
  (`not a supported version[39]`). Only Cpp2IL handles v39, and it yields rough pseudocode that would
  still need heavy Ghidra RE — with the expected outcome being "no mod-callable path" anyway.

### If you ever revisit
- The achievable, unbuilt alternative is **loose-node respawn**: `Item_Stone_Raw` (80hp, mineable),
  `Item_Wood_RawLog`/`RawLongStick`/`Bark` are `InventoryItemInstance` (a `WorldItemInstance`) and
  ARE managed-visible live. The `WorldItemInstance` reactivation idea
  (`_contextFlags &= ~c_flagDestroyed; _OnActivateInstance()`) is untested but plausible for those.
- Or chase the clumps via Cpp2IL + Ghidra on the v39 binary (heavy, low odds).

Last updated: 2026-06-18.

---

## 1. The Goal

Respawn mineable nodes after a configurable number of in-game days, the same way
`TreeRespawnMod` already respawns felled trees and exhausted gather resources.

Target node types (config keys, in `[MiningRespawn]`): Small/Large Stone Clump,
Small/Large Jotun Clump, Jotun Blood Shard, Large Rock, Cave Stone.
**NOTE:** these config key names are guesses — see §6, the real GameObject name we
actually observe is `Harvest_Stone4` (parent) / `HarvestInteraction` (child).

---

## 2. What Works vs. What Doesn't

| Resource | Mechanism | Status |
|---|---|---|
| Trees | `BiomeItemInstance.Replenish()` via position-keyed `ActiveInstances` registry | ✅ works |
| Gather (reeds/berries/etc.) | same `BiomeItemInstance.Replenish()` path | ✅ works |
| Stone clumps | ??? (this doc) | ❌ unsolved |

The tree/gather path: `BiomeInstancePatch` (Postfix on `BiomeItemInstance.Initialize`)
populates `Plugin.ActiveInstances[posKey]` at world load; `DayTracker.Update()` looks up
the live instance by position and calls `Replenish()`. Stones are **not** BiomeItemInstances
so this path does not apply to them (confirmed early this session — do NOT re-try Replenish).

---

## 3. Confirmed Facts About Stone Clumps

From in-game diagnostic logging (the decisive `Stone hit #1:` line):

```
Stone hit #1: goName="HarvestInteraction" parent="Harvest_Stone4"
  netObj=NULL  hittable.Object=NULL  HP=0.0  vegeData=0
```

- The damaged component is a **`HarvestInteraction`** on a child GameObject literally
  named `"HarvestInteraction"`, whose **parent GameObject is `"Harvest_Stone4"`**.
- `HarvestInteraction._worldInstance` is **null** for stones (it's a `BiomeItemInstance`
  for trees). → not a biome item.
- `HarvestInteraction.NetworkObject` is **null** (the NetworkObject lives on the parent
  `Harvest_Stone4`, not on the `HarvestInteraction` child).
- `HarvestInteraction._hittable` (a `NetworkHittable`) is **present** (non-null reference),
  BUT `_hittable.Object` (its owning `NetworkObject`, from `SimulationBehaviour.Object`)
  is **null at TakeDamage time**.
- By the time **any** managed code sees the stone, it is already despawned: HP=0,
  vegeData=0, NetworkObject null/zeroed, GUID `00000000-...`.
- Mining is **15-ish tool swings**, but `HarvestInteraction.TakeDamage` fires **only once**
  — on the final/fatal hit, **after** the Fusion despawn. The 15 intermediate hits never
  reach any managed `HarvestInteraction` method we found.
- The `Harvest_Stone4` GameObject **stays active** after mining (earlier observation:
  `wasActive=True, nowActive=True`). Strongly suggests Fusion **object pooling** — the GO is
  returned to a pool, not destroyed.

### The core problem
`HarvestInteraction.TakeDamage(DamageData)` is the **only** managed hook that fires for a
stone, and it fires as a **post-despawn notification** with everything Fusion-related already
null/zeroed. There is **no managed pre-despawn signal** that we've been able to intercept.
We therefore cannot capture the prefab GUID needed for `runner.Spawn(guid, pos, rot)`.

---

## 4. Everything Tried (and why it failed)

### Patch points that DID fire
- `HarvestInteraction.TakeDamage(DamageData)` Postfix — **fires, but post-despawn.**
  Everything Fusion is null. This is where the stone branch currently lives.
- `SSSGame.Network.NetworkSpawner.Awake()` Postfix — **fires**, used to cache the
  `NetworkSpawner` (→ `_runner`) into `Plugin.GameNetworkSpawner`. Works.

### Patch points that did NOT fire (no log output at all)
- `HarvestInteraction.Use(IInteractionAgent)` Prefix — **never fires.** Stones are mined via
  combat damage, not the interaction/"press-to-use" system.
- `HarvestInteraction._HitFromHittable(HitInfo)` Prefix — **never fires.** (Invoked via the
  `NetworkHittable.OnHit` Action delegate, dispatched natively → Harmony can't intercept.)
- `SSSGame.Combat.HarvestDamageReceiver.TakeDamage` Postfix — **never fires** for stones.
- `Fusion.NetworkObject.Awake()` Postfix — **never fires** (HarmonyX can't patch Fusion
  `Behaviour`-derived methods in this IL2CPP build).
- `Fusion.NetworkObject.ResetNetworkState()` Prefix — **never fires** (same reason).
- `SSSGame.Network.NetworkHittable.Spawned()` Postfix — **never fires** (same reason).
- `Fusion.NetworkRunner.Despawn(NetworkObject, bool)` Prefix — **never fires.** Stones aren't
  despawned through this public overload; it's an internal Fusion simulation-step despawn.

**Takeaway:** HarmonyX in this BepInEx-IL2CPP build appears **unable to patch Fusion's
`NetworkObject` / `NetworkHittable` / `SimulationBehaviour`-derived methods** (none ever fired,
even simple ones like `Awake`). It CAN patch normal MonoBehaviour-derived game classes
(`HarvestInteraction`, `Creature`, etc. — those patches all work). Treat all of
`Fusion.*` and `SSSGame.Network.NetworkHittable` as **un-patchable** going forward.

### Respawn attempts (in DayTracker)
- `runner.Spawn(guid, pos, rot, ...)` — code is in place and compiles, but we can never feed
  it a valid GUID because we can't capture one pre-despawn. Always hits "no GUID cached —
  giving up."
- `BiomeItemInstance.Replenish()` — N/A for stones (not biome items). Ruled out early.
- WorldItemInstance branch (`_contextFlags &= ~c_flagDestroyed; _OnActivateInstance()`) —
  written for "Large Stone dropped on ground" type nodes; **never confirmed working**, never
  reached for the `Harvest_Stone4` clumps (those go down the stone branch, not this one).
- Clearing harvest bits + `SetActive(true)` on the pooled GO — earlier attempt, **no visual
  effect** (the GO was already active; HP lives in Fusion state we can't reach).

---

## 5. ⚠️ Critical Tooling Limitation Discovered

The `_explore/` Cecil scripts read the **interop wrapper DLLs** in `BepInEx\interop\`. These
preserve **type/field/property/method SIGNATURES** (member dumps work great) but the method
**bodies are all identical native trampolines**:

```
=== HarvestInteraction.TakeDamage IL ===
  -> IL2CPP.Il2CppObjectBaseToPtrNotNull
  -> IL2CPP.il2cpp_runtime_invoke
  -> Il2CppException.RaiseExceptionIfNecessary
```

**Consequence:** searching IL instruction operands for "who calls X" is **useless** on interop
DLLs — every body is the same stub, so every caller-search returns nothing (this is why all
the "Callers of NetworkHittable.Hit / Despawn / SetWorldInstance" searches came back empty —
that's a false negative, not evidence those calls don't exist).

**To do real call-graph / data-flow analysis next session you must dump the actual
`GameAssembly.dll`** with Il2CppDumper (produces `dump.cs` + method offsets) or open it in
Ghidra/IDA. Member dumps via Cecil are still fine for discovering signatures.

---

## 6. Suspicions / Next Directions (ranked)

### Direction A — Periodic "capture-while-alive" enumeration  ⭐ most promising, untried
Since we can't get a pre-despawn signal, **proactively** record every live stone's GUID
*before* it's ever mined, then look it up at despawn time.

1. In `DayTracker.Update()` (throttled, e.g. every few seconds), enumerate live
   `HarvestInteraction`s. The generic `FindObjectsByType<T>()` **crashes** (AOT
   MissingMethodException — see CLAUDE.md), but the **non-generic** overloads exist and
   should avoid the AOT path:
   - `UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<HarvestInteraction>())`
   - or `FindObjectsByType(Il2CppType.Of<HarvestInteraction>(), FindObjectsSortMode.None)`
2. Casting hurdle: results come back as `UnityEngine.Object` wrappers, and CLAUDE.md notes
   `.TryCast<T>()` is unavailable on `UnityEngine.Object`. **Workaround:** reinterpret the
   pointer — `new HarvestInteraction(obj.Pointer)` (standard Il2CppInterop idiom). Verify
   `obj.Pointer` is accessible; if not, try `Il2CppInterop.Runtime.Il2CppObjectPool.Get<>`
   or marshal via the array's element pointers.
3. For each stone (filter: `_worldInstance == null`), read the **parent**
   `Harvest_Stone4`'s `NetworkObject` while alive:
   `hi._hittable.Object` (should be non-null here, unlike at despawn) →
   `.NetworkGuid`, `.IsSceneObject`, `.transform.rotation`. Cache by `PosKey`.
4. On depletion (existing TakeDamage Postfix), the GUID is already cached → respawn.

**Key unknowns this directly answers (we've NEVER seen a live stone's NetworkObject):**
- Does `Harvest_Stone4` have a `NetworkObject` with a **valid, non-zero GUID** while alive?
- Is it `IsSceneObject == true`? If **true**, `runner.Spawn(prefabGuid)` is the WRONG API —
  Fusion scene objects have a baked identity and a different (re)spawn lifecycle. If
  **false**, the GUID→Spawn path should finally work.

Start by just LOGGING what enumeration finds at world load (how many, names, GUIDs,
IsSceneObject) before wiring up respawn. That single log will redirect the whole effort.

### Direction B — Dump GameAssembly.dll, find the owning manager
Use Il2CppDumper on `D:\SteamLibrary\steamapps\common\ASKA\GameAssembly.dll`. Look for:
- Who **spawns** `Harvest_Stone4` (a resource/node spawner per chunk). Candidates already
  spotted via member dumps: `SSSGame.HarvestSpawner`, `SSSGame.BiomeItemsOwner`,
  `SSSGame.NodeTemplate` / `SkewedNodeTemplate`, and the terrain generators
  `OfflineTerrainVS._GenerateBiomeItems` / `StreamingTerrainVS._GenerateBiomeItems`.
- Who **despawns** the stone on depletion (the real call path TakeDamage is notified from).
- Whether there's a manager holding a collection of nodes with a "respawn/regenerate" method
  we can call directly (à la `Replenish()` for trees).

### Direction C — Re-activate the pooled object in place
The `Harvest_Stone4` GO survives mining (pooled, stays active). Instead of spawning a new
prefab, figure out how to **re-add the existing object to the Fusion simulation** and reset
its HP/harvest state. Needs the GameAssembly dump to find the right re-init/re-spawn call.
Risk: in co-op this must replicate; doing it client-side only may desync.

### Direction D — Confirm/abandon the WorldItemInstance branch
The "Large Stone" (`_worldInstance != null`, non-biome) branch in `HarvestPatch`/`DayTracker`
(`_contextFlags`/`_OnActivateInstance`) was never confirmed. If some mineables DO have a
`WorldItemInstance`, test that branch in isolation. The `Harvest_Stone4` clumps do NOT (they
have null `_worldInstance`), so this only matters for other node types.

---

## 7. Current Code State (needs cleanup once solved)

All paths under `d:\Claude Projects\askamods\TreeRespawnMod\`.

- **`Patches/HarvestPatch.cs`**
  - `HarvestUsePatch` — Prefix on `HarvestInteraction.Use`. **Dead (never fires) — delete.**
  - `HarvestPatch` — Postfix on `HarvestInteraction.TakeDamage`. Stone branch currently full
    of **diagnostic logging** (`_stoneHits < 20` block dumping netObj/hittable.Object/parent).
    Strip diagnostics once a real approach lands.
  - `HarvestDamageReceiverPatch` — Postfix on `HarvestDamageReceiver.TakeDamage`. **Dead
    (never fires) — delete.**
- **`Patches/NetworkSpawnerPatch.cs`** — caches `NetworkSpawner`. **Keep** (works); only
  needed if we end up using `runner` to spawn.
- **`Patches/BiomeInstancePatch.cs`** — tree/gather registry. **Keep, unrelated, works.**
- **`Patches/GatherPatch.cs`** — gather respawn. **Keep, works.**
- **`DayTracker.cs`** — `Update()` loop. Mining section has the stone branch
  (`runner.Spawn(guid,...)`, always "no GUID — giving up") and the WorldItemInstance branch.
  This is where Direction A's periodic enumeration scan would also live.
- **`Plugin.cs`**
  - `CachedStoneData : Dictionary<string,(NetworkObjectGuid guid, Quaternion rot, string name)>`
    — persisted to disk as a 4th CSV field in the `# mining` save section.
  - `ActiveStoneInstances : Dictionary<string, HarvestInteraction>`
  - `GameNetworkSpawner` — cached NetworkSpawner.
  - `[MiningRespawn]` config keys (`Small Stone Clump`, etc.) — **likely wrong names**; the
    observed GameObject is `Harvest_Stone4`. Once we can read the live prefab/descriptor name,
    fix the config keys + the substring match in `GetMiningThreshold`.

### Removed dead patches (do not recreate — they never fire)
`NetworkRunnerDespawnPatch`, `NetworkHittableSpawnedPatch`, `NetworkObjectAwakePatch`,
`NetworkObjectResetPatch`.

---

## 8. How To Test (the established loop)

1. The user has a save standing in front of **two stone clumps**. Load save → mine both
   clumps → quit **without saving**. (World state resets each run; only the mod's own save
   file persists.)
2. Build: `cd TreeRespawnMod && dotnet build -c Release` (the `CopyToPlugins` target deploys
   the DLL automatically).
3. Log: `D:\SteamLibrary\steamapps\common\ASKA\BepInEx\LogOutput.log`.
4. **Mod save file (clear between tests if the pending queue interferes):**
   `D:\SteamLibrary\steamapps\common\ASKA\BepInEx\config\com.askamods.treerespawn.save`
   — sections `# tree`, `# gather`, `# mining`. Mining rows accumulate across runs
   independently of the game save, so a stale entry can trip the
   `PendingMiningRespawns.ContainsKey` early-exit. Diagnostics are placed **before** that
   early-exit so they still fire, but clear the file for clean state.
5. Observed stone positions in the test save: `45.9:49.3:173.1` and `36.2:51.2:168.3`.

---

## 9. Environment Notes (for the other machine)

- Game/install + BepInEx layout per `CLAUDE.md` (`D:\SteamLibrary\steamapps\common\ASKA`).
  Verify drive/paths match on the new machine; `_explore/Program.cs` hardcodes
  `D:\SteamLibrary\...\interop`.
- `.NET 10 SDK` to build; mods target `net6.0`.
- Interop DLLs are generated by BepInEx on first launch — present at
  `ASKA\BepInEx\interop\` (Assembly-CSharp.dll, SandSailorStudio.dll, Fusion.Runtime.dll, etc.).
- For Direction B you'll additionally need **Il2CppDumper** (not currently in the repo) run
  against `ASKA\GameAssembly.dll` + `global-metadata.dat`.

---

## 10. TL;DR for the next session

We can only see a stone **after** it's despawned, when its Fusion identity is already wiped,
and we can't patch any Fusion method to catch it earlier. So either (A) record every stone's
GUID **while alive** via periodic non-generic `FindObjectsOfType` enumeration — and first just
LOG whether a live stone even has a valid, non-scene-object GUID — or (B) dump
`GameAssembly.dll` to find the manager/spawner that owns `Harvest_Stone4` and call its
respawn path directly. Start with A's diagnostic log; it answers the make-or-break question
(valid GUID? scene object?) in one test.

---

## 11. Session 2026-06-18 (continuation) — static analysis + corrected approach

### Architecture confirmed via Cecil member dumps (signatures are reliable on interop DLLs)
- **Only two `WorldItemInstance` subtypes exist: `BiomeItemInstance` and `InventoryItemInstance`.**
  Trees/gather = `BiomeItemInstance` (→ `Replenish()` works). Stones are **neither** → they are
  Fusion-spawned networked prefabs **outside** the `WorldItemInstance`/biome system entirely.
  This is *why* `Replenish()` can never apply to stones — not a timing artifact.
- **No built-in per-node respawn for stones exists.** `ResourceManager` only *finds* resources for
  AI and *spawns new ones around the settlement* (`SpawnResourceAroundSettlement` / `_SearchForPosition`
  → `_CompleteSpawnResource`); it never regenerates a depleted node in place. `IResourceResupply`
  (`Activate/Available/IsActive/Request`) is implemented by **structure outlets**, not world nodes.
  All the `Replenish` members belong to the biome system (`BiomeItemDescriptor`/`BiomeItemInstance`/
  `BiomeProceduralDataHandler`) or to gather (`GatherInteraction.ReplenishCharges`) or fishing/
  settlements — none apply to mined stone prefabs.

### ⚠️ Direction A (FindObjects enumeration) is a DEAD END in this interop build — do not retry
- `UnityEngine.Object`-derived interop types (`Component → … → HarvestInteraction`) expose **neither
  `.Pointer` nor `.TryCast<T>()`** here (compile errors CS1061). Confirms the CLAUDE.md note. So a
  `UnityEngine.Object` returned by `Object.FindObjectsByType(type,…)` (the non-generic overload does
  compile/run) **cannot be reinterpreted or downcast** to `HarvestInteraction`. `.TryCast`/`.Pointer`/
  `.GetIl2CppType()` ARE available on plain Il2Cpp objects like `WorldItemInstance` (base
  `Il2CppObjectBase`), just not on the UnityEngine.Object-derived ones.
- Corollary: also can't downcast a base `Interaction __instance` to `HarvestInteraction`.
  **The only way to a typed live instance is a Harmony patch on a method DEFINED on
  `HarvestInteraction` itself** (HarmonyX then hands a correctly-typed `__instance`).
- Note on `FindObjectsByType`: the interop param is managed `System.Type`, so pass
  `typeof(HarvestInteraction)` (NOT `Il2CppType.Of<>()`, which is `Il2CppSystem.Type`).

### Current diagnostic build (deployed 2026-06-18) — awaiting in-game test
- `Patches/HarvestLifecyclePatch.cs` (`HarvestCapture` + 5 Postfix patches) captures live, typed
  `HarvestInteraction`s into `Plugin.LiveHarvestables` (keyed by position) from getters the HUD/
  targeting likely calls while a stone is **alive**: `GetTotalRemainingHitPoints`,
  `GetCurrentPieceIndex`, `GetMaxHarvestHitPoints`, `Check`, `RefreshInteractionArea`.
  `HarvestCapture.FiredHooks` records which actually fired (tells us which path works for stones).
- `DayTracker.RunStoneDiagnostic()` runs 3× (~6s apart) once `WeatherSystem.Instance != null`,
  logging each live node's `_worldInstance` type, `NetworkObject`/`_hittable.Object`
  `valid/scene/auth/guid`, HP, and parent name. **`[StoneDiag]` log lines answer: does a live stone
  have a valid NON-scene GUID? → decides whether `runner.Spawn(guid,…)` is even viable.**
- All of the above is clearly marked for removal once solved (search `StoneDiag` / `HarvestCapture`).
- The old enumeration version (with `.Pointer`) was removed because it can't compile here.

### Tooling note
- `_explore/dump.ps1` drives Mono.Cecil from PowerShell for member dumps. Use this instead of the
  compiled `_explore` exe: **Smart App Control now blocks the freshly-built `explore.dll`** (0x800711C7),
  but loading the (signed) `Mono.Cecil.dll` into trusted `powershell.exe` works fine.
  Usage: `& _explore/dump.ps1 -Types "SSSGame.Foo","SSSGame.Bar"`.

### CONCLUSIVE in-game results (2026-06-18) — Harvest_Stone4 is a Fusion SCENE OBJECT
Three test builds, all confirming the clumps are unreachable by managed code while alive:
1. **No managed `HarvestInteraction` method fires for a live clump.** Capture hooks on
   `GetTotalRemainingHitPoints`/`GetMaxHarvestHitPoints`/`GetCurrentPieceIndex`/`Check`/
   `RefreshInteractionArea` (+ manual `_HitFromHittable`) ALL fired for ~180 other harvestables
   (trees, and loose `Item_*` drops) but **never once for `Harvest_Stone4`**. The clump only ever
   surfaces at the final post-despawn `TakeDamage` (everything null/zeroed). Confirmed twice with a
   per-parent log cap so it wasn't a logging-budget artifact.
2. **Clumps do NOT spawn through `NetworkSpawner`.** A Postfix on `NetworkSpawner._OnBeforeSpawn`
   logged every spawn (structures, dens, villagers, warehouses, markers…) — `Harvest_Stone4` never
   appeared, even when one was mined that session. (`TryGetPrefabID` works for real prefabs once the
   runner is up; returns `[TypeId:xxxx]`.)
3. **`Harvest_Stone4` is NOT in the Fusion prefab table.** Dumped all 364 entries of
   `NetworkProjectConfig.Global.PrefabTable`; the only Stone/Harvest/Ore matches are settlement
   structures/markers/bosses (`Structure_StoneLantern`, `StoneCutter_L*`, `*HarvestMarker`,
   `StoneJotun*`, `HearthstoneCore*` …). No clump, no `Harvest_*` resource node.

**Therefore the clumps are Fusion SCENE OBJECTS** (baked into the streamed world/chunk data), not
runtime-spawned prefabs. Consequences:
- `runner.Spawn(prefabId/guid, …)` is the WRONG API — there is no prefab id/guid to spawn (the stored
  `runner.Spawn(stoneData.guid,…)` branch in `DayTracker` can never work; the captured guid is always
  `00000000-…`). Respawning a depleted scene object needs either chunk re-streaming or a
  world-data "destroyed" flag flip — neither has a known managed API yet.
- ⚠️ **Do NOT force-load the prefab table at menu.** Calling `PrefabTable.TryGetPrefab` for all 364
  ids at the main menu **crashed world-load** (Fusion expects on-demand loads). Removed.

### Side discovery — loose nodes ARE managed-visible (potentially respawnable)
The loose ground resources `Item_Stone_Raw` (80 HP, mineable), `Item_Wood_RawLog`/`RawLongStick`/
`Bark` are **`InventoryItemInstance`** (a `WorldItemInstance` subtype) and DO fire all the
HarvestInteraction hooks live (`_worldInstance` non-null, NetworkObject null). These are NOT the
clumps, but the existing `WorldItemInstance` reactivation branch
(`_contextFlags &= ~c_flagDestroyed; _OnActivateInstance()`) is worth testing on them if loose-node
respawn is desired — separate from the (much harder) `Harvest_Stone4` clump problem.

### State after this session
All session diagnostics removed; mod restored to known-good (trees/gather working). The dormant
non-functional stone branches in `HarvestPatch`/`DayTracker` remain but never fire usefully and
never crash. Next real step for clumps = **Il2CppDumper on GameAssembly.dll** to find the
chunk/scene-object streaming + world-data "destroyed" state (Direction B).
