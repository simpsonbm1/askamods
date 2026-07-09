# Mod 21: DenRespawnMod — WIP (v1.1.1, NOT shipped; core hotkey refresh confirmed in-game up close 2026-07-08; v1.1.x adds natural-respawn suppression, map-pin revive, timed auto-respawn — all ⚠️ pending in-game test)

**Goal:** Refresh/revive defeated monster and beast dens (wulfar, bear, skeleton, etc.) back to life via a configurable hotkey, bringing them back into the creature-spawning rotation.

**Game subsystem:** [Dens & Population Spawners](../architecture.md#dens--population-spawners-confirmed-in-game-2026-07-08)
— the classes, data structures, and spawn-state managers governing ASKA dens and their population lifecycle.

**Working approach:**
- **Hotkey-Triggered Refresh**: A persistent MonoBehaviour (`DenTracker`) polls for a configurable hotkey (default: `j`). When pressed within a config radius (default: 150m; 0 = whole map), it refreshes defeated dens.
- **Defeat Detection via Spawner State**: The game marks a den as DEFEATED by setting `ignoreRespawning=true` on the den's node spawners (`Den.affectedSpawners`), NOT via `den.isActive` (which has murky day/night semantics and is not reliable for defeat detection). A den needs refresh if any of: `!isActive`, any node spawner has `ignoreRespawning=true`, or any node spawner has no alive creatures (`HasNoAliveCreatures()`).
- **Multi-Lever Refresh Recipe** (the full set confirmed SUFFICIENT in-game 2026-07-08; which levers are individually necessary has NOT been isolated): call these in sequence on a defeated den:
  1. `Den.Revive()` — observed to bump `ReviveCooldown` 0→1 and NOT flip `isActive` (12 observations, v1.0.x tests); whatever else it does internally is unisolated.
  2. `Den.IgnorePopulationSpawnersRespawning(false)` — clear the spawner block flags.
  3. `Den.SetDenActive(false, true)` when `!isActive` — reactivate if currently inactive.
  4. For each `affectedSpawners` element with `HasNoAliveCreatures()`: call `SetActiveSpawner(true, true)` then `RespawnAllPopulations(false)` — triggers instant repopulation (verified: creatures appear immediately post-call, native in-game toast "The monsters from [Den Name] are back!").
- **NEVER touch `alphaSpawner`** — the boss spawner; empty/inactive is its normal pre-boss state.
- **Structure-Block Bypass** (config-gated, patches fire-verified in-game):
  - `Den.IsBlockedByStructures()` postfix → returns `false` if config `AllowRespawnNearStructures=true`, allowing revive even when buildings are nearby.
  - `SSSGame.Combat.PopulationSpawner._UpdateBlockedByStructures()` postfix clears `RespawningBlockedByStructures` flag to prevent the game from re-blocking spawns.
- **Per-World State Management**: Capture dens via `Den.Start()` + `Den.Spawned()` postfixes into a static list, deduplicated by native pointer using the `(object)den is Il2CppObjectBase b` boxing pattern (necessary because `Den`'s compile-time base chain passes through a unity-libs stub). Clear per-world state via a throttled `StorageManager.ActiveSessionID` poll; **critical**: the FIRST non-empty session ID after the menu must NOT trigger a clear — dens are captured during world load before the ID becomes readable.
- **Cosmetic Feedback**: On-screen HUD notification (yellow text with black drop shadow) confirming refresh success.

**Key IL2CPP Interop Learnings:**
- **Pointer-based deduplication of `Den` instances**: Because compile-time type chains include unity-libs stubs, the `.Pointer` boxing escape hatch is necessary — `(object)den is Il2CppObjectBase b` → `b.Pointer` for identity checks / `new Den(IntPtr)` rewraps.
- **Session ID poll for world-leave detection**: `StorageManager.ActiveSessionID` becomes empty when the player leaves a world; the first non-empty ID after that signals a new world load. A naive clear-on-entry would erase the dens captured *during* that load (before the ID is readable) — gate the clear on a state machine transition, not a value presence check.

**Config (`com.askamods.denrespawn.cfg`):**
- `General/ReviveHotkey` (string, default: `"j"`): The key to trigger den refresh. (U taken by MineRefresh, N by Vacuum, V by emote wheel.)
- `General/ReviveRadiusMeters` (float, default: `150.0`): Maximum distance to scan for defeated dens (0 = whole map).
- `General/AllowRespawnNearStructures` (bool, default: `true`): If true, bypass the game's structure-block check so dens near buildings can respawn.
- `General/ClearIgnoreRespawning` (bool, default: `true`): Clear the spawner `ignoreRespawning` flags as part of refresh.
- `General/ForceRespawnPopulations` (bool, default: `true`): Call `RespawnAllPopulations()` to instantly repopulate.
- `Diagnostics/DenDiagnostics` (bool, default: `true` — must flip to `false` before shipping to Nexus): Log den state and refresh steps.
- `Diagnostics/DiagnosticsIntervalSeconds` (float, default: `30`): How often to poll and log.

**v1.1.x MVP Features (⚠️ ALL pending in-game confirmation):**

The v1.1.x branch adds three new features on top of v1.0.2's confirmed hotkey refresh plumbing:

1. **Natural-Respawn Suppression** — `[NaturalRespawns] SuppressNaturalRespawns` (default false): Harmony PREFIX gate on `Den.Revive()` with a re-entrancy allow-flag (`Plugin.AllowReviveCall`, set only around the mod's own Revive calls); foreign calls are ALWAYS logged (`Foreign Den.Revive() on '<name>' (day N)` — doubles as the detector for the game's natural ~1-in-game-year respawn driver, which the user reports exists) and blocked only when the config is on. Fails open (exception → run original).

2. **Map Revive (Shift+Click)** — hold `[MapRevive] MapReviveModifier` (default LeftShift) + left-click a den map pin: postfix on `SSSGame.UI.MapMenu._OnMarkersLeftClick(WorldObjectiveMarker marker)`. Resolves pin→registry record within `[MapRevive] MapPinMatchRadius` (default 75 m); unknown pins whose name looks den-like get a provisional record so never-visited pinned dens are revivable. If the den isn't loaded, force-streams its tile (`WorldTileId.GetLowest(x, z, tileSize)` FLOOR convention + `WorldStreamingManager.RequestLoadWorldTile` with a hidden anchor GameObject — SeedScout-proven primitives), pending queue matches the den on capture within 75 m, 30 s timeout. Native click behavior untouched (postfix).

3. **Timed Auto-Respawn** — `[AutoRespawn] Rules` (default "", format `Wulfar Den:2, Skeleton Den:5`, case-insensitive; known names: Wulfar Den, Skeleton Den, Skeleton Den Cluster, Wight Den, Draugar den, Baby Crawler Den): N in-game days after a den's recorded defeat, auto-revives it (remote force-load path if unloaded). Day source: `SSSGame.Weather.WeatherSystem.Instance.GetDaysPassed()` polled ~5 s (TreeRespawn's DayTracker has no absolute day count — this is the documented API on the same singleton).

**Shared Plumbing (new files):** `DenRegistry.cs` — position-keyed per-world registry (`x|y|z|type|defeated|defeatedOnDay` lines, saved at `BepInEx\config\DenRespawn_<sanitizedSessionId>.save`, TreeRespawn SanitizeId+FNV-1a convention; loaded on session-id resolve, flushed+cleared on world leave). Defeat detection = periodic (~5 s) scan of live dens for any node spawner `ignoreRespawning==true` (the vanilla defeat flag confirmed in v1.0.2 testing; NOT HasNoAliveCreatures — transient). `DayCounter.cs` — day poll. New capture patch on `WorldStreamingManager.Awake`.

**Known cosmetic issue:** a map-click on a never-visited den creates a provisional record at the PIN position which can end up duplicating the real den's record (den-actual-position) — inert (provisional stays non-defeated), FindNearest tolerance covers it; clean up in a later version.

**Version history:**
- v1.0.0: Initial — selection keyed on `den.isActive`, action was `Revive()` alone; falsified in-game (6 dens "revived" twice, `isActive` never flipped, nothing spawned).
- v1.0.1: Fixed a `PollWorld()` first-load bug — the first non-empty session id after the menu wiped the dens captured during world load (they arrive before the id is readable).
- v1.0.2: Switched to spawner-state selection (the game's ACTUAL defeat marker) + multi-lever refresh recipe. Renamed `ReviveRadius` → `ReviveRadiusMeters` with new default so old cfg 0 doesn't override. Confirmed in-game 2026-07-08 (controlled test: cleared den nodes + boss → defeat flag appeared; pressed hotkey → multi-lever refresh → instant repopulation + native toast).
- v1.1.0: Added three MVP features on shared plumbing — natural-respawn suppression (SuppressNaturalRespawns config), Shift+click map-pin revive (MapRevive path + force-load), timed auto-respawn (AutoRespawn Rules), plus per-world den registry (DenRegistry.cs) and day counter (DayCounter.cs). Built clean; ⚠️ all features pending in-game test.
- v1.1.1: Backfill fix for dens defeated before first observation — they were stamped `DefeatedOnDay=-1` and permanently skipped by auto-rules. The scan now starts their clock at first observation once the day is known, logging `defeat day unknown — clock started at day N`. Day poll moved BEFORE the defeat scan in the service tick.

**Still open (record as ⚠️ untested / future work):**
- Remote whole-map refresh (ReviveRadiusMeters=0) — plausible but untested.
- Real-world effect of structure-block bypass — the test world had no structure-blocked dens.
- Save/reload persistence — whether a refreshed den stays alive after quit-to-menu and reload (expected yes, untested).
- Auto-revive after N days (candidate v1.1, using `Den.ReviveCooldown` semantics and a day tracker like TreeRespawn).
- Map-click revive from the POI pin (Phase 2, requires map UI work — see `NEW_MOD_IDEAS_PLAN.md`).
- Villager den-attack blacklist to prevent workers from destroying dens (Phase 2b, needs diagnostics on whether villagers actually attack `Den` hitzones vs. the structure-blocking explanation).

**Dead-ends (don't retry):**
- `den.isActive` is NOT a reliable defeat marker — it has murky day/night semantics and doesn't track actual spawner state. The game records defeat at the spawner level (`ignoreRespawning` flag on `Den.affectedSpawners`).
- `Den.Revive()` alone produced no visible effect (v1.0.0: bumps `ReviveCooldown`, doesn't flip `isActive`, nothing respawned). The full multi-lever set is confirmed sufficient; per-lever necessity has not been isolated — don't claim any single lever is "the" fix.
