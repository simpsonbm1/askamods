# TerrainLevelerMod — Session Handoff (2026-07-01)

**Status:** WORKING for normal use, un-parked in practice, **NOT committed yet**. One open bug
(aggressive-drag crash). Current build: **v1.3.12** (live DLL deployed + confirmed loaded in-game).

## What is now WORKING (verified in-game this session)
The mod was previously PARKED as "impossible." It now does everything the user asked, on a single
hit (press E on a placed terraforming grid):
1. **Clean flatten** of the whole grid to a flat plane.
2. **Obstacle clearing** — trees, harvestable rocks, AND the fixed "invincible" rocks.
3. **Grid marker/UI dismissed** after leveling.
4. **Player + villagers unharmed** (hostile creatures DO die — user is fine with this).

## The working recipe (key discoveries)
- **Flatten:** `HeightmapTool.RunOnArea(TerraformingToolOperation.LEVEL, width, depth, 0, center, 0)`
  over the grid footprint. Get the tool from `TerraformingFieldInteraction.heightmapTool`.
  - DEAD END: the `TerraformingGrid` / `TryLevelTile` path. `TryLevelTile`/`MarkCellInteracted` are
    **inlined by the IL2CPP AOT compiler** (Harmony patches never fire). Leveling only routes through
    `TerraformingFieldInteraction.Use(IInteractionAgent)` — hook THAT (postfix). Driving `TryLevelTile`
    directly corrupts the mesh into spikes; `GetCellAverageHeight` is a fixed baseline (useless as a
    convergence signal); `IsCellLeveled` returns true ~1.5 m off target; `Rpc_DebugCompleteField`
    finalises the field but does NOT deform terrain.
- **Obstacles:** build our own `SSSGame.Combat.AOESpell` (Box shape sized to the grid) and cast it via
  the player's `SpellsManager.CastSpellOnPos(aoe, ref pos)` — the proper entry point (calling
  `OnDetonated` directly NRE'd). Key spell config:
  - `clearItemInstances = true` (clears the fixed WorldItemInstance rocks via data layer)
  - `dealDamageToHarvestables = true` + `baseDamage = 999999`
  - **`skipAllCollisionChecks = false`** ← critical; true skips `CollisionCheck()` (the overlap that
    gathers harvestable targets), so trees survived while static rocks cleared.
  - `friendlyFire = false`
  - `damageMask` = all layers EXCEPT layers of any `Character` (player/villagers/creatures) found via an
    `OverlapBox` in the blast box — this is how player/villagers are protected. See
    `BuildLivingExclusionMask`.
  - Cast `BombShots` times (default 2): first knocks trees to stumps, second clears stumps + rocks.
- **Getting the player's `SpellsManager`:** it is a **standalone Fusion network object**, NOT under the
  player hierarchy (GetComponent searches fail). Capture it from a Harmony patch on
  `SpellsManager.Awake` (register every instance into a list), then pick `IsPlayer && HasAuthority` at
  cast time. **DO NOT** patch its Fusion state-sync methods (`CopyBackingFieldsToState` /
  `CopyStateToBackingFields`) — that HANGS the game at load (learned the hard way, v1.3.5).
  - NOTE: in testing the registry picked a `SpellsManager` on `WorldDataManager` (authority fallback,
    IsPlayer was false) — casting through it still worked. Fine, but see if the true player one is
    better for team-based friendly-fire later.
- **Grid UI dismiss:** `grid._state.Rpc_DebugCompleteField()` after the flatten.

## THE OPEN BUG (where we stopped)
**Symptom:** place the marker, then run away to grow the grid bigger and bigger — eventually the game
**hard-crashes** (no managed exception in log, native crash) instead of just turning red/unplaceable at
the max. Reproduced on v1.3.12. This is the same class as the documented, parked engine limits
(Fusion NetworkArray >512, mesh-NaN normals over steep terrain, OverlapBoxNonAlloc overflow) — the
user's world is a **quarry with steep walls**, so aggressive drag spans a big vertical difference.

**All 4 of Antigravity's documented fixes ARE present in the current Plugin.cs and were verified by
reading the code:**
1. Turn-red at 512: `_OnSnap` blocks placement if `gridSize.x*gridSize.z > 512`;
   `TerraformingGrid.OnDynamicBuildingDimensionsChanged` returns false (freezes) if `GetCellsCount() > 512`.
2. `_ValidateFootprint` prefix always sets `__result = true` (bypasses OverlapBoxNonAlloc overflow).
3. Y-clamp: `_OnBuildingDimensionsChanged` clamps vertical stretch to 15 m (+ 20 m horizontal tether).
4. `maxNumberOfTiles = 512` on `CreatePreview`/`Create`.

**What changed this session re: the bug:** the crash reappeared when the user set the old
`BypassHeightLimits=false`. Turned out several protections (incl. `maxHeightDifference=10000` in
`Begin`/`_OnBuildRayChanged`, `_ValidateFootprint` bypass, `CheckLevelDifference` bypass) were **gated
behind that toggle**. v1.3.12 removed the toggle and made ALL of them **unconditional** (always the
known-good "true" behaviour). Despite that, aggressive drag STILL crashed on 1.3.12.

**UNRESOLVED QUESTION → this is where to resume:**
My code matches Antigravity's *summary* of the fix exactly, yet still crashes. So Antigravity's *actual*
code must differ from its summary in some detail (most likely the **numbers**: tether distance, Y-clamp
meters, tile cap, `maxRange`, `maxHeightDifference`). Need to determine:
- **Is Antigravity editing the SAME `Plugin.cs` on this machine, or a separate copy on the other machine?**
  (User works across 2 machines, Antigravity + Claude Code, syncing via git. My session changes are
  UNCOMMITTED, so the other machine doesn't have them.)
- Get Antigravity's **actual** method bodies for `_OnBuildingDimensionsChanged`, `_OnSnap`,
  `OnDynamicBuildingDimensionsChanged`, and the `maxRange`/`maxHeightDifference` values — diff against
  ours to find the real difference. If it's a separate working copy, have Antigravity commit+push it.

Leading hypothesis to test next: it may simply be the documented engine limit that only bites on
**aggressive drag across a steep cliff** (mesh-NaN), which the reactive marker-clamp can't fully prevent;
the mod is stable for normal placement. A pragmatic mitigation is lowering `MaxDragRange` (currently 20)
toward vanilla (~10-12) to keep the grid inside the stable envelope. Antigravity's version may use
smaller numbers — hence "turns red" before reaching crash territory.

## Config (current, in Plugin.cs)
`General`: `MaxDragRange`(20), `OneHitClear`(true), `MaxPassesPerTile`(256, now unused by flatten),
`UseDebugComplete`(false, experimental/unused). **Removed:** `BypassHeightLimits`, `MaxGridTiles`.
`Obstructions`: `ClearObstructions`(true), `BombShots`(2), `ClearVerticalRange`(30),
`ClearDiagnostics`(false — flip true for `[Bomb]`/`[Flatten]` logs), plus legacy `BruteForce*` (unused).
> NOTE: user's live config still has a stale `BypassHeightLimits` line — harmless, ignored.

## Dead code to clean up (deferred)
`ClearObstructionsInGrid`, `TryHarvestDestroy`, `FindNamedTarget`, `ParseFilterTokens` (legacy physics
sweep, replaced by the bomb), `LogFlattenState`/`SampleCell`/`SafeCellHeight` (old flatten diag),
`UseDebugComplete` + `MaxPassesPerTile` + `BruteForce*` configs. All unused but harmless.

## TODO at the real finish line (commit checkpoint — NOT done yet)
Per CLAUDE.md rituals, once the crash is resolved & confirmed in-game:
1. **Un-park** TerrainLevelerMod in BOTH `CLAUDE.md` and `.agents/AGENTS.md` (status → WORKING v1.x,
   describe HeightmapTool + AOESpell recipe). Keep them in sync (Ritual 2/3).
2. Rewrite `docs/mods/terrain-leveler.md` (currently says PARKED) with the working recipe above.
3. Update `docs/architecture.md` "Terrain / Terraforming Dead Ends" — several are now SOLVED; add the
   new confirmed facts (HeightmapTool.RunOnArea LEVEL, AOESpell via SpellsManager.CastSpellOnPos,
   skipAllCollisionChecks meaning, Awake-capture of SpellsManager, DebugCompleteField doesn't deform,
   TryLevelTile inlined/corrupts, DON'T patch Fusion Copy*State methods).
4. Check `sync-plugins.ps1 $ParkedByDefault` (TerrainLevelerMod not in it; the live `.dll.off` was
   deleted this session so it installs active).
5. Commit + push (source, built `TerrainLevelerMod.dll`, docs) — **only with user go-ahead.**

## Session build log (for context)
1.2.0 flatten-loop attempt → 1.2.2 hooked Use (probes found only Use fires) → 1.2.3 tried
DebugCompleteField (no deform) → 1.2.4/1.2.5 round-based loop (still lumpy/spiky; TryLevelTile
corrupts) → 1.2.6 **HeightmapTool flatten WORKS** → 1.2.7 grid-UI dismiss → 1.3.0-1.3.8 bomb via
AOESpell (interop OverlapBox ref fix; SpellsManager capture; skipAllCollisionChecks=false;
living-layer exclusion) → **1.3.8 full feature verified in-game** → 1.3.9 diag default off →
1.3.10/1.3.11 (bad crash re-diagnosis, reverted) → **1.3.12 un-gated all protections (current)**.

Also this session: fixed ResourceMarkerRadiusMod chatter — user had edited the stale
`com.askamods.resourcemarkerradius.cfg`; the live GUID is `simpsonbm1.askamods.resourcemarkerradius`,
set `EnableDiagnostics=false` in that active file.
