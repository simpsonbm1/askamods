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

**Only record CONFIRMED learnings.** Flag anything not yet verified as ⚠️ pending. Date in-game
findings (`confirmed in-game (YYYY-MM-DD)`). Always capture dead-ends — the whole point is to
stop future sessions re-treading a ruled-out path.

### Diagnostics Default Behavior
Whenever you add a configuration option for a diagnostic or debug logger, **always default it to `true` initially.** This saves the user from having to boot the game just to generate the config file, close it, edit it, and launch again. Once a mod is verified and ready to ship, update the code to flip the default to `false` so it doesn't spam normal users' logs. These doc updates ride along with the related work when it's committed.

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

**Rituals 1+3 are mechanically enforced at commit** by `.githooks/pre-commit` (one-time per clone:
`git config core.hooksPath .githooks` — set on the laptop 2026-07-05; the desktop must run it once).
It blocks commits on: mod folder missing from either file, stated version ≠ csproj `<Version>`,
PLUGIN_VERSION ≠ csproj `<Version>` (the SAC half-bump), doc missing from either Documentation Map,
duplicated `# AskaMods` header. Do NOT bypass with `--no-verify` — fix the drift; that's the point.

**Cadence — these rituals are commit-gated, NOT build-gated.** In an iterative build→test→build loop,
each cycle do only: edit code, bump the version (`PLUGIN_VERSION` + csproj `<Version>` — needed so Smart
App Control re-evaluates the new DLL hash and so the loaded version is confirmable), build, and convey
test steps in chat. **Defer the dual-write sync (Ritual 2), the integrity guard (Ritual 3), and any
handoff/`docs/` prose to the commit checkpoint, and do them once** — uncommitted edits don't reach the
other machine until a push, so nothing is lost. A full doc pass on every build is wasted token cost
(the user runs every task, including doc cleanup, on Opus).

If unsure whether a detail belongs in the orientation file vs. deeper docs, match what the other file
does — both stay parallel in scope and structure.

## Session Handoff Doc (standing instruction — see global convention)
This project follows the global session-handoff practice (`~/.claude/CLAUDE.md`): maintain a
`SESSION_HANDOFF.md` at repo root, kept continuously current during a session — updated right after
each meaningful step, not saved up for one big write at the end — as a safety net against Claude
Code's usage-limit hard-stops (which can cut a session off mid-action with no warning). Scoped to
askamods only; never mix in another project's content. Gitignored, not auto-committed — normal
commit policy still applies. If it exists at the start of a session, read it first, fold any durable
learnings into this project's own docs (`docs/architecture.md`, `docs/mods/*.md`, or the relevant
`*_HANDOFF.md`) per this project's own doc rules, then clear it once absorbed or the task is done.
Don't create it speculatively — only once there's real in-progress work worth protecting.

**Opted in to cross-machine WIP sync** (see `~/.claude/CLAUDE.md` → Session Handoff Continuity —
this exact phrase is the marker the `handoff-sync.ps1` hook greps for). On every `SESSION_HANDOFF.md`
update here, the PostToolUse hook automatically snapshots this repo's uncommitted changes (tracked +
untracked, respecting `.gitignore`) to a disposable `wip/<hostname>` branch on `origin` — no model
action, no confirmation. A narrow exception scoped **only** to that throwaway branch; the ask-first,
verified-only policy for `master` below is unchanged. At session start, act on the SessionStart
hook's report: `git cherry-pick --no-commit` an incoming `wip/<other-hostname>` branch (stop and ask
on conflict), then **delete the remote branch immediately after the cherry-pick succeeds** — the
content re-snapshots under this machine's hostname on the next handoff update.

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
`$ParkedByDefault` list (CookingStationFixMod, SeedHarvesterMod, TimeWarpMod) makes parked spikes install disabled
on a fresh machine — keep that list in step with mod parked-status changes (see Ritual 2).

## Model-tiered subagent delegation (Claude Code only)
Three project subagents live in `.claude/agents/` so a main session on an expensive model (Opus)
can delegate cheap, self-contained subtasks instead of doing everything inline — added 2026-07-06,
confirmed in real workflow 2026-07-06 (mod-implementer ×4 incl. a resume and correct SAC-guard refusal, log-analyst triage, doc-scribe checkpoint pass):
- **`mod-implementer`** (Sonnet) — a fully-planned code change: edit source, bump `PLUGIN_VERSION` + csproj `<Version>`, build, report.
- **`doc-scribe`** (Haiku) — doc-only work incl. the commit-checkpoint dual-write rituals.
- **`log-analyst`** (Haiku) — post-test-run `LogOutput.log` triage (loaded versions, exceptions, fire-verification markers).
**Delegate to them proactively when a task matches**; planning, research, and root-cause debugging
stay in the main thread. Delegation prompts must be fully self-contained — subagents load CLAUDE.md
but never see the conversation. Full recipe, invocation syntax, and gotchas:
[`docs/agent-delegation.md`](docs/agent-delegation.md). (Antigravity ignores `.claude/agents/` —
this mechanism is Claude Code-specific; in Antigravity, do these tasks inline as before.)

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
**loaded version** in `LogOutput.log` before trusting a test. Turning SAC off is permanent/irreversible — don't. **Mechanically
enforced since 2026-07-05:** `Directory.Build.targets` (repo root) fails any build whose `<Version>`
is already deployed live (`SAC BUMP GUARD`); a deliberate same-version rebuild needs
`-p:SkipSacGuard=true`. After deploy + game launch, run `.\check-loaded.ps1` for a one-table
repo/live/loaded version verdict per mod (SAC-safe: reads PE headers, never loads the DLLs).

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
  bisect-plugins.ps1         ← enable/disable live plugins for framerate/crash bisection (state-saving; -Restore reverts)
  docs/                      ← detailed knowledge base (read on demand)
    architecture.md          ← how the game works + what doesn't (by subsystem)
    nexus-upload.md          ← publishing/CI workflow
    mods/                    ← one file per mod (shipped recipe + config)
  _explore/                  ← throwaway Mono.Cecil inspector scripts (not a mod)
  BowDamageMod/              ← Mod 1: buff early-game bow damage            [COMPLETE v1.0.0 — docs/mods/bow-damage.md]
  TreeRespawnMod/            ← Mod 2: tree/gather respawn + constructed-well refill [COMPLETE v1.5.5 — per-frame work throttled for framerate (confirmed in-game 2026-07-07); trees no longer respawn up through player-built structures: durable BlockedPositions set + reusable StructureQuery footprint check ([TreeRespawn] BlockRespawnUnderStructures/StructureBlockMargin), confirmed in-game 2026-07-07; mushrooms year-round + rain-independent ([MushroomAvailability] config) 2026-07-07; same-world reload crash fixed 2026-07-06; well refill 2026-07-04; co-op detection 2026-07-03 — docs/mods/tree-respawn.md + TREERESPAWN_HANDOFF.md]
  HealthRegenMod/            ← Mod 3: regenerate player/villager HP out of combat [COMPLETE v1.3.1 — villager/soldier passive regen CONFIRMED IN-GAME (2026-07-08; `Character.CurrentHealth` writes stick on villagers); v1.3.x gives villagers their OWN regen rates (`[VillagerRegen] HealPerTick/SecondsPerTick/OutOfCombatSeconds`, independent of the player's `[HealthRegen]`) + `[VillagerRegen] ApplyToVillagers`; whole mod throttled to every 4th frame (~15 Hz) — docs/mods/health-regen.md]
  TorchFuelMod/              ← Mod 4: keep torches perpetually fueled       [COMPLETE v1.2.4 — docs/mods/torch-fuel.md]
  DynamicVillagerNeedsMod/   ← Mod 5: needs-based villager behavior         [COMPLETE v1.4.4 — per-frame work throttled for framerate (confirmed in-game 2026-07-07); idea-11 Phase 0 diagnostics verified in-game 2026-07-09 (hour source = floor(WeatherSystem.TimeOfDay), slot k == clock hour k; player-edit detection = _schedule array diff — packed __NetworkedSchedule is transient, never pulses on UI edits); v1.4.x = Phase 1 [DynamicNeeds] RespectManualSchedule (default false): 2+-cohort villagers with mixed painted schedules man their painted Work hours, one back-loaded rest-boosted top-up sleep per off-window + leisure fill, write-silent Manual baseline, emergency-only off-post ⚠️ pending in-game confirmation (v1.4.1 flap → v1.4.4 back-load iterated in-game 2026-07-09) — docs/mods/dynamic-villager-needs.md]
  VillagerFightBackMod/      ← Mod 7: villagers fight whitelisted enemies   [COMPLETE v1.0.29 — per-frame work throttled for framerate (confirmed in-game 2026-07-07) — docs/mods/villager-fight-back.md]
  CookingStationFixMod/      ← Mod 8: read-only cooking-pipeline diagnostic [PARKED v0.2.0, .dll.off, not shipped]
  SeedScoutMod/              ← Mod 9: reveal ALL native POI map pins at world load, no teleport [COMPLETE v1.3.1 — per-frame work throttled for framerate (confirmed in-game 2026-07-07) — docs/mods/seed-scout.md]
  WarpTourMod/               ← Mod 10: teleport-tour for native map pins    [v1.0.0 — SUPERSEDED by SeedScout for pin reveal; keep for the PlayerDrive.Teleport primitive — WARP_TOUR_HANDOFF.md]
  MineRefreshMod/            ← Mod 11: safe, on-demand mine/cave refresh    [COMPLETE v1.3.1 — docs/mods/mine-refresh.md]
  JotunBloodYieldMod/        ← Mod 13: increases jotun blood yields         [COMPLETE v1.1.0 — docs/mods/jotun-blood-yield.md]
  SeedHarvesterMod/          ← Mod 14: fast in-memory seed-scan experiment  [PARKED v0.16.0, .dll.off, blocked — SEED_HARVESTER_HANDOFF.md]
  TerrainLevelerMod/         ← Mod 15: "Bulldozer Field" instant-flatten build-menu square [COMPLETE v1.5.0 — co-op fix confirmed in-game 2026-07-03 — docs/mods/terrain-leveler.md]
  ResourceMarkerRadiusMod/   ← Mod 16: configurable radii for markers       [WIP v1.1.2 — scaling confirmed in-game 2026-06-30; some markers fall back when resolve fails — MAP_RADIUS_HANDOFF.md]
  TaskUnlockerMod/           ← Mod 17: unlock all cooking + fishing tasks   [COMPLETE v1.2.2 — per-frame work throttled for framerate (confirmed in-game 2026-07-07) — docs/mods/task-unlocker.md]
  ZeroTaskWorkersMod/        ← Mod 18: newly assigned workers inherit zero tasks [COMPLETE v1.0.1 — per-frame work throttled for framerate (confirmed in-game 2026-07-07) — docs/mods/zero-task-workers.md]
  GroundItemVacuumMod/       ← Mod 19: hotkey/auto vacuum for loose ground items [COMPLETE v1.1.0 — v1.1.0 live config hot-reload (cfg re-read every 5 s in-session, hotkey re-binds on change; SeedScout pattern), confirmed in-game 2026-07-08; v1.0.2 default hotkey changed from V to N (V conflicted with emote wheel), confirmed in-game 2026-07-08. Removes debris cleanly (own-set OnEnable/OnDisable tracking + RemoveObjectFromWorld), no crash. Ground clutter was NOT the framerate bottleneck (a loaded mod is — bisect pending) — docs/mods/ground-item-vacuum.md]
  FishFilletMod/             ← Mod 20: fillet fish directly in the inventory via Shift+Right-click [COMPLETE v1.1.1 — Shift+RMB a raw fish in inventory fillets it through the game's OWN harvest (temp-null the 'Knives' tool req → drive ItemThumbnailPanel.CommandHarvestItem → restore), confirmed in-game 2026-07-08. Trigger = OnPointerClick PREFIX gated on button==Right + Shift, returns false to suppress the native right-click; Shift+RMB deliberately dodges the native Shift+LMB harvest router that pops the cosmetic "can't be harvested" toast. Generic (any ResourceInfo item that CanBeHarvested and requires a configured tool category — default Knives, config HarvestToolCategories); yield delegated to the game so it always matches drop-and-skin (validated across Mackerel whole-harvest + Seabass bit-harvest loot models) — docs/mods/fish-fillet.md]
  DenRespawnMod/             ← Mod 21: hotkey/map-pin/timed revive for defeated monster dens [COMPLETE v1.2.2 — map-pin Shift+click revive + pin recolor, timed auto-respawn day rule, defeat-day reload persistence, natural-respawn suppression all confirmed in-game (2026-07-09); stale-registry timer guard (v1.2.1) ⚠️ pending in-game confirmation — docs/mods/den-respawn.md]
  TimeWarpMod/               ← Mod 22: dev/test time accelerator (K=native fast-forward cycle to 162x, L=skip day) [DEV TOOL v0.1.0 — confirmed in-game 2026-07-09; NOT for Nexus — docs/mods/time-warp.md]
```

> **SeedHarvesterMod (Mod 14)** is a parked spike (patch commented out, installed DLL renamed
> `.dll.off` 2026-06-28): cave positions can't be read because `UpdateDataAsync` never instantiates
> cave `AreaInstance` GameObjects — full dead-end evidence in `SEED_HARVESTER_HANDOFF.md`.

Each mod is a separate `.csproj` that outputs its own `.dll` to `BepInEx\plugins\<ModName>\`.
The build target `CopyToPlugins` handles this automatically on build.

## IL2CPP interop gotchas (apply to every mod)
The recurring traps in the BepInEx 6 / Il2CppInterop layer — keep these in mind on *any* mod.
Full detail + per-subsystem dead-ends in [`docs/architecture.md`](docs/architecture.md#il2cpp-interop-gotchas-universal).
- **Don't subscribe to game `Action` events** (`_onNewDay`, `OnFullyHarvested`, etc.) — `Il2CppSystem.Action(methodRef)` only takes an `IntPtr` here. **Use a registered `MonoBehaviour` + `Update()` polling** (the `DayTracker`/`RegenTracker`/`TorchFuelTracker`/`NeedsController` pattern).
- **`FindObjectsByType<T>()` throws** `MissingMethodException` through the trampoline — get instances from a Harmony patch's `__instance`, not from a search. (`FindAnyObjectByType<T>()` — singular — works.)
- **The plural generic `GetComponentsInChildren<T>(bool)` is missing too** (same `MissingMethodException` family); the singular `GetComponentInChildren<T>(bool)` works. To collect all matches, walk the hierarchy manually with per-object `GetComponent<T>()` (MineRefreshMod / TreeRespawn `WellRefill` pattern).
- **`SettlementManager.settlements` stays null even in a loaded world** — resolve settlements via `GetPlayerSettlement()` / `GetCurrentSettlement()` / `worldSettlement` instead.
- **Don't patch IL2CPP methods with by-ref primitive params** (`Single&`, `Int32&`, `Boolean&`) — trampoline NREs outside try/catch. Patch a sibling method with a safe signature instead.
- **Don't patch `Initialize`/lifecycle methods on `Interaction` MonoBehaviours** — they fire during prefab init before the GC handle is set up → `Handle is not initialized` crash. (This is the cause of the old TreeRespawn co-op crash.)
- **No `.TryCast<T>()` on `UnityEngine.Object`** (its base chain is just `System.Object`) — obtain an already-typed instance from a patch parameter instead. **Root cause is compile-time only** (csprojs reference the `unity-libs` CoreModule stub; the runtime loads the interop copy whose chain reaches `Il2CppObjectBase`) — so `(object)x is Il2CppObjectBase b` works at runtime, giving `b.Pointer` for native class checks / `new T(IntPtr)` rewraps (confirmed in-game 2026-07-04, SeedScout). The same stub-chain problem hits ANY game type whose base chain passes through a unity-libs stub (e.g. `SSSGame.Workstation` → … → MonoBehaviour): CS1503 when passing to an `Il2CppObjectBase` param; the same `(object)x is Il2CppObjectBase b` boxing pattern fixes it (build-verified 2026-07-06, ZeroTaskWorkers).
- **Don't interpolate Unity structs (Vector3/Vector4/…) into direct `Logger.Log*($"…")` calls** — BepInEx's interpolated-string handler throws `VerificationException` at the log site (silently killed SeedScout v1.2.0's island resolver). `.ToString()` struct args first, or route through a `(string)`-typed wrapper method.
- **Key dictionaries by world position, not `UniqueId`** — `UniqueId`-style indices restart per spatial chunk and aren't globally unique.
- **Gate all state writes on authority** (`HasAuthority` / `_hasAuthority`) and **prefer the game's own RPCs** (`Rpc_AddFuel`, `Rpc_ChangeSchedule`) over direct networked-state writes — both for co-op safety.
- **Query persistent managers over ephemeral components** — some interactive components (like `CaveWallInteraction` walls) are destroyed and removed from the scene once depleted. To restore them, query their persistent parent managers (like `DigVolume`) which remain in the scene hierarchy, and invoke their native regeneration methods (e.g., `ResetWalls(true)`) to let the game rebuild the visual and physical objects correctly.
- **Never patch a `NetworkBehaviour`'s `CopyBackingFieldsToState`/`CopyStateToBackingFields`** (Fusion state-sync) — hangs the game at load, no exception. Capture instances from a plain lifecycle method (`Awake`/`Spawned`) into a static list instead.
- **Managed `as`/`is` casts LIE for interop objects materialized under a base declared type** (list elements, base-typed params) — the wrapper IS the declared type, so the cast returns null even when the native object is the derived type. Identify by asset `name`/`id` or native class name (`IL2CPP.il2cpp_object_get_class` + `il2cpp_class_get_name`), then construct the derived wrapper via `new T(IntPtr)`.
- **The IL2CPP AOT compiler inlines small/single-caller methods — patches on them silently never fire.** Prefer virtual/vtable-dispatched methods (`Init`, `Show`, `Use`) or multi-caller publics, and always fire-verify a new patch with a log line.
- **Never cache interop wrappers of per-world native objects across world sessions** (managers, handlers, instance registries). Quit-to-menu → reload of the SAME world does NOT change `StorageManager.ActiveSessionID`, so same-ID checks won't clear caches — and reads through a stale wrapper AV in native code (no managed exception; try/catch can't help), killing the process via a CLR fatal error whose WER signature is always `coreclr.dll+0x1d1fdd`. Detect world-leave via `ActiveSessionID` becoming empty and drop all per-world state (TreeRespawn v1.4.5 `NoteWorldLeft` pattern, confirmed in-game 2026-07-06). Full crash-forensics recipe: docs/architecture.md → Native Crash Diagnosis.
- **If a mod hard-crashes the game natively with no managed exception in the log, don't guess** — map the Windows Error Reporting crash offset to a method with Cpp2IL. See [Native Crash Diagnosis](docs/architecture.md#native-crash-diagnosis-wer--cpp2il).

## Documentation map
| Read this | When you're working on |
|---|---|
| [`docs/architecture.md`](docs/architecture.md) | **Any** game subsystem — confirmed APIs + dead-ends, grouped: damage pipeline, player vs. creature, resource/tree, gather, structures/workstations, settlement hauling (Mod 6 groundwork), inventory/settlement/recipes, cooking station pipeline, torch/fire-fuel, villager needs/schedule/happiness, villager combat/fight-vs-flee, build menu/structure templates/localization, terrain/terraforming, dens/population spawners, native crash diagnosis (WER+Cpp2IL) |
| [`docs/mods/bow-damage.md`](docs/mods/bow-damage.md) | Mod 1 — BowDamageMod |
| [`docs/mods/tree-respawn.md`](docs/mods/tree-respawn.md) | Mod 2 — TreeRespawnMod (incl. the v1.4.x constructed-well refill recipe, the v1.4.7 mushroom year-round/rain-independent recipe + the v1.5.x block-respawn-under-structures recipe: durable BlockedPositions + StructureQuery footprint check) |
| [`NEW_MOD_IDEAS_PLAN.md`](NEW_MOD_IDEAS_PLAN.md) | Researched mod ideas (2026-07-03, +7–8 on 2026-07-06, +10 on 2026-07-07, +11 on 2026-07-08): well refill (✔ shipped, TreeRespawn v1.4.x), den respawn (Den.Revive + structure-blocking insight), crafting multiplier (BlueprintInfo.quantity data edit), ground-item vacuum (DynamicItemObjectManager), freezing hunters (warmth objective thresholds), pre-construction worker/task setup (Structure.Activate + buildsite-aware WorkstationMenu), zero-task new workers (_CanAddVillagerToTaskData gate), inventory fish filleting (ResourceInfo.exhaustableComponents + ItemThumbnailPanel.CanHarvestCurrentItem gate), opt-in manual-schedule/coverage-staggering mode for DynamicVillagerNeeds (RespectManualSchedule; same-station-cohort off-window sleep confinement preserves 24/7 phase separation + off-window builder-fill; reuses the mod's existing schedule snapshot + Rpc_ChangeSchedule) — approaches + Cecil-confirmed API leads |
| [`docs/mods/health-regen.md`](docs/mods/health-regen.md) | Mod 3 — HealthRegenMod |
| [`docs/mods/torch-fuel.md`](docs/mods/torch-fuel.md) | Mod 4 — TorchFuelMod |
| [`docs/mods/dynamic-villager-needs.md`](docs/mods/dynamic-villager-needs.md) | Mod 5 — DynamicVillagerNeedsMod |
| [`docs/mods/villager-fight-back.md`](docs/mods/villager-fight-back.md) | Mod 7 — VillagerFightBackMod |
| [`docs/mods/mine-refresh.md`](docs/mods/mine-refresh.md) | Mod 11 — MineRefreshMod |
| [`docs/mods/jotun-blood-yield.md`](docs/mods/jotun-blood-yield.md) | Mod 13 — JotunBloodYieldMod |
| [`docs/mods/terrain-leveler.md`](docs/mods/terrain-leveler.md) | Mod 15 — TerrainLevelerMod (shipped recipe: bulldozer menu square + per-template gating + HeightmapTool flatten + AOESpell clear + BitSet256 tile-cap fix) |
| [`TerrainLevelerMod/BULLDOZER_UI_PLAN.md`](TerrainLevelerMod/BULLDOZER_UI_PLAN.md) | Mod 15 — the bulldozer build-menu square: design, v1.4.0→v1.4.8 evidence chain, build-menu/localization ground truth (COMPLETE 2026-07-01) |
| [`TerrainLevelerMod/DRAG_CRASH_PLAN.md`](TerrainLevelerMod/DRAG_CRASH_PLAN.md) | Mod 15 — TerrainLevelerMod drag-crash root cause (BitSet256 network-state overflow) + fix, confirmed in-game 2026-07-01 |
| [`TerrainLevelerMod/TERRAIN_DRAG_HANDOFF.md`](TerrainLevelerMod/TERRAIN_DRAG_HANDOFF.md) | Mod 15 — TerrainLevelerMod drag crash handoff (2026-07-01) — SUPERSEDED, see DRAG_CRASH_PLAN.md |
| [`TERRAIN_LEVELER_HANDOFF.md`](TERRAIN_LEVELER_HANDOFF.md) | Mod 15 — early (v1.3.12) session handoff — SUPERSEDED by docs/mods/terrain-leveler.md + the TerrainLevelerMod/*.md plans |
| [`docs/mods/seed-scout.md`](docs/mods/seed-scout.md) | Mod 9 — SeedScoutMod (shipped recipe: native-pin MarkerItem system, handler construction, targeted tile sweep, home-island scope, POI-type classifier) |
| [`docs/mods/resource-marker-radius.md`](docs/mods/resource-marker-radius.md) | Mod 16 — ResourceMarkerRadiusMod |
| [`docs/mods/task-unlocker.md`](docs/mods/task-unlocker.md) | Mod 17 — TaskUnlockerMod (cooking = Rpc_AddDiscoverable; fishing = mark grounds via the NWDM Request path — NOT item discovery) |
| [`docs/mods/zero-task-workers.md`](docs/mods/zero-task-workers.md) | Mod 18 — ZeroTaskWorkersMod (prefix-block on the four _CanAddVillagerToTaskData impls; deserialize-grace gate; Buildstation exemption) |
| [`docs/mods/ground-item-vacuum.md`](docs/mods/ground-item-vacuum.md) | Mod 19 — GroundItemVacuumMod (own-set tracking via DynamicItemObject.OnEnable/OnDisable + WorldItemObject.RemoveObjectFromWorld; the v1.0.0 raw `_head`→`NextDynamicObject` linked-list walk = native-AV dead-end; incl. the framerate finding: ground clutter was NOT the bottleneck) |
| [`docs/mods/fish-fillet.md`](docs/mods/fish-fillet.md) | Mod 20 — FishFilletMod (Shift+RMB inventory filleting: OnPointerClick prefix gesture + temp-null tool-req + CommandHarvestItem drive; why Shift+RMB dodges the native Shift+LMB harvest-router toast; dead-ends: CanHarvestCurrentItem-flip-alone, CommandHarvestItem patch, persistent bare-hand) |
| [`docs/mods/den-respawn.md`](docs/mods/den-respawn.md) | Mod 21 — DenRespawnMod (hotkey revive: multi-lever refresh via spawner-state detection, instant repopulation, structure-block bypass) |
| [`docs/mods/time-warp.md`](docs/mods/time-warp.md) | Mod 22 — TimeWarpMod |
| [`docs/nexus-upload.md`](docs/nexus-upload.md) | Publishing to Nexus Mods |
| [`docs/agent-delegation.md`](docs/agent-delegation.md) | Delegating subtasks to cheaper-model subagents (Claude Code only): agent roster (`.claude/agents/`), delegation-prompt checklist, invocation syntax, runtime gotchas |
| [`TreeRespawnMod/STONE_RESPAWN_HANDOFF.md`](TreeRespawnMod/STONE_RESPAWN_HANDOFF.md) | Why mining/stone respawn was abandoned |
| [`DYNAMIC_HAULING_HANDOFF.md`](DYNAMIC_HAULING_HANDOFF.md) | Mod 6 — settlement hauling plan |
| [`WAREHOUSE_CAPACITY_HANDOFF.md`](WAREHOUSE_CAPACITY_HANDOFF.md) | Mod 12 (Planned) — warehouse capacity |
| [`DYNAMIC_NEEDS_HANDOFF.md`](DYNAMIC_NEEDS_HANDOFF.md) | DynamicVillagerNeedsMod handoff |
| [`VILLAGER_FIGHTBACK_HANDOFF.md`](VILLAGER_FIGHTBACK_HANDOFF.md) | Mod 7 — VillagerFightBackMod test run + fallback |
| [`SEED_SCOUT_HANDOFF.md`](SEED_SCOUT_HANDOFF.md) | Mod 9 — old scorer/overlay-era handoff (worldgen findings, force-load addressing, den classification data, seed-read dead-end) — SUPERSEDED by docs/mods/seed-scout.md for the shipped mod; still the reference for the removed scorer + den-tier data |
| [`WARP_TOUR_HANDOFF.md`](WARP_TOUR_HANDOFF.md) | Mod 10 — WarpTourMod: teleport-tour POIs for native map pins — SUPERSEDED by SeedScout v1.x (pins without teleport); keep for the PlayerDrive.Teleport recipe |
| [`TREERESPAWN_HANDOFF.md`](TREERESPAWN_HANDOFF.md) | Mod 2 — TreeRespawn bug tracker & handoff (co-op respawn, cross-world save fix, issues C/D RESOLVED v1.2.10, parked issue E, plus tracked-but-out-of-scope Issue F — a vanilla villager-AI fiber lockout) |
| [`SEED_HARVESTER_HANDOFF.md`](SEED_HARVESTER_HANDOFF.md) | Mod 14 — SeedHarvesterMod: fast in-memory seed scan (blocked — see dead-ends) |
| [`ResourceMarkerRadiusMod/MAP_RADIUS_HANDOFF.md`](ResourceMarkerRadiusMod/MAP_RADIUS_HANDOFF.md) | Mod 16 — ResourceMarkerRadiusMod map marker radius debugging handoff |

## Reference Paths
| Purpose | Path |
|---|---|
| BepInEx core DLLs | `ASKA\BepInEx\core\` |
| Game interop assemblies | `ASKA\BepInEx\interop\` |
| Unity engine modules | `ASKA\BepInEx\unity-libs\` |
| Plugin output folder | `ASKA\BepInEx\plugins\` |
| BepInEx log | `ASKA\BepInEx\LogOutput.log` |
