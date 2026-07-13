# Mod 27: NoNeedsMod — COMPLETE (v1.0.0)

**Goal:** pin the player's and all villagers' survival needs at maximum on a polling tick
("god mode" for needs).

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
  `TickSeconds` seconds (minimum clamp 0.25 s). Each tick, for each enabled need attribute, set
  `attr.SetValue(attr.max)`. The accumulator resets after each pass.
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
- Same pinning logic as the player, per villager. All villager needs pinned to max each tick when
  enabled.

## Config

- `[Player]`: `Enabled` (bool, default true), `Food` / `Water` / `Warmth` (bool, all default
  true), `Energy` (bool, default false — pins the stamina meter; off by default because the
  original request was hunger/thirst/warmth only; turn on for full god mode).
- `[Villagers]`: `Enabled` (bool, default true), `Food` / `Water` / `Warmth` / `Rest` /
  `Happiness` (bool, all default true). Rest note: villagers never get tired, but the game still
  forces sleep at nightfall (vanilla). Happiness note: re-clamped to HappinessCap by the game.
- `[General]`: `TickSeconds` (float, default 2.0), `DebugLogging` (bool, default **false** since
  v1.0.0 — shipped; logs load banner, "Local player character registered.", "Player needs pinned
  (food X->Y)" (once), "Pinned needs for N villagers" (once), and 60 s tick summaries when on).

## Version history

- **v0.1.0** (2026-07-13): initial implementation; player + villager bars pinned; log markers
  fired for player and 48 villagers; zero exceptions. In-game-verified same day.
- **v1.0.0** (2026-07-13): ship prep — `DebugLogging` default flipped from true to false
  (shipped per diagnostics rule), Energy config description reworded to stamina. No behavior
  change. Existing configs keep their saved value.
