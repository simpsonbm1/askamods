# AskaMods — Antigravity bootstrap (pointer stub)

<!-- ORIENTATION-STUB: this file is deliberately a THIN POINTER. The single orientation source
     is CLAUDE.md (repo root). Do NOT add project content here — .githooks/pre-commit blocks any
     commit where this file loses this marker or re-grows a full orientation body. Full agent
     onboarding lives in AGENTS.md (repo root). Until 2026-07-16 this file was a full parallel
     copy of CLAUDE.md kept in sync by a dual-write ritual; that ritual is retired. -->

**Read `AGENTS.md` (repo root) first, then `CLAUDE.md` — CLAUDE.md is the single orientation
source** (project structure, every mod's status/version, the IL2CPP gotcha list, build/SAC rules,
documentation map, doc-maintenance and commit rituals). Root `AGENTS.md` tells you which CLAUDE.md
sections are Claude-Code-specific (subagent delegation, harness hooks — they do not run for you)
and carries the working agreement with the user.

Non-negotiables, even if you read nothing else (full detail in CLAUDE.md):

- **Smart App Control**: bump `PLUGIN_VERSION` + csproj `<Version>` together before EVERY build;
  confirm the loaded version in `ASKA\BepInEx\LogOutput.log` before trusting any test result.
- **Never commit or push without the user's explicit go-ahead** — and only at verified-success or
  end-of-session checkpoints.
- **Nothing is RESOLVED/COMPLETE until the user confirms it in-game** — until then it is
  FIX ATTEMPTED / PENDING CONFIRMATION.
- **Before touching any game subsystem, read its section in `docs/architecture.md`** — it records
  dead-ends that will hard-crash the game or waste whole sessions if re-tried.
- Maintain `SESSION_HANDOFF.md` (repo root) continuously during work — update it right after each
  meaningful step, with the freshness header format shown in root `AGENTS.md`.
