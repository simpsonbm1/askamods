# Mod 7: VillagerFightBackMod — COMPLETE (Verified In-Game)

**Goal:** Let regular (non-warrior) villagers **stand and fight a whitelisted set of enemies** instead of fleeing — e.g., Wisps, which anything can kill — while still fleeing from genuinely dangerous attackers (draugar, wolves, ...). Villager-only; the player is never touched.

**Game subsystem:** [Villager Combat / Fight-vs-Flee System](../architecture.md#villager-combat--fight-vs-flee-system)

## Working Design (v1.0.24)
The solution requires two axes: redirecting the combat behavior FSM and resolving QuestRunner arbitration (work quests competing with combat).

1. **FSM Behaviour Swap (`UseNaturalCombatBehaviour`)**
   - **Harmony Patch:** Postfix `CombatQuest.GetFSMBehavior(QuestData)`.
   - **Mechanism:** A villager has two combat FSMs: `fleeCombatBehaviour` (runs/kites) and `naturalCombatBehaviour` (stands & fights). `FleeCombatQuest` runs the flee behavior by default. When a whitelisted enemy is encountered, this patch overrides the return to use `naturalCombatBehaviour` instead.
   - **Pointer Safety:** Standard C# `as` casting (`var cqd = questData as CombatQuest.CombatQuestData`) and `Plugin.PtrOf(cqd) == IntPtr.Zero` checks are used to prevent native IL2CPP `TryCast` wrapper crashes on uninitialized assets during startup.

2. **Quest Arbitration & Work Suspension (`SuspendWorkWhileEngaged`)**
   - **Harmony Patch:** Postfix `QuestRunner.Update`.
   - **Mechanism:** In gameplay, an active work quest (e.g. `TerraformingQuest`) out-prioritizes combat, causing villagers to oscillate and try to run back to their job markers. To solve this, when a villager is actively engaged with a whitelisted enemy, we suspend/remove their active work quest via the game's `QuestRunner.RemoveQuest` (making them effectively idle, so the combat quest takes over fully).
   - Once the enemy is dead or gone, we restore the suspended work quests using `QuestRunner.AddQuest`.

3. **Fast Combat Drop (`KeepCombatAlive` / Exit)**
   - **Harmony Patch:** Postfix `FleeCombatQuestData.ShouldFight` (getter).
   - **Mechanism:** Keeps the combat timer topped up to a tight `8f` seconds (rather than `60f` seconds) during the fight to bridge any frame gaps.
   - **Instant Exit:** The moment the target dies (`!decisionTarget.IsAlive()`), we set `_combatTimeRemaining = 0f` to drop combat instantly, prompting the QuestRunner to restore work quests and return the villager to their job in under a second.

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
1. **Wisp:** `IAttackTarget.GetTargetName() == "Wisp"`, Faction is `Undead`. Whitelisting "Wisp" targets them precisely without affecting Draugar (which are also `Undead`).
2. **Attacking:** Standard villagers using `naturalCombatBehaviour` swing their weapons/fists and successfully kill Wisps.
3. **Resuming Jobs:** Once the combat timer is reset upon Wisp death, the suspended work quest is restored and building/terraforming tasks are resumed within seconds.
4. **Fleeing Threats:** Villagers still correctly flee from wolves and draugar since they are not on the whitelist.
