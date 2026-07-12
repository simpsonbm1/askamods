# Agent Onboarding — start here if you are new to this project

This file bootstraps ANY coding agent (OpenAI Codex reads this path natively; Claude Code loads
`CLAUDE.md`; Antigravity loads `.agents/AGENTS.md`). It is deliberately a THIN POINTER plus the
context that lives nowhere else — the project knowledge itself is NOT here.

## Reading order (the breadcrumb trail)

1. **[`CLAUDE.md`](CLAUDE.md)** — the canonical orientation: what the project is, every mod's
   status/version, the IL2CPP gotcha list, build/SAC/commit rules, and the Documentation map.
   (`.agents/AGENTS.md` is a byte-identical copy; read either, keep BOTH in sync when editing.)
2. **[`SESSION_HANDOFF.md`](SESSION_HANDOFF.md)** (repo root, gitignored, may be absent) — the live
   "what was in flight when the last session ended". If it exists, read it before doing anything.
3. **[`docs/architecture.md`](docs/architecture.md)** — before touching ANY game subsystem, read
   its section there. It records confirmed APIs AND dead-ends; most failed approaches are
   game/interop facts that will bite again if re-tried.
4. **`docs/mods/<mod>.md`** — the current recipe + config + per-mod dead-ends for whichever mod
   you're changing.
5. **[`NEW_MOD_IDEAS_PLAN.md`](NEW_MOD_IDEAS_PLAN.md)** — researched-but-unbuilt ideas, when
   starting something new.

## What in CLAUDE.md is tool-specific (skip if you are not Claude Code)

- **"Model-tiered subagent delegation"** and `.claude/agents/`, `docs/agent-delegation.md` —
  Claude Code subagent machinery. Ignore; do the work inline.
- **Hook-based automation referenced under "Session Handoff Doc"** (handoff mirroring to
  `~/.claude`, automatic `wip/<hostname>` snapshot branches, session-start pulls) — these are
  Claude Code hooks and DO NOT RUN for you. What to do instead:
  - Still maintain `SESSION_HANDOFF.md` continuously (update after each meaningful step, start it
    with `> Last updated: YYYY-MM-DD HH:MM (<hostname>)`). It is the crash-safety net.
  - Cross-machine transport for uncommitted work does not exist for you — flag risky uncommitted
    state to the user rather than assuming it synced anywhere.
- References to `~/.claude/...` paths are the Claude Code config tree on this machine. The two
  methodology guides there are plain markdown and worth reading regardless of tool:
  - `C:\Users\simps\.claude\skills\game-modding-research\SKILL.md` — root-cause methodology for
    this kind of IL2CPP modding; consult BEFORE declaring anything blocked/impossible.
  - `C:\Users\simps\.claude\skills\process-hygiene\SKILL.md` — read before building anything
    "automatic"/recurring or writing standing instructions.

**Everything else in CLAUDE.md is tool-agnostic project truth and binding**: the IL2CPP interop
gotchas, the Smart App Control version-bump rule, the CLAUDE.md↔.agents/AGENTS.md sync rituals
(mechanically enforced by `.githooks/pre-commit`), the commit/push policy, and the documentation
maintenance rules.

## Working agreement with the user (Ben) — conventions that lived in assistant memory

- **Nothing is "fixed"/"resolved"/"COMPLETE" until Ben confirms it in-game.** Until then it is
  FIX ATTEMPTED / ⚠️ PENDING CONFIRMATION. One successful test = proof of concept; only Ben
  promotes WIP→COMPLETE or retires an idea.
- **Ask, don't assume.** When evidence contradicts his report (or your model of the game), ask a
  targeted question instead of asserting an assumption as fact.
- **Never commit or push without his go-ahead**, and only at verified-success or end-of-session
  checkpoints. He works desktop + laptop synced ONLY through `origin`; when he approves a commit,
  push it too (source + built DLL + docs) so the other machine gets it.
- **Batch doc-sync to commit checkpoints.** In a build→test loop, per cycle only: edit code, bump
  version (BOTH `PLUGIN_VERSION` and csproj `<Version>` — the SAC gotcha), build, give him test
  steps. The dual-write rituals and docs prose happen ONCE at the commit checkpoint.
- **New diagnostic/debug config options default to `true`** while unverified; flip to `false`
  before shipping to Nexus.
- **Lean orientation, deep knowledge in `docs/architecture.md`** — record confirmed facts and
  dead-ends there proactively (dated `confirmed in-game (YYYY-MM-DD)`), not in chat only.
- He plays co-op with a friend regularly — co-op safety (host-authority gating, the game's own
  RPCs) is a real requirement, not theoretical.

## Build / test loop in one paragraph

Each mod is its own `.csproj` (target `net6.0`, built with the .NET 10 SDK); `dotnet build` in a
mod folder auto-copies the DLL to `D:\SteamLibrary\steamapps\common\ASKA\BepInEx\plugins\<Mod>\`
(`CopyToPlugins` target). Smart App Control blocks a rebuilt DLL with an already-seen version —
`Directory.Build.targets` fails such builds (`SAC BUMP GUARD`); always bump `PLUGIN_VERSION` +
csproj `<Version>` together. After the user launches the game, verify with `.\check-loaded.ps1`
(repo/live/loaded version table) and read `ASKA\BepInEx\LogOutput.log` — always fire-verify a new
Harmony patch with a log line (the AOT inliner silently eats patches). `.\sync-plugins.ps1`
pushes committed DLLs to the live folder after a pull; `.\bisect-plugins.ps1` bisects live-plugin
regressions. Test evidence comes from Ben playing; you cannot run the game.

## Repo state signals

- `git log --oneline -20` reads as a project journal — commit messages here are deliberately
  detailed and are themselves breadcrumbs.
- `docs/archive/` is history only (superseded investigation logs); never treat it as current.
- Feature branches named `wip/<feature>` are normal in-progress work. Branches named
  `wip/<HOSTNAME>` (machine names) were Claude Code auto-snapshots of uncommitted state — if one
  exists, tell Ben; don't build on it silently.
