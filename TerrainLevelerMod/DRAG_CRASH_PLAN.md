# TerrainLevelerMod — Drag Crash: CONFIRMED Root Cause & Implementation Plan (2026-07-01)

**Status:** **RESOLVED — confirmed in-game by the user 2026-07-01.** Root cause confirmed from crash-dump
evidence, fix implemented by a Sonnet session (v1.3.21, then v1.3.22 after Phase 4 cleanup: dead code
removed, diagnostics defaulted back to off). See `docs/mods/terrain-leveler.md` for the current shipped
recipe and `docs/architecture.md`'s Terrain/Terraforming section for the durable facts/dead-ends.
Originally written by a Fable session for implementation by Opus/Sonnet. Read this whole file before
making further changes.

---

## Root cause (confirmed)

**The drag preview's per-tile validity is stored in a fixed 256-bit Fusion struct
(`BitSet256 _GridValidity`) on `SSSGame.Network.NetworkDynamicDimensionBuildingState_256`. When the
dragged grid exceeds 256 tiles, `UpdateGridValidityOnNetwork` writes validity bits past the struct →
network-state/heap corruption → hard native crash.** Every guard in the mod (v1.1.21 through v1.3.20)
compared tile counts against **512** — the capacity of a *different* variant (`_512`, used by other
building types) — and/or guarded the wrong layer entirely, so the game corrupted itself at ~257+ tiles
long before any guard tripped.

### Evidence chain
1. **Windows Error Reporting** (Application log, provider "Application Error") holds 12 Aska.exe crashes
   across 06-30 → 07-01. Signatures:
   - Most: `0xc0000005` wild jump — faulting module **unknown, offset 0x0** (call through corrupted
     pointer = delayed heap-corruption fallout).
   - Two recurring **deterministic** sites: `GameAssembly.dll+0xf975f2` (06-30 12:58, 06-30 14:12) and
     `GameAssembly.dll+0xf98fff` (06-30 14:21, 07-01 15:54) — same offsets across days/mod versions.
   - Two `0xc00000fd` stack overflows (07-01 13:13, v1.3.15-era) — patch-induced recursion, separate
     bug, see Phase 1 notes.
2. **Offsets mapped to methods** with Cpp2IL (see Appendix A for the exact recipe):
   - `+0xf975f2` → `NetworkDynamicDimensionBuildingState_256.UpdateGridValidityOnNetwork` (+0x162)
   - `+0xf98fff` → `NetworkDynamicDimensionBuildingState._OnProxyGridValidityChanged` (+0x3f)
3. **Decompile** (Cpp2IL diffable-cs) of the `_256` variant:
   ```csharp
   public class NetworkDynamicDimensionBuildingState_256 : NetworkDynamicDimensionBuildingState {
       public const int c_NetworkedGridSize = 256;
       [Networked(OnChanged = "_OnProxyGridValidityChanged", OnChangedTargets = Proxies)]
       protected BitSet256 _GridValidity;          // one bit per tile, FIXED 256 bits
       public virtual int NetworkedGridSize => 256;
   }
   ```
   A `_512` sibling (`BitSet512`) exists for other building types — that's where the mod's wrong "512"
   numbers came from. The terraforming field preview uses the **256** variant (the crash offset is
   inside `_256`'s method).
4. **In-game diag corroborates:** the v1.3.18 log shows `[Diag] OnSnap: gridSize=20x14 product=280
   blocked=False` shortly before a crash — 280 > 256 (already corrupting), and the guard said "fine"
   because it compared against 512.

### Secondary confirmed findings (log-proven this session)
- **`DynamicDimensionsPlacementTool.firstMarker` is NOT the drag anchor.** Diag showed it a constant
  ~575–590 m away from the live marker during the whole drag (stale/fixed object). Every tether/clamp
  ever built on it — including the committed v1.1.21 "working reference" — measured garbage and never
  restricted real grid growth. v1.3.18's per-frame clamp *teleported the visible marker ~560 m away*
  (that was the "initial marker disappeared" bug).
- **The real drag pipeline** (from interop dumps) lives in preview types the mod never touched:
  `DynamicDimensionsPlacement.gridSize` (Vector3Int) → `DynamicDimensionBuilding.SetDimensions(float
  tileSize, Vector3Int GridSize, bool forceSet)` → `NetworkDynamicDimensionBuildingState.ChangeGridSize
  (Vector3Int)` → `_256.UpdateGridValidityOnNetwork()`. The mod's >512 guards sit on `TerraformingGrid`
  / `_OnSnap` — the **placed-structure** layer, which is why they never fired during the drag
  (`TerraformingGrid.OnDynamicBuildingDimensionsChanged` diag stayed silent).
- **The "steep quarry" theory was a red herring.** Steeper terrain just makes the user drag farther →
  more tiles → past 256. Crash reproduces on gently-curved ground at the same *tile count*.
- **v1.3.19/20's `_OnBuildRayChanged` skip approach is UX-broken by design** (skipping the original
  hides/never-updates the preview: invisible grid, 10 m marker jumps on frames where the mod's raycast
  missed). Do not iterate on it — delete it.

---

## The fix

### Phase 1 — Delete the broken patches (restores stock drag UX)
In `Plugin.cs` (currently v1.3.20):
1. **Delete** `DynamicDimensionsPlacementTool_OnBuildRayChanged_Prefix`'s raycast/skip logic — keep
   only the `maxHeightDifference = 10000f` line (returning void, no skip).
2. **Delete** `ClampMarkerToFirst` and the call to it in
   `DynamicDimensionsPlacementTool_OnBuildingDimensionsChanged_Prefix` (the whole firstMarker tether +
   Y-clamp + marker-position writes). The prefix can be deleted outright.
3. **Do not resurrect** anything from `Plugin_WorkingReference.txt`'s tether — same broken anchor.
4. Keep: `Begin` prefix (`maxRange`, `maxHeightDifference`), `_ValidateFootprint` bypass,
   `CheckLevelDifference` bypass, the whole E-press flatten/bomb machinery (all unrelated to the drag
   crash; flatten via `HeightmapTool.RunOnArea` is NOT tile-capped and still covers the full footprint).
5. `maxNumberOfTiles` overrides (`CreatePreview`/`Create` prefixes): change 512 → **256**, or better,
   log the vanilla value once before overwriting (diag) — if vanilla is already ≤ 256 for the
   terraforming template, drop the override entirely and let the native red/limit logic work.
6. Recursion warning: the 13:13 stack overflows came from v1.3.15-era marker writes re-firing dimension
   callbacks. Any patch that re-calls its own target MUST keep the static-bool recursion guard pattern
   (`isRecursion` in the WR's TryLevelTile is the project-proven shape).

### Phase 2 — Cap the grid at the source (the actual fix)
1. **Primary: prefix `SSSGame.Network.NetworkDynamicDimensionBuildingState.ChangeGridSize(Vector3Int
   gridSize)`** (base class; by-value arg — patchable):
   ```csharp
   private static bool _inChangeGridSize;
   [HarmonyPatch(typeof(NetworkDynamicDimensionBuildingState), nameof(NetworkDynamicDimensionBuildingState.ChangeGridSize))]
   [HarmonyPrefix]
   public static bool ChangeGridSize_Prefix(NetworkDynamicDimensionBuildingState __instance, Vector3Int gridSize)
   {
       if (_inChangeGridSize) return true;
       int cap = 256; try { cap = __instance.NetworkedGridSize; } catch { }   // 256 or 512 per variant
       long tiles = (long)gridSize.x * gridSize.z;
       if (tiles <= cap) return true;
       // shrink the dominant axis until it fits (preserves drag direction/shape)
       var c = gridSize;
       while ((long)c.x * c.z > cap) { if (c.x >= c.z) c.x--; else c.z--; }
       if (c.x < 1) c.x = 1; if (c.z < 1) c.z = 1;
       _inChangeGridSize = true;
       try { __instance.ChangeGridSize(c); } finally { _inChangeGridSize = false; }
       return false;   // skip the oversized original
   }
   ```
   (Adapt names/`using`s; `Vector3Int` marshals fine by value. Read `NetworkedGridSize` at runtime —
   don't hardcode — so the `_512` buildings keep their bigger cap.)
2. **Safety net: prefix `UpdateGridValidityOnNetwork()` on BOTH concrete interop types**
   `SSSGame.Network.NetworkDynamicDimensionBuildingState_256` **and** `_512`: if
   `__instance._building` is non-null and `_building.GridSize.x * GridSize.z > __instance.NetworkedGridSize`,
   return false (skip the OOB write; the preview shows stale validity but cannot corrupt).
   - ⚠️ **DO NOT** patch `CopyBackingFieldsToState` / `CopyStateToBackingFields` on these types —
     known hard-learned load-hang (same rule as SpellsManager).
3. **MANDATORY fire-verification:** gate a `[Diag]` log line in each new prefix behind
   `PlacementDiagnostics` and confirm in `LogOutput.log` that they actually fire during a drag before
   trusting them — this project has already been burned by IL2CPP AOT inlining (`TryLevelTile`,
   `MarkCellInteracted` patches silently never fired). If `ChangeGridSize` never fires:
   - Fallback A: prefix `DynamicDimensionBuilding.SetDimensions(Single, Vector3Int, Boolean)` (all
     by-value — patchable) and clamp the `Vector3Int` the same way (recursion-guarded re-call).
   - Fallback B: prefix `DynamicPlacementGrid.OnDynamicBuildingDimensionsChanged(Boolean)`.
   - **Never** patch `PlacementGrid`/`DynamicPlacementGrid.SetDimensions(Int32&, Int32&)` — by-ref
     primitives = trampoline NRE (standing gotcha).
4. Retarget the belt-and-suspenders guards that stay: `_OnSnap` block and
   `TerraformingGrid.OnDynamicBuildingDimensionsChanged` freeze → compare against **256** (or the
   runtime capacity), not 512. (They're the wrong layer for the drag but harmless as backstops.)

### Phase 3 — Test protocol (user runs, in order)
1. Confirm loaded version in `LogOutput.log` (SAC: bump `PLUGIN_VERSION` + csproj `<Version>` every build).
2. Start placement: **initial marker visible and orientable** (v1.3.19/20 broke this — must re-verify fixed).
3. Place first corner, drag normally: **grid preview visible and follows aim** (v1.3.20 broke this).
4. Creep test on any terrain: run far away — expect the grid to **stop growing (or turn red) at 256
   tiles** (≈16×16; with ~2 m tiles ≈ a 32×32 m footprint) and **no crash at any distance**. Check
   `[Diag]` lines show the clamp firing with `cap=256`.
5. Press E: flatten + obstacle-clear still work over the full grid.
6. If the user plays co-op later: `_OnProxyGridValidityChanged` (the second crash site) is the
   client/proxy path — the same cap protects it, but a two-player drag test is worth one run.

### Expectation to set with the user
**256 tiles is an engine-hard ceiling for this structure's preview** — `c_NetworkedGridSize` is a
compiled Fusion codegen constant on a weaved network-state type; a mod cannot enlarge it. "Town-sized"
areas = multiple adjacent 256-tile placements (each E-press flattens its full grid instantly, so this
is quick in practice). Do not burn tokens trying to raise the cap.

### Phase 4 — Ship checklist (commit-gated, per CLAUDE.md rituals)
- Flip `PlacementDiagnostics` + `ClearDiagnostics` defaults to `false` once user-confirmed in-game.
- Delete dead code listed in the old handoff (legacy physics sweep, flatten diag helpers, unused configs).
- Un-park TerrainLevelerMod in **both** `CLAUDE.md` and `.agents/AGENTS.md`; rewrite
  `docs/mods/terrain-leveler.md` with the working recipe (HeightmapTool flatten + AOESpell bomb + this cap).
- Record in `docs/architecture.md` (Terrain/Terraforming section): the BitSet256/512 capacity system,
  the real drag pipeline chain, firstMarker-is-not-the-anchor dead-end, the "skip _OnBuildRayChanged"
  dead-end, and the crash-offset→method mapping technique (Appendix A) as a general debugging recipe.
- `sync-plugins.ps1`: TerrainLevelerMod must NOT be in `$ParkedByDefault`.
- Commit + push only with explicit user go-ahead.

---

## Appendix A — Native crash → method name recipe (reusable for any future hard crash)
1. Crash offsets: `Get-WinEvent -FilterHashtable @{LogName='Application'; ProviderName='Application
   Error'}` → filter message for `Aska`, note `Faulting module` + `Fault offset` (= RVA). Dumps also
   land in `%LOCALAPPDATA%\CrashDumps\Aska.exe.*.dmp` (~100 MB each — the user may want to purge).
2. **Il2CppDumper does NOT work** on this game (metadata v39 / Unity 6000.3 unsupported — confirmed).
   Use **Cpp2IL** (same engine BepInEx uses): standalone exe from GitHub releases
   (`2022.1.0-pre-release.21` worked), then:
   `cpp2il.exe --game-path <ASKA dir> --use-processor attributeinjector --output-as dummydll`
   (the `attributeinjector` processor is REQUIRED or dummy DLLs carry no `AddressAttribute` RVAs;
   ~7 s total). `--output-as diffable-cs` gives per-type C# skeletons incl. Fusion `[Networked]`
   fields and codegen constants.
3. Map offset→method: `_explore/map_crash_offsets.ps1` (Mono.Cecil binary-search over all dumped
   AddressAttribute RVAs — edit the `$targets` array).
4. Unity `Player.log` (`%USERPROFILE%\AppData\LocalLow\Sand Sailor Studio\ASKA\`) does NOT contain a
   native stack for these crashes — WER events are the reliable source.

## Appendix B — Current file state (as of this doc)
- `Plugin.cs` = **v1.3.22, confirmed working in-game (2026-07-01).** Phase 1 done — ray-skip prefix and
  `ClampMarkerToFirst`/firstMarker tether deleted entirely; `_OnBuildRayChanged` prefix reduced to just
  the height-difference raise; `Begin`'s `maxRange` no longer hard-capped at 20m (follows `MaxDragRange`
  directly, since the real cap is now enforced at the tile-count layer). Phase 2 done — new
  `NetworkDynamicDimensionBuildingState_ChangeGridSize_Prefix` clamps tile count at the source (reads
  `NetworkedGridSize` at runtime rather than hardcoding 256, so `_512`-variant buildings keep their own
  cap); safety-net prefixes added on both `_256.UpdateGridValidityOnNetwork` and
  `_512.UpdateGridValidityOnNetwork`; the belt-and-suspenders `_OnSnap` and
  `TerraformingGrid.OnDynamicBuildingDimensionsChanged` guards and the `maxNumberOfTiles` overrides were
  all retargeted from the wrong 512 to the confirmed-correct 256. All signatures (virtuality, by-value
  params, `_building`/`GridSize`/`NetworkedGridSize` accessibility) were re-verified against the interop
  DLL with a dedicated Cecil dump before coding (`_explore/dump_networkstate.ps1`). Phase 4 done in
  v1.3.22 — `PlacementDiagnostics`/`ClearDiagnostics` defaulted back to `false`; the dead legacy
  physics-sweep cluster (`ClearObstructionsInGrid`, `TryHarvestDestroy`, `FindNamedTarget`,
  `ParseFilterTokens`, their `BruteForce*` configs) and old flatten-diag helpers (`LogFlattenState`,
  `SampleCell`, `SafeCellHeight`) and unused configs (`MaxPassesPerTile`, `UseDebugComplete`) deleted
  (745 → 638 lines); `sync-plugins.ps1`'s `$ParkedByDefault` no longer lists TerrainLevelerMod; both
  `CLAUDE.md` and `.agents/AGENTS.md` un-parked (Ritual 2/3 verified). Not yet committed — pending user
  go-ahead per project git policy.
- `Plugin_WorkingReference.txt` = committed v1.1.21 (UTF-16). Historical only — its tether was equally
  anchor-broken; nothing in it fixed the crash. Kept for now; safe to delete on a future cleanup pass.
- `_explore/dump_placement.ps1`, `_explore/dump_placement2.ps1` (edit `$frags`), `_explore/dump_stage.ps1`,
  `_explore/find_stage.ps1`, `_explore/map_crash_offsets.ps1`, `_explore/dump_networkstate.ps1` — this
  session's inspector scripts.
- Cpp2IL + dumps live in the session scratchpad (`...\scratchpad\cpp2il_out\`) — regenerate as needed
  (6–8 s), don't commit.
- Interaction stage enum (for reference): `DynamicDimensionsPlacementTool.DynamicPlacementToolStage`
  = StartPoint(0), EndPoint(1), Rotate(2). (No longer used by the mod after Phase 1's cleanup, but the
  enum itself is still valid/available if a future patch needs the drag stage.)
