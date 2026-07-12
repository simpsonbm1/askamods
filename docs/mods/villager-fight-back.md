# Mod 7: VillagerFightBackMod — COMPLETE (Verified In-Game, v1.0.27)

**Goal:** Let regular (non-warrior) villagers **stand and fight a whitelisted set of enemies**
instead of fleeing — e.g., Wisps, which anything can kill — while still fleeing from genuinely
dangerous attackers (draugar, wolves, ...). Villager-only; the player is never touched.

**Game subsystem:** [Villager Combat / Fight-vs-Flee System](../architecture.md#villager-combat--fight-vs-flee-system)

## Working Design (v1.0.25)
The solution requires two axes: redirecting the combat behavior FSM and resolving QuestRunner arbitration (work quests competing with combat).

1. **FSM Behaviour Swap (`UseNaturalCombatBehaviour`)**
   - **Harmony Patch:** Postfix `CombatQuest.GetFSMBehavior(QuestData)`.
   - **Mechanism:** A villager has two combat FSMs: `fleeCombatBehaviour` (runs/kites) and
     `naturalCombatBehaviour` (stands & fights). `FleeCombatQuest` runs the flee behavior by
     default. When a whitelisted enemy is encountered, this patch overrides the return to use
     `naturalCombatBehaviour` instead.
   - **Pointer Safety:** Standard C# `as` casting (`var cqd = questData as
     CombatQuest.CombatQuestData`) and `Plugin.PtrOf(cqd) == IntPtr.Zero` checks are used to prevent
     native IL2CPP `TryCast` wrapper crashes on uninitialized assets during startup.

2. **Quest Arbitration & Work Suspension (`SuspendWorkWhileEngaged`)**
   - **Harmony Patch:** Postfix `QuestRunner.Update`.
   - **Mechanism:** In gameplay, an active work quest (e.g. `TerraformingQuest`) out-prioritizes
     combat, causing villagers to oscillate and try to run back to their job markers. To solve this,
     when a villager is actively engaged with a whitelisted enemy, we suspend/remove their active
     work quest via the game's `QuestRunner.RemoveQuest` (making them effectively idle, so the
     combat quest takes over fully).
   - Once the enemy is dead or gone, we restore the suspended work quests using `QuestRunner.AddQuest`.

3. **Fast Combat Drop (`KeepCombatAlive` / Exit)**
   - **Harmony Patch:** Postfix `FleeCombatQuestData.ShouldFight` (getter).
   - **Mechanism:** Keeps the combat timer topped up to a tight `8f` seconds (rather than `60f` seconds) during the fight to bridge any frame gaps.
   - **Instant Exit:** The moment the target dies (`!decisionTarget.IsAlive()`), we set
     `_combatTimeRemaining = 0f` to drop combat instantly, prompting the QuestRunner to restore work
     quests and return the villager to their job in under a second.

---

## Config `[VillagerFightBack]`
(`BepInEx/config/com.askamods.villagerfightback.cfg`)
- `Enabled` (`true`) — Master switch.
- `FightBackAgainst` (`"Wisp"`) — Comma-separated name substrings to fight back against.
- `FightBackFactions` (`""`) — Optional factions (coarser).
- `CombatTopUpSeconds` (`8.0`) — Time in seconds to keep combat active after the last attack (ends instantly on enemy death).
- `DebugLogging` (`false`) — Logs when spook events and swaps occur.

---

## Verified In-Game Findings (2026-06-27)
1. **Wisp:** `IAttackTarget.GetTargetName() == "Wisp"`, Faction is `Undead`. Whitelisting "Wisp"
   targets them precisely without affecting Draugar (which are also `Undead`).
2. **Attacking:** Standard villagers using `naturalCombatBehaviour` swing their weapons/fists and successfully kill Wisps.
3. **Resuming Jobs:** Once the combat timer is reset upon Wisp death, the suspended work quest is
   restored and building/terraforming tasks are resumed within seconds.
4. **Fleeing Threats:** Villagers still correctly flee from wolves and draugar since they are not on the whitelist.

---

## v1.0.27 — Inconsistent post-combat return-to-work, FIXED (confirmed in-game 2026-07-03)

**Symptom:** after the v1.0.25 fix above, return-to-work was inconsistent rather than reliably fast —
sometimes a villager resumed work within seconds of a fight, other times she stayed "in combat" for
around a minute before dropping back to her job.

**Root cause:** two compounding gaps in the original `KeepCombatAlive` timer logic
(`FleeCombatShouldFightPatch`, postfix on `FleeCombatQuestData.ShouldFight`):
1. **The top-up only ever RAISED `_combatTimeRemaining`** (below `CombatTopUpSeconds/2` → set to
   `CombatTopUpSeconds`). But vanilla itself **re-arms the timer to ~60s mid-fight** (observed
   `combatTime 63.8 -> ...` in-game — plausibly on the villager taking a hit). Nothing ever brought an
   already-high value back down, so a re-arm meant riding out close to the full vanilla timer.
2. **Death-detection lived only in the `ShouldFight` getter postfix**, which is polled by the FSM during
   active engagement — but is frequently **not polled again** once the target is actually dead, so the
   `!isAlive` → `_combatTimeRemaining = 0f` branch could simply never run for a given fight.

Whichever fight hit case (2) — or hit case (1) after case (2) failed — rode out the vanilla ~60s. Fights
where the death-poll happened to land inside the then-8s spook-memory window exited fast; the rest didn't.
This is why the same whitelist/config produced both behaviors from one session to the next.

**Fix (`FleeCombatPatch.cs`):**
- **Down-clamp:** in the same `ShouldFight` postfix, if `_combatTimeRemaining` is ever found ABOVE
  `CombatTopUpSeconds` (i.e. vanilla re-armed it), clamp it back down instead of leaving it. Bounds
  lingering even if death-detection misses entirely.
- **Frame-driven combat end:** `QuestRunner.Update` (already patched for work-suspension, runs every
  tick regardless of whether any getter is being polled) now also holds the live
  `CombatQuest.CombatQuestData` wrapper per villager. The moment the remembered whitelisted enemy reads
  `!IsAlive()`, it zeroes `_combatTimeRemaining` directly, purges the engagement/spook records
  (`ForgetVillagerCombat`/`ForgetSpook` — collapses the priority-boost window immediately rather than
  coasting on the remaining spook TTL), and lets the existing restore-suspended-quests branch run the
  same tick.
- **New `TimerDiagnostics` config key** (default `true`): logs the re-arm clamp
  (`[timer] clamped combatTime 63.8 -> 8.0 (vanilla re-arm)`), the frame-driven end
  (`[timer] enemy 'Wisp' down — combat ended (combatTime X -> 0), restoring work`), and a short
  post-combat watch (`[postcombat] activeQuest=... prio=... combatTime=...`) showing which quest the
  villager runs for a few seconds after — useful if a future regression strands a villager again, since
  the log will name the quest holding her.

**Confirmed in-game (2026-07-03, solo):** villager killed two Wisps back-to-back; both times returned to
work within seconds. Log showed the re-arm clamp firing (`63.8 -> 8.0`) on one fight — direct proof of
root cause (1) above — and the frame-driven end firing on both (`enemy 'Wisp' down — combat ended`),
with the post-combat watch confirming a clean priority handoff `FleeCombatQuest(41) → TerraformingQuest(15)`.

## v1.0.28–1.0.29 — per-frame cost reduction (confirmed in-game 2026-07-07)

**Problem:** `QuestRunner.Update` postfix patches one villager per frame in a settlement, summing to
~15–16 Hz of patch invocations for a medium settlement. Entry cost (trampoline, instance marshaling)
was the bottleneck even with a cheap body.

**v1.0.28:** added per-villager throttle via `Dict<IntPtr, (CombatQuest, DateTime)>` keyed by
`Plugin.PtrOf(runner)`, running the patch body only once per villager per second (~1 Hz per villager
= 15 Hz total for 15 villagers, but spread across the frame).

**v1.0.29:** added global frame-gate `if ((Time.frameCount & 3) != 0) return;` as the **first line**
of the postfix — skips the entire trampoline cost on 75% of invocations, recovering ~75% of the
per-frame entry cost (~75% of ~5ms = ~3.75ms recovered). The per-villager dict cache is retained for
actual work.

**Confirmed in-game (2026-07-07):** per-frame FPS cost fell ~75%; combined throttle (frame-gate +
per-villager limit) brought `QuestRunner.Update` patch from dominant (~25% of frame) to negligible.
