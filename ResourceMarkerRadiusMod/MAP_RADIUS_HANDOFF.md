# Map Radius Handoff (ResourceMarkerRadiusMod)

**Status:** WIP (map ring now scales, but not for every marker) — in-world radius, gather range, and
**map/compass hover ring all scale correctly** for markers the `AddMarker` resolver can reach.
**Deployed version:** v1.1.2 (diagnostics ON via config; see bottom).
**Last verified in-game (2026-06-30):** map ring confirmed scaling at 2× (`radius=120` → `sizeDelta=240`)
and again at 4× (`radius=240` → `sizeDelta=480`) after editing the config and **relaunching the game**
(config changes were not tested for live pickup without a relaunch — assume a relaunch is required).
Some markers still fall back to vanilla ring size (`resolve FAIL (structure=null)`).

> This handoff supersedes the earlier speculative version. The facts in **"Confirmed at runtime"**
> come from live `[MapDiag]` / `[MapFix]` logging in v1.1.0–v1.1.2 — trust them over the older guesses.

---

## The issue
`HarvestMarker.Awake` multiplies the **in-world** radius (villagers gather farther ✓, in-world ground
ring scales ✓). But the **map/compass radius ring** shown when hovering a gathering marker icon stays
vanilla size.

## Confirmed at runtime (the important part)
1. **The hover ring IS the `CompassObjectiveMarker` `range` Image**, sized inside
   `ObjectiveMarkerContainer.AddMarker(WorldObjectiveMarker)`. Measured relationship, dead flat:
   ```
   ring _rangeTransform.sizeDelta == 2 * marker.range     (localScale = uniform map-zoom factor)
   range 60 -> sizeDelta 120 | range 48 -> 96 | range 32 -> 64
   ```
   So **to enlarge the ring you only need to increase `WorldObjectiveMarker.range`** before/at
   `AddMarker`. `AddMarker` re-fires every time the map/compass rebuilds icons, and it reads `range`
   **live** (a prefix that changes `range` is read by the very next native sizing step).
2. **`marker.range` is INDEPENDENT of the gathering `radius`.** Observed native ranges 60/48/32 are
   unchanged by our multiplier — that is exactly why the ring never grows.
3. The marker handed to `AddMarker` reports `GetType().Name == "WorldObjectiveMarker"` (not
   `StructureObjectiveMarker`).
4. **Our `OutpostStructure` sync never ran** — 0 `[Diagnostic] Syncing map marker range` lines across a
   full session. `TrackedOutposts` was empty and/or `objectiveMarker` != the map's marker instance.
   The whole `OutpostStructure.objectiveMarker`/`StructureObjectiveMarker` avenue is a dead end.
5. **`WorldObjectiveMarker.structure` → `GetComponent<OutpostStructure>()` returns null every time**
   (`resolve OK = 0` in v1.1.1). We could not reach the `HarvestMarker` from the map marker that way.

## Data-flow model (current best understanding)
```
HarvestMarker.radius   ← multiplied by us (Awake). Drives gather + in-world ring + ShowRadius*. WORKS.
WorldObjectiveMarker.range  ← drives map/compass ring via AddMarker (sizeDelta = 2*range). NOT linked
                              to radius; stays at vanilla 60/48/32. THIS is what must be raised.
```

## What is deployed now (v1.1.2)
- `HarvestMarkerPatch`: Awake radius multiply, `ShowRadiusAbsolute/Routine` prefixes, `AllMarkers` list
  (all HarvestMarkers, tracked via Awake/OnDestroy). **All working.**
- `OutpostStructurePatch` + `ResourceMarkerRadiusTracker`: register outposts (Awake/Activate/
  _RefreshRanges) and poll `objectiveMarker.range = harvestMarker.radius`. **No-op in practice — leave
  or delete; it is not the fix.**
- `ObjectiveMarkerContainerPatch` (in `MapMarkerDiagnostics.cs`): **the live attempt.** `AddMarker`
  PREFIX resolves the marker's `HarvestMarker` and writes `radius` into `__0.range`. POSTFIX logs the
  resulting ring. v1.1.2 upgraded the resolver to try `OutpostStructure` via
  self/parent/child **and** a nearest-tracked-`HarvestMarker`-by-position fallback (5 m), and logs
  *why* a lookup fails (`[MapFix] AddMarker resolve FAIL (...)`).

## Confirmed in-game (2026-06-30) — SOLVED for the majority path
The `AddMarker` prefix's **position fallback** (`resolve OK via=posN.Nm ...`) is what's actually firing for
gathering markers in practice — not the `structure`/self/parent/child path, which still resolves `null` for
every marker observed so far. Log evidence, same session, multiplier changed live from 2× → 4×:
```
resolve OK via=pos0.0m area=WOOD radius=120.0 oldRange=120.0
built ring range=120.0 sizeDelta=(240.00, 240.00) ...        (2x: 60 vanilla -> 120)
...
resolve OK via=pos0.0m area=FORAGING, SETTLEMENT radius=240.0 oldRange=240.0
built ring range=240.0 sizeDelta=(480.00, 480.00) ...        (4x: 60 vanilla -> 240, after relaunch)
```
User confirmed visibly larger rings in-game at 4× vs. 2×, testing each value with a game relaunch after
the config edit. `sizeDelta == 2*range` held in both cases. Whether the config can be picked up live
without a relaunch is untested.

## Remaining gap — `resolve FAIL (structure=null)` markers stay vanilla size
Some markers (observed at vanilla ring sizes 32/48) never get a `.structure` reference at all, so neither
the `OutpostStructure` path nor the position fallback catches them, and their map ring stays unscaled.
**Next step if revisiting:** widen the position-fallback threshold (currently 5 m), or resolve via
`ObjectiveIcon.trackTransform`/`_wMarker` instead of `.structure`. Not urgent — majority of gathering
markers (Wood/Stone/Foraging/Hunting/Forestry) already resolve correctly via position fallback.

## Release cleanup (once the structure=null gap is accepted or fixed)
Delete the dead `OutpostStructurePatch`/`ResourceMarkerRadiusTracker` polling sync (confirmed no-op —
0 hits) and flip `EnableDiagnostics` to `false` in the live cfg.

## If the ring STILL won't grow even after `range` is provably raised
Then this specific hover circle is drawn by a **different pipeline** than `CompassObjectiveMarker`, and
`range` isn't its input. Pivot to the map manager's own circle renderer (Antigravity's lead, not yet
disproven for the *full-map* area circle as opposed to the compass ring):
- `UIGameMapManager` — has a ComputeShader + `_circlesCache` NativeArray and `SetupAreaInstancesMarkers`.
  It may draw area circles independently, keyed off `MarkerInfo.markerRange` / the item template.
- `MapMenu.ShowAllRangesToggle` may use yet another path.
- Dump these with the `_explore` Cecil scripts (`cc_type.ps1 "SSSGame.UIGameMapManager"`, etc.).
  NOTE: interop DLLs are **trampoline stubs only — no IL bodies**; reason from field/method signatures
  + runtime logging, not decompiled logic.

## Dead ends (do not re-tread)
| # | Attempt | Why it failed |
|---|---------|---------------|
| 1 | Patch `WorldObjectiveMarker` base lifecycle (`OnEnable`/`Deserialize`/`Refresh`) | IL2CPP virtual dispatch — base-class patch misses derived `StructureObjectiveMarker` instances; logs never fired |
| 2 | Patch `OutpostStructure.get_OutpostRange` | Map UI never queries it |
| 3 | Patch `WorldObjectiveMarker.get_range` (C# getter) | Native engine reads the raw field, bypassing the interop C# property; only other mods' reads are intercepted |
| 4 | Overwrite `StructureObjectiveMarker.OnEnable/Initialize/_OnOwnerStructureOutpostDataChanged` range | Wrong object/never confirmed to run; map uses a `WorldObjectiveMarker` from `AddMarker`, not that instance |
| 5 | `OutpostStructure._RefreshRanges/Activate/Awake` register + polling sync `objectiveMarker.range = harvestMarker.radius` | Sync **never fired** (0 lines); tracker empty and/or `objectiveMarker` != map marker |
| 6 | `AddMarker` prefix resolving via `WorldObjectiveMarker.structure.GetComponent<OutpostStructure>()` | Returned null every call (`resolve OK = 0`) — OutpostStructure not on that exact GameObject |

## Test / diagnostics notes
- Log: `D:\SteamLibrary\steamapps\common\ASKA\BepInEx\LogOutput.log`.
- **Config gotcha that burned a cycle:** the GUID changed from `com.askamods.*` to `simpsonbm1.askamods.*`,
  so there are TWO cfg files. The LIVE one is `config\simpsonbm1.askamods.resourcemarkerradius.cfg`.
  BepInEx keeps the **persisted** `EnableDiagnostics` value and ignores the code default, so flipping
  the default in code does nothing if the file already has it. It is currently set to `true` in that
  file. The stale `com.askamods.*.cfg` is dead and can be deleted.
- **SAC:** every rebuild must bump `PLUGIN_VERSION` + csproj `<Version>` or Smart App Control reblocks
  the identical hash. Confirm the loaded version string in the log before trusting a test.
- Multipliers default 2× (Wood/Stone/Foraging/Forestry/Hunting), 1× (Settlement/Patrol).
