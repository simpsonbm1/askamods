# ResourceMarkerRadiusMod (Mod 16)

**Status:** WIP (v1.1.2) — in-world radius, gather range, and **map/compass hover ring all scale
correctly**, confirmed in-game (2026-06-30) at 2x and 4x multipliers (each tested with a game relaunch
after the config edit). A
minority of markers still fall back to vanilla ring size — see handoff for the residual gap.
**Nexus ID:** [Pending]
**Handoff / open bug:** [`ResourceMarkerRadiusMod/MAP_RADIUS_HANDOFF.md`](../../ResourceMarkerRadiusMod/MAP_RADIUS_HANDOFF.md)

## Overview
Independent configurable multipliers for resource-marker radii (Wood, Stone, Foraging, Forestry,
Hunting, Settlement, Patrol). Villagers search the enlarged area, the **in-world** ground ring scales
correctly, and the **map/compass radius ring** (shown on hover) now scales too for markers the
`AddMarker` position-fallback resolver can reach. A minority of markers (`resolve FAIL
(structure=null)`) still show a vanilla-size ring — see the handoff for the runtime investigation and
the remaining gap.

## Mechanism (what works)
- `HarvestMarker.Awake` (Prefix): multiplies `__instance.radius` by the per-`AreaType` multiplier.
  Drives villager gather range + in-world ring. **Confirmed working.**
- `HarvestMarker.ShowRadiusAbsolute` / `ShowRadiusRoutine` (Prefix): feed the multiplied
  `__instance.radius` into the native ring coroutine so the in-world `RadiusSphere` + HDRP decal scale
  cleanly. **Confirmed working.**
- `HarvestMarkerPatch.AllMarkers`: every `HarvestMarker` tracked via Awake/OnDestroy (avoids the
  `FindObjectsOfType` IL2CPP crash). Used by the diagnostic and by the map-ring position fallback.

## Map ring — confirmed working via position fallback (2026-06-30)
- The hover ring is the `CompassObjectiveMarker` `range` Image, sized in
  `ObjectiveMarkerContainer.AddMarker(WorldObjectiveMarker)`: `ring sizeDelta == 2 * marker.range`,
  read **live**. So raising `WorldObjectiveMarker.range` at/-before `AddMarker` enlarges the ring.
- `marker.range` is **independent** of the gathering `radius` natively (stays vanilla 60/48/32) —
  the `AddMarker` prefix is what bridges the two.
- Reaching the `HarvestMarker` from the map marker via `WorldObjectiveMarker.structure →
  GetComponent<OutpostStructure>()` still returns null every time (dead end, confirmed), but v1.1.2's
  **nearest-tracked-HarvestMarker position fallback** (`resolve OK via=posN.Nm`) catches the majority
  of gathering markers (Wood/Stone/Foraging/Hunting/Forestry) and correctly scales the ring —
  confirmed in-game at 2x (`radius=120 -> sizeDelta=240`) and 4x (`radius=240 -> sizeDelta=480`),
  each verified after relaunching the game following the config edit (live pickup without a relaunch
  is untested). Some markers still resolve
  `structure=null` and keep a vanilla-size ring — see handoff for the residual gap.

## Config Options
Multipliers default `2×` for resource jobs, `1×` for Settlement/Patrol.
`WoodRadiusMultiplier`, `StoneRadiusMultiplier`, `ForagingRadiusMultiplier`, `ForestryRadiusMultiplier`,
`HuntingRadiusMultiplier`, `SettlementRadiusMultiplier`, `PatrolRadiusMultiplier`,
`EnableDiagnostics` (dev default `true`; flip to `false` for release).

> **Config gotcha:** GUID changed `com.askamods.*` → `simpsonbm1.askamods.*`, so two cfg files exist.
> The live one is `config\simpsonbm1.askamods.resourcemarkerradius.cfg`. BepInEx keeps the persisted
> `EnableDiagnostics`, ignoring the code default — edit the file, not just the code.

## Key findings / dead-ends
- Hooking `GatherInteraction.GatherItemsCharge` is a foolproof distance-verification diagnostic
  (`ResourceManager.FindResource` is too brittle; the AI mixes search methods).
- `Object.FindObjectsOfType<T>()` throws `MissingMethodException` on IL2CPP 2023+ — keep static lists
  via Awake/OnDestroy instead.
- Six map-ring approaches ruled out (base-class virtual-dispatch, `get_OutpostRange`, `get_range`
  getter, `StructureObjectiveMarker` overwrite, `OutpostStructure` sync, `structure.GetComponent`
  resolution). Full table + reasons in the handoff.
