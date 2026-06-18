# Stone / Mining-Node Respawn — Handoff Doc

**Status:** UNSOLVED. Trees + gather resources respawn fine. Stone clumps (and other
mineable nodes) do **not** yet respawn. This doc captures everything tried so a fresh
session (likely on a different machine) can pick up without re-deriving.

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
