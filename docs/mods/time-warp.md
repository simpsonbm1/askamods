# Mod 22: TimeWarpMod — DEV TOOL (v0.1.1, NOT for Nexus)

**Purpose:** Accelerate in-game time for mod testing. A day is 24 real minutes; a year ~20 hours — naturally untestable. Deliberately distorts gameplay (raids/events pile up under fast-forward). Local development/test tool only.

**v0.1.1 — Typing guard (confirmed in-game 2026-07-10)**
K and L hotkeys now ignored while a game text field is focused. Confirmed: hotkeys work again after the rename window closes.

**Implementation:** Zero Harmony patches. Hotkeys call the game's own debug RPCs on `SSSGame.Weather.WeatherSystem.Instance` (read FRESH every call, never cached — per-world singleton gotcha).

**Hotkeys (configurable):**
- `K` = `Rpc_ToggleFastForward()` — cycles `TimeSpeedMultiplier` through 1→10→36→75→162→1 (confirmed in-game 2026-07-09).
- `L` = `Rpc_SetDayOfYear(DayOfYear+1)` — advances BOTH `dayOfYear` AND `GetDaysPassed()` (confirmed in-game 2026-07-09; drives other mods' day-boundary logic in seconds).

**Config (`com.askamods.timewarp.cfg`):**
- `General/FastForwardHotkey` (string, default: `"k"`): Key to cycle speed multiplier.
- `General/SkipDayHotkey` (string, default: `"l"`): Key to advance day.
- `Diagnostics/TimeDiagnostics` (bool, default: `true` — set for user comfort, no ship impact since this mod never ships): Dump `[TimeWarp][state]` blocks periodically + pre/post around each action (daysPassed/dayOfYear/timeOfDay/dayLength/daysInYear/speedMult/timeRunning/gameTime).
- `Diagnostics/DiagnosticsIntervalSeconds` (float, default: `10`): How often to log diagnostics.

**Confirmed semantics (in-game 2026-07-09):**
- `Rpc_ToggleFastForward()` CYCLES the multiplier (not toggle) — 1→10→36→75→162→1.
- `Rpc_SetDayOfYear(Int32)` with DayOfYear+1 advances both day-of-year AND GetDaysPassed (so day-boundary logic in mods like DenRespawnMod fires immediately).
- Also present (untested): `Rpc_SetGameTime(Single)`, `Rpc_ToggleTimePassing()`, `TimeRunningEnabled`.

**Known cosmetic:** Vanilla oddity — `dayOfYear` can exceed `daysInOneYear` (74 vs 57 read in the same session; year-wrap warning fires on every L press because of this — harmless).

**Authority:** Gated on `ws.Runner.IsServer || ws.Runner.IsSharedModeMasterClient` (solo = host), with try/catch. Proceeds on check failure (permissive fallback).

**Files:** TimeWarpMod/{MyPluginInfo.cs, Plugin.cs, TimeWarpTracker.cs, TimeWarpMod.csproj, TimeWarpMod.dll}. GUID com.askamods.timewarp. Version 0.1.0 (PLUGIN_VERSION = csproj <Version>).

**Not for Nexus:** This is a development/testing tool. Installs parked (.dll.off) on fresh machines via `sync-plugins.ps1`.
