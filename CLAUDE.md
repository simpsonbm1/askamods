# AskaMods — Project Context

This file is the always-loaded orientation. Deep detail lives in `docs/` and is pulled in on demand —
see [Documentation map](#documentation-map) below. **Before working on any game subsystem, read its
section in `docs/architecture.md` first** — it carries the confirmed facts *and* the dead-ends, so a
new mod touching a familiar subsystem won't re-tread a path we've already ruled out.
(Root [`AGENTS.md`](AGENTS.md) is the bootstrap for NON-Claude agents — a thin pointer here plus
the working agreement and tool-specific caveats; it is not a third orientation copy to sync.)

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

### Doc update style — stateless, de-accreted (pattern locked in by the 2026-07-12 doc audit)
Docs record **current truth, not the journey**. These rules exist because the pre-audit docs had
decayed into chronological fact→correction chains a reader had to replay to learn what's true now:
- **Write stateless.** A `docs/mods/*.md` file is: current design/recipe + config reference +
  per-mod dead-ends + a compressed version-history list. Never a chronological changelog. When a
  new version changes a fact, rewrite the fact in place — don't append a correction beneath the
  stale statement.
- **Supersede in place** (architecture.md included). A finding that invalidates older text updates
  or deletes that text in the same edit — no dangling "superseded by X below" chains. Clear a
  ⚠️ pending flag the moment its fact is confirmed; never leave both states in the file.
- **Collapse finished ideas-plan entries to pointers.** When a NEW_MOD_IDEAS_PLAN idea (or a phase
  of one) ships or is ruled out, collapse the finished portion to a one-liner pointing at the
  mod's doc, keeping only still-open phases/research in the body. Never leave a body that still
  recommends an approach later proven fatal.
- **Archive absorbed handoffs.** Once a handoff/investigation doc's live conclusions are absorbed
  into architecture.md / docs/mods/, `git mv` it to `docs/archive/` and fix all cross-references.
  Orientation files reference archives only via the Documentation map's archive row.
- **Wrap doc lines at ≤100 chars.** Mega-lines silently break ripgrep-based doc recall
  ("[Omitted long matching line]"). Verify after a doc pass: `grep -rnE '.{161,}' docs/` must
  return only justifiable hits (tables, URLs) — rewrap any prose it finds.
- Dead-ends and dated confirmations still accumulate forever — that's the knowledge base's point.
  What gets pruned is narrative accretion: the journey, the corrections, the superseded plans.

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
    mods/                    ← one file per mod (current recipe + config + dead-ends)
    archive/                 ← superseded handoffs/investigation logs (history only, never orientation)
  _explore/                  ← throwaway Mono.Cecil inspector scripts (not a mod)
  BowDamageMod/              ← Mod 1: buff early-game bow damage [COMPLETE v1.0.0 — docs/mods/bow-damage.md]
  TreeRespawnMod/            ← Mod 2: tree/gather respawn, well refill, year-round mushrooms, stump protection [COMPLETE v1.6.1 — v1.6.x perf hardening ⚠️ pending in-game confirmation — docs/mods/tree-respawn.md]
  HealthRegenMod/            ← Mod 3: player + villager out-of-combat HP regen, per-villager rates [COMPLETE v1.3.1 — docs/mods/health-regen.md]
  TorchFuelMod/              ← Mod 4: keep torches perpetually fueled [COMPLETE v1.2.5 — docs/mods/torch-fuel.md]
  DynamicVillagerNeedsMod/   ← Mod 5: needs-based villager behavior + opt-in manual-schedule mode [COMPLETE v1.9.7 — docs/mods/dynamic-villager-needs.md]
  VillagerFightBackMod/      ← Mod 7: villagers fight whitelisted enemies [COMPLETE v1.0.30, on Nexus — docs/mods/villager-fight-back.md]
  CookingStationFixMod/      ← Mod 8: read-only cooking-pipeline diagnostic [PARKED v0.2.0, .dll.off, not shipped]
  SeedScoutMod/              ← Mod 9: reveal ALL native POI map pins at world load, no teleport [COMPLETE v1.3.1 — docs/mods/seed-scout.md]
  WarpTourMod/               ← Mod 10: teleport-tour for native map pins [v1.0.0 — SUPERSEDED by SeedScout; keep for the PlayerDrive.Teleport primitive — docs/archive/WARP_TOUR_HANDOFF.md]
  MineRefreshMod/            ← Mod 11: safe, on-demand mine/cave refresh [COMPLETE v1.3.4 — docs/mods/mine-refresh.md]
  JotunBloodYieldMod/        ← Mod 13: increases jotun blood yields [COMPLETE v1.1.0 — docs/mods/jotun-blood-yield.md]
  SeedHarvesterMod/          ← Mod 14: fast in-memory seed-scan experiment [PARKED v0.16.0, .dll.off, blocked — SEED_HARVESTER_HANDOFF.md]
  TerrainLevelerMod/         ← Mod 15: "Bulldozer Field" instant-flatten build-menu square [COMPLETE v1.5.0 — docs/mods/terrain-leveler.md]
  ResourceMarkerRadiusMod/   ← Mod 16: configurable radii for markers [WIP v1.1.2 — some markers fall back when resolve fails — MAP_RADIUS_HANDOFF.md]
  TaskUnlockerMod/           ← Mod 17: unlock cooking recipes, fishing grounds + per-category
                                item-journal task discovery (tavern/harbor/etc.) [COMPLETE v1.4.1,
                                on Nexus — docs/mods/task-unlocker.md]
  ZeroTaskWorkersMod/        ← Mod 18: newly assigned workers inherit zero tasks [COMPLETE v1.0.1 — docs/mods/zero-task-workers.md]
  GroundItemVacuumMod/       ← Mod 19: hotkey/auto vacuum for loose ground items [COMPLETE v1.1.3, on Nexus — docs/mods/ground-item-vacuum.md]
  FishFilletMod/             ← Mod 20: Shift+RMB fillets fish directly in the inventory [COMPLETE v1.1.1 — docs/mods/fish-fillet.md]
  DenRespawnMod/             ← Mod 21: hotkey/map-pin/timed revive for defeated monster dens [COMPLETE v1.2.2 — v1.2.1 stale-registry timer guard ⚠️ pending in-game confirmation — docs/mods/den-respawn.md]
  TimeWarpMod/               ← Mod 22: dev/test time accelerator (K=fast-forward cycle, L=skip day) [DEV TOOL v0.1.1, NOT for Nexus — docs/mods/time-warp.md]
  SummonTimerMod/            ← Mod 23: remove Eye of Odin villager-summon wait timer [COMPLETE v0.1.0, local-only, NOT for Nexus — docs/mods/summon-timer.md]
  VillagerAmmoMod/           ← Mod 24: villagers never run out of arrows (polling refund + stuck-arrow cull) [COMPLETE v1.0.0, on Nexus — docs/mods/villager-ammo.md]
  OuthouseComposterMod/      ← Mod 25: food/seeds convert to Compost inside the Outhouse storage [COMPLETE v1.0.0 — docs/mods/outhouse-composter.md]
  SupplyChainMod/            ← Mod 26: idea-12 supply-chain autopilot [WIP v0.14.2 — SURPLUS v2
                                flow-aware classifier, v0.14.1–v0.14.2 in-game-verified
                                2026-07-16; arming design approved 2026-07-16 (failure-mode →
                                lever routing + rails, case-grounded — see
                                SupplyChainMod/DEMAND_MODEL_PLAN.md → "Arming design"); next:
                                case-layer build; dry-run only,
                                dev tool NOT for Nexus — docs/mods/supply-chain.md]
  NoNeedsMod/                ← Mod 27: pin player + villager needs at max — needs "god mode" [COMPLETE v1.0.0, on Nexus ("Max All Needs") — docs/mods/no-needs.md]
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
- **The plural generic `GetComponentsInChildren<T>(bool)` is missing too** (same `MissingMethodException` family); the singular `GetComponentInChildren<T>(bool)` works. **Non-generic `GameObject.GetComponents(System.Type)` is ALSO missing** through the trampoline (confirmed in-game 2026-07-11, OuthouseComposter) — the per-node singular `GetComponent<T>()` walk remains the workaround. Note that base-typed singular `GetComponent<T>()` DOES return derived instances; probing with `GetComponent<Fusion.NetworkBehaviour>()` / `GetComponent<SSSGame.Interaction>()` then reading the native class name works (confirmed in-game 2026-07-11). To collect all matches, walk the hierarchy manually with per-object `GetComponent<T>()` (MineRefreshMod / TreeRespawn `WellRefill` pattern).
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
- **DO NOT Harmony-patch methods with inventory-family parameter types** (`Item`, `ItemCollection`, `ItemEventContext`, e.g. `RangedManager._OnAmmoRemoved(ItemCollection, Item, …)`). The patch mechanism resolves target method parameter types at setup time, forcing too-early il2cpp class-init of those types during plugin load — causing a native crash (`coreclr.dll+0x1d1fdd` fatal CLR exit, no managed exception). Reproduced 3× (2026-07-11, VillagerAmmoMod v0.1.0/v0.1.1; crash dumps saved). **Workaround:** capture instances via zero-parameter lifecycle methods (`Awake` postfix only) + polling; never detour a method with inventory-family parameters. Full evidence: [`docs/mods/villager-ammo.md`](docs/mods/villager-ammo.md) → Dead-end section.
- **Interop link-properties can lie at runtime** — `Workstation.NetworkWorkstations` returns null/empty even when the `NetworkCraftingStation` component exists on the same GameObject; `GetComponent<T>()` finds it correctly (confirmed in-game 2026-07-12, SupplyChainMod v0.3.5). Reach network components via `GetComponent<T>()`, not cached link properties.
- **Never cache interop wrappers of per-world native objects across world sessions** (managers, handlers, instance registries). Quit-to-menu → reload of the SAME world does NOT change `StorageManager.ActiveSessionID`, so same-ID checks won't clear caches — and reads through a stale wrapper AV in native code (no managed exception; try/catch can't help), killing the process via a CLR fatal error whose WER signature is always `coreclr.dll+0x1d1fdd`. Detect world-leave via `ActiveSessionID` becoming empty and drop all per-world state (TreeRespawn v1.4.5 `NoteWorldLeft` pattern, confirmed in-game 2026-07-06). Full crash-forensics recipe: docs/architecture.md → Native Crash Diagnosis.
- **If a mod hard-crashes the game natively with no managed exception in the log, don't guess** — map the Windows Error Reporting crash offset to a method with Cpp2IL. See [Native Crash Diagnosis](docs/architecture.md#native-crash-diagnosis-wer--cpp2il).

## Documentation map
| Read this | When you're working on |
|---|---|
| [`docs/architecture.md`](docs/architecture.md) | **Any** game subsystem — confirmed APIs + dead-ends, grouped by subsystem: damage pipeline, player vs. creature, resource/tree, gather, structures/workstations/task-priority, villager complaints/issue tracker, settlement hauling, inventory/settlement/recipes, cooking pipeline, torch/fire-fuel, villager needs/schedule/happiness, villager combat/fight-vs-flee, villager ranged/ammo, worldgen/streaming, caves/mines, build menu/structure templates/localization, terrain/terraforming, dens/population spawners, villager summoning (Eye of Odin), native crash diagnosis (WER + Cpp2IL) |
| `docs/mods/<mod>.md` | The matching mod — current recipe, config reference, per-mod dead-ends (pointers also in the Project Structure above): bow-damage.md, tree-respawn.md, health-regen.md, torch-fuel.md, dynamic-villager-needs.md, villager-fight-back.md, seed-scout.md, mine-refresh.md, jotun-blood-yield.md, terrain-leveler.md, resource-marker-radius.md, task-unlocker.md, zero-task-workers.md, ground-item-vacuum.md, fish-fillet.md, den-respawn.md, time-warp.md, summon-timer.md, villager-ammo.md, outhouse-composter.md, supply-chain.md, no-needs.md |
| [`NEW_MOD_IDEAS_PLAN.md`](NEW_MOD_IDEAS_PLAN.md) | Researched mod ideas with Cecil-confirmed API leads. Open: crafting multiplier (idea 3), freezing hunters (5), pre-construction worker/task setup (7), DVN Phase 3 schedule-UI overlap warning (11), demand-driven supply-chain autopilot (12), outhouse composter Phases 2–3 (13), rocks-only remover (14). Shipped ideas are one-line pointers to their mod docs |
| [`AGENTS.md`](AGENTS.md) (repo root) | Onboarding a NEW/non-Claude agent (e.g. OpenAI Codex auto-loads this path): reading order, which CLAUDE.md sections are Claude-Code-specific, the user working agreement, hook automation that must be replaced manually |
| [`docs/nexus-upload.md`](docs/nexus-upload.md) | Publishing to Nexus Mods |
| [`docs/agent-delegation.md`](docs/agent-delegation.md) | Delegating subtasks to cheaper-model subagents (Claude Code only): agent roster (`.claude/agents/`), delegation-prompt checklist, invocation syntax, runtime gotchas |
| [`TerrainLevelerMod/BULLDOZER_UI_PLAN.md`](TerrainLevelerMod/BULLDOZER_UI_PLAN.md) | Mod 15 — the bulldozer build-menu square: design + evidence chain, build-menu/localization ground truth (COMPLETE 2026-07-01) |
| [`TerrainLevelerMod/DRAG_CRASH_PLAN.md`](TerrainLevelerMod/DRAG_CRASH_PLAN.md) | Mod 15 — drag-crash root cause (BitSet256 network-state overflow) + fix, confirmed in-game 2026-07-01 |
| [`SupplyChainMod/DEMAND_MODEL_PLAN.md`](SupplyChainMod/DEMAND_MODEL_PLAN.md) | Mod 26 — approved demand-model & Phase 2d redesign plan (demand loop incl. future task creation, container ground truth + slot math, lever map, gated build sequence; user decisions 2026-07-15) |
| [`TreeRespawnMod/STONE_RESPAWN_HANDOFF.md`](TreeRespawnMod/STONE_RESPAWN_HANDOFF.md) | Why mining/stone respawn was abandoned (don't re-attempt) |
| [`VILLAGER_FIGHTBACK_HANDOFF.md`](VILLAGER_FIGHTBACK_HANDOFF.md) | Mod 7 — the lever-by-lever combat investigation log (deep evidence behind architecture.md's fight-vs-flee section) |
| [`SEED_HARVESTER_HANDOFF.md`](SEED_HARVESTER_HANDOFF.md) | Mod 14 — why the fast seed-scan spike is blocked (cave `AreaInstance`s never instantiate from `UpdateDataAsync`) |
| [`ResourceMarkerRadiusMod/MAP_RADIUS_HANDOFF.md`](ResourceMarkerRadiusMod/MAP_RADIUS_HANDOFF.md) | Mod 16 — map marker radius debugging handoff (WIP) |
| [`DYNAMIC_HAULING_HANDOFF.md`](DYNAMIC_HAULING_HANDOFF.md) | Mod 6 (planned) — settlement hauling plan |
| [`WAREHOUSE_CAPACITY_HANDOFF.md`](WAREHOUSE_CAPACITY_HANDOFF.md) | Mod 12 (planned) — warehouse capacity |
| `docs/archive/` | History only — superseded handoffs whose live conclusions were absorbed into the docs above: TREERESPAWN_HANDOFF.md (Issues A–F evidence log), SEED_SCOUT_HANDOFF.md (scorer-era worldgen findings + den-tier table), WARP_TOUR_HANDOFF.md (PlayerDrive.Teleport recipe — also in architecture.md), TERRAIN_LEVELER_HANDOFF.md, TERRAIN_DRAG_HANDOFF.md, DYNAMIC_NEEDS_HANDOFF.md. Read only when chasing how a conclusion was reached |

## Reference Paths
| Purpose | Path |
|---|---|
| BepInEx core DLLs | `ASKA\BepInEx\core\` |
| Game interop assemblies | `ASKA\BepInEx\interop\` |
| Unity engine modules | `ASKA\BepInEx\unity-libs\` |
| Plugin output folder | `ASKA\BepInEx\plugins\` |
| BepInEx log | `ASKA\BepInEx\LogOutput.log` |
