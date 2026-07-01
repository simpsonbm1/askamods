# Map Radius Handoff (ResourceMarkerRadiusMod)

**Status:** WIP — in-world radius works; **map/compass hover ring still shows vanilla size.**
**Deployed version:** v1.1.2 (diagnostics ON via config; see bottom).
**Last verified in-game (2026-06-30):** map ring unchanged. In-world ring + villager gather radius work.

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

## NEXT STEP (do this first, on the other machine)
1. Launch, confirm `v1.1.2 is loaded`, open map, hover a couple of gathering markers.
2. Read the log: `Select-String LogOutput.log -Pattern 'MapFix'`.
   - **`resolve OK via=structure ... radius=X oldRange=Y`** → resolver now works. Check the POSTFIX
     `built ring` line: if `sizeDelta` grew to `2*X`, and the ring is visibly bigger → **SOLVED**
     (then just delete the dead OutpostStructure tracker and flip diagnostics off for release).
   - **`resolve OK via=posN.Nm ...`** → structure link still broken but position fallback caught it;
     same success check on the ring.
   - **`resolve FAIL (structure=null)`** → the map marker has no `.structure`; rely on the position
     fallback (widen threshold, or verify `marker.transform.position` is really co-located with the
     HarvestMarker — if not, match via `ObjectiveIcon.trackTransform`/`_wMarker` instead).
   - **`resolve FAIL (struct='X' but no OutpostStructure ...)`** → log/inspect what `X` is; the
     gather-area structure type may not be `OutpostStructure` at all.

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
