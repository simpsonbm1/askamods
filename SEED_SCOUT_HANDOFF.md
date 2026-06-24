# SeedScoutMod — Handoff (Mod 9, WIP)

**Last build: v0.12.0** (built, deployed, **CONFIRMED working in-game 2026-06-24**). v0.12.0 adds the
**force-load** feature (see its section below — it works) and recolors cave dots **gray** (`0.78,0.78,0.80`
— cyan was too close to lake blue). **`Den` hostile capture is now verified too** (dens logged at sane
coords, red dots drawn); `HostileVikingSettlement`/EnemyCamp still unverified (none in the test seed).
Status: a working **in-world seed "scorer"** + **in-game map overlay**. Not a gameplay mod — it
reads worldgen data, logs analysis to `LogOutput.log`, and draws POI dots on the map. No game
state is changed (overlay is pure UI; co-op-safe).

## Origin / goal
User question: *"can we determine ASKA's worldgen seeding to find a seed with a lake near a mine
near spawn?"* Pure seed→outcome prediction is impractical (IL2CPP native, deterministic but
complex). Pivoted to: **load a seed → identify caves (mines) + lakes + hostiles → score by
distance from spawn → show on the in-game map.** User decisions:
- Scope: **"scorer now, decide auto-finder later."**
- "Mine" = **cave entrance** (primary). Surface ore = secondary (no worldgen primitive for ore;
  it's mostly cave-bound `BiomeItemInstance` anyway).
- Lakes: investigated a map-level source — **not available cheaply** (see below).
- Add **hostile areas** (dens/enemy camps) as a danger term in scoring.

## What works (verified in-game)
- **Caves (mines): fully known at world-load, no streaming.** Enumerate
  `BiomesManager._worldGenerator.GetDataMap()._areaInstances` (a `Dictionary<int, AreaInstance>`),
  filter `AreaInstance.TryCast<CaveAreaInstance>()`. ~3 caves per world.
- **Coordinate system: `AreaInstance.position` (Vector2Int) = WORLD METERS (x, z)** — NOT tile
  coords. Verified twice against ground truth (player standing at cave entrance ≈ `cave.position`
  within ~10 m). `tileSize` (=128) is irrelevant to `position`.
- **Map overlay (the UX win):** plain `UnityEngine.UI.Image` dots parented to `MapMenu.Content`,
  positioned via `MapMenu.GetMappedPosition(Vector3 world) → Vector2`. Caves = **cyan**.
  Confirmed working (user: "the blue marker worked… got the job done"). Pure UI; cannot freeze
  or disconnect.
- **Score log** at load + as you explore: per-cave `spawnDist`, `nearestLake`, `nearestHostile`.

## What's per-tile / streamed (fills in as you explore, like the lakes do)
- **Lakes:** `BiomeLakeStampMask`, captured at `_OnSpawnBiome`. Drawn **blue**. Lake-near-cave
  only becomes known once you walk near that cave's tiles.
- **Hostiles (v0.11.0, UNVERIFIED):** `Den` (animal den / "wolf spawner") at `Den.Start`;
  `HostileVikingSettlement` (enemy camp) at `Awake`. Drawn **red**. Folded into the score as
  `nearestHostile`.

## Force-load (v0.12.0 — CONFIRMED working 2026-06-24)
**Goal:** eliminate the "run to every cave to load the rest of the map" step. Caves are fully known
at load, but lakes/hostiles only spawn when the `WorldStreamingManager` streams a tile near its
tracked actor — that's why they fill in as you walk. The idea: make those tiles stream *in place*,
without moving the player, so the existing lake/den hooks fire.

**What v0.12.0 does:** at world-load (3 s after caves are known), for each cave it calls
`WorldStreamingManager.RequestLoadWorldTile(reqId, anchorTransform, CaveAreaInstance.WorldTileId)`,
parking a hidden GO at the cave as the anchor. One-shot per world, gated by config
`SeedScout/ForceLoadCaveTiles` (default true). Requests are **not** released (tiles stay resident so
the markers stick). Verbose logs: `SeedScout force-load: requested tile … -> WorldTile|null`.

**RESULT (test seed, 3 caves at 468/641/1120 m):** all 3 requests returned `WorldTile`, and **dens +
a lake spawned at the distant cave tiles while the player never moved** (every heartbeat stayed at
`player=(-209.9,33,-13.7)`). So the open question is answered **YES** — a *requested/resident* tile
instantiates its biome/den/lake entities; it is **not** gated on visibility near the player. The map
showed caves/lakes/hostiles near every cave without exploring. The teleport-tour fallback is therefore
**not needed** for this purpose (keep it in the back pocket only if fuller coverage ever demands real
presence).

**Known limitation → next lever:** `RequestLoadWorldTile` loads only the **single tile** containing the
cave (~128 m square). A lake/den just *outside* that tile still won't show. To widen coverage, request a
**ring of neighbour tiles** around each cave (config a radius: 0 = cave tile only, 1 = 3×3, 2 = 5×5).
Address neighbours via `WorldTileId.GetClosest(cave.x ± n·tileSize, cave.y ± n·tileSize, tileSize)` or
`WorldTileId.WorldXY` grid offsets. Tradeoff: more resident tiles = more memory/streaming work.

**Confirmed streaming API (types/members verified to exist via Cecil dump):**
- `SandSailorStudio.Streaming.WorldStreamingManager` (MonoBehaviour): tracks one `IStreamingActor
  TrackedActor` / `Transform TrackedTransform`; `SetTrackTransform(Transform)`, `_UpdateTrackedPosition`,
  `SetStartPosition`, `SetIdlePosition`, `IsTeleporting`. Tile ops: `RequestLoadWorldTile(int, Transform,
  WorldTileId) -> WorldTile`, `GetTile(int x,int y)` / `GetTile(float worldX,float worldZ)`,
  `RegisterWorldTile(WorldTileId)`, `CancelLoading(WorldStreamingTile)`, `StopAllStreaming()`. Resident
  vs visible vs active tile sets are distinct (`_activeTiles`, `ResidentArea`, `_visibleDistance`,
  `_criticalTiles`) — the crux of the open question above.
- `WorldTileId` (struct): `GetClosest/GetHighest/GetLowest(worldX, worldZ, worldTileSize)`, `WorldXY`,
  `GetWorldPosition(tileSize)`. **`CaveAreaInstance.WorldTileId`** gives each cave's tile directly (no
  need to compute from position).
- Reaching the instance: captured via `WorldStreamingManager.Awake` (StreamingCapture.cs). Also held by
  `MapMenu._streamingManager`, `UIGameMapManager._worldStreamingManager`, `WorldDataManager.streamingManager`.
- **Teleport path (for the fallback):** the player's `PlayerDrive` IS the `IStreamingActor` and exposes
  `Teleport(Vector3)` / `IsTeleporting()` / `_Teleport()` — a first-class game teleport (handles the
  resident-area rebuild), not a raw transform write. `SailingShip.Rpc_Teleport*` exists for boats.
- `SSSGame.StreamingInstigator` (MonoBehaviour): the game's own "force-stream at this point" primitive
  (`Request()` / `RequestFromEvent()`, holds a `WorldStreamingManager` + own transform) — an alternative
  force-load mechanism if `RequestLoadWorldTile` proves insufficient.

## Confirmed architecture (so the next session doesn't re-derive it)
- **Worldgen is deterministic, seed-phrase driven.** `SandSailorStudio.RNG.RandomGeneratorManager`
  (`SetSeedPhrase`, `GetGenerator`), `SandSailorStudio.WorldGen.Deterministic.*`,
  `PerlinGeneratorModule`. Seed string → hashed per-subsystem RNGs.
- **No live preview in the create-world UI.** `NewGameTabPage` is just Name + Seed field +
  Generate + Start; generation happens at **world LOAD** (`GameState_Load`). `WorldPreviewScene`
  exists but is a dev/editor tool, **not** in the player flow — do not try to ride it.
- **`WorldDataMap.GetWorldAreaInstances()` returns 0 post-load**, and `_worldInstances` is empty.
  The real area set lives in **`_areaInstances`** (≈1300–1470 entries). Type tally:
  `Other`≈islands (named `MainIsland`, `DesertIsland_Ravendrake`, `MarshIsland_Boss`,
  `SmallIsland-<id>`), `Terrain`≈70, `Sea`≈15–20, `Cave`=3.
- **Spawn point:** currently approximated by the player's first-known position (≈ spawn at load).
  True spawn = `BiomesManager.GetPlayerStartWorldPositionAsync` (async coroutine, not yet wired).
- **No whole-world lake/hostile source resident at load.** `LandMask.GetCellType(x,z)` knows
  `LandMaskCellType.LAKE`, but the landmask is built **per-tile on exploration**
  (`UIGameMapManager._UpdateTileLandmask`); no member anywhere is typed `LandMask` (nothing holds a
  whole-world one). Dens are `NetworkBehaviour` creatures; `PopulationManager._densMap` /
  `_biomePopulations` are resident, not whole-world.

## Dead ends — DO NOT retread
- **Bare `AddComponent<WorldObjectiveMarker>()`** → NRE in `ObjectiveIcon.CheckFilter` →
  **froze the map (~1 fps)**. Bare markers lack the prefab's child icon objects.
- **Cloning a real marker:** the player's marker is a component **on the player character GO**
  (`CharacterRagnar`). `Instantiate` cloned the whole player → rogue networked players →
  **"disconnected from server."** Never clone entity-attached markers. (→ this is why we switched
  to the pure-UI map overlay, which works.)
- **Seed read is UNRESOLVED.** `RandomGeneratorManager.seedPhrase` reads `"<rng-null>"` — the
  manager isn't captured via `OnEnable` and `SetSeedPhrase` is never called during load, so the
  seed reaches the generators by another path. `WorldConfiguration...seedPhrase` is just the
  default placeholder. Non-blocking for the scorer (you type the seed, so you know it); **required
  for any auto-finder.** Leads to chase next time: `SettingsMenu._SetupSeedLabel()` /
  `SettingsMenu.CopyCurrentSeed()` (the game shows the current seed in-game, so it IS readable);
  `CavesManager._worldSeedHash` is an int hash.

## Build/deploy gotchas hit
- **SAC blocks fresh DLL hashes intermittently** (`0x800711C7`). Fix = bump `PLUGIN_VERSION`
  (MyPluginInfo.cs) **and** `<Version>` (csproj) → new hash → SAC re-evaluates. Confirm the loaded
  version in `LogOutput.log`. Happened at 0.9.0 (→0.9.1 fixed it).
- **IL2CPP interop quirks:** `WorldDataMap.GetWorldAreaInstances<T>()` and
  `AreaInstancesContainer.GetAreaInstances<T>()` are exposed **generic** — call `<AreaInstance>`.
  `GetGeneratedBounds(out RectInt)`. `AreaInstance.TryCast<CaveAreaInstance>()` is valid (it's a
  plain `Il2CppSystem.Object`, not `UnityEngine.Object`). Keep injected MonoBehaviours minimal
  (only `Update`) — methods with unsupported param types log interop warnings.
- **csproj references needed:** BepInEx core set, `SandSailorStudio`, `Assembly-CSharp`,
  `Il2Cppmscorlib`, `UnityEngine.CoreModule`, **`UnityEngine.UI`** (map dots), **`Fusion.Runtime`**
  (`Den : NetworkBehaviour`).

## File map (SeedScoutMod/)
- `Plugin.cs` — BasePlugin; registers `ScoutTracker`, Harmony `PatchAll`; static refs + POI lists;
  `EnableMapMarkers` config (toggles overlay).
- `ScoutTracker.cs` — minimal injected MonoBehaviour, `Update() → Scout.Tick()`.
- `Scout.cs` — core. Polls captured `BiomesManager`; at world-ready dumps `AREA INSTANCES`
  (caves + type tally) and a recurring `SCORE` block; exposes `Scout.Caves` for the overlay.
- `BiomesCapture.cs` — capture `BiomesManager` (Awake/OnDisable) → `_worldGenerator`,
  `_localPlayerTransform`.
- `LakeCapture.cs` — `BiomeLakeStampMask._OnSpawnBiome` → `Plugin.Lakes`.
- `HostileCapture.cs` — `Den.Start` + `HostileVikingSettlement.Awake` → `Plugin.Hostiles` (NEW,
  unverified).
- `CavesCapture.cs` — `CavesManager.RegisterCaves` (streamed cross-check; harmless).
- `StreamingCapture.cs` — capture `WorldStreamingManager` (Awake) → `Plugin.Streaming` (force-load).
- `RngCapture.cs` — seed-read attempt (currently null; see dead ends).
- `MapOverlay.cs` — `MapMenu` OnActivate/OnUpdate/OnDeactivate/OnClosed → draw caves(gray)/
  lakes(blue)/hostiles(red) dots.
- `Scout.cs` also owns the **force-load** routine (`ForceLoad()` + `_caveTiles`/`_reqGos`).

## Next steps (priority order)
0. **DONE — force-load verified (v0.12.0).** Next lever for *range*: expand force-load from the single
   cave tile to a **ring of neighbour tiles** (config radius) so features just outside the cave's tile also
   spawn (see the limitation note in the force-load section). Teleport tour is no longer needed for this.
1. **Verify `HostileVikingSettlement`/EnemyCamp capture** (Den is now confirmed). Re-test on a seed that
   has an enemy camp. Do `Den`/`EnemyCamp` log at
   sane positions (not `(0,0)`)? Do red dots land on the actual den/camp? **`HostileVikingSettlement`
   is hooked at `Awake`, which may run before positioning** — if the camp lands at origin, rehook to
   a post-positioning method (`UpdateTerrainAnchors` / `_EnsureStructurePositions` /
   `_CreateStructures`).
2. **Collapse the per-cave numbers into one rankable score** (define weights with the user, e.g.
   reward low `spawnDist`+`lakeDist`, penalize low `nearestHostile`).
3. **Polish map dots** — icon/label instead of plain 16px squares; maybe a legend.
4. **(For auto-finder, later)** Resolve the seed read; build a load→read→next loop (each seed ≈ one
   world load, ~20–40 s; hundreds–low-thousands overnight). Also wire true spawn via
   `GetPlayerStartWorldPositionAsync`.

## Tooling
`_explore/` has Mono.Cecil inspectors run via PowerShell against `BepInEx/interop`:
- `dump.ps1 -Types @("Full.Type.Name", ...)` — fields/props/methods of specific types.
- `search.ps1 -TypeKeywords @(...) [-MemberKeywords @(...) -Members]` — find types/members by keyword.

## Test reference
Seeds reliably yield 3 caves; coordinate accuracy ~10 m, confirmed twice. Example seed (3 caves):
`TlV7tkbxQBXRcKHc5HCRkNmLobJ9SuDqGWztm1DgR8uCE66C`. **Seed must be typed manually** — the mod can't
read it yet. To reproduce a world, type the same seed in the create-world Seed field.
