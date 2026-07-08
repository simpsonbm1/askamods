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
| `doc-scribe` | Haiku | Doc-only work: dual-write CLAUDE.md ↔ AGENTS.md, `docs/` updates, drift-check + integrity rituals at commit checkpoints | Deciding *what* is true — it records only facts stated in its prompt |
| `log-analyst` | Haiku | Post-test-run `LogOutput.log` triage: loaded versions, exceptions, fire-verification markers; `check-loaded.ps1` | Interpreting *why* something failed — it reports evidence, the main thread diagnoses |
| Explore (built-in) | inherits (≤ Opus) | Broad codebase/interop-assembly searches where only the conclusion matters | — |

Division of labor: **the main thread plans, decides, and diagnoses; agents execute and report.**
Research, root-cause work, and anything touching an unfamiliar game subsystem stays on the main
thread — those need the strongest model *and* the conversation context.

> **Two rosters coexist here.** A generic user-level roster (`implementer`, `doc-worker`,
> `log-triager` in `~/.claude/agents/` — master list in its `README.md`, routing table in the
> global `~/.claude/CLAUDE.md`) is available in every project. In askamods, **prefer the three
> project agents above** for matching tasks — they encode this project's rituals (dual-write,
> SAC bump, BepInEx paths) that the generic ones only know indirectly via CLAUDE.md.

## The commit-checkpoint dual-write ALWAYS goes to doc-scribe (cost-critical)

This is the single most expensive doc task in the project and the one most often skipped, so it gets
its own rule: **at every commit checkpoint, delegate the whole Ritual-2 dual-write pass to
`doc-scribe` — do not do it inline on the main (Opus) model.** Measured 2026-07-07: doing it inline
cost ~10% of a 5h session. Why it's costly on the main thread (and cheap on Haiku):

- It writes **CLAUDE.md + `.agents/AGENTS.md`** — near-duplicate 277-line files — so the same info is
  generated *twice*, plus a new `docs/mods/*.md` and often an `architecture.md` subsection (1221
  lines). That is a large volume of **output** tokens (≈5× input cost).
- It runs at **end of session**, when the main context is at its largest and often past the 5-min
  prompt-cache TTL → full-price re-reads on every one of its 6–10 turns.
- `doc-scribe` sidesteps both: output is generated on **Haiku (~1/10–1/15 the cost)**, in a **fresh,
  tiny context** that never carries the session history — only its short report returns to Opus.

Do **not** rationalize this as a "micro-edit that's cheaper inline" (the "Delegation isn't free"
gotcha below) — it is multi-file, high-output, peak-context work: the canonical delegation case, not
a borderline one. The main thread's only remaining commit-time jobs are deciding *what* is true
(stated to doc-scribe in the prompt), then asking the user and running `git commit/push` (never
delegated). This can't be hook-enforced — the harness can't tell which model wrote an edit — so
compliance rides on this instruction; honor it.

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
  implementation cycle, a whole doc pass), not one-file micro-edits — a tiny task is cheaper done
  inline on the main model than paid for twice. **Do NOT misapply this to the commit-checkpoint
  dual-write** — it is *not* a micro-edit (see below) and must always be delegated.
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
