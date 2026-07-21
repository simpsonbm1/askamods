# Mod 22: TimeWarpMod — DEV TOOL (v0.1.1, NOT for Nexus)

**Purpose:** accelerate in-game time for mod testing. A day is 24 real minutes; a year ~20 hours —
naturally untestable. Deliberately distorts gameplay (raids/events pile up under fast-forward).
Local development/test tool only.

**Implementation:** zero Harmony patches. Hotkeys call the game's own debug RPCs on
`SSSGame.Weather.WeatherSystem.Instance` (read FRESH every call, never cached — per-world singleton
gotcha). Typing guard since v0.1.1 (confirmed in-game 2026-07-10): hotkeys ignored while a game
text field is focused.

**Hotkeys (configurable):**
- `K` = `Rpc_ToggleFastForward()` — cycles `TimeSpeedMultiplier` through 1→10→36→75→162→1
  (confirmed in-game 2026-07-09).
- `L` = `Rpc_SetDayOfYear(DayOfYear+1)` — advances BOTH `dayOfYear` AND `GetDaysPassed()`
  (confirmed in-game 2026-07-09; drives other mods' day-boundary logic in seconds).

**Config (`com.askamods.timewarp.cfg`):**
- `General/FastForwardHotkey` (string, default `"k"`): key to cycle the speed multiplier.
- `General/SkipDayHotkey` (string, default `"l"`): key to advance the day.
- `Diagnostics/TimeDiagnostics` (bool, default `true` — user comfort; no ship impact since this
  mod never ships): dump `[TimeWarp][state]` blocks periodically + pre/post around each action
  (daysPassed/dayOfYear/timeOfDay/dayLength/daysInYear/speedMult/timeRunning/gameTime).
- `Diagnostics/DiagnosticsIntervalSeconds` (float, default `10`): diagnostics cadence.

**Confirmed semantics (in-game 2026-07-09):**
- `Rpc_ToggleFastForward()` CYCLES the multiplier (not a toggle) — 1→10→36→75→162→1.
- `Rpc_SetDayOfYear(Int32)` with DayOfYear+1 advances both day-of-year AND GetDaysPassed (so
  day-boundary logic in mods like DenRespawnMod fires immediately).
- Also present (untested): `Rpc_SetGameTime(Single)`, `Rpc_ToggleTimePassing()`,
  `TimeRunningEnabled`.

**Known cosmetic:** vanilla oddity — `dayOfYear` can exceed `daysInOneYear` (74 vs 57 read in the
same session; the year-wrap warning fires on every L press because of this — harmless).

**Authority:** gated on `ws.Runner.IsServer || ws.Runner.IsSharedModeMasterClient` (solo = host),
with try/catch. Proceeds on check failure (permissive fallback).

**Time-measurement note for testing time-based features (confirmed in-game 2026-07-21):**
The game's built-in fast-forward and skip-day (which this mod toggles) DO advance
`WeatherSystem.NetworkedCurrentGameTime` correctly — it tracks the day cycle 1:1 with
daysPassed/dayOfYear. An experiment with 2 skip-days plus 162× fast-forward across 5 days
advanced `NetworkedCurrentGameTime` by ~6.7 days, matching ~7 day-counter increments. Two
subtleties: (a) skip-day increments daysPassed/dayOfYear one frame before `NetworkedCurrentGameTime`
catches up (+1 day), so for a single frame the counter leads the clock — harmless and
self-correcting; (b) crossing one midnight is one day-boundary but only a few hours of elapsed
time if you started mid-day, so measure elapsed time by the `NetworkedCurrentGameTime` delta, not
by midnight crossings. When testing mods that measure elapsed time (e.g. TreeRespawn respawns),
use `NetworkedCurrentGameTime` deltas directly.

**Files:** TimeWarpMod/{MyPluginInfo.cs, Plugin.cs, TimeWarpTracker.cs, TimeWarpMod.csproj,
TimeWarpMod.dll}. GUID `com.askamods.timewarp`. PLUGIN_VERSION = csproj `<Version>` = 0.1.1.

**Not for Nexus:** development/testing tool. Installs parked (`.dll.off`) on fresh machines via
`sync-plugins.ps1`.
