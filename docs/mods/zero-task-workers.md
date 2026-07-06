# Mod 18: ZeroTaskWorkersMod — newly assigned workers inherit zero tasks

**Status: COMPLETE v1.0.0 — confirmed in-game 2026-07-06**

When a villager is assigned to a workstation, they inherit ZERO of the station's tasks by default.
All task choices are manually enabled per-villager via the station's checkboxes. Fired/unassigned
villagers still auto-join the Buildstation, preserving access to building and firekeeping work.

## Problem & approach

Vanilla behavior: assigning a villager to any workstation auto-enables all the station's tasks for
that villager (via `_CanAddVillagerToTaskData` returning `true`). Players wanting a specialized
worker have to manually uncheck every task they don't want.

Solution: Harmony prefix-block on all four `_CanAddVillagerToTaskData` implementations so they
return `false`, preventing task inheritance on spawn. The game's task-assignment UI (checkbox
toggles) never calls `_CanAdd` — those RPCs route directly — so manual opt-in still works.

Full subsystem facts: `docs/architecture.md` → "Workstation task assignment (Mod 18 groundwork)".

## Shipped recipe (Harmony + gate chain)

- **Patch target: four Harmony prefixes (all return `false`, all set `__result=false`):**
  - `SSSGame.Workstation._CanAddVillagerToTaskData(Villager, WorkstationTaskData)`
  - `SSSGame.Buildstation._CanAddVillagerToTaskData(Villager, WorkstationTaskData)` (derived override)
  - `SSSGame.Marketplace._CanAddVillagerToTaskData(Villager, WorkstationTaskData)` (derived override)
  - `SSSGame.ResourceStorage._CanAddVillagerToTaskData(Villager, WorkstationTaskData)` (derived override)

  All four are required — AOT patches concrete methods, not virtual slots.

- **Gate chain in `Diag.ShouldBlock()` (called from every prefix; if any step throws, don't block):**

  1. **World gate:** `FindAnyObjectByType<BlueprintConditionsDatabase>()` non-null = world loaded
     (TaskUnlocker world-gate pattern; same as ModVersion)
  2. **Grace window:** block ONLY if `LastDeserializeAt >= 0 && (now - LastDeserializeAt) > LoadGraceSeconds`
     AND `(now - WorldGateSeenAt) > LoadGraceSeconds`. Reason: vanilla load order is
     `AddToTaskDatas` (add all tasks) then `DeserializeTaskData` (load/overwrite from save). On
     first load, `Deserialize` hasn't fired yet (no chance to stamp it), so the initial `Add` burst
     must not block or saved tasks stay lost. `LastDeserializeAt` is stamped in `DeserializeTaskData`
     postfixes (+ `DeserializeTaskDataForRecreation`) BEFORE the logging gate, so an unseen value
     means "deserialize hasn't run yet" = "don't block".
  3. **Authority gate:** `HasStateAuthority` (compiles directly on `Workstation`; host-only, co-op safe)
  4. **Buildstation exemption:** identify by NATIVE class name (not managed cast — interop casts lie;
     use `IL2CPP.il2cpp_object_get_class` + `il2cpp_class_get_name`). Exemption covers
     `Buildstation`, `BoatBuildingStation`, `HarboringStation` (the station family). Reason:
     unassigned villagers auto-transfer here (Remove → Add(Buildstation)), so blocking this
     prevents build/firekeep workers from losing their tasks.
  5. **Config name match (only if `ApplyToAllBuildings=false`):** case-insensitive substring match
     of `_structure.GetName()` against each entry in `BuildingNameList` (semicolon-separated).
     Allows per-station opt-in/opt-out.

- **Postfixes on deserialization methods:** stamp `LastDeserializeAt = now` so grace window
  knows the deserialize has run (gated before the logging line, not after).

- **Logging:**
  - If `LogTaskEvents=false` (default since v1.0.0), no output except NO-OP/error warnings
  - If `true`, every block logs `[BLOCKED] <villager> + <station> (reason)`, plus summary stats

## Config (`com.askamods.zerotaskworkers.cfg`)

| Key | Default | Meaning |
|---|---|---|
| `ApplyToAllBuildings` | `true` | Apply to all workstations (if false, filter by `BuildingNameList`) |
| `BuildingNameList` | `` (empty) | Semicolon-separated substring list; only used when `ApplyToAllBuildings=false` |
| `LoadGraceSeconds` | `10` | Grace window duration (seconds after world load to skip blocking) |
| `LogTaskEvents` | `false` (since v1.0.0) | Per-villager block events + summary (was `true` in v0.1.0–v0.2.1) |

## Known residuals (harmless, self-correcting)

- **Brief BLOCKED-line leakage at reload start:** `LastDeserializeAt` from the previous session
  segment persists across quit-to-menu; the grace window hasn't aged yet so a few early-load
  `_CanAdd` calls may log BLOCKED before deserialize runs and resets the timer. Vanilla's later
  Deserialize overwrites `VillagersInCharge` from save anyway, so saved assignments self-heal.
- **`ZeroTaskTracker.WorldGateSeenAt` doesn't reset on quit-to-menu:** the
  `BlueprintConditionsDatabase` is a persistent manager (not destroyed on menu), so
  `WorldGateSeenAt` tracks the age of the manager, not the age of the currently-loaded world.
  Superseded by `LastDeserializeAt` as the live gate, so this is unused as of v1.0.0.

## Version history

| Version | Date | Change |
|---|---|---|
| v0.1.0 | 2026-07-06 | Diagnostics-only: postfixes logged deserialization without blocking |
| v0.2.0 | 2026-07-06 | First blocking build: grace-window direction bug — blocked whole initial load burst (1,327 BLOCKED lines) |
| v0.2.1 | 2026-07-06 | Grace fix: never block before first-observed deserialize |
| v1.0.0 | 2026-07-06 | Ship: diagnostics flipped to `false` default; confirmed in-game 2026-07-06 (hire → zero inherited tasks; manual opt-in works; fired villager returns to builder pool) |

## Tested in-game (2026-07-06)

- ✅ Hired a villager at a CraftingStation → no tasks inherited (checkboxes unchecked)
- ✅ Manually enabled a task via checkbox → checked and held (not reversed by mod)
- ✅ Fired a villager from the station → auto-joined Buildstation for building/firekeeping
- ✅ Two reload cycles on the same save → saved task assignments untouched

The reload crash observed during testing was TreeRespawnMod's same-world reload bug
(fixed separately in TreeRespawn v1.4.5 — see `docs/mods/tree-respawn.md`).

## Nexus

Published 2026-07-06 as **"Assigned Workers Start Idle"**, file group ID `7626437` — wired into
`docs/nexus-upload.md` and `.github/workflows/nexus-upload.yml`, so updates ship via the standard
workflow. Page text source: `ZeroTaskWorkersMod/NEXUS_PAGE_DRAFT.md`.
