# SummonTimerMod

**Status: COMPLETE v0.1.0 — confirmed in-game (2026-07-09). Personal/local-use mod, NOT for Nexus.**

## Overview
Removes the escalating wait timer when summoning a new villager at the Eye of Odin (costs 5 jotun
blood). In vanilla ASKA, the wait time grows as your current villager count increases — starting at
a few seconds but reaching 10+ minutes at high populations. This mod eliminates that wait entirely
(configurable via multiplier).

## How it works

**Harmony postfix on `SSSGame.VillagerOutlet.Spawned()`** (the safe `NetworkBehaviour` lifecycle
capture point, fires once per VillagerOutlet instance per world load):

1. **Captures the original delay values** from the outlet's `_spawnTimeline.cooldowns` list on first
   initialization (keyed by the asset's name in a process-static `Dictionary<string, float[]>`),
   ensuring idempotency across repeated world loads and asset re-use.

2. **Scales both delay sources** by config `[SummonTimer] TimerMultiplier` (default **0.0 = no wait**):
   - Every `_spawnTimeline.cooldowns.thresholds` entry's `value` field
   - The per-instance `gametimeToSpawnVillager` float

3. **No cached interop wrappers** — uses plain managed floats only, so shared-asset re-scaling and world reloads cannot compound or leave stale state.

**Diagnostic postfixes** (two of them, for verification):
- `OnStorageMenuConfirmationPressed` logs the timer state when the summon confirm is pressed
- `SpawnVillager` logs when the timer expires and the villager is instantiated

## Configuration

| Key | Default | Notes |
|---|---|---|
| `[SummonTimer] Enabled` | `true` | Enable/disable the mod entirely |
| `[SummonTimer] TimerMultiplier` | `0.0` | Scales both delay sources. `0.0` = no wait; `0.5` = half-speed; `1.0` = vanilla. ⚠️ Fractional multipliers (0 < m < 1) are untested and may not scale linearly. |
| `[SummonTimer] Diagnostics` | `true` | Log timer state (left true by default since this is a personal/local-use mod with low-volume output) |

## Diagnostics / Fire-Verification Markers

Expected log lines when summoning with `Diagnostics=true`:
- `[SummonTimer] VillagerOutlet.Spawned — scaling applied (xN entries)` — printed once at world
  load, confirms the scale factor and number of threshold entries adjusted.
- `[SummonTimer] confirm: <timer-state>` — printed when summon confirm is pressed (shows current timer values).
- `[SummonTimer] SpawnVillager fired at <timestamp>` — printed when the timer expires and the villager spawns.

All three patches are fire-verified to execute in-game (2026-07-09) — not AOT-inlined.

## Known Issues & Game Facts

- **⚠️ Cooldown-table semantics UNKNOWN:** vanilla config shows all 9 thresholds (3, 8, 12, 20, 40,
  70, 110, 150, 200 villager counts) holding the SAME NEGATIVE value `-2.5980988`, and
  `gametimeToSpawnVillager` = 10. The per-count escalation is NOT a direct per-threshold duration
  lookup; the value may be a rate/curve input, and the actual growth math may involve
  `gametimeCustomizationData : GametimeCustomizationData` (world-customization slots; role
  unconfirmed). **Consequence:** multiplier `0` (remove wait) is confirmed SUFFICIENT; FRACTIONAL
  multipliers (`0 < m < 1`) are **untested** and may not scale linearly.

- **In-game confirmation (2026-07-09):** user summoned 2 villagers back-to-back with
  `TimerMultiplier=0` and reported no wait. Zeroing BOTH delay levers (thresholds + per-instance
  timer) is confirmed sufficient; individual lever necessity was NOT isolated.

- **Harmless degenerate observed:** with `mult=0`, `__NetworkedVillagerTimerEnd` field reads
  `-2147483.8` on confirm (likely underflow from scaling the default 10.0 by 0); game handles it
  gracefully — no crash, timer expires immediately.
