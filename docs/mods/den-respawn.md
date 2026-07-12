# Mod 21: DenRespawnMod — COMPLETE (v1.2.2; shipped 2026-07-09 — map-pin Shift+click revive + pin recolor, timed auto-respawn day rule, defeat-day reload persistence, natural-respawn suppression all confirmed in-game; stale-registry timer guard v1.2.1 ⚠️ pending in-game confirmation)

**Goal:** Refresh/revive defeated monster and beast dens (wulfar, bear, skeleton, etc.) back to life
via a configurable hotkey, bringing them back into the creature-spawning rotation.

**Game subsystem:** [Dens & Population Spawners](../architecture.md#dens--population-spawners-confirmed-in-game-2026-07-08)
— the classes, data structures, and spawn-state managers governing ASKA dens and their population lifecycle.

**Working approach:**
- **Hotkey-Triggered Refresh**: A persistent MonoBehaviour (`DenTracker`) polls for a configurable
  hotkey (default: `j`). When pressed within a config radius (default: 150m; 0 = whole map), it
  refreshes defeated dens.
- **Defeat Detection via Spawner State**: The game marks a den as DEFEATED by setting
  `ignoreRespawning=true` on the den's node spawners (`Den.affectedSpawners`), NOT via
  `den.isActive` (which has murky day/night semantics and is not reliable for defeat detection). A
  den needs refresh if any of: `!isActive`, any node spawner has `ignoreRespawning=true`, or any
  node spawner has no alive creatures (`HasNoAliveCreatures()`).
- **Multi-Lever Refresh Recipe** (the full set confirmed SUFFICIENT in-game 2026-07-08; which levers
  are individually necessary has NOT been isolated): call these in sequence on a defeated den:
  1. `Den.Revive()` — observed to bump `ReviveCooldown` 0→1 and NOT flip `isActive` (12
     observations, v1.0.x tests); whatever else it does internally is unisolated.
  2. `Den.IgnorePopulationSpawnersRespawning(false)` — clear the spawner block flags.
  3. `Den.SetDenActive(false, true)` when `!isActive` — reactivate if currently inactive.
  4. For each `affectedSpawners` element with `HasNoAliveCreatures()`: call `SetActiveSpawner(true,
     true)` then `RespawnAllPopulations(false)` — triggers instant repopulation (verified: creatures
     appear immediately post-call, native in-game toast "The monsters from [Den Name] are back!").
- **NEVER touch `alphaSpawner`** — the boss spawner; empty/inactive is its normal pre-boss state.
- **Structure-Block Bypass** (config-gated, patches fire-verified in-game):
  - `Den.IsBlockedByStructures()` postfix → returns `false` if config `AllowRespawnNearStructures=true`, allowing revive even when buildings are nearby.
  - `SSSGame.Combat.PopulationSpawner._UpdateBlockedByStructures()` postfix clears
    `RespawningBlockedByStructures` flag to prevent the game from re-blocking spawns.
- **Per-World State Management**: Capture dens via `Den.Start()` + `Den.Spawned()` postfixes into a
  static list, deduplicated by native pointer using the `(object)den is Il2CppObjectBase b` boxing
  pattern (necessary because `Den`'s compile-time base chain passes through a unity-libs stub).
  Clear per-world state via a throttled `StorageManager.ActiveSessionID` poll; **critical**: the
  FIRST non-empty session ID after the menu must NOT trigger a clear — dens are captured during
  world load before the ID becomes readable.
- **Cosmetic Feedback**: On-screen HUD notification (yellow text with black drop shadow) confirming refresh success.

**Key IL2CPP Interop Learnings:**
- **Pointer-based deduplication of `Den` instances**: Because compile-time type chains include
  unity-libs stubs, the `.Pointer` boxing escape hatch is necessary — `(object)den is
  Il2CppObjectBase b` → `b.Pointer` for identity checks / `new Den(IntPtr)` rewraps.
- **Session ID poll for world-leave detection**: `StorageManager.ActiveSessionID` becomes empty when
  the player leaves a world; the first non-empty ID after that signals a new world load. A naive
  clear-on-entry would erase the dens captured *during* that load (before the ID is readable) — gate
  the clear on a state machine transition, not a value presence check.

**Config (`com.askamods.denrespawn.cfg`):**
- ~~`General/ReviveHotkey`~~ (REMOVED in v1.2.0 — hotkey revive deleted per user decision)
- ~~`General/ReviveRadiusMeters`~~ (REMOVED in v1.2.0)
- `General/AllowRespawnNearStructures` (bool, default: `true`): If true, bypass the game's structure-block check so dens near buildings can respawn.
- `General/ClearIgnoreRespawning` (bool, default: `true`): Clear the spawner `ignoreRespawning` flags as part of refresh.
- `General/ForceRespawnPopulations` (bool, default: `true`): Call `RespawnAllPopulations()` to instantly repopulate.
- `Diagnostics/DenDiagnostics` (bool, default: `true` — must flip to `false` before shipping to Nexus): Log den state and refresh steps.
- `Diagnostics/DiagnosticsIntervalSeconds` (float, default: `30`): How often to poll and log.

**v1.1.x MVP Features (⚠️ ALL pending in-game confirmation):**

The v1.1.x branch adds three new features on top of v1.0.2's confirmed hotkey refresh plumbing:

1. **Natural-Respawn Suppression** — `[NaturalRespawns] SuppressNaturalRespawns` (default false):
   Harmony PREFIX gate on `Den.Revive()` with a re-entrancy allow-flag (`Plugin.AllowReviveCall`,
   set only around the mod's own Revive calls); foreign calls are ALWAYS logged (`Foreign
   Den.Revive() on '<name>' (day N)` — doubles as the detector for the game's natural
   ~1-in-game-year respawn driver, which the user reports exists) and blocked only when the config
   is on. Fails open (exception → run original).

2. **Map Revive (Shift+Click)** — hold `[MapRevive] MapReviveModifier` (default LeftShift) +
   left-click a den map pin: postfix on `SSSGame.UI.MapMenu._OnMarkersLeftClick(WorldObjectiveMarker
   marker)`. Resolves pin→registry record within `[MapRevive] MapPinMatchRadius` (default 75 m);
   unknown pins whose name looks den-like get a provisional record so never-visited pinned dens are
   revivable. If the den isn't loaded, force-streams its tile (`WorldTileId.GetLowest(x, z,
   tileSize)` FLOOR convention + `WorldStreamingManager.RequestLoadWorldTile` with a hidden anchor
   GameObject — SeedScout-proven primitives), pending queue matches the den on capture within 75 m,
   30 s timeout. Native click behavior untouched (postfix).

3. **Timed Auto-Respawn** — `[AutoRespawn] Rules` (default "", format `Wulfar Den:2, Skeleton
   Den:5`, case-insensitive; known names: Wulfar Den, Skeleton Den, Skeleton Den Cluster, Wight Den,
   Draugar den, Baby Crawler Den): N in-game days after a den's recorded defeat, auto-revives it
   (remote force-load path if unloaded). Day source:
   `SSSGame.Weather.WeatherSystem.Instance.GetDaysPassed()` polled ~5 s (TreeRespawn's DayTracker
   has no absolute day count — this is the documented API on the same singleton).

**Shared Plumbing (new files):** `DenRegistry.cs` — position-keyed per-world registry
(`x|y|z|type|defeated|defeatedOnDay` lines, saved at
`BepInEx\config\DenRespawn_<sanitizedSessionId>.save`, TreeRespawn SanitizeId+FNV-1a convention;
loaded on session-id resolve, flushed+cleared on world leave). Defeat detection = periodic (~5 s)
scan of live dens for any node spawner `ignoreRespawning==true` (the vanilla defeat flag confirmed
in v1.0.2 testing; NOT HasNoAliveCreatures — transient). `DayCounter.cs` — day poll. New capture
patch on `WorldStreamingManager.Awake`.

**Known cosmetic issue:** a map-click on a never-visited den creates a provisional record at the PIN
position which can end up duplicating the real den's record (den-actual-position) — inert
(provisional stays non-defeated), FindNearest tolerance covers it; clean up in a later version.

**Version history:**
- v1.0.0: Initial — selection keyed on `den.isActive`, action was `Revive()` alone; falsified
  in-game (6 dens "revived" twice, `isActive` never flipped, nothing spawned).
- v1.0.1: Fixed a `PollWorld()` first-load bug — the first non-empty session id after the menu wiped
  the dens captured during world load (they arrive before the id is readable).
- v1.0.2: Switched to spawner-state selection (the game's ACTUAL defeat marker) + multi-lever
  refresh recipe. Renamed `ReviveRadius` → `ReviveRadiusMeters` with new default so old cfg 0
  doesn't override. Confirmed in-game 2026-07-08 (controlled test: cleared den nodes + boss → defeat
  flag appeared; pressed hotkey → multi-lever refresh → instant repopulation + native toast).
- v1.1.0: Added three MVP features on shared plumbing — natural-respawn suppression
  (SuppressNaturalRespawns config), Shift+click map-pin revive (MapRevive path + force-load), timed
  auto-respawn (AutoRespawn Rules), plus per-world den registry (DenRegistry.cs) and day counter
  (DayCounter.cs). Built clean; ⚠️ all features pending in-game test.
- v1.1.1: Backfill fix for dens defeated before first observation — they were stamped
  `DefeatedOnDay=-1` and permanently skipped by auto-rules. The scan now starts their clock at first
  observation once the day is known, logging `defeat day unknown — clock started at day N`. Day poll
  moved BEFORE the defeat scan in the service tick.
- v1.1.2: Rehooked map-pin-click hotspot detection to `MapMenu.OnPointerClick(PointerEventData)`
  (IPointerClickHandler — interface-dispatched, can't be inlined; fire-verified in-game 2026-07-09).
  v1.1.1's `MapMenu._OnMarkersLeftClick(WorldObjectiveMarker)` postfix was AOT-inlined (patch never
  fired; fire-verify proved two sibling patches DID fire, so the issue was specific to that method's
  inlining). v1.1.2 confirmed: empty-space Shift+click revived the correct den at ~19 m from the
  click point. LIMITATION found: clicks landing ON a pin widget itself never reach
  MapMenu.OnPointerClick (pin swallows them). csproj gained `UnityEngine.UI` (interop) +
  `UnityEngine.UIModule` (unity-libs) references. Patch fires cleanly; ⚠️ full feature pending
  in-game test.
- v1.1.3: Pin recolor via `MarkerRefresher` (postfixes on
  `SSSGame.AreaInstanceMarkerHandler.CreateOrUpdateMarkerObject` + game's own load-time
  marker-setup). CONFIRMED working in-game 2026-07-09 (`marker=MarkerObject refreshed=True` in logs;
  pins went red→yellow on revive). BiomesCapture patches added (SeedScout pattern). "Cemetery"
  substring added to den-name matching. Dedupe guard added. TRIGGER BUG found:
  `CompassObjectiveMarker.OnSelect` fires on mere mouse-HOVER (no click needed) — Shift+hover
  revived dens as a side effect (5 map-source revives incl. 2 collateral). Issue identified but not
  fixed in v1.1.3; fixed in v1.1.4 via a per-frame polling change.
- v1.1.4 CONFIRMED IN-GAME (2026-07-09): OnSelect/OnDeselect postfixes only TRACK the hovered pin
  (clear on deselect only if widget pointer matches); actual trigger = per-frame check in
  DenTracker.Update: `HaveHovered && Input.GetMouseButtonDown(0) && ModifierHeld()` → TryRevive (0.3
  s unscaledTime dedupe window). Shift+hover now inert, Shift+click revives + recolors as intended.
  No hover-revive side effect.
- v1.1.5 CONFIRMED IN-GAME (2026-07-09): `RefreshSpawnersHostileStatus(false)`
  (noNotification=false) so game fires native "monsters are back" toast on remote revives — exact
  toast appearance/wording ⚠️ not yet explicitly confirmed but fires reliably.
- v1.1.6: Warning cleanup; csproj `UnityEngine.UI` ref moved to conditional. SAC bump guard
  correctly refused same-version rebuild (1.1.6 superseded within minutes). Added null-check on
  `MarkerObject._biomePopulation` before RefreshSpawnersHostileStatus (long-defeated crawler dens'
  markers have it null, causing a managed NRE inside the native call — caught but noisy).
- v1.1.7 CONFIRMED (map/registry/suppression 2026-07-09, hotkey selection pending): Hotkey selection
  now `needsWork = anyIgnore` ALONE (was `anyEmpty || anyIgnore`). Evidence from live tests: J
  refreshed a HEALTHY Wulfar Den (matching on anyEmpty=false, wolves streaming-culled), yet two Baby
  Crawler Dens matching on ignoreRespawning=true were USER-CONFIRMED long-cleared dens (correct
  revives). The broader filter was false-positive. ⚠️ v1.1.7 selection change (narrowed gate)
  pending in-game test.
- v1.2.0: J-hotkey revive REMOVED per user decision. Map-pin Shift+click (`MapRevive` path) is the
  primary manual driver; config auto-respawn rules (`AutoRespawn Rules`) cover timed revive.
  Deleted: `TriggerRevive()`, the hotkey input block, and the `ReviveHotkey` + `ReviveRadiusMeters`
  config binds (hotkey-only settings). Load log now reports `MapReviveModifier` instead. The v1.1.7
  defeat-flag-only hotkey selection logic (`needsWork = anyIgnore`) was tied to `TriggerRevive()`
  and died with it; the same defeat-flag insight remains in `ScanDefeatTransitions` and the
  auto-rules. Map-pin revive + recolor, timed auto-respawn day rule, defeat-day reload persistence,
  natural-respawn suppression all confirmed in-game 2026-07-09.
- v1.2.1: NEW stale-registry guard on the TIMER path (protects against: defeat a den → quit WITHOUT
  game-saving → reload older save → game reverts den alive but mod registry says Defeated →
  auto-rule would phantom-revive an alive den). Guard mechanics: `PendingRefresh` struct now carries
  `Source` ("map"/"timer"); pending resolutions call `RunRefresh(found, entry.Source)` (previously
  always "remote"); new `IsDenDefeated(den)` checks any `affectedSpawner.ignoreRespawning`; new
  `SkipStaleTimerRevive(rec)` logs "Registry stale — … already alive; skipping timer revive", marks
  the record `MarkAlive`, and saves. Applied at BOTH timer→RunRefresh points (immediate-match in
  `EnqueueRemoteRefresh` + resolve in `ScanPendingRefreshes`). Map clicks deliberately unguarded
  (explicit user intent). ⚠️ Guard has never fired in-game — pending confirmation. (Same desync
  class as TreeRespawn Issue E; volatile/committed designs rejected/parked — no known "game just
  saved" hook; this point-of-action guard is the chosen mitigation.)
- v1.2.2: Ship-prep release — `DenDiagnostics` config default flipped `true`→`false` per project
  convention (no behavior changes). FileVersion 1.2.2.0 deployed clean.

**Confirmed in-game (2026-07-09):**
- Timed auto-respawn day rule: 'Wulfar Den' DEFEATED (day 56) → TimeWarp day-skip (daysPassed 56→57)
  → auto-rule firing logged → spawners visibly returned. **Cosmetic note:** the immediate-match
  timer path shows NO mod toast — "Monsters back at …" only fires in the pending force-load path
  (user OK with this).
- Defeat-day persistence across save→quit→reload: registry loaded with defeated=6 unchanged, day-stamps intact.
- Natural-respawn suppression VERIFIED: 3 long-ago-cleared Baby Crawler dens received foreign
  `Den.Revive()` attempts AT EVERY WORLD LOAD, all BLOCKED (SuppressNaturalRespawns=true). Defeated
  Wulfar+cemetery dens stayed gone across 74 fast-forwarded in-game days with zero mid-session
  attempts. (Vanilla natural respawn = **load-time check, not mid-session elapsed-time trigger** —
  see architecture.md for the model.)

**Still open (record as ⚠️ untested / future work):**
- Stale-registry timer guard (v1.2.1) — guard logic untested in-game.
- Real-world effect of structure-block bypass — the test world had no structure-blocked dens.
- Remote whole-map revive (would need hotkey re-implementation, per user decision to remove hotkey path).
- Map-click revive from the POI pin widget itself (currently clicks ON a pin swallow before reaching
  the map; Phase 2, requires map UI work — see `NEW_MOD_IDEAS_PLAN.md`).
- Villager den-attack blacklist to prevent workers from destroying dens (Phase 2b, needs diagnostics
  on whether villagers actually attack `Den` hitzones vs. the structure-blocking explanation).

**Dead-ends (don't retry):**
- `den.isActive` is NOT a reliable defeat marker — it has murky day/night semantics and doesn't
  track actual spawner state. The game records defeat at the spawner level (`ignoreRespawning` flag
  on `Den.affectedSpawners`).
- `Den.Revive()` alone produced no visible effect (v1.0.0: bumps `ReviveCooldown`, doesn't flip
  `isActive`, nothing respawned). The full multi-lever set is confirmed sufficient; per-lever
  necessity has not been isolated — don't claim any single lever is "the" fix.
