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

## Never Consider an Issue Resolved Until Confirmed In-Game (standing instruction)
Do not mark any issue as RESOLVED or CLOSED in orientation files or handoff documents until the user has explicitly confirmed it is fixed in-game. Until then, use terms like FIX ATTEMPTED or PENDING CONFIRMATION.

## Keep CLAUDE.md ↔ AGENTS.md In Sync — and VERIFY it, don't just intend to (standing instruction)
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

## Git (commit/push policy + false-negative warning)
This folder **IS** a git repository (`master`, remote `origin` → `https://github.com/simpsonbm1/askamods.git`).
The session-startup environment readout reports **"Is a git repository: false"** — that is a **false
negative** (a Windows detection bug, likely the space in the `D:\Claude Projects\...` path). Git works
normally here. Do not refuse or skip git operations because of that line; if unsure, verify with
`git rev-parse --is-inside-work-tree` (returns `true`).

**Ask before you commit or push — do NOT do it automatically.** The user decides when history is
written. Commit/push only:
- **after a verified success** — a change actually tested and confirmed working in-game (*not* merely
  because code compiled or a file was edited), **or**
- at a **general end-of-session** checkpoint,

and in **both** cases **confirm with the user first.** Never commit or push work-in-progress or
unverified changes just because files changed. (Pushing after every edit — Antigravity's habit — is
exactly what to avoid.)

**Two machines — but sync at those gated moments, not continuously.** The user works across a desktop
and a laptop and syncs only through `origin`. So when you *do* commit at a verified-success or
end-of-session checkpoint (with the user's go-ahead), **push** it too — source, the built `<Mod>.dll`,
docs, configs — so the other machine has it on next pull. The repo already tracks each mod's built DLL;
only `bin/`, `obj/`, and `*.save` stay gitignored. Don't strand files or knowledge on the current
machine — but don't pre-empt the user's go-ahead to push, either.

## Syncing live plugins from git (helper script)
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
  sync-plugins.ps1           ← push committed mod DLLs → live BepInEx\plugins (see "Syncing live plugins from git")
  docs/                      ← detailed knowledge base (read on demand)
    architecture.md          ← how the game works + what doesn't (by subsystem)
    nexus-upload.md          ← publishing/CI workflow
    mods/                    ← one file per mod (shipped recipe + config)
  _explore/                  ← throwaway Mono.Cecil inspector scripts (not a mod)
  BowDamageMod/              ← Mod 1: buff early-game bow damage
  TreeRespawnMod/            ← Mod 2: respawn trees (stump condition) + gather resources (reeds, berries, etc.) [v1.2.20 — moved noisy init diagnostics to their own config toggle; v1.2.19 fixed stale pointer false-positives for gather nodes (Issues C/D resolved); Issue A pending confirmation. See TREERESPAWN_HANDOFF.md]
  HealthRegenMod/            ← Mod 3: regenerate player HP after 10s out of combat
  TorchFuelMod/              ← Mod 4: keep torches perpetually fueled (no resin chore)
  DynamicVillagerNeedsMod/   ← Mod 5: needs-based villager behavior (auto sleep/leisure/work, no manual schedule)
  VillagerFightBackMod/      ← Mod 7: villagers fight whitelisted enemies [COMPLETE v1.0.25]
  CookingStationFixMod/      ← Mod 8: read-only cooking-pipeline diagnostic (parked .dll.off; not shipped)
  SeedScoutMod/              ← Mod 9: seed scorer + map overlay           [WIP v0.15.0]
  WarpTourMod/               ← Mod 10: teleport-tour for native map pins  [WORKING v1.0.0]
  MineRefreshMod/            ← Mod 11: safe, on-demand mine/cave refresh  [COMPLETE v1.3.1]
  JotunBloodYieldMod/        ← Mod 13: increases jotun blood yields       [COMPLETE v1.1.0]
  SeedHarvesterMod/          ← Mod 14: fast in-memory seed-scan experiment [PARKED — patch disabled, blocked; installed .dll renamed to .dll.off 2026-06-28]
```

> **SeedHarvesterMod (Mod 14)** is a parked spike: its "Fast Harvest" coroutine regenerates seeds
> in-memory in seconds, but every seed scores `-9999` because cave `AreaInstance` GameObjects are
> never instantiated by `UpdateDataAsync`, so cave positions can't be read (a 1-frame `yield` does
> not force instantiation — dead-end confirmed 2026-06-28). The Harmony patch is commented out, and
> as of 2026-06-28 the installed plugin DLL is also renamed to `SeedHarvesterMod.dll.off` (same
> convention as CookingStationFixMod) so BepInEx doesn't load it at all. Reading positions would
> require dumping `GameAssembly.dll` to parse the raw data buffers. See `SEED_HARVESTER_HANDOFF.md`.

Each mod is a separate `.csproj` that outputs its own `.dll` to `BepInEx\plugins\<ModName>\`.
The build target `CopyToPlugins` handles this automatically on build.

## IL2CPP interop gotchas (apply to every mod)
The recurring traps in the BepInEx 6 / Il2CppInterop layer — keep these in mind on *any* mod.
Full detail + per-subsystem dead-ends in [`docs/architecture.md`](docs/architecture.md#il2cpp-interop-gotchas-universal).
- **Don't subscribe to game `Action` events** (`_onNewDay`, `OnFullyHarvested`, etc.) — `Il2CppSystem.Action(methodRef)` only takes an `IntPtr` here. **Use a registered `MonoBehaviour` + `Update()` polling** (the `DayTracker`/`RegenTracker`/`TorchFuelTracker`/`NeedsController` pattern).
- **`FindObjectsByType<T>()` throws** `MissingMethodException` through the trampoline — get instances from a Harmony patch's `__instance`, not from a search.
- **Don't patch IL2CPP methods with by-ref primitive params** (`Single&`, `Int32&`, `Boolean&`) — trampoline NREs outside try/catch. Patch a sibling method with a safe signature instead.
- **Don't patch `Initialize`/lifecycle methods on `Interaction` MonoBehaviours** — they fire during prefab init before the GC handle is set up → `Handle is not initialized` crash. (This is the cause of the old TreeRespawn co-op crash.)
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
| [`docs/mods/jotun-blood-yield.md`](docs/mods/jotun-blood-yield.md) | Mod 13 — JotunBloodYieldMod |
| [`docs/nexus-upload.md`](docs/nexus-upload.md) | Publishing to Nexus Mods |
| [`TreeRespawnMod/STONE_RESPAWN_HANDOFF.md`](TreeRespawnMod/STONE_RESPAWN_HANDOFF.md) | Why mining/stone respawn was abandoned |
| [`DYNAMIC_HAULING_HANDOFF.md`](DYNAMIC_HAULING_HANDOFF.md) | Mod 6 — settlement hauling plan |
| [`WAREHOUSE_CAPACITY_HANDOFF.md`](WAREHOUSE_CAPACITY_HANDOFF.md) | Mod 12 (Planned) — warehouse capacity |
| [`DYNAMIC_NEEDS_HANDOFF.md`](DYNAMIC_NEEDS_HANDOFF.md) | DynamicVillagerNeedsMod handoff |
| [`VILLAGER_FIGHTBACK_HANDOFF.md`](VILLAGER_FIGHTBACK_HANDOFF.md) | Mod 7 — VillagerFightBackMod test run + fallback |
| [`SEED_SCOUT_HANDOFF.md`](SEED_SCOUT_HANDOFF.md) | Mod 9 — SeedScoutMod: worldgen findings, seed scorer + map overlay (WIP) |
| [`WARP_TOUR_HANDOFF.md`](WARP_TOUR_HANDOFF.md) | Mod 10 — WarpTourMod: teleport-tour POIs for native map pins (why cheap pins fail, tour design, tuning) |
| [`TREERESPAWN_HANDOFF.md`](TREERESPAWN_HANDOFF.md) | Mod 2 — TreeRespawn bug tracker & handoff (co-op respawn, cross-world save fix, issues C/D RESOLVED v1.2.10, parked issue E, plus tracked-but-out-of-scope Issue F — a vanilla villager-AI fiber lockout) |
| [`SEED_HARVESTER_HANDOFF.md`](SEED_HARVESTER_HANDOFF.md) | Mod 14 — SeedHarvesterMod: fast in-memory seed scan (blocked — see dead-ends) |

## Reference Paths
| Purpose | Path |
|---|---|
| BepInEx core DLLs | `ASKA\BepInEx\core\` |
| Game interop assemblies | `ASKA\BepInEx\interop\` |
| Unity engine modules | `ASKA\BepInEx\unity-libs\` |
| Plugin output folder | `ASKA\BepInEx\plugins\` |
| BepInEx log | `ASKA\BepInEx\LogOutput.log` |
