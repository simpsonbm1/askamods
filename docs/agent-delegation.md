# Model-tiered subagent delegation (Claude Code)

**Why this exists.** Subscription usage is consumed at each model's own rate, and the main
conversation's model cannot switch automatically mid-session. Subagents are the supported way to
get model-per-task: the main session stays on the strongest available model for planning, research,
and debugging, and dispatches self-contained subtasks to agents pinned to cheaper models. All facts
below verified against `code.claude.com/docs/en/sub-agents` (2026-07-06).

## Agent roster (`.claude/agents/`)

| Agent | Model | Delegate | Never delegate |
|---|---|---|---|
| `mod-implementer` | Sonnet | A fully-planned code change: edit source, bump `PLUGIN_VERSION` + csproj `<Version>`, build, report | Anything whose approach is undecided; debugging with an unknown root cause |
| `doc-scribe` | Haiku | Doc-only work: milestone doc passes (mod-doc status/history), `docs/` updates from supplied facts | Deciding *what* is true — it records only facts stated in its prompt. Ordinary commits need no doc pass (see below) |
| `log-analyst` | Haiku | Post-test-run `LogOutput.log` triage: loaded versions, exceptions, fire-verification markers; `check-loaded.ps1` | Interpreting *why* something failed — it reports evidence, the main thread diagnoses |
| Explore (built-in) | inherits (≤ Opus) | Broad codebase/interop-assembly searches where only the conclusion matters | — |

Division of labor: **the main thread plans, decides, and diagnoses; agents execute and report.**
Research, root-cause work, and anything touching an unfamiliar game subsystem stays on the main
thread — those need the strongest model *and* the conversation context.

> **Two rosters coexist here.** A generic user-level roster (`implementer`, `doc-worker`,
> `log-triager` in `~/.claude/agents/` — master list in its `README.md`, routing table in the
> global `~/.claude/CLAUDE.md`) is available in every project. In askamods, **prefer the three
> project agents above** for matching tasks — they encode this project's rituals (SAC bump,
> BepInEx paths, doc conventions) that the generic ones only know indirectly via CLAUDE.md.

## Commit checkpoints are fast-path; doc-scribe handles MILESTONE doc passes (restructured 2026-07-16)

The old rule here — "the commit-checkpoint dual-write ALWAYS goes to doc-scribe" — is retired
along with the dual-write itself: `.agents/AGENTS.md` is now a pointer stub, CLAUDE.md is the
single orientation copy, and `.githooks/pre-commit` auto-fixes stale version tokens at commit.
(History: the dual-write cost ~10% of a 5h session inline, measured 2026-07-07; delegating it
to Haiku fixed tokens but still left the user waiting ~10 minutes per checkpoint, which deterred
committing at all — the restructure removed the work instead of optimizing it.)

**An ordinary checkpoint commit involves NO doc pass**: stage → commit (hook auto-fixes) → push,
plus at most one inline blurb edit when a mod's status/approach genuinely changed. Narrative
docs are written as-you-go during the work (see CLAUDE.md → "Single-source orientation" →
doc cadence).

**doc-scribe's remaining job — milestone doc passes** (mod shipped/parked/phase complete:
mod-doc status header + compressed version-history entry, architecture.md subsections from
supplied facts). When dispatching one, the old wall-clock rules still apply:

- **Surgical prompts.** Give exact anchor text + exact replacement text, never "search for X and
  supersede" descriptions; instruct it not to read beyond the edit sites. (Measured 2026-07-15:
  a semantic prompt cost 35 tool calls / 3.5 min; surgical roughly halves it.)
- **Trust the pre-commit hook.** Don't re-verify the scribe's edits with main-thread greps —
  attempt the commit; investigate only if the hook bounces.
- **Background the scribe when a next task is queued.** Fire it with `run_in_background` and
  keep working; run synchronously only when it's the last act of the session.
- **Pointers in secondary docs.** The mod doc + architecture.md carry the facts;
  NEW_MOD_IDEAS_PLAN and similar get one-line pointers (the de-accretion rule), which shrinks
  the edit set.

## How delegation gets triggered

- **Automatic**: Claude matches tasks against each agent's `description` field. The orientation
  files instruct sessions to delegate proactively when a task matches, so a planning session on
  Opus can hand off implementation without being asked each time.
- **Explicit (user)**: name the agent in your prompt ("use the mod-implementer subagent to …"), or
  @-mention it — type `@` and pick e.g. `mod-implementer (agent)`, which *guarantees* that agent
  runs. Manual form: `@agent-mod-implementer`.
- **Whole session**: `claude --agent <name>` runs the entire session as that agent (its prompt,
  tools, and model replace the defaults). Useful for a pure doc-cleanup session on Haiku.

## What a subagent sees — and the delegation-prompt checklist

A subagent starts with a **fresh context**: its own system prompt (the agent file body), the
delegation prompt, **all CLAUDE.md levels** (global + project — so the IL2CPP gotchas and standing
instructions do reach it), and a git-status snapshot. It does **not** see the conversation, files
already read, or skills already invoked. (Built-in Explore/Plan skip CLAUDE.md entirely — restate
any project rule they need in the prompt.)

So every delegation prompt from the main thread must be self-contained:

1. The mod name and exact files to touch.
2. The exact change (what to edit and to what — decisions already made, not "figure out").
3. The new version number (implementer bumps `PLUGIN_VERSION` + csproj together; the SAC bump
   guard fails the build otherwise).
4. Any conversation-derived facts the agent needs (e.g. "the resolver NREs when X — guard it").
5. What to report back, and that the report should be a summary, not raw output.
6. For log-analyst: which marker lines the new build should have emitted.

Subagents **cannot ask the user questions** (`AskUserQuestion` is unavailable to them) — never
delegate a task containing an open decision.

## Runtime behavior worth knowing

- **Background by default** (v2.1.198+): agents run concurrently; the main session is notified on
  completion. Permission prompts a subagent hits surface in the main session, naming the agent.
- **Results return to the main thread** — which relays them to you. Project rule unchanged: a
  subagent's "build succeeded" is still *FIX ATTEMPTED, pending in-game confirmation*.
- **Resume, don't respawn**: a completed agent can be continued with its context intact ("continue
  that agent's work and also …") — Claude uses `SendMessage` with the agent's ID. Explore/Plan are
  one-shot and can't be resumed.
- **Model resolution order**: `CLAUDE_CODE_SUBAGENT_MODEL` env var → per-invocation `model` param →
  the agent file's `model:` frontmatter → inherit from the main session. Frontmatter accepts
  `haiku`, `sonnet`, `opus`, `fable`, a full model ID, or `inherit` (the default when omitted).
- **Nesting**: subagents can spawn their own subagents (depth limit 5). Not needed for this
  project's workflow; the agent files don't rely on it.

## Gotchas (confirmed from the docs, 2026-07-06)

- **First-ever agent file needs a restart.** The file watcher only covers `agents/` directories
  that existed when the session started. `.claude/agents/` was created 2026-07-06, so the first
  session after that date must restart Claude Code once; afterwards edits hot-reload in seconds.
- The `/agents` interactive wizard was removed in v2.1.198 — create/edit the markdown files
  directly (or ask Claude to).
- **Delegation isn't free**: each spawn re-reads CLAUDE.md and re-derives context, and each result
  consumes main-thread context on return. Delegate meaty self-contained chunks (a whole
  implementation cycle, a whole milestone doc pass), not one-file micro-edits — a tiny task is
  cheaper done inline on the main model than paid for twice.
- **Tight build→test loops stay coordinated by the main thread**: the user tests in-game between
  cycles and reports back to the main conversation. Delegate the mechanical inside of a cycle
  (edit + bump + build → implementer; log triage → log-analyst) when it's fully specified; keep the
  decide-what-changed-this-cycle step on the main thread.
- A user/project agent named `Explore` would *override* the built-in (e.g. `model: haiku` to force
  cheap exploration). Not currently done here; noted as an available lever.

## Typical mod-cycle shape (Opus main thread)

```
Opus: research + plan the change (docs/architecture.md, Cecil, interop) — main thread
  → mod-implementer (Sonnet): "In TorchFuelMod, change X in TorchFuelTracker.cs …, bump to 1.2.5, build, report"
  → user launches game, tests
  → log-analyst (Haiku): "Check LogOutput.log: TorchFuelMod 1.2.5 loaded? markers 'TF: refuel fired' present? exceptions?"
  → Opus: interpret findings, decide next cycle
  → … repeat …
  → at commit checkpoint: doc-scribe (Haiku): "Record: TorchFuelMod v1.2.5, confirmed in-game (YYYY-MM-DD), technique …; run rituals 1–3"
  → Opus: ask user, then commit/push (never delegated)
```

## Enforcement: the delegation gate (`.claude/hooks/delegation-gate.ps1`)

A PreToolUse hook (wired in `.claude/settings.json`, so both machines get it on `git pull`) that
refuses a MAIN-SESSION code edit / build / BepInEx-log review and forces the one-off-vs-delegate
decision, then lets the retry through. Delegated work passes silently. Prose guidance executes only
when remembered; this gates the action, not the memory.

**Discriminator (fire-verified 2026-07-21):** a PreToolUse payload carries `agent_id` ONLY inside a
subagent. So a gated action with no `agent_id` is the main thread doing it inline; with `agent_id`
present it is a subagent (mod-implementer / log-analyst) and passes.

**What it gates (main session only):**
- Write/Edit/MultiEdit to `*.cs` / `*.csproj`, and `dotnet build` / `msbuild` → refusal points at mod-implementer.
- Read/Grep/shell touching `LogOutput.log` / `ErrorLog.log` → refusal points at log-analyst.
- Docs (`*.md`), config, hooks, reads of non-log files, and non-build shell are NOT gated.

**Block-then-allow:** exit 2 on the first matching action, with a per-session marker so the deliberate
retry proceeds. Code markers are per-file (edits) / per-command (builds); the log marker is coarse
(per-session). Fails OPEN on any error — never wedges the session.

**Verify:** `powershell -File .claude/hooks/delegation-gate.ps1 -SelfTest` must print `SELFTEST PASS`
(synthesized events: positive/negative/idempotency/fail-open). A reworded refusal that breaks an
assertion fails the self-test rather than silently reopening a hole — fix the assertion alongside the reword.

**Residual gaps (named, not covered):** a source edit or log read done via a shell command that does
not literally name the file (`sed -i`, a path in a variable) evades the matcher; the coarse log marker
gates only the first log review per session; and it is a prompt with retry, not a hard block — the
one-off judgment stays with the model. A new evasion gets a matcher line plus a self-test case, never
a workaround.
