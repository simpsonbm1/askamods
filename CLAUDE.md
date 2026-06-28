# AskaMods — Project Context

This file is the always-loaded orientation. Deep detail lives in `docs/` and is pulled in on demand —
see [Documentation map](#documentation-map) below. **Before working on any game subsystem, read its
section in `docs/architecture.md` first** — it carries the confirmed facts *and* the dead-ends, so a
new mod touching a familiar subsystem won't re-tread a path we've already ruled out.

## Keeping this knowledge base current (standing instruction)
**Proactively maintain the docs as you work — you don't need to be asked.** Whenever you *confirm* a new
fact about the game, the IL2CPP interop, or a mod's behavior — especially **in-game-verified** facts and
**dead-ends** — record it before treating the task as done:
- game / interop / subsystem facts → the matching subsection of [`docs/architecture.md`](docs/architecture.md)
  (add a new subsection if that subsystem isn't there yet);
- mod-specific recipe/config → that mod's file under `docs/mods/`.

**Only record CONFIRMED learnings.** In-game-verified facts and definitively-established structure are fair
game; flag anything not yet verified as ⚠️ pending (the existing convention) and keep pure speculation out.
Date in-game findings (`confirmed in-game (YYYY-MM-DD)`). Update an existing entry rather than duplicating,
and always capture the **dead-ends**, not just what worked — the whole point is to stop a future session
re-treading a ruled-out path. These doc updates ride along with the related work when it's committed.

## Keep CLAUDE.md ↔ AGENTS.md In Sync (standing instruction)
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

## Git (false-negative warning)
This folder **IS** a git repository (`master`, remote `origin` → `https://github.com/simpsonbm1/askamods.git`).
The session-startup environment readout reports **"Is a git repository: false"** — that is a **false
negative** (a Windows detection bug, likely the space in the `D:\Claude Projects\...` path). Git works
normally here. Do not refuse or skip git operations because of that line; if unsure, verify with
`git rev-parse --is-inside-work-tree` (returns `true`) and proceed with commits/pushes as usual.

**Two machines — keep everything in `origin`.** The user works across a desktop and a laptop and syncs
only through the remote, so **nothing relevant is ever left purely local.** When work reaches a
committable state, commit *and push* it — source, the built `<Mod>.dll`, docs, configs — so the other
machine has it on next pull. The repo already tracks each mod's built DLL; only `bin/`, `obj/`, and
`*.save` stay gitignored. Don't strand files or knowledge on the current machine.

## Game
**ASKA** — co-op Viking survival/city-builder on Steam.
Install path: `D:\SteamLibrary\steamapps\common\ASKA`

## Mod Loader Stack
- **BepInEx 6.0.0** (IL2CPP build) — installed at `ASKA\BepInEx\`
- **HarmonyX** (0Harmony.dll) — bundled with BepInEx 6, used for runtime method patching
- **Il2CppInterop** — also bundled; generates C# wrapper assemblies from the native IL2CPP binary
- **.NET 10 SDK** — used to compile mods, targeting `net6.0`

## Build gotcha: Smart App Control blocks fresh DLLs (this machine)
Windows **Smart App Control is ENFORCED** here (`HKLM\SYSTEM\CurrentControlSet\Control\CI\Policy\VerifiedAndReputablePolicyState = 1`).
It intermittently **blocks a freshly-built, unsigned mod DLL** at load:
`System.IO.FileLoadException … An Application Control policy has blocked this file. (0x800711C7)`.
Because **.NET builds are deterministic**, rebuilding identical source yields the **same hash** → the
**same block**; relaunching won't help. **Fix: bump the mod version** (`PLUGIN_VERSION` + csproj `<Version>`)
so the DLL hash changes — SAC re-evaluates the new (unknown) hash and lets it load. Always confirm the
**loaded version** in `LogOutput.log` before trusting a test. Turning SAC off is permanent/irreversible — don't.

## Why IL2CPP Matters
ASKA ships as IL2CPP (not Mono). The game's C# code is compiled to native machine code in
`GameAssembly.dll`. BepInEx 6 generates interop wrapper assemblies on first launch at
`ASKA\BepInEx\interop\` (158 DLLs, including `Assembly-CSharp.dll`). Mods reference these interop
DLLs, not the original game DLLs. The interop layer has sharp edges — see
[IL2CPP interop gotchas](#il2cpp-interop-gotchas-apply-to-every-mod).

## Project Structure
```
askamods/
  CLAUDE.md                  ← this file (orientation + pointers)
  docs/                      ← detailed knowledge base (read on demand)
    architecture.md          ← how the game works + what doesn't (by subsystem)
    nexus-upload.md          ← publishing/CI workflow
    mods/                    ← one file per mod (shipped recipe + config)
  _explore/                  ← throwaway Mono.Cecil inspector scripts (not a mod)
  BowDamageMod/              ← Mod 1: buff early-game bow damage
  TreeRespawnMod/            ← Mod 2: respawn trees (stump condition) + gather resources (reeds, berries, etc.)
  HealthRegenMod/            ← Mod 3: regenerate player HP after 10s out of combat
  TorchFuelMod/              ← Mod 4: keep torches perpetually fueled (no resin chore)
  DynamicVillagerNeedsMod/   ← Mod 5: needs-based villager behavior (auto sleep/leisure/work, no manual schedule)
  VillagerFightBackMod/      ← Mod 7: villagers fight whitelisted enemies [COMPLETE v1.0.25]
  CookingStationFixMod/      ← Mod 8: read-only cooking-pipeline diagnostic (parked .dll.off; not shipped)
  SeedScoutMod/              ← Mod 9 (WIP tool): in-world seed scorer + in-game map overlay (caves/lakes/hostiles)
  WarpTourMod/               ← Mod 10: teleport-tour every POI so the game drops its OWN native map pins (master on/off switch)
  MineRefreshMod/            ← Mod 11: safe, on-demand mine/cave refresh (hotkey + safety proximity check) [COMPLETE v1.3.0]
```

Each mod is a separate `.csproj` that outputs its own `.dll` to `BepInEx\plugins\<ModName>\`.
The build target `CopyToPlugins` handles this automatically on build.

## IL2CPP interop gotchas (apply to every mod)
The recurring traps in the BepInEx 6 / Il2CppInterop layer — keep these in mind on *any* mod.
Full detail + per-subsystem dead-ends in [`docs/architecture.md`](docs/architecture.md#il2cpp-interop-gotchas-universal).
- **Don't subscribe to game `Action` events** (`_onNewDay`, `OnFullyHarvested`, etc.) — `Il2CppSystem.Action(methodRef)` only takes an `IntPtr` here. **Use a registered `MonoBehaviour` + `Update()` polling** (the `DayTracker`/`RegenTracker`/`TorchFuelTracker`/`NeedsController` pattern).
- **`FindObjectsByType<T>()` throws** `MissingMethodException` through the trampoline — get instances from a Harmony patch's `__instance`, not from a search.
- **No `.TryCast<T>()` on `UnityEngine.Object`** (its base chain is just `System.Object`) — obtain an already-typed instance from a patch parameter instead.
- **Key dictionaries by world position, not `UniqueId`** — `UniqueId`-style indices restart per spatial chunk and aren't globally unique.
- **Gate all state writes on authority** (`HasAuthority` / `_hasAuthority`) and **prefer the game's own RPCs** (`Rpc_AddFuel`, `Rpc_ChangeSchedule`) over direct networked-state writes — both for co-op safety.
- **Query persistent managers over ephemeral components** — some interactive components (like `CaveWallInteraction` walls) are destroyed and removed from the scene once depleted. To restore them, query their persistent parent managers (like `DigVolume`) which remain in the scene hierarchy, and invoke their native regeneration methods (e.g., `ResetWalls(true)`) to let the game rebuild the visual and physical objects correctly.

## Documentation map
| Read this | When you're working on |
|---|---|
| [`docs/architecture.md`](docs/architecture.md) | **Any** game subsystem — confirmed APIs + dead-ends, grouped: damage pipeline, player vs. creature, resource/tree, gather, structures/workstations, settlement hauling (Mod 6 groundwork), inventory/settlement/recipes, cooking station pipeline, torch/fire-fuel, villager needs/schedule/happiness, villager combat/fight-vs-flee |
| [`docs/mods/bow-damage.md`](docs/mods/bow-damage.md) | Mod 1 — BowDamageMod |
| [`docs/mods/tree-respawn.md`](docs/mods/tree-respawn.md) | Mod 2 — TreeRespawnMod |
| [`docs/mods/health-regen.md`](docs/mods/health-regen.md) | Mod 3 — HealthRegenMod |
| [`docs/mods/torch-fuel.md`](docs/mods/torch-fuel.md) | Mod 4 — TorchFuelMod |
| [`docs/mods/dynamic-villager-needs.md`](docs/mods/dynamic-villager-needs.md) | Mod 5 — DynamicVillagerNeedsMod |
| [`docs/mods/villager-fight-back.md`](docs/mods/villager-fight-back.md) | Mod 7 — VillagerFightBackMod |
| [`docs/mods/mine-refresh.md`](docs/mods/mine-refresh.md) | Mod 11 — MineRefreshMod |
| [`docs/nexus-upload.md`](docs/nexus-upload.md) | Publishing a mod update to Nexus Mods |
| [`TreeRespawnMod/STONE_RESPAWN_HANDOFF.md`](TreeRespawnMod/STONE_RESPAWN_HANDOFF.md) | Why mining/stone respawn was abandoned |
| [`DYNAMIC_HAULING_HANDOFF.md`](DYNAMIC_HAULING_HANDOFF.md) | Mod 6 — settlement hauling plan |
| [`WAREHOUSE_CAPACITY_HANDOFF.md`](WAREHOUSE_CAPACITY_HANDOFF.md) | Mod 12 (Planned) — warehouse capacity |
| [`DYNAMIC_NEEDS_HANDOFF.md`](DYNAMIC_NEEDS_HANDOFF.md) | DynamicVillagerNeedsMod handoff |
| [`VILLAGER_FIGHTBACK_HANDOFF.md`](VILLAGER_FIGHTBACK_HANDOFF.md) | Mod 7 — VillagerFightBackMod test run + fallback |
| [`SEED_SCOUT_HANDOFF.md`](SEED_SCOUT_HANDOFF.md) | Mod 9 — SeedScoutMod: worldgen findings, seed scorer + map overlay (WIP) |
| [`WARP_TOUR_HANDOFF.md`](WARP_TOUR_HANDOFF.md) | Mod 10 — WarpTourMod: teleport-tour POIs for native map pins (why cheap pins fail, tour design, tuning) |
| [`COOP_RESPAWN_HANDOFF.md`](COOP_RESPAWN_HANDOFF.md) | Mod 2 — co-op client respawn fix |

## Reference Paths
| Purpose | Path |
|---|---|
| BepInEx core DLLs | `ASKA\BepInEx\core\` |
| Game interop assemblies | `ASKA\BepInEx\interop\` |
| Unity engine modules | `ASKA\BepInEx\unity-libs\` |
| Plugin output folder | `ASKA\BepInEx\plugins\` |
| BepInEx log | `ASKA\BepInEx\LogOutput.log` |
