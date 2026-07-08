# SeedScoutMod (Mod 9)

**Status:** COMPLETE v1.3.0 (v1.2.1 confirmed in-game 2026-07-04; v1.3.0 is the same code with
`EnableLogging` defaulting to false for release)
**Nexus ID:** [Pending]

## Overview
At world load, reveals the game's **OWN native map pins** (correct icon + thematic name, e.g.
"Unexplored Cave", "Large Cemetery", "Wulfar Den") for every POI — caves, creature dens, enemy
camps, fishing grounds, lakes, islands, landmarks — **without moving the player**, within seconds
(home island) to ~30–40s (whole world). Pins persist in the save exactly like real discoveries.
Supersedes the WarpTour teleport-tour approach (Mod 10) for this purpose.

The old v0.x scorer / base-placement recommender / colored map-dot overlay were removed for this
release (git history ≤ v0.18.0 has them). A placement recommender may return later as a feature.

## The pin mechanism (all confirmed in-game 2026-07-04)
- A map pin is a **persistent `MarkerItem` (an `Item`)** spawned through the item pipeline —
  that's why real pins survive save/reload and render at any distance. The display side
  (`WorldObjectiveMarker`/`ObjectiveMarkerFlags`) is stateless; there is no "discovered" flag.
- Marker-bearing `AreaInstance`s carry an **`AreaInstanceMarkerHandler`** (~450 exist natively at
  load; NOT created by streaming). Setting `area.isExplored = true` and calling
  `handler.RefreshExploration()` materializes the pin.
- **Caves have no native handler** — construct one: `new AreaInstanceMarkerHandler(caveArea,
  CavesManager.caveMarkerInfo)` (public ctor), assign via
  `area.areaInstanceMarkerHandler = h.Cast<IAreaInstanceMarkerHandler>()`.
- Non-cave pin templates: `area.area.itemInfo` IS the `MarkerInfo : ItemInfo` for marker-bearing
  areas — but verify the NATIVE class (`il2cpp_object_get_class` name ends "MarkerInfo") and
  rewrap `new MarkerInfo(ptr)`; managed casts lie and `TryCast` is unavailable on
  UnityEngine.Object wrappers (see gotchas below). Handler subclasses `BiomeAreaMarkerHandler` /
  `TerrainAreaMarkerHandler` share the (AreaInstance, MarkerInfo) ctor — match by area type.
- **Tile residency is the coverage limiter**: most pins only materialize once the POI's world tile
  is streamed in. Residency-independent: caves, islands (Terrain areas), some fishing grounds.
  Handlers exist regardless; it's the marker-object SPAWN that needs the tile.

## How the mod works
1. **Enumerate** `BiomesManager._worldGenerator.GetDataMap()._areaInstances` at world-ready
   (~1450 areas; POIs, islands, seas — fully resident at load).
2. **Reveal passes** every 2s (≤30): per enabled area — ensure handler (build if cave), set
   `isExplored`, `RefreshExploration()` only while `GetMarkerObject()` is null (duplicate-pin
   guard), check the pin materialized.
3. **Targeted tile sweep**: pass 2 requests the tiles of still-unpinned areas via
   `WorldStreamingManager.RequestLoadWorldTile(reqId, anchorGO.transform, WorldTileId.GetLowest(
   pos.x, pos.y, tileSize))` (position = world meters; FLOOR tile convention). Pass 8 adds a 3×3
   ring for stragglers. No release API exists — requested tiles stay resident for the session
   (`SweepMaxTiles` caps them; ~46 tiles covers a home island, ~10²–10³ area world ≈ 30–40s full).
4. **Home-island scope** (default): only POIs inside the spawn island's bounds + 250m margin
   (catches its fishing ring). Island = the `MainIsland*` area; its `GetBounds()` Vector4 is
   **confirmed `(xMin, zMin, xMax, zMax)` directly** (interpretation A — validated in-game
   2026-07-05; the code still probes three other layouts as a defensive net, since "spawn must be
   inside" self-validates whichever one matches). Falls back to 1600m around spawn with a warning
   if none match. Exit: full coverage, 5 stale passes, or the attempt cap.
5. **Live config**: the cfg file is re-read every 5s (BepInEx does NOT auto-reload) — flipping
   `HomeIslandOnly` to false mid-session widens the reveal to the whole world within seconds,
   no relaunch (confirmed in-game 2026-07-04).

## Config (`BepInEx/config/com.askamods.seedscout.cfg`)
| Key | Default | Meaning |
|---|---|---|
| `RevealNativeCavePins` | true | Master switch (legacy key name kept — renaming would reset users). |
| `HomeIslandOnly` | true | Spawn island (+fish ring) only; flip false (live) for the whole world. |
| `SweepStreamTiles` | true | Stream POI tiles so their pins can spawn (off = sparse map). |
| `SweepMaxTiles` | 400 | Cap on resident sweep tiles per world. |
| `[RevealTypes]` Caves/Islands/Lakes/FishingGrounds/EnemyCamps/CreatureDens/Landmarks/Unknown | true | Per-POI-type toggles; disabled types get no pin AND no tile streamed. |
| `EnableLogging` | false | Diagnostic output (heartbeat, area dump, per-POI reveal lines). |

## POI type classifier (name stems from live logs, 2026-07-04)
`Habitat_Settlement{Desert,Marsh}{Tiny,Medium,Large,Stronghold}*` → EnemyCamps ·
`Biome_FishingGrounds_{Salmon,Wolfish}` → FishingGrounds · `LakeBiome_*` → Lakes ·
`Biome_ExplorationTower`/`*Shipwreck*`/`Habitat_Heads` → Landmarks ·
`Habitat_{Wolves,SkeletonsSwordSmall,SkeletonsCemetaryLarge,Wights,MarshBoss,RavenDrake,
WhaleSlug,Kyckling}`/`HabitatBear`/`HabitatStoneJotun`/`BiomeDraugars` → CreatureDens ·
Cave category → Caves · Terrain category (`{Desert,Marsh,Small,Tiny}Island*`) → Islands.
Match order matters (FishingGrounds/Lake/Settlement/Landmark before the broad Habitat/Biome
catch). Unmatched → Unknown (own toggle, future-proofing).

## Gotchas hit building this (also in architecture.md / CLAUDE.md)
- **BepInEx interpolated-string logging throws `VerificationException` on Vector3/Vector4 args**
  (`BepInExLogInterpolatedStringHandler.AppendFormatted` constraint) — `.ToString()` struct args
  in direct `Logger.Log*($"...")` calls, or route through a `(string)`-typed wrapper.
- **The "no `TryCast` on UnityEngine.Object" gotcha's root cause**: csprojs reference the
  `unity-libs` CoreModule stub (UnityEngine.Object : System.Object) while the RUNTIME loads the
  interop CoreModule (→ Il2CppObjectBase). Compile-illegal conversions work at runtime via
  box-cast: `(object)x is Il2CppObjectBase b` (confirmed in-game).
- **`AreaInstance.GetWorldTileCoordinates()` returns an empty rect** (0,0,0,0) — derive tiles
  from `area.position` instead (world meters; FLOOR convention).
- `WorldStreamingManager` has **no request-release API** (only `CancelLoading` for in-flight);
  force-loaded tiles stay resident for the session.
- Old dead-ends still stand: no bare `AddComponent<WorldObjectiveMarker>` (map freeze), no cloning
  entity-attached markers (player clone → disconnect), `areaInstanceMarkerHandler` is null on
  caves natively.

## Files (`SeedScoutMod/`)
`Plugin.cs` (configs/statics) · `Scout.cs` (reveal loop, sweep, home scope, classifier) ·
`BiomesCapture.cs` / `CavesCapture.cs` / `StreamingCapture.cs` (lifecycle captures — never
FindObjectsByType) · `ScoutTracker.cs` (injected MonoBehaviour → `Scout.Tick()`).

## Test reference
Confirmed on multiple seeds 2026-07-04. Home island: ~87 pinnable areas, 46 tiles, <15s.
Whole world: ~450 pinnable areas, ~30–40s. Save/reload keeps all pins. Version history:
0.17.0 cave-pin proof → 0.18.0 all-POI reveal → 1.0.0 strip-down (sparse regression: sweep
removed) → 1.0.1 force-load restored → 1.1.0 targeted sweep + type toggles → 1.2.x home-island
scope + live toggle → 1.3.0 release build.

## Performance (v1.3.1 — confirmed in-game 2026-07-07)

`Scout.Tick()` gated to 2 Hz (every 0.5 s at 60 FPS), feeding accumulated `deltaTime` to internal timers so the reveal pace and tile-sweep cadence remain correct. Was ticking every frame; now ~30 invocations for a full-world scan instead of ~1800.
