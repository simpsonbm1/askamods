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

## v1.2.0 — Villager/Soldier Regen (⚠️ pending in-game confirmation, built 2026-07-08)

**Feature:** Extends passive out-of-combat health regen to all villagers and soldiers (gear-flagged `Villager` instances — no separate Soldier/Guard class).

**Implementation:**
- Villagers tracked via `Villager.Spawned()`/`Despawned(NetworkRunner, bool)` Harmony postfixes into a static list (the DynamicVillagerNeeds pattern: add on spawn guarded by Contains, remove on despawn, prune `== null` entries in the tick loop; per-villager state in a `Dictionary<Villager, VState>` cleared via `RegenTracker.ForgetVillager` on despawn).
- Same detection + heal logic as the player, per villager: poll `GetDamageTakenHistory().LastDamageTime`, accumulate seconds-since-hit, then discrete-tick or continuous heal clamped to `MaxHealth`. Reuses the **SAME three config values** as the player (`HealPerTick`, `SecondsPerTick`, `OutOfCombatSeconds`).
- All villager health writes gated on `villager.HasAuthority` (host-authoritative in co-op; clients skip writes).
- New config: `[VillagerRegen] ApplyToVillagers` (bool, default true) and `[VillagerRegen] DebugLogging` (bool, default true until in-game verified per the diagnostics rule — flip to false + version bump once confirmed). Debug logs: villager registered / regen started / regen interrupted.

**Performance:** The whole mod's `Update()` (player pass included — previously every frame) is now gated to every 4th frame (`(Time.frameCount & 3) != 0` first line) with `Time.deltaTime` accumulated across skipped frames and fed as dt, per the project throttle convention (confirmed in-game framerate recovery 2026-07-07 on all per-frame-work mods).

**Open questions for in-game test:**
- Whether `Character.CurrentHealth` writes stick on villagers; fallback = `CharacterSurvival._healthVAttr`.
- Soldiers/warriors are just `Villager`s with `IsWarrior == true` — no separate handling needed (binary-confirmed via Cecil, 2026-07-08).
