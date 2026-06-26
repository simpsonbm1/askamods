# AskaMods — Project Context (Antigravity)

This file is the always-loaded orientation for the AskaMods project. It was ported from the
Claude Code `CLAUDE.md` and the chain of documentation in `docs/`. Deep detail lives in `docs/`
and the per-mod handoff files — read those on demand before working on any specific subsystem.

## Standing Instructions

### Proactive Documentation Maintenance
Whenever you **confirm** a new fact about the game, the IL2CPP interop, or a mod's behavior —
especially **in-game-verified** facts and **dead-ends** — record it before treating the task as done:
- Game / interop / subsystem facts → matching subsection of [`docs/architecture.md`](file:///d:/Claude%20Projects/askamods/docs/architecture.md)
- Mod-specific recipe/config → that mod's file under `docs/mods/`

**Only record CONFIRMED learnings.** Flag anything not yet verified as ⚠️ pending. Date in-game
findings (`confirmed in-game (YYYY-MM-DD)`). Always capture dead-ends — the whole point is to
stop future sessions re-treading a ruled-out path.

### Keep CLAUDE.md ↔ AGENTS.md In Sync
The user works with **both Claude Code (`CLAUDE.md`) and Antigravity (`.agents/AGENTS.md`)**.
These two files serve the same purpose for their respective tools. Whenever you update one, **also
update the other** with any new information, pointers, structural changes, or status updates so
both tools stay current. This includes:
- New mods or status changes (WIP → COMPLETE, new blockers, etc.)
- New IL2CPP gotchas or dead-ends
- New documentation map entries or handoff files
- Project structure changes

If you're unsure whether a change belongs in the orientation file vs. deeper docs, match what the
other file already does — both should stay parallel in scope and structure.

### Git
This folder **IS** a git repository (`master`, remote `origin` → `https://github.com/simpsonbm1/askamods.git`).
Session-startup may falsely report "not a git repo" due to the space in `D:\Claude Projects\...`.
Verify with `git rev-parse --is-inside-work-tree` if unsure. Git works normally.

**Two machines — keep everything in `origin`.** The user works across desktop and laptop, syncing
only through the remote. When work is committable, **commit AND push** — source, built DLLs, docs,
configs. Only `bin/`, `obj/`, and `*.save` stay gitignored.

---

## Game & Mod Loader Stack

| Item | Detail |
|---|---|
| **Game** | **ASKA** — co-op Viking survival/city-builder on Steam |
| **Install path** | `D:\SteamLibrary\steamapps\common\ASKA` |
| **BepInEx** | 6.0.0 (IL2CPP build) at `ASKA\BepInEx\` |
| **HarmonyX** | Bundled with BepInEx 6 — runtime method patching |
| **Il2CppInterop** | Bundled — generates C# wrappers from IL2CPP binary |
| **.NET SDK** | 10 — targeting `net6.0` |

### Build Gotcha: Smart App Control (SAC)
Windows SAC is **ENFORCED** on this machine. It intermittently blocks freshly-built unsigned DLLs.
**.NET builds are deterministic** → same source = same hash = same block. **Fix: bump the mod version**
(`PLUGIN_VERSION` + csproj `<Version>`) so the DLL hash changes. Always confirm the loaded version
in `LogOutput.log` before trusting a test.

---

## IL2CPP Interop Gotchas (Apply to ALL Mods)

These are the recurring traps. Full detail in [`docs/architecture.md`](file:///d:/Claude%20Projects/askamods/docs/architecture.md).

1. **Don't subscribe to game `Action` events** — `Il2CppSystem.Action(methodRef)` only takes `IntPtr`.
   Use registered `MonoBehaviour` + `Update()` polling (DayTracker/RegenTracker/TorchFuelTracker pattern).
2. **`FindObjectsByType<T>()` throws** `MissingMethodException` — get instances from Harmony `__instance`.
3. **NEVER patch IL2CPP methods with by-ref primitive params** (`Single&`, `Int32&`, `Boolean&`) —
   trampoline NREs, outside try/catch. Patch a sibling method with safe signature instead.
4. **No `.TryCast<T>()` on `UnityEngine.Object`** — only on `Il2CppObjectBase`-derived types.
5. **Don't patch `Initialize`/lifecycle on `Interaction` MonoBehaviours** (KilnInteraction, etc.) —
   fires during prefab init before GC handle setup → `Handle is not initialized` crash.
6. **`UniqueId` is NOT globally unique** — per-buffer index restarting per chunk. Key by **world position**.
7. **Gate all state writes on authority** (`HasAuthority` / `_hasAuthority`).
8. **Prefer game's own RPCs** over direct networked-state writes for co-op safety.

---

## Project Structure

```
askamods/
  CLAUDE.md                  ← Claude Code orientation (canonical source)
  .agents/AGENTS.md          ← THIS FILE (Antigravity context)
  docs/
    architecture.md          ← master knowledge base: game internals + dead-ends by subsystem
    nexus-upload.md          ← Nexus Mods CI/publishing workflow
    mods/                    ← one file per mod (shipped recipe + config)
  _explore/                  ← throwaway Mono.Cecil inspector scripts (not a mod)
  BowDamageMod/              ← Mod 1: buff early-game bow damage         [COMPLETE]
  TreeRespawnMod/            ← Mod 2: respawn trees + gather resources    [COMPLETE]
  HealthRegenMod/            ← Mod 3: player HP regen after combat        [COMPLETE]
  TorchFuelMod/              ← Mod 4: perpetual torch fuel                [COMPLETE]
  DynamicVillagerNeedsMod/   ← Mod 5: needs-based villager behavior       [COMPLETE]
  (WarehouseFilterMod)       ← Mod 6: hauling filter (DESIGN READY, NO CODE)
  VillagerFightBackMod/      ← Mod 7: villagers fight whitelisted enemies [BLOCKED - crash on launch]
  CookingStationFixMod/      ← Mod 8: diagnostic only (parked .dll.off)
  SeedScoutMod/              ← Mod 9: seed scorer + map overlay           [WIP v0.15.0]
  WarpTourMod/               ← Mod 10: teleport-tour for native map pins  [WORKING v1.0.0]
  MineRefreshMod/            ← Mod 11: safe, on-demand mine/cave refresh  [COMPLETE v1.2.0]
```

Each mod is a separate `.csproj` outputting its `.dll` to `BepInEx\plugins\<ModName>\`.
The `CopyToPlugins` MSBuild target handles deployment automatically on build.

---

## Mod Status Summary

### Complete & Shipped
| Mod | Key Technique | Nexus |
|---|---|---|
| **BowDamageMod** (1) | Prefix on `Creature.TakeDamage`, match arrow name in `DamageData.weapon` | Not on Nexus |
| **TreeRespawnMod** (2) | Postfix `HarvestInteraction.TakeDamage` + `GatherInteraction.GatherItemsCharge`; `Replenish()` after days; stump protection via `CanProvideItem` | Group 7551668 |
| **HealthRegenMod** (3) | `RegenTracker` MonoBehaviour; polls `LastDamageTime`; discrete tick regen | Group 7551800 |
| **TorchFuelMod** (4) | Postfix `FireStructure.Initialize`; `TorchFuelTracker` tops off via `Rpc_AddFuel()`; DON'T fuel Bloomery | Not on Nexus |
| **DynamicVillagerNeedsMod** (5) | `NeedsController` MonoBehaviour; drives `Rpc_ChangeSchedule`; hysteresis-based need decisions | Group 7567346 |
| **WarpTourMod** (10) | Teleport-tour POIs for native map pins; DwellSeconds min 0.5, DrainSeconds=8; Enabled=false by default | Group 7617637 |
| **MineRefreshMod** (11) | Traverses cave tree, resets DigData (ResetCrackData/wallIndex), clears collapses, proximity safety scan, global identity-based wall search & restore | Not on Nexus |

### In Progress / Blocked
| Mod | Status | Key Issue |
|---|---|---|
| **WarehouseFilterMod** (6) | Design ready, no code | Prefix on `ResourceStorage.CanCreateStorageTaskForItemInfo` to block crafting-station input hauling |
| **VillagerFightBackMod** (7) | 🛑 BLOCKED — v1.0.14 crashes on launch | Root cause: flee = `fleeCombatBehaviour` FSM, not quest/flag. Fix: swap to `naturalCombatBehaviour` via `CombatQuest.GetFSMBehavior` postfix. Crash needs bisection. |
| **SeedScoutMod** (9) | WIP v0.15.0 | Seed scorer + map overlay working. Native pins failed (→ WarpTour). Seed read returns `<rng-null>`. Den classification needs `affectedSpawners`. |

---

## Key Subsystem Quick Reference

Read the full detail in [`docs/architecture.md`](file:///d:/Claude%20Projects/askamods/docs/architecture.md) before working on any subsystem.

| Subsystem | Key Entry Points |
|---|---|
| **Damage** | `Creature.TakeDamage(DamageData)` — `DamageData.weapon` = arrow for ranged |
| **Player** | `PlayerCharacter : Character` (separate from `Creature`); `PlayerCharacter.Spawned()` for init |
| **Trees/Resources** | `HarvestInteraction.TakeDamage`; `BiomeItemInstance.Replenish()`; stumps = same object at last harvestPiece |
| **Gather** | `GatherInteraction.GatherItemsCharge` postfix; `.GetGatherableItemInfo().Name` = yielded item name |
| **Fire/Torch** | `FireStructure.Initialize`; `Rpc_AddFuel(float)`; cave sconces = `CaveTorchOutlet` (different system) |
| **Villager Needs** | `VillagerSurvival._foodVAttr/_waterVAttr/_warmthVAttr/_energyVAttr`; `Villager.Rpc_ChangeSchedule(long)` |
| **Villager Combat** | `FleeCombatQuest` vs `WarriorCombatQuest`; FSMs on `VillagerSurvival`: `fleeCombatBehaviour` / `naturalCombatBehaviour` |
| **Settlement/Inventory** | `Settlement.QuerySettlementResources() → ItemManifest`; `ResourceStorage` + `StorageSupply` |
| **Worldgen** | `BiomesManager._worldGenerator.GetDataMap()._areaInstances` for caves; `WorldStreamingManager.RequestLoadWorldTile()` |
| **Weather/Time** | `WeatherSystem.Instance.GetDaysPassed()`, `NetworkedCurrentGameTime`, `IsNight`, `DayNightValue` |

---

## Reference Paths

| Purpose | Path |
|---|---|
| BepInEx core DLLs | `ASKA\BepInEx\core\` |
| Game interop assemblies | `ASKA\BepInEx\interop\` |
| Unity engine modules | `ASKA\BepInEx\unity-libs\` |
| Plugin output folder | `ASKA\BepInEx\plugins\` |
| BepInEx log | `ASKA\BepInEx\LogOutput.log` |

## Documentation Map

| Read this | When working on |
|---|---|
| [`docs/architecture.md`](file:///d:/Claude%20Projects/askamods/docs/architecture.md) | **Any** game subsystem |
| [`docs/mods/bow-damage.md`](file:///d:/Claude%20Projects/askamods/docs/mods/bow-damage.md) | Mod 1 — BowDamageMod |
| [`docs/mods/tree-respawn.md`](file:///d:/Claude%20Projects/askamods/docs/mods/tree-respawn.md) | Mod 2 — TreeRespawnMod |
| [`docs/mods/health-regen.md`](file:///d:/Claude%20Projects/askamods/docs/mods/health-regen.md) | Mod 3 — HealthRegenMod |
| [`docs/mods/torch-fuel.md`](file:///d:/Claude%20Projects/askamods/docs/mods/torch-fuel.md) | Mod 4 — TorchFuelMod |
| [`docs/mods/dynamic-villager-needs.md`](file:///d:/Claude%20Projects/askamods/docs/mods/dynamic-villager-needs.md) | Mod 5 — DynamicVillagerNeedsMod |
| [`docs/mods/villager-fight-back.md`](file:///d:/Claude%20Projects/askamods/docs/mods/villager-fight-back.md) | Mod 7 — VillagerFightBackMod |
| [`docs/mods/mine-refresh.md`](file:///d:/Claude%20Projects/askamods/docs/mods/mine-refresh.md) | Mod 11 — MineRefreshMod |
| [`docs/nexus-upload.md`](file:///d:/Claude%20Projects/askamods/docs/nexus-upload.md) | Publishing to Nexus Mods |
| [`DYNAMIC_HAULING_HANDOFF.md`](file:///d:/Claude%20Projects/askamods/DYNAMIC_HAULING_HANDOFF.md) | Mod 6 — warehouse hauling filter |
| [`VILLAGER_FIGHTBACK_HANDOFF.md`](file:///d:/Claude%20Projects/askamods/VILLAGER_FIGHTBACK_HANDOFF.md) | Mod 7 — crash debug + behavior-swap approach |
| [`SEED_SCOUT_HANDOFF.md`](file:///d:/Claude%20Projects/askamods/SEED_SCOUT_HANDOFF.md) | Mod 9 — worldgen findings, scorer + overlay |
| [`WARP_TOUR_HANDOFF.md`](file:///d:/Claude%20Projects/askamods/WARP_TOUR_HANDOFF.md) | Mod 10 — teleport-tour design + tuning |

---

## Namespaces (Game Code)

- `SSSGame.*` — ASKA code (Sand Sailor Studio)
- `Invector.*` — third-party character controller framework
- `SandSailorStudio.Inventory.*` — custom inventory system

## Nexus Mods Publishing

Automated via GitHub Actions (`.github/workflows/nexus-upload.yml`, manual `workflow_dispatch`).
Uses committed DLLs (CI can't build — interop DLLs aren't redistributable).
API can only UPDATE existing file groups, not create pages. Description edits are manual.
