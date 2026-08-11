# Mod 27: NoNeedsMod — COMPLETE (v1.1.1)

**Goal:** govern the player's and all villagers' survival needs on a polling tick — hold them at
maximum ("god mode"), or run them at a chosen multiple of the game's own drain and refill rates.

**Origin:** Nexus user request (tomkat2351) for a god mode covering hunger/thirst/warmth for
player AND villagers.

**Game subsystem:** [Villager Schedule / Needs / Happiness
System](../architecture.md#villager-schedule--needs--happiness-system) — writable
`VariableAttribute` pinning confirmed across player and all villager contexts.

## Working approach

### Shared detection + pin logic (player and villager passes)
- **Player needs** live on `SSSGame.PlayerSurvival` (accessed via `GetComponent<PlayerSurvival>()`
  on `Plugin.LocalPlayer`, cached per spawn/respawn). Gated on `survival._hasAuthority` &&
  `survival.Initialized`.
- **Villager needs** via `villager.GetSurvival()`. All writes gated on `survival._hasAuthority` &&
  `survival.Initialized` and `villager.HasAuthority` (host-authoritative in co-op; clients skip).
- Pinning is a **discrete tick** based: a `_secondsSinceLastTick` accumulator fires every
  `TickSeconds` seconds (minimum clamp 0.25 s). The accumulator resets after each pass. While any
  need is in rate mode the interval is additionally capped at 0.5 s, because rate mode reads the
  need's own movement between passes and a long interval lets the bar visibly overshoot before the
  correction lands.
- **Two modes per need, chosen by its `<Need>DrainRate`:**
  - `0` (default) — **pin**: `attr.SetValue(attr.max)` each tick, the mod's original behaviour.
  - `> 0` — **rate**: measure `delta = current - lastValueThisModWrote`, rescale it (`delta *
    DrainRate` when falling, `delta * GainRate` when rising), write back
    `Mathf.Clamp(last + scaled, min, max)`, and store that as the new baseline. Net drain ends up
    `DrainRate ×` vanilla without the mod ever needing the game's own drain constant. Same
    technique as DynamicVillagerNeedsMod's `ScaleDrain`, which is in-game-confirmed.
  - Baseline is re-primed (no correction that pass) on the first tick for a need, and whenever the
    movement exceeds 90 % of the attribute's range — that size of jump is a world load, respawn or
    sleep transition, not the ordinary drain the mod governs.
- Rate-mode baselines live in a `Dictionary<IntPtr, NeedState[]>` keyed by the villager's **native
  object pointer** (via the `(object)v is Il2CppObjectBase b` boxing pattern). Only floats and
  bools are stored — no interop wrapper is cached — and entries not touched in the latest pass are
  dropped on the 60 s summary beat.
- Attributes targeted: player — `_foodVAttr`, `_waterVAttr`, `_warmthVAttr`, optionally
  `_energyVAttr` (stamina meter); villagers — same three plus `_restVariableAttribute`
  (0..24 h, drains awake) and `Villager._happinessVAttr` (re-clamped to `HappinessCap` by the
  game — plateau below 100% expected, vanilla behavior).

### Player pass
- Postfix patches on `PlayerCharacter.Spawned()`/`Despawned()` capture/release
  `Plugin.LocalPlayer`, gated on `HasAuthority`.
- `NeedsTracker` (ClassInjector-registered MonoBehaviour, `Update()` polling) watches
  `Plugin.LocalPlayer`; tracking resets whenever the reference changes (covers spawn/respawn).

### Villager pass (confirmed in-game 2026-07-13)
- Villagers tracked via `Villager.Spawned()`/`Despawned(NetworkRunner, bool)` postfixes into a
  static list (add on spawn, remove on despawn, prune nulls in the tick loop).
- Same pin-or-rate logic as the player, applied per villager to each enabled need, with each
  villager's rate-mode baselines kept separately.

## Config

The booleans decide **which needs the mod governs**; the rates decide **how**. A need whose boolean
is false is untouched, and its rates are ignored.

- `[Player]`: `Enabled` (bool, default true), `Food` / `Water` / `Warmth` (bool, all default
  true), `Energy` (bool, default false — the stamina meter; off by default because the
  original request was hunger/thirst/warmth only; turn on for full god mode).
- `[Villagers]`: `Enabled` (bool, default true), `Food` / `Water` / `Warmth` / `Rest` /
  `Happiness` (bool, all default true). Rest note: at `RestDrainRate = 0` villagers never get
  tired, but the game still forces sleep at nightfall (vanilla). Happiness note: re-clamped to
  HappinessCap by the game.
- **Rates**, both sections, range 0–10, one pair per need
  (`FoodDrainRate`/`FoodGainRate`, `WaterDrainRate`/`WaterGainRate`,
  `WarmthDrainRate`/`WarmthGainRate`, `[Player] EnergyDrainRate`/`EnergyGainRate`,
  `[Villagers] RestDrainRate`/`RestGainRate`, `[Villagers] HappinessDrainRate`/`HappinessGainRate`):
  - `<Need>DrainRate` (float, **default 0.0**) — multiple of the game's own drain speed. `0` holds
    the need at maximum; `1` is vanilla; `0.5` is half speed; `2` is twice as fast. The 0 default
    means an existing config keeps the pin-everything behaviour with no edits.
  - `<Need>GainRate` (float, **default 1.0**) — multiple of what the game's own sources restore
    (eating, drinking, a fire, sleeping). `0` means nothing restores the need. Has no effect while
    the matching drain rate is 0, since the need is already held at maximum.
- **Happiness caveat:** in rate mode the mod and the game's HappinessCap clamp are both writing.
  Any corrected value above the villager's housing-based cap is pulled straight back down, so
  happiness rates only have room to act below that cap.
- `[General]`: `TickSeconds` (float, default 2.0), `DebugLogging` (bool, default **false** since
  v1.0.0 — shipped; logs load banner, "Local player character registered.", "Player needs handled
  (food X, max Y)" (once), "Pinned needs for N villagers" (once), and 60 s tick summaries when on).

## Version history

- **v0.1.0** (2026-07-13): initial implementation; player + villager bars pinned; log markers
  fired for player and 48 villagers; zero exceptions. In-game-verified same day.
- **v1.0.0** (2026-07-13): ship prep — `DebugLogging` default flipped from true to false
  (shipped per diagnostics rule), Energy config description reworded to stamina. No behavior
  change. Existing configs keep their saved value.
- **v1.1.0** (2026-08-10): per-need drain/gain rate multipliers for every governed need (player
  food/water/warmth/stamina, villager food/water/warmth/rest/happiness), 20 new config entries.
  `DrainRate = 0` preserves v1.0.0's pin-at-max, so existing configs are unchanged.
  **Rate mode confirmed in-game 2026-08-10** on the player's food need at
  `FoodDrainRate = 5` / `FoodGainRate = 5`: the user played and reported it fine, and the log
  read `Player needs handled (food 71.6, max 100.0)` — food below max, so the need was running
  on the rate path rather than being pinned. 87 villagers registered and pinned in the same
  session with no exceptions. The other needs' rate multipliers share this one code path but
  were not individually exercised.
- **v1.1.1** (2026-08-10): the load banner now prints each need's gain rate beside its drain rate
  (`Food=True/5x drain/5x gain`), so one log line verifies both halves of a need's configuration.
  Log text only, no behavior change. Shipped without an in-game run of this exact build — the user
  declined a relaunch for a logging-only change (his decision, 2026-08-10).
