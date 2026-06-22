# Mod 3: HealthRegenMod — COMPLETE

**Goal:** Regenerate player HP (default 1/sec) after 10s (configurable) without taking damage.

**Game subsystem:** [Player Character vs. Creature](../architecture.md#player-character-vs-creature)
— the reason this patches `PlayerCharacter` (not `Creature`) lives there, along with the
`FindObjectsByType` / `TryCast` interop dead-ends this mod ran into.

**Working approach:**
- No damage-pipeline patch needed — `PlayerCharacter`/`Character` already maintain `GetDamageTakenHistory().LastDamageTime` internally on every hit; poll it instead of patching `TakeDamage`
- Postfix patches on `PlayerCharacter.Spawned()` and `PlayerCharacter.Despawned()` capture/release `Plugin.LocalPlayer`, gated on `HasAuthority` to pick out this client's own avatar (other players' characters are visible locally too, with `HasAuthority == false`)
- `RegenTracker` (registered `MonoBehaviour`, `Update()` polling, same pattern as `DayTracker`) watches `Plugin.LocalPlayer`; reset tracking whenever the reference changes (covers spawn/respawn)
- "Out of combat" detection: track `LastDamageTime`; if it hasn't changed since the previous frame, accumulate `Time.deltaTime` into a seconds-since-last-hit counter; once that counter clears `OutOfCombatSeconds`, regen kicks in
- Regen is **discrete tick** based: a separate `_secondsSinceLastTick` accumulator adds `HealPerTick` HP every `SecondsPerTick` seconds (a `while` loop drains the accumulator so it catches up if a frame is long), clamped to `MaxHealth`. Tick timer resets on hit / death / player change so the first tick can't fire early. `SecondsPerTick = 0` falls back to smooth/continuous regen (`HealPerTick` treated as HP/sec, the original pre-2026-06-20 behavior)
- Config: `HealthRegen/HealPerTick` (float, default 1.0), `HealthRegen/SecondsPerTick` (float, default 1.0; 0 = continuous), `HealthRegen/OutOfCombatSeconds` (float, default 10.0). Default 1 HP/1s matches the old average rate, just in 1-HP steps
- Confirmed working in-game: regen kicked in ~10s after spawning at half health; taking damage mid-regen paused it, then it resumed ~10s after the last hit
