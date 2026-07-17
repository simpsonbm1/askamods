# Agent Onboarding — start here if you are new to this project

<!-- ORIENTATION-STUB: pointer file — the single orientation source is CLAUDE.md. Enforced by
     .githooks/pre-commit (marker + no-accretion checks). Do not add project knowledge here. -->

This file bootstraps ANY coding agent (OpenAI Codex reads this path natively; Claude Code loads
`CLAUDE.md`; Antigravity loads `.agents/AGENTS.md`). It is deliberately a THIN POINTER plus the
context that lives nowhere else — the project knowledge itself is NOT here.

## Reading order (the breadcrumb trail)

1. **[`CLAUDE.md`](CLAUDE.md)** — the canonical orientation: what the project is, every mod's
   status/version, the IL2CPP gotcha list, build/SAC/commit rules, and the Documentation map.
   (It is the ONLY full orientation copy — `.agents/AGENTS.md` is a pointer stub like this file;
   the pre-2026-07-16 dual-write ritual is retired.)
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
  Claude Code hooks and DO NOT RUN for you. See "Automation inventory" below for what each one
  accomplished and what you must recreate.
- References to `~/.claude/...` paths are the Claude Code config tree on this machine. The two
  methodology guides there are plain markdown and worth reading regardless of tool:
  - `C:\Users\simps\.claude\skills\game-modding-research\SKILL.md` — root-cause methodology for
    this kind of IL2CPP modding; consult BEFORE declaring anything blocked/impossible.
  - `C:\Users\simps\.claude\skills\process-hygiene\SKILL.md` — read before building anything
    "automatic"/recurring or writing standing instructions.

**Everything else in CLAUDE.md is tool-agnostic project truth and binding**: the IL2CPP interop
gotchas, the Smart App Control version-bump rule, the single-source orientation rituals
(CLAUDE.md canonical, mechanically enforced by `.githooks/pre-commit`), the commit/push policy,
and the documentation maintenance rules.

## Working agreement with the user (Ben) — conventions that lived in assistant memory

- **Nothing is "fixed"/"resolved"/"COMPLETE" until Ben confirms it in-game.** Until then it is
  FIX ATTEMPTED / ⚠️ PENDING CONFIRMATION. One successful test = proof of concept; only Ben
  promotes WIP→COMPLETE or retires an idea.
- **Ask, don't assume.** When evidence contradicts his report (or your model of the game), ask a
  targeted question instead of asserting an assumption as fact.
- **Never commit or push without his go-ahead**, and only at verified-success or end-of-session
  checkpoints. He works desktop + laptop synced ONLY through `origin`; when he approves a commit,
  push it too (source + built DLL + docs) so the other machine gets it.
- **Commits are lightweight; docs update as-you-go.** In a build→test loop, per cycle only: edit
  code, bump version (BOTH `PLUGIN_VERSION` and csproj `<Version>` — the SAC gotcha), build, give
  him test steps. Confirmed facts land in `docs/` when confirmed, during the work; the pre-commit
  hook auto-fixes CLAUDE.md's version tokens at commit; status blurbs change only when a mod's
  status/approach actually changes; mod-doc version history batches to natural milestones.
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

## Automation inventory — the functions matter, not the implementations

Design philosophy behind all of it (from the process-hygiene methodology): **an instruction is a
hope; a mechanism is a guarantee.** Every "every time X, do Y" rule in this project got turned
into something the OS/git/build executes, after prose versions failed. When the same gotcha bites
twice under your tooling, mechanize it the same way. Two groups:

### In the repo — these work for ANY agent as-is. Use them; do not rebuild or bypass them.

- **`.githooks/pre-commit`** (activate once per clone: `git config core.hooksPath .githooks`) —
  keeps CLAUDE.md (the single orientation source) honest: AUTO-FIXES a stale mod version token
  from the csproj `<Version>` (re-stages, reports `AUTO-FIXED`), and BLOCKS on real drift: mod
  folder or handoff/mod doc missing from CLAUDE.md, `PLUGIN_VERSION` ≠ csproj `<Version>` (the
  Smart App Control half-bump — needs a rebuild, not a text fix), a duplicated `# AskaMods`
  header, or a pointer stub (this file, `.agents/AGENTS.md`) losing its `ORIENTATION-STUB`
  marker / re-growing a body. GOAL: the always-loaded orientation can never silently lie, at
  zero per-commit ceremony. Never bypass with `--no-verify`; fix the drift.
- **`Directory.Build.targets` (SAC BUMP GUARD)** — fails any build whose `<Version>` is already
  deployed in the live plugins folder (deliberate same-version rebuild: `-p:SkipSacGuard=true`).
  GOAL: make it impossible to test a rebuilt DLL whose unchanged hash Smart App Control will
  block, which otherwise looks like a mysterious stale-behavior session.
- **`CopyToPlugins` build target** — every `dotnet build` auto-deploys the DLL to
  `ASKA\BepInEx\plugins\<Mod>\`. GOAL: no manual copy step to forget mid-iteration.
- **`sync-plugins.ps1`** — copies each committed `<Mod>.dll` to the live game folder
  (hash-compared; preserves each mod's enabled/`.dll.off` state; backs up replaced DLLs; its
  `$ParkedByDefault` list keeps parked mods disabled on fresh installs). GOAL: after a `git pull`
  on either machine, one command brings the live game up to date.
- **`check-loaded.ps1`** — one table of repo vs live vs actually-loaded version per mod (reads PE
  headers + `LogOutput.log`; never loads the DLLs). GOAL: verify what the game REALLY loaded
  before trusting any test result — the SAC gotcha makes "it built" meaningless.
- **`bisect-plugins.ps1`** — enable/disable live plugins in halves for framerate/crash
  bisection; state-saving, `-Restore` reverts. GOAL: attribute a live regression to a mod fast.

### Claude Code harness hooks — these will NOT run for you. Recreate the FUNCTION in your format.

- **Continuous session-handoff persistence + cross-machine mirror** (PostToolUse hook on every
  `SESSION_HANDOFF.md` write: mirrored the file into a synced config repo and pushed it).
  FUNCTION TO RECREATE: sessions can be killed mid-action with no warning (usage limits), so the
  handoff file must be (a) updated right after each meaningful step, not at session end, and
  (b) transported to the other machine WITHOUT waiting for a project commit (the file itself is
  gitignored). Minimum viable replacement: keep (a) as discipline; for (b), tell Ben when a
  session ends with a meaningful handoff so he can move it, or agree on a new transport.
- **Uncommitted-WIP snapshot transport** (same hook: on every handoff update, snapshotted the
  repo's uncommitted tracked+untracked state via a temp index — working tree never touched — and
  force-pushed it to a disposable `wip/<hostname>` branch on origin; consumed on the other
  machine by `git cherry-pick --no-commit`, then the branch deleted immediately). FUNCTION TO
  RECREATE: in-progress code reaches the other machine even though the project's ask-first
  commit policy forbids committing unverified work to `master`. The disposable-branch pattern is
  tool-agnostic — you may reuse it directly if you can run git; the ask-first policy for real
  branches is unchanged.
- **Session-start freshness check** (SessionStart hook: pulled the synced config repo; copied
  down a strictly-newer other-machine handoff; fast-forwarded this repo when the tree was clean;
  ran `sync-plugins.ps1 -DryRun` and reported stale live plugins; reported incoming
  `wip/<other-host>` branches with resume instructions). FUNCTION TO RECREATE: start every
  session by pulling, reading `SESSION_HANDOFF.md`, checking for foreign `wip/*` branches, and
  offering `sync-plugins.ps1` if live DLLs lag the repo — as an explicit checklist if your
  tooling has no hook equivalent.
- **Continuous assistant-memory sync** (Stop hook: auto-committed/pushed the `~/.claude` config
  tree — memory, skills, global conventions — after every turn). FUNCTION TO RECREATE: whatever
  durable cross-session memory your tooling has, persist working-agreement learnings there AND
  mirror anything project-critical into this repo's docs (this file exists because
  assistant-side memory does not transfer between vendors).

## Repo state signals

- `git log --oneline -20` reads as a project journal — commit messages here are deliberately
  detailed and are themselves breadcrumbs.
- `docs/archive/` is history only (superseded investigation logs); never treat it as current.
- Feature branches named `wip/<feature>` are normal in-progress work. Branches named
  `wip/<HOSTNAME>` (machine names) were Claude Code auto-snapshots of uncommitted state — if one
  exists, tell Ben; don't build on it silently.

## The break-glass prompt (for Ben — paste this as the new tool's first message)

```
You are joining an active game-modding project (ASKA, a Viking survival game — BepInEx 6 /
IL2CPP mods in C#) as a COLLABORATOR. The primary agent on this project is Claude (Claude
Code); you are the secondary/backup collaborator. You both work inside the same structure and
conventions, and each of you must leave the project legible to the other: Claude's next session
will pick up exactly where you stop, using only what you wrote down.

Working directory: D:\Claude Projects\askamods (a git repo; origin =
https://github.com/simpsonbm1/askamods.git)

Do this IN ORDER before any other action:
1. Read AGENTS.md at the repo root in full. It was written specifically for you: reading order,
   the working agreement with me, which docs are canonical, and an inventory of automation whose
   FUNCTIONS you must recreate in your own tooling.
2. Follow its reading order: CLAUDE.md (canonical orientation — skip the sections it marks as
   Claude-Code-specific), then SESSION_HANDOFF.md at repo root (live in-flight state), then
   `git log --oneline -15` (commit messages here are a deliberate project journal).
3. Verify the safety mechanisms are active for your clone: `git config core.hooksPath` must
   output `.githooks` (set it if not). Never bypass the pre-commit hook with --no-verify.

Hard rules, effective immediately (full detail in AGENTS.md):
- Never commit or push without my explicit go-ahead.
- Nothing is "fixed" or "complete" until I confirm it in-game; until then it is FIX ATTEMPTED /
  PENDING CONFIRMATION.
- Before touching any game subsystem, read its section in docs/architecture.md first — it
  records dead-ends that WILL hard-crash the game or waste whole sessions if re-tried.
- Keep your collaborator posted. You have no live channel to Claude; your outbound channel IS
  the project structure: SESSION_HANDOFF.md (update it right after each meaningful step, with
  the freshness header format shown in AGENTS.md), detailed commit messages, and doc updates.
  Write them as messages to the other agent, not private notes — Claude's next session starts
  by reading exactly those, and it will act on what they say (and only on what they say).
- The same conventions that bind Claude bind you: docs record confirmed facts as-you-go,
  CLAUDE.md is the single orientation source (never grow the AGENTS.md stubs), and the
  pre-commit hook's report is to be obeyed, never bypassed.

When you have finished orienting, reply with: (a) your summary of the project and its current
in-flight work; (b) which mod versions are pending in-game confirmation; (c) whether any
wip/* branches exist on origin; (d) the next approved task as you understand it, plus the two
or three constraints from docs/architecture.md or the active plan doc most likely to bite while
doing it; and (e) one thing in the trail that surprised you or that you'd want clarified before
starting — then STOP and wait for my direction. Do not change anything yet.
```

Why the readback step: it fire-verifies the bootstrap. If the readback comes back wrong, the new
agent skipped or misread the trail — correct it before letting it touch anything.

**Fire-verified with Codex (2026-07-16):** full readback (a)–(e) came back accurate on first
contact — current in-flight state incl. uncommitted worktree edits (proof it read
SESSION_HANDOFF.md), the exact ⚠️-pending list, correct wip-branch handling (report, don't
touch), and lever-map specifics (`SetPriority + HostUpdateTasks`, the rank-3/Off protection, the
arming rails) reproduced verbatim from the plan doc. Zero changes made; stopped when told.
