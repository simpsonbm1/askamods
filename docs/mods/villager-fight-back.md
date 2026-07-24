# Mod 7: VillagerFightBackMod — COMPLETE (v1.0.31, on Nexus as "Villager Fight Back")

**Goal:** let regular (non-warrior) villagers **stand and fight a whitelisted set of enemies**
instead of fleeing — e.g. Wisps, which anything can kill — while still fleeing from genuinely
dangerous attackers (draugar, wolves, …). Villager-only; the player is never touched.

**Locale-safe as of v1.0.31 (confirmed in-game, German, 2026-07-24):** the `FightBackAgainst` name
whitelist matches the enemy's locale-invariant creature asset name (`CreatureDataSheet.name`) in
addition to its translated display name, so a token like `Wisp` or `Crawler` works in every game
language, not just English.

**Game subsystem:** [Villager Combat / Fight-vs-Flee System](../architecture.md#villager-combat--fight-vs-flee-system).
Deep lever-by-lever evidence: [`VILLAGER_FIGHTBACK_HANDOFF.md`](../../VILLAGER_FIGHTBACK_HANDOFF.md).

## Working design

Three cooperating pieces — behavior swap, quest arbitration, and combat-timer control:

1. **FSM behaviour swap** — postfix on `CombatQuest.GetFSMBehavior(QuestData)`. A villager has two
   combat FSMs: `fleeCombatBehaviour` (runs/kites) and `naturalCombatBehaviour` (stands & fights);
   `FleeCombatQuest` runs the flee behavior by default. Against a whitelisted enemy the patch
   overrides the return to `naturalCombatBehaviour`. Pointer safety: `as` cast +
   `Plugin.PtrOf(...) == IntPtr.Zero` checks to avoid wrapper crashes on uninitialized assets at
   startup.

2. **Quest arbitration / work suspension** — postfix on `QuestRunner.Update`. Active work quests
   (e.g. `TerraformingQuest`) out-prioritize combat, making villagers oscillate back toward job
   markers mid-fight. While engaged with a whitelisted enemy, the active work quest is removed via
   `QuestRunner.RemoveQuest` (villager effectively idle → combat quest takes over fully); once the
   enemy is dead or gone, suspended quests are restored via `QuestRunner.AddQuest`.

3. **Combat-timer control** (postfix on `FleeCombatQuestData.ShouldFight` + the `QuestRunner.Update`
   patch above; the v1.0.27 shape — earlier top-up-only logic was unreliable, see Version history):
   - **Top-up AND down-clamp:** `_combatTimeRemaining` is held at `CombatTopUpSeconds` (default 8 s
     vs vanilla ~60 s) — raised when low, and **clamped back down when vanilla re-arms it to ~60 s
     mid-fight** (observed on the villager taking a hit; without the clamp a re-arm rode out the
     full vanilla timer).
   - **Frame-driven combat end:** the `QuestRunner.Update` patch (runs every tick regardless of
     getter polling) holds the live `CombatQuest.CombatQuestData` wrapper per villager; the moment
     the remembered enemy reads `!IsAlive()`, it zeroes `_combatTimeRemaining`, purges the
     engagement/spook records, and lets the restore-suspended-quests branch run the same tick.
     (Death detection can't live only in the `ShouldFight` postfix — that getter is frequently not
     polled again after the target dies.)
   - Confirmed in-game 2026-07-03: back-to-back Wisp kills, return-to-work within seconds both
     times; log showed the re-arm clamp (`63.8 -> 8.0`) and the frame-driven end firing, with a
     clean priority handoff `FleeCombatQuest(41) → TerraformingQuest(15)`.

**Perf throttles (confirmed in-game 2026-07-07):** global frame-gate
`if ((Time.frameCount & 3) != 0) return;` as the first line of the `QuestRunner.Update` postfix
(skips the trampoline cost on 75% of invocations) + a per-villager once-per-second work limit via a
`Dict<IntPtr, (CombatQuest, DateTime)>` keyed by `Plugin.PtrOf(runner)`. Brought the patch from
~25% of frame time to negligible.

## Config (`[VillagerFightBack]`, `com.askamods.villagerfightback.cfg`)

- `Enabled` (`true`) — master switch (false = vanilla flee).
- `FightBackAgainst` (`"Wisp"`) — comma-separated enemy NAME substrings (case-insensitive). Matched
  against BOTH the attacker's translated display name AND its locale-invariant creature asset name
  (`CreatureDataSheet.name`), so a token works in every game language. Empty = fight nobody.
- `FightBackFactions` (`""`) — optional coarser faction list (Critters, Danger, Undead, Vikings,
  Neutral, Structures, Ignore). Note Undead includes draugar — usually leave empty and use names.
- `CombatTopUpSeconds` (`8.0`) — combat kept active this long after the last attack; ends
  instantly on enemy death.
- `DebugLogging` (`false`) — logs each spook (name + faction, for filling `FightBackAgainst`) and
  each fight flip.
- `TimerDiagnostics` (`false` since v1.0.30 — shipped) — logs re-arm clamps, end-of-combat
  zero-out, and a short post-combat quest watch; flip on if a villager ever gets stuck in combat
  (the log names the quest holding her).

## Locale-invariant name matching (v1.0.31)

`Plugin.IsWhitelisted(IAttackTarget)` is the single chokepoint every patch routes through. It
matches each `FightBackAgainst` token against two names: the translated `IAttackTarget.GetTargetName()`
(as before) and the invariant `Creature.dataSheet.name`. Reaching the datasheet from an
`IAttackTarget`: `TryCast<Creature>()` does NOT compile (`Creature`'s base chain runs through the
stripped unity-libs stub — the universal "no TryCast on a UnityEngine.Object type" gotcha), so
`Plugin.InvariantCreatureName` walks the native class chain (`il2cpp_object_get_class` →
`il2cpp_class_get_parent`, matching class name `"Creature"`, depth-bounded) and rewraps
`new Creature(b.Pointer)` — the TaskUnlocker/SupplyChain ancestor-walk pattern. Non-creature targets
(players/structures) return null and fall back to display-name matching. The `DebugLogging` spook
line prints both names + faction, so the language-proof token is visible in the log.

## Verified in-game findings (2026-06-27)

0. **Mine crawler** (confirmed 2026-07-24): faction `Critters`, asset name contains `Crawler`.
   `Crawler` in `FightBackAgainst` makes miners fight them; verified in a German session.

1. **Wisp:** `IAttackTarget.GetTargetName() == "Wisp"`, faction `Undead`. Whitelisting "Wisp"
   targets them precisely without affecting draugar (also `Undead`).
2. Standard villagers on `naturalCombatBehaviour` swing weapons/fists and successfully kill Wisps.
3. Suspended work quests restore within seconds of the kill (building/terraforming resumes).
4. Villagers still correctly flee wolves and draugar (not whitelisted).

## Version history

- **v1.0.25** (2026-06-27): working design shipped (behaviour swap + work suspension + 8 s
  top-up), verified in-game.
- **v1.0.27** (2026-07-03): fixed inconsistent post-combat return-to-work. Root causes: (1) the
  top-up only ever RAISED the timer, so vanilla's mid-fight ~60 s re-arm rode out nearly the full
  timer; (2) death detection lived only in the `ShouldFight` getter, which often isn't polled
  after the target dies. Fix = down-clamp + frame-driven combat end (see Working design);
  `TimerDiagnostics` added. Confirmed in-game same day.
- **v1.0.28–v1.0.29** (2026-07-07): per-frame cost reduction (per-villager 1 Hz throttle, then the
  frame-gate) — patch cost fell ~75%, confirmed in-game.
- **v1.0.30** (2026-07-12): `TimerDiagnostics` default flipped to false (shipped; the v1.0.27
  verification it defaulted on for is long done).
- **v1.0.31** (2026-07-24): `FightBackAgainst` matches the locale-invariant creature asset name
  (`Creature.dataSheet.name`) alongside the translated display name, fixing non-English games where
  the whitelist matched nothing (Nexus report: German miners fleeing mine crawlers). Discovery log
  prints both names. Confirmed in-game in a German session (2026-07-24): crawlers respawned via
  DenRespawn, two miners stood and fought them with `Crawler` in the whitelist.
