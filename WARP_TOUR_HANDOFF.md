# WarpTourMod — Handoff (Mod 10)

**Status: WORKING, confirmed in-game (2026-06-24).** Standalone mod split out of SeedScoutMod (Mod 9).
GUID `com.askamods.warptour`, current version **1.0.0**, output `WarpTourMod.dll`.

## What it does
On world load — and **only if the master switch `WarpTour/Enabled` is true** — WarpTour:
1. reads the world's caves from the area map + force-loads the tiles around them so dens/lakes stream in;
2. **teleports the local player to every POI** (cave / hostile / lake) just long enough for the game's
   own proximity-discovery to fire, so the game drops its **native map pins** (correct icon + thematic
   name: `Unexplored Cave`, `Large/Small Cemetery`, `Large Spire`, `Draugar Field`, `Wulfar Den`,
   `Large Lake`, `Shipwreck`, …);
3. returns the player to spawn, holding HP steady the whole time so the accumulated fall-damage from the
   landings can't kill you.

It is a scouting/utility mod (teleports the player), so it is gated behind the master switch — leave it
**off** to play normally, flip it **on** before loading the seed you want to scout.

## Why a teleport tour (the dead-end that forced it)
Investigated under SeedScout v0.16–v0.17 (diagnostics, since reverted off Mod 9): **you cannot cheaply
flip a flag to show a POI's native pin.**
- The generic display layer is `WorldObjectiveMarker` + `WorldManager.RegisterObjective` /
  `_registeredMarkers` (every map/compass/minimap is an `IObjectiveMarkerDisplay`). `ObjectiveMarkerFlags`
  has **no** "discovered" bit — the marker is pure display.
- A POI's `MarkerObject` is **not created until the player is within ~10m** (confirmed: stationary
  force-load spawned 12 dens but **0** markers; `MarkerObject.Setup` fired only for the shipwreck ~9m from
  spawn). So there is nothing dormant to enable — the marker literally doesn't exist until proximity.
- `areaInstanceMarkerHandler` is biome/terrain-only (null on caves) — a separate earlier dead-end.
- **Therefore the only way to a native pin for an unvisited POI is to put the player there.** Hence the
  teleport tour, using `PlayerDrive.Teleport(Vector3)` (a first-class game teleport that rebuilds the
  resident area). Confirmed in-game that this produces correct native pins for every POI visited.

## How it works (the pieces)
- **Captures** (`Captures.cs`, all from each type's own lifecycle, never FindObjectsByType): `BiomesManager`
  (caves + local player transform), `WorldStreamingManager` (force-load), `Den.Start` → hostile positions,
  `BiomeLakeStampMask._OnSpawnBiome` → lake positions, `PlayerDrive.Spawned` → teleporter,
  `PlayerCharacter.Spawned` (HasAuthority) → HP.
- **Force-load** (`Warp.cs`, copied verbatim from SeedScout's proven routine): requests a radius of tiles
  around each cave so dens/lakes are known up front. Same addressing-calibration gotcha applies (address
  neighbours off the cave **tile centre** `WorldTileId.GetWorldMidPosition`, not the entrance; self-
  calibrate Lowest/Highest/Closest; drop to radius-0 if none round-trips). Config `ForceLoadRadius` (0–3).
- **Dynamic tour** (`Warp.cs` state machine Idle→Teleporting→Dwell→Draining→Returning→Done): each hop goes
  to the **nearest unvisited POI** from the player's current spot (NOT a fixed route — that missed dens that
  stream in later). Teleporting streams in MORE dens, so after the known set is exhausted it **drains**
  (the timer resets each time a new straggler appears) before returning to spawn. Visited-dedup is a ~6m
  proximity check. Landing height via `Physics.Raycast` down → terrain + 1.5m (needs
  `UnityEngine.PhysicsModule`). Safety cap 80 stops.
- **HP guard** (`ProtectHp`): snapshot HP at tour start; each frame restore `CurrentHealth` to that value if
  it dropped (the writable HP path HealthRegenMod proved). **Must persist ~10s past DONE** (`TourPostGuard`)
  — the return-to-spawn landing settles a beat after the tour flips Done and was costing ~40 HP unguarded.

## Confirmed tuning (in-game 2026-06-24)
- `DwellSeconds` (default 0.75, **code floor 0.5**): time per POI. **~0.2s CRASHES the game** (teleporting
  faster than the streamer settles the resident-area rebuild); 0.5s confirmed safe. Discovery itself is
  instant (the `markerObj Setup` fires the moment you land).
- `DrainSeconds` (default 8): dens keep streaming for ~30s after force-load; the original 3s drain ended the
  tour early (left ~10 dens unvisited). 8s caught the bulk. Raise toward 12–15 for fuller coverage.
- Coverage is good but **not provably 100%** — very-late stream-ins past the drain window can still slip.
  Untried next lever if needed: a "return to spawn → rescan → resume" loop (spawn-area dens sometimes only
  appear once you're back at spawn).
- Den → native-pin name is **thematic, not 1:1** with den type (Skeleton Den→`Large Cemetery`, Skeleton Den
  Cluster→`Small Cemetery`, Wulfar→`Wulfar Den`/`Large Spire`, Draugar→`Draugar Field`/`Large Spire`, Wight→
  `Large Spire`/nearby `Wight Shipwreck`). For authoritative den type, cross-check SeedScout's `HostileCapture`.

## Config (`BepInEx/config/com.askamods.warptour.cfg`, all live-editable)
| Key | Default | Meaning |
|---|---|---|
| `Enabled` | **false** | **Master switch (default off).** Flip true to scout; false = dormant (play normally). |
| `ForceLoadCaveTiles` | true | Force-load cave tiles before the tour to seed the route. |
| `ForceLoadRadius` | 2 | Tile rings around each cave (0–3). |
| `DwellSeconds` | 0.75 | Time per POI (floored at 0.5; ~0.2 crashes). |
| `DrainSeconds` | 8 | Straggler-wait after the last POI before returning to spawn. |

## Files (`WarpTourMod/`)
- `Plugin.cs` — BasePlugin; configs (incl. master switch); statics (Biomes/Streaming/Drive/Player +
  Hostiles/Lakes lists); registers `WarpTracker` + Harmony `PatchAll`.
- `WarpTracker.cs` — minimal injected MonoBehaviour, `Update() → Warp.Tick()`.
- `Captures.cs` — the six Harmony captures above.
- `Warp.cs` — core: collect caves, force-load, the tour state machine, HP guard.
- `MyPluginInfo.cs`, `WarpTourMod.csproj` (refs: BepInEx core, `SandSailorStudio`, `Assembly-CSharp`,
  `Il2Cppmscorlib`, `UnityEngine.CoreModule`, **`UnityEngine.PhysicsModule`**, **`Fusion.Runtime`**;
  no `UnityEngine.UI` — no overlay, that's SeedScout's job).

## Relationship to SeedScout (Mod 9)
Complementary, run both: **SeedScout** draws the manual colored-dot overlay + logs the seed score (and the
authoritative den types); **WarpTour** makes the game show its own native pins. WarpTour does its own
force-load + captures (independent mod), so if both are installed and both enabled they'll each force-load
(harmless, the streamer dedups). Keep WarpTour's master switch off except when scouting.

## Next steps / ideas
- If coverage needs to be higher: bump `DrainSeconds`, or implement the return-to-spawn rescan loop.
- Could expose a hotkey to trigger the tour on demand instead of auto-running at load (currently auto on
  `Enabled=true`).
- Note: the seed read is still `<rng-null>` (shared SeedScout dead-end) — irrelevant to WarpTour since
  you load the seed manually, but required for any future auto-finder.
