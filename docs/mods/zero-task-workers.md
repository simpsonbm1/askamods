# Mod 18: ZeroTaskWorkersMod — newly assigned workers inherit zero tasks

**Status: COMPLETE v1.1.0 — all features confirmed in-game 2026-07-30 (core v1.0.0 on 2026-07-06, farm/forestry exemption v1.1.0 on 2026-07-30)**

When a villager is assigned to a workstation, they inherit ZERO of the station's tasks by default.
All task choices are manually enabled per-villager via the station's checkboxes. Fired/unassigned
villagers still auto-join the Buildstation, preserving access to building and firekeeping work.
Farms and forestry huts are exempt, because they are the one station family with no per-villager
task checkboxes to opt back in with.

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
  4. **Station-family exemptions**, identified by NATIVE class name (not managed cast — interop
     casts lie; use `IL2CPP.il2cpp_object_get_class` + `il2cpp_class_get_name`). Two families:
     - `Buildstation`, `BoatBuildingStation`, `HarboringStation` — unassigned villagers
       auto-transfer here (Remove → Add(Buildstation)), so blocking this would cost build/firekeep
       workers their tasks.
     - `FarmingStation`, `ForestryStation` — the only stations with no per-villager task UI, so a
       block here can never be undone by the player. Both strings are required: `ForestryStation`
       derives from `FarmingStation` but reports its own native class name. See the farming section
       below for the evidence.
  5. **Config name match (only if `ApplyToAllBuildings=false`):** case-insensitive substring match
     of `_structure.GetName()` against each entry in `BuildingNameList` (COMMA-separated —
     `Patches.cs` splits on `,`). It is an INCLUDE list: only listed buildings are blocked, so
     there is no way to express "all buildings EXCEPT X".

- **Postfixes on deserialization methods:** stamp `LastDeserializeAt = now` so grace window
  knows the deserialize has run (gated before the logging line, not after).

- **Logging:**
  - If `LogTaskEvents=false` (default since v1.0.0), no output except NO-OP/error warnings
  - If `true`, every block logs `[BLOCKED] <villager> + <station> (reason)`, plus summary stats

## Config (`com.askamods.zerotaskworkers.cfg`)

| Key | Default | Meaning |
|---|---|---|
| `ApplyToAllBuildings` | `true` | Apply to all workstations (if false, filter by `BuildingNameList`) |
| `BuildingNameList` | `` (empty) | Comma-separated substring INCLUDE list; only used when `ApplyToAllBuildings=false` |
| `LoadGraceSeconds` | `10` | Grace window duration (seconds after world load to skip blocking) |
| `LogTaskEvents` | `false` (since v1.0.0) | Per-villager block events + summary (was `true` in v0.1.0–v0.2.1) |

## Farms and forestry huts are exempt (v1.1.0) — they have no per-villager task UI

Confirmed in-game 2026-07-30: newly assigned worker to a farming station does not inherit tasks
(block fired: `[ZTW] BLOCKED inheritance villager='Njal' task=CraftingStationTaskData(
item='Large Crude Iron Battle Axe') structure='Workshop House 5'`; 10 such lines all Njal at
Workshop House 5; user confirmed her task checkboxes were unticked in-game). Farm assignment
still goes through (`SetTaskAgent(Workstation) result=True station=FarmingStation structure=
'Farm'`). Reported by Nexus user impiousmessiah 2026-07-29 ("my farmers are stuck in idle, and will
not work"). The causal link — empty `VillagersInCharge` meaning the farming quest never dispatches
— was a reasonable inference; the v1.1.0 fix exempts farms so that link is now impossible. The
structure that makes this possible is Cecil-confirmed 2026-07-30:

- `SSSGame.FarmingStation : SSSGame.Workstation` with **no own `_CanAddVillagerToTaskData`**, so
  the base-`Workstation` prefix above fires for farms. `SSSGame.ForestryStation : FarmingStation`
  inherits the same exposure.
- Farm tasks are `SSSGame.AI.FarmingStationTaskData : WorkstationTaskData` — one per painted crop
  cell, ctor `(seedsConfiguration, List<Villager> villagers, removable, FarmCrop, cellIndex)` — so
  they carry `VillagersInCharge` exactly like every other station's tasks.
- **The per-villager toggle UI does not exist for farms.** Every other station renders task rows as
  `SSSGame.UI.TaskDataPanel`, which owns the toggles (`_villagersDiv`, `_villagersDisplayer`,
  `_selectAllVillagersButton`, `_onVillagerToggleValueChanged(VillagerPanel, Boolean)`). A farm
  renders `SSSGame.UI.FarmCropTaskPanel` instead, whose entire member set is
  `currentSeedIcon` / `previousSeedIcon` / `notSeededIcon` / `HostPanel` / `TileIndex` — a crop-grid
  tile, no villager members at all. Farm tasks are authored by
  `FarmCropPainterPanel.CreateTaskDatas(Boolean)` from the paint grid.
- A farm's villager control is instead the **whitelist**: `FarmingStation` implements
  `SSSGame.IWhitelistingSite` (`IsWhitelisted`, `Rpc_ChangeWhitelistedVillager`,
  `WhitelistNewVillagers`), surfaced by `WorkstationMenu._PrepareWhitelistPageVisuals`. That grants
  access to the station; it is not a per-task assignment.

Net effect: block inheritance at a farm and the crop tasks' `VillagersInCharge` stays empty with no
UI path to refill it. Exempting costs nothing, because farm work is not per-villager specializable
in vanilla either — every farmer works whatever crops are painted on the grid.

Workaround for anyone still on v1.0.1: set `ApplyToAllBuildings=false` and list the
buildings that SHOULD be affected in `BuildingNameList` — it is an include list, so farms simply go
unlisted.

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
| v1.0.1 | 2026-07-07 | `Update()` gated to 1 Hz — was calling `FindAnyObjectByType<BlueprintConditionsDatabase>()` every frame for the world gate |
| v1.1.0 | 2026-07-30 | Exempt `FarmingStation` + `ForestryStation`; confirmed in-game 2026-07-30 |

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
