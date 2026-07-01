# ResourceMarkerRadiusMod (Mod 16)

**Status:** WIP (v1.1.2) — in-world radius works; **map/compass hover ring not yet scaling.**
**Nexus ID:** [Pending]
**Handoff / open bug:** [`ResourceMarkerRadiusMod/MAP_RADIUS_HANDOFF.md`](../../ResourceMarkerRadiusMod/MAP_RADIUS_HANDOFF.md)

## Overview
Independent configurable multipliers for resource-marker radii (Wood, Stone, Foraging, Forestry,
Hunting, Settlement, Patrol). Villagers search the enlarged area and the **in-world** ground ring
scales correctly. The **map/compass radius ring** (shown on hover) is still stuck at vanilla size —
see the handoff for the full runtime investigation and the next step.

## Mechanism (what works)
- `HarvestMarker.Awake` (Prefix): multiplies `__instance.radius` by the per-`AreaType` multiplier.
  Drives villager gather range + in-world ring. **Confirmed working.**
- `HarvestMarker.ShowRadiusAbsolute` / `ShowRadiusRoutine` (Prefix): feed the multiplied
  `__instance.radius` into the native ring coroutine so the in-world `RadiusSphere` + HDRP decal scale
  cleanly. **Confirmed working.**
- `HarvestMarkerPatch.AllMarkers`: every `HarvestMarker` tracked via Awake/OnDestroy (avoids the
  `FindObjectsOfType` IL2CPP crash). Used by the diagnostic and by the map-ring position fallback.

## Map ring (unsolved) — confirmed facts
- The hover ring is the `CompassObjectiveMarker` `range` Image, sized in
  `ObjectiveMarkerContainer.AddMarker(WorldObjectiveMarker)`: `ring sizeDelta == 2 * marker.range`,
  read **live**. So raising `WorldObjectiveMarker.range` at/-before `AddMarker` enlarges the ring.
- `marker.range` is **independent** of the gathering `radius` (stays vanilla 60/48/32) — the reason
  the ring never grows.
- Reaching the `HarvestMarker` from the map marker is the blocker: `WorldObjectiveMarker.structure →
  GetComponent<OutpostStructure>()` returned null every time, and the `OutpostStructure.objectiveMarker`
  sync never fired. v1.1.2's `AddMarker` prefix now tries self/parent/child + a nearest-HarvestMarker
  position fallback, with diagnostics logging *why* it fails. **Next test decides it — see handoff.**

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
