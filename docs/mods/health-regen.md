# Mod 3: HealthRegenMod — COMPLETE (v1.3.1)

**Goal:** out-of-combat HP regeneration for the player AND all villagers/soldiers, each side with
its own configurable rate.

**Game subsystem:** [Player Character vs. Creature](../architecture.md#player-character-vs-creature)
— the reason this patches `PlayerCharacter` (not `Creature`) lives there, along with the
`FindObjectsByType` / `TryCast` interop dead-ends this mod ran into.

## Working approach

### Shared detection + heal logic (player and villager passes)
- No damage-pipeline patch needed — `PlayerCharacter`/`Character` already maintain
  `GetDamageTakenHistory().LastDamageTime` internally on every hit; poll it instead of patching
  `TakeDamage`.
- "Out of combat" detection: if `LastDamageTime` hasn't changed since the previous check,
  accumulate elapsed time; once the counter clears `OutOfCombatSeconds`, regen kicks in.
- Regen is **discrete tick** based: a `_secondsSinceLastTick` accumulator adds `HealPerTick` HP
  every `SecondsPerTick` seconds (a `while` loop drains the accumulator if a frame is long),
  clamped to `MaxHealth`. Tick timer resets on hit / death / target change so the first tick can't
  fire early. `SecondsPerTick = 0` = smooth/continuous regen (`HealPerTick` treated as HP/sec).
- Perf: the whole mod's `Update()` is gated to every 4th frame (`(Time.frameCount & 3) != 0`) with
  `Time.deltaTime` accumulated across skipped frames, per the project throttle convention
  (confirmed in-game framerate recovery 2026-07-07).

### Player pass
- Postfix patches on `PlayerCharacter.Spawned()`/`Despawned()` capture/release `Plugin.LocalPlayer`,
  gated on `HasAuthority` (other players' avatars are visible locally with `HasAuthority == false`).
- `RegenTracker` (registered MonoBehaviour, `Update()` polling — the DayTracker pattern) watches
  `Plugin.LocalPlayer`; tracking resets whenever the reference changes (covers spawn/respawn).
- Confirmed in-game: regen kicked in ~10 s after spawning at half health; damage mid-regen paused
  it, then it resumed ~10 s after the last hit.

### Villager pass (confirmed in-game 2026-07-08)
- Villagers tracked via `Villager.Spawned()`/`Despawned(NetworkRunner, bool)` postfixes into a
  static list (the DynamicVillagerNeeds pattern: add on spawn guarded by Contains, remove on
  despawn, prune nulls in the tick loop; per-villager state in a `Dictionary<Villager, VState>`
  cleared on despawn).
- Same detection + heal logic as the player, per villager, reading the separate `[VillagerRegen]`
  rate keys (independent of the player's since v1.3.0 — confirmed in-game 2026-07-08: villagers
  heal at their own configured rate while the player heals at the `[HealthRegen]` rate).
- All villager health writes gated on `villager.HasAuthority` (host-authoritative in co-op;
  clients skip writes).
- **`Character.CurrentHealth` writes DO stick on villagers** (confirmed in-game 2026-07-08 — a
  hurt villager's health bar visibly ticked upward; no `CharacterSurvival._healthVAttr` fallback
  needed). Soldiers/warriors are just `Villager`s with `IsWarrior == true` — no separate handling
  (binary-confirmed via Cecil, 2026-07-08).

## Config

- `[HealthRegen]` (player): `HealPerTick` (float, default 1.0), `SecondsPerTick` (float, default
  1.0; 0 = continuous), `OutOfCombatSeconds` (float, default 10.0).
- `[VillagerRegen]`: `ApplyToVillagers` (bool, default true); `HealPerTick` / `SecondsPerTick` /
  `OutOfCombatSeconds` (floats, defaults 1.0 / 1.0 / 10.0 — identical to the player defaults, so
  behavior is unchanged until tuned; same `SecondsPerTick = 0` semantics); `DebugLogging` (bool,
  default **false** since v1.3.1 — shipped; logs villager registered / regen started / regen
  interrupted when on).

## Version history

- **v1.0.x–v1.1.x:** player-only regen, discrete-tick model (originally continuous;
  pre-2026-06-20 behavior survives as `SecondsPerTick = 0`).
- **v1.2.0** (2026-07-08): villager/soldier regen added, sharing the player's rate keys; every-4th-
  frame throttle for the whole mod.
- **v1.3.0** (2026-07-08): villager rates split into their own `[VillagerRegen]` keys, confirmed
  in-game.
- **v1.3.1**: `DebugLogging` default flipped to false (verified; diagnostics rule). Existing
  configs keep their written value.
