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

### Keep CLAUDE.md ↔ AGENTS.md In Sync — and VERIFY it, don't just intend to
The user works with **both Claude Code (`CLAUDE.md`) and Antigravity (`.agents/AGENTS.md`)** across
two machines, switching tools when a token budget runs out. These two files are the same orientation
for different tools and **WILL silently drift unless every session actively runs the checks below.**
This has already bitten the project: a whole mod (SeedHarvesterMod) went missing from both files, a
version + technique change (JotunBloodYieldMod v1.1.0) went unrecorded, and CLAUDE.md ended up with a
duplicated copy of its own body. Treat the three rituals here as non-optional.

**Ritual 1 — Drift check at session START (and again before any commit).** Do not trust the
orientation files; reconcile them against ground truth first. Verify (commands illustrative — adapt
to your shell):
- `ls -d */` → **every** mod folder must appear in BOTH files' Project Structure.
- `grep -r PLUGIN_VERSION */MyPluginInfo.cs` → the version shown for each mod in BOTH files must match the code.
- `ls *HANDOFF*.md docs/mods/*.md` → every handoff/mod doc must be in BOTH Documentation Maps.
- `git log --oneline` since you last worked → scan for new mods, version bumps, status changes,
  new gotchas, or new docs that never made it into the orientation files.
Fix every mismatch in BOTH files before starting new work.

**Ritual 2 — Definition of Done (dual-write, blocks the commit).** A change is **NOT done and must
NOT be committed** until BOTH `CLAUDE.md` AND `.agents/AGENTS.md` reflect it. Edit them in the same
change whenever you:
- add / rename / remove a mod folder, or change a mod's status (WIP→COMPLETE, COMPLETE→PARKED/BLOCKED, new blocker) — if a mod is PARKED or un-parked, also update `$ParkedByDefault` in `sync-plugins.ps1`;
- bump a mod version, or change its core technique/approach (update the mod's `docs/mods/*.md` too);
- add a new IL2CPP gotcha or a dead-end;
- add a handoff doc or a `docs/mods/` file → add it to BOTH Documentation Maps.
Tag in-game-verified facts with `confirmed in-game (YYYY-MM-DD)`.

**Ritual 3 — Integrity guard (catch accidental duplication).** After rewriting a whole orientation
file, confirm you didn't append a second copy instead of replacing: `grep -c "^# AskaMods" CLAUDE.md`
and `grep -c "^# AskaMods" .agents/AGENTS.md` must each return **1**.

**Cadence — these rituals are commit-gated, NOT build-gated.** In an iterative build→test→build loop,
each cycle do only: edit code, bump the version (`PLUGIN_VERSION` + csproj `<Version>` — needed so Smart
App Control re-evaluates the new DLL hash and so the loaded version is confirmable), build, and convey
test steps in chat. **Defer the dual-write sync (Ritual 2), the integrity guard (Ritual 3), and any
handoff/`docs/` prose to the commit checkpoint, and do them once** — uncommitted edits don't reach the
other machine until a push, so nothing is lost. A full doc pass on every build is wasted token cost
(the user runs every task, including doc cleanup, on Opus).

If unsure whether a detail belongs in the orientation file vs. deeper docs, match what the other file
does — both stay parallel in scope and structure.

### Git — ask before committing; never auto-push
This folder **IS** a git repository (`master`, remote `origin` → `https://github.com/simpsonbm1/askamods.git`).
Session-startup may falsely report "not a git repo" due to the space in `D:\Claude Projects\...`.
Verify with `git rev-parse --is-inside-work-tree` if unsure. Git works normally.

**Ask before you commit or push — never do it automatically.** Commit/push only **after a verified
success** (a change actually tested and confirmed working in-game — not just "code changed/compiled")
**or** at a **general end-of-session** checkpoint, and **confirm with the user first** each time. Do
NOT push work-in-progress or unverified edits just because files changed. **This is the single most
important habit to break vs. the previous workflow** — do not push after every code update.

**Two machines — sync at those gated moments only.** The user syncs desktop ↔ laptop solely through
`origin`, so when you *do* commit (with the user's go-ahead), **push** it — source, built DLLs, docs,
configs — so the other machine pulls it. Only `bin/`, `obj/`, and `*.save` are gitignored. Don't
strand files locally, but don't pre-empt the user's go-ahead to push, either.

### Syncing live plugins from git (helper script)
`sync-plugins.ps1` (repo root) copies each committed `<Mod>\<Mod>.dll` into the live
`ASKA\BepInEx\plugins\<Mod>\`. A `git pull` refreshes the repo's DLLs but never the live game folder,
so after pulling on a machine the live mods lag until this runs.

**When the user asks to "sync the plugins from git to the live folder" (or to push / refresh the live
mods — e.g. after a pull, or between machines), RUN this script. Don't hand-roll a manual copy.**
- Preview: `.\sync-plugins.ps1 -DryRun`  ·  Apply: `.\sync-plugins.ps1`
- A machine whose ASKA lives elsewhere: pass `-PluginsDir <path>` or set `$env:ASKA_PLUGINS`.

It copies only when the hash differs, preserves each mod's live enabled/`.dll.off` state, backs up
replaced DLLs under `%TEMP%\askamods-sync-backups\`, and reminds you to confirm the loaded versions in
`LogOutput.log` — it can't dodge SAC, so a blocked DLL still needs a version bump + rebuild. Its
`$ParkedByDefault` list (CookingStationFixMod, SeedHarvesterMod) makes parked spikes install disabled
on a fresh machine — keep that list in step with mod parked-status changes (see Ritual 2).

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
9. **Query persistent managers over ephemeral components** — some interactive components (like `CaveWallInteraction` walls) are destroyed and removed from the scene once depleted. To restore them, query their persistent parent managers (like `DigVolume`) which remain in the scene hierarchy, and invoke their native regeneration methods (e.g., `ResetWalls(true)`) to let the game rebuild the visual and physical objects correctly.

---

## Project Structure

```
askamods/
  CLAUDE.md                  ← Claude Code orientation (canonical source)
  .agents/AGENTS.md          ← THIS FILE (Antigravity context)
  sync-plugins.ps1           ← push committed mod DLLs → live BepInEx\plugins (see "Syncing live plugins from git")
  docs/
    architecture.md          ← master knowledge base: game internals + dead-ends by subsystem
    nexus-upload.md          ← Nexus Mods CI/publishing workflow
    mods/                    ← one file per mod (shipped recipe + config)
  _explore/                  ← throwaway Mono.Cecil inspector scripts (not a mod)
  BowDamageMod/              ← Mod 1: buff early-game bow damage         [COMPLETE]
  TreeRespawnMod/            ← Mod 2: respawn trees + gather resources    [v1.2.10 — Issues C/D RESOLVED 2026-06-30: BiomeProceduralDataHandler.GetInstance(onlyIfActive:false) + Replenish() refills a deactivated gather node's persistent data without force-loading its tile; WorldItemInstanceId persisted across save/reload (RefillUnloadedGatherNodes, default on) — confirmed in-game across a single session and a save/reload boundary; Issue A (co-op) still open, see TREERESPAWN_HANDOFF.md]
  HealthRegenMod/            ← Mod 3: player HP regen after combat        [COMPLETE]
  TorchFuelMod/              ← Mod 4: perpetual torch fuel                [COMPLETE]
  DynamicVillagerNeedsMod/   ← Mod 5: needs-based villager behavior       [COMPLETE]
  (WarehouseFilterMod)       ← Mod 6: hauling filter (DESIGN READY, NO CODE)
  VillagerFightBackMod/      ← Mod 7: villagers fight whitelisted enemies [COMPLETE v1.0.25]
  CookingStationFixMod/      ← Mod 8: diagnostic only (parked .dll.off)
  SeedScoutMod/              ← Mod 9: seed scorer + map overlay           [WIP v0.15.0]
  WarpTourMod/               ← Mod 10: teleport-tour for native map pins  [WORKING v1.0.0]
  MineRefreshMod/            ← Mod 11: safe, on-demand mine/cave refresh  [COMPLETE v1.3.0]
  JotunBloodYieldMod/        ← Mod 13: increases jotun blood yields       [COMPLETE v1.1.0]
  SeedHarvesterMod/          ← Mod 14: fast in-memory seed-scan experiment [PARKED — patch disabled, blocked; installed .dll renamed to .dll.off 2026-06-28]
```

> **TreeRespawnMod (2)** is at **v1.2.10** — co-op client respawn fix (see `TREERESPAWN_HANDOFF.md`) plus
> **per-world save isolation** (confirmed in-game 2026-06-28 — SP and co-op produced two separate files, on
> both machines): the pending-respawn file is keyed by `StorageManager.ActiveSessionID`
> (`DayTracker.PollWorldId`) so singleplayer and co-op worlds no longer share/cross-contaminate respawn
> state. **The world SEED was a dead-end** (empty on loaded saves — v1.2.0 silently did nothing); see
> `docs/mods/tree-respawn.md` + architecture.md "Identifying the loaded world". **v1.2.2 was a
> version-only bump** — Smart App Control blocked the v1.2.1 DLL hash on the second machine
> (`FileLoadException ... 0x800711C7`); bumping the version changes the hash so SAC re-evaluates it.
> **v1.2.3-v1.2.7** built up diagnostics for Issues C (distant villager gather doesn't respawn) and D
> (overdue respawns stuck while their node is unloaded) and forked two candidate mechanisms — **M1**
> (harvested while unloaded, never registered) vs **M2** (fake respawn against a stale `ActiveInstances`
> pointer never pruned on per-node unload). Along the way, found Issue F (a vanilla villager-AI fiber
> lockout, unrelated, tracked separately). **v1.2.8 (2026-06-29/30) confirmed a deactivated node's
> persistent data buffer stays addressable without force-loading its tile** — the finding that unlocked
> the fix: **v1.2.9** resolves a fresh, writable instance via
> `SSSGame.BiomeProceduralDataHandler.GetInstance(tileId, widId, onlyIfActive:false, noPooling:true)` and
> calls the game's own `Replenish()` on it, confirmed in-game refilling a distant shoreline reed marker
> while the player stayed at base. **v1.2.10 (2026-06-30) productionizes that mechanism**
> (`RefillUnloadedGatherNodes`, default ON) with the node's `WorldItemInstanceId` persisted across
> save/reload and a 30s retry/liveness-guard cooldown for an unresolved node. Confirmed in-game across a
> save→reload→reload test sequence: the deactivated-refill path kept working correctly after a reload —
> fixing the original "shoreline reeds never refill" symptom. **Issues C and D are RESOLVED.** Issue A
> (co-op) still open. Full mechanism + test evidence: `TREERESPAWN_HANDOFF.md` → "RESOLVED 2026-06-30".

Each mod is a separate `.csproj` outputting its `.dll` to `BepInEx\plugins\<ModName>\`.
The `CopyToPlugins` MSBuild target handles deployment automatically on build.

---

## Mod Status Summary

### Complete & Shipped
| Mod | Key Technique | Nexus |
|---|---|---|
| **BowDamageMod** (1) | Prefix on `Creature.TakeDamage`, match arrow name in `DamageData.weapon` | Not on Nexus |
| **TreeRespawnMod** (2, v1.2.10) | Postfix `HarvestInteraction.TakeDamage` + `GatherInteraction.GatherItemsCharge`; `Replenish()` after days; stump protection via `CanProvideItem`; **per-world save** keyed by `StorageManager.ActiveSessionID` (DayTracker poll; seed was a dead-end) confirmed in-game 2026-06-28 on both machines; for a gather node whose chunk has deactivated, `BiomeProceduralDataHandler.GetInstance(onlyIfActive:false)` + `Replenish()` refills it without force-loading the tile, with `WorldItemInstanceId` persisted across save/reload (`RefillUnloadedGatherNodes`, default on) — **Issues C/D RESOLVED v1.2.10**, confirmed in-game 2026-06-30 | Group 7551668 |
| **HealthRegenMod** (3) | `RegenTracker` MonoBehaviour; polls `LastDamageTime`; discrete tick regen | Group 7551800 |
| **TorchFuelMod** (4) | Postfix `FireStructure.Initialize`; `TorchFuelTracker` tops off via `Rpc_AddFuel()`; DON'T fuel Bloomery | Not on Nexus |
| **DynamicVillagerNeedsMod** (5) | `NeedsController` MonoBehaviour; drives `Rpc_ChangeSchedule`; hysteresis-based need decisions | Group 7567346 |
| **VillagerFightBackMod** (7) | FSM redirection to `naturalCombatBehaviour` + work quest suspension during fight + instant exit on target death | Group 7587134 |
| **WarpTourMod** (10) | Teleport-tour POIs for native map pins; DwellSeconds min 0.5, DrainSeconds=8; Enabled=false by default | Group 7617637 |
| **MineRefreshMod** (11) | Traverses cave tree, resets DigData (ResetCrackData/wallIndex), clears collapses, proximity safety scan, native DigVolume wall refresh | Group 7586480 |
| **JotunBloodYieldMod** (13, v1.1.0) | Postfix `HarvestSpawner.Awake` duplicates "Blood"/"Jotun" entries in `pieceLoot`/`bitLoot` (1 entry→3 rolls, 2→6; guard skips lists already ≥3); postfix `LootSpawner.GetLootStack` remaps quantity 1→3, 2→5, 3+→6. (Replaced the old `_GetAwardedLootCount` hook, which didn't reliably fire.) **Confirmed in-game 2026-06-28.** | Not on Nexus |

### In Progress / Blocked
| Mod | Status | Key Issue |
|---|---|---|
| **WarehouseFilterMod** (6) | Design ready, no code | Prefix on `ResourceStorage.CanCreateStorageTaskForItemInfo` to block crafting-station input hauling |
| **SeedScoutMod** (9) | WIP v0.15.0 | Seed scorer + map overlay working. Native pins failed (→ WarpTour). Seed read returns `<rng-null>`. Den classification needs `affectedSpawners`. |
| **SeedHarvesterMod** (14) | PARKED — patch disabled; installed .dll renamed to .dll.off 2026-06-28 | "Fast Harvest" coroutine regenerates seeds in-memory in seconds, but every seed scores `-9999`: cave `AreaInstance` GameObjects are never instantiated by `UpdateDataAsync` (a 1-frame `yield` doesn't force it — dead-end 2026-06-28), so cave positions can't be read. Would need a `GameAssembly.dll` dump to parse raw data buffers. `SEED_HARVESTER_HANDOFF.md`. |

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
| [`docs/mods/jotun-blood-yield.md`](file:///d:/Claude%20Projects/askamods/docs/mods/jotun-blood-yield.md) | Mod 13 — JotunBloodYieldMod |
| [`docs/nexus-upload.md`](file:///d:/Claude%20Projects/askamods/docs/nexus-upload.md) | Publishing to Nexus Mods |
| [`DYNAMIC_HAULING_HANDOFF.md`](file:///d:/Claude%20Projects/askamods/DYNAMIC_HAULING_HANDOFF.md) | Mod 6 — warehouse hauling filter |
| [`WAREHOUSE_CAPACITY_HANDOFF.md`](file:///d:/Claude%20Projects/askamods/WAREHOUSE_CAPACITY_HANDOFF.md) | Mod 12 (Planned) — warehouse capacity |
| [`VILLAGER_FIGHTBACK_HANDOFF.md`](file:///d:/Claude%20Projects/askamods/VILLAGER_FIGHTBACK_HANDOFF.md) | Mod 7 — crash debug + behavior-swap approach |
| [`SEED_SCOUT_HANDOFF.md`](file:///d:/Claude%20Projects/askamods/SEED_SCOUT_HANDOFF.md) | Mod 9 — worldgen findings, scorer + overlay |
| [`WARP_TOUR_HANDOFF.md`](file:///d:/Claude%20Projects/askamods/WARP_TOUR_HANDOFF.md) | Mod 10 — teleport-tour design + tuning |
| [`TREERESPAWN_HANDOFF.md`](file:///d:/Claude%20Projects/askamods/TREERESPAWN_HANDOFF.md) | Mod 2 — TreeRespawn bug tracker & handoff (co-op respawn, cross-world save fix, issues C/D RESOLVED v1.2.10, parked issue E, plus tracked-but-out-of-scope Issue F — a vanilla villager-AI fiber lockout) |
| [`SEED_HARVESTER_HANDOFF.md`](file:///d:/Claude%20Projects/askamods/SEED_HARVESTER_HANDOFF.md) | Mod 14 — SeedHarvesterMod: fast in-memory seed scan (blocked — see dead-ends) |

---

## Namespaces (Game Code)

- `SSSGame.*` — ASKA code (Sand Sailor Studio)
- `Invector.*` — third-party character controller framework
- `SandSailorStudio.Inventory.*` — custom inventory system

## Nexus Mods Publishing

Automated via GitHub Actions (`.github/workflows/nexus-upload.yml`, manual `workflow_dispatch`).
Uses committed DLLs (CI can't build — interop DLLs aren't redistributable).
API can only UPDATE existing file groups, not create pages. Description edits are manual.
