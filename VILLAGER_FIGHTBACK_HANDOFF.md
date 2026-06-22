# Mod 7 — VillagerFightBackMod — Handoff (BUILT, AWAITING IN-GAME TEST)

**Status as of 2026-06-22:** Code is **written, compiles clean, and is deployed** to
`BepInEx\plugins\VillagerFightBackMod\`. **Nothing has been verified in-game yet** — the whole point of
the next session is the test run below. The deciding facts can't be read from the IL2CPP binary (FSM
state method bodies aren't dumpable), so this *must* be confirmed live before the mod can be called
working or shipped to Nexus.

Source of truth for the design: [`docs/mods/villager-fight-back.md`](docs/mods/villager-fight-back.md)
(mod recipe) and the **"Villager Combat / Fight-vs-Flee System"** section in
[`docs/architecture.md`](docs/architecture.md#villager-combat--fight-vs-flee-system) (subsystem facts +
the pending-verification list). Read both first. This handoff is just the test plan + the fallback.

## The request (Nexus comment on DynamicVillagerNeedsMod, user rondi112, 2026-06-22)
> "it pisses me off that the villagers can't fight back against the Wisps. I'd like a mod that doesn't
> scare them into killing them."

Goal: regular (non-warrior) villagers should **stand and fight a whitelisted enemy set** (Wisps — weak,
anything can kill them) instead of fleeing, while still fleeing genuinely dangerous attackers
(draugar/wolves). Per-enemy whitelist by name. Villager-only; player untouched.

## What's built (so it isn't re-derived)
- `Patches/FleeCombatPatch.cs`:
  - **`FleeCombatShouldFightPatch`** — Postfix on `FleeCombatQuest/FleeCombatQuestData.get_ShouldFight`.
    If it returns `false` (flee) but `__instance._engagedEnemy` is whitelisted → set `__result=true`.
    Gated on `__instance._survival._hasAuthority` (host/solo). Pure read + return-bool → co-op-safe.
  - **`FleeCombatGetSpookedPatch`** — log-only Postfix on `FleeCombatQuestData._GetSpooked(IAttackTarget)`;
    logs each distinct attacker's `GetTargetName()` + `Faction` once (creature names are runtime SO
    values, not in the binary).
- Config `[VillagerFightBack]` (`com.askamods.villagerfightback.cfg`): `Enabled`=true,
  `FightBackAgainst`=`"Wisp"`, `FightBackFactions`=`""`, `DebugLogging`=**true** (defaulted on while unverified).

## The test run needed (pick up here)
Pre-req: the user is starting a **new save** and walking a villager to the **beach where Wisps spawn**
(closest reliable Wisp encounter). Build is already deployed; just launch the game with the mod active.

Watch `BepInEx\LogOutput.log` for `[VillagerFightBack]` lines, in order:

1. **Discovery — does the spook log fire and what is the Wisp actually called?**
   Expect: `Villager spooked by '<name>' (faction=<X>) — ...`. **Record the exact `<name>` and `<faction>`.**
   - If the name is NOT literally "Wisp" → set `FightBackAgainst` to the logged substring and continue.
   - If `_GetSpooked` never logs when a Wisp attacks → the flee path isn't `_GetSpooked`; see Open Q #1.

2. **Flip — does `ShouldFight` get forced true?**
   Expect: `Forced ShouldFight=TRUE vs '<name>' (faction=<X>).` (throttled to ~once/2s).
   - If this never appears even though spook logged a whitelisted name → `_engagedEnemy` was null at
     getter time, OR `get_ShouldFight` isn't being polled. See Open Q #2.

3. **★ THE make-or-break observation — does the villager actually ATTACK the Wisp, or just stop running?**
   This is the one fact the binary can't answer. Watch the villager on-screen:
   - **Attacks + kills the Wisp** → SUCCESS. Proceed to step 4.
   - **Stops fleeing but stands idle / doesn't engage** → `ShouldFight` alone is insufficient; go to
     **Fallback (warrior path)** below.

4. **Whitelist precision — does a dangerous enemy still cause flight?**
   Spawn/encounter a draugr or wolf (NOT on the list). Confirm the villager still flees (no "Forced"
   log for it, no engagement). This proves the whitelist is doing the discriminating, not a blanket
   "fight everything."

5. **Co-op (optional, if a session is easy):** confirm host-driven behavior replicates to a client and
   nothing desyncs. The patch writes no networked state, so this is expected-safe, but worth a look.

**Outcome decision tree:**
- Steps 1-4 pass → tune `FightBackAgainst` to the real name, flip `DebugLogging` default back to `false`,
  bump nothing else, ship to Nexus (see publishing note).
- Step 3 fails (idle, no attack) → implement the Fallback, re-test from step 3.
- Step 1 fails (no spook log) → Open Q #1.

## Fallback if `ShouldFight=true` doesn't produce attacking (warrior path)
A non-warrior may have no fight-capable behavior wired at all, so flipping the flee quest's flag just
stops the running. In that case route a whitelisted encounter through the game's **warrior** combat,
which is known to fight. Relevant members (confirmed present; mechanics need RE):
```
Villager.IsWarrior (bool)
VillagerSurvival._CheckWarriorStatus()                  ← recomputes warrior status (from equipped warriorGear)
VillagerSurvival._warriorCombatQuest : WarriorCombatQuest
VillagerSurvival._warriorQuestAdded (bool) / ._warriorBuffAdded (bool)
SSSGame.AI.WarriorCombatQuest (: CombatQuest) .TriggerPriority   ← outranks FleeCombatQuest when active
SSSGame.AI.CombatQuest .GetPriority(QuestData) / .TriggerPriority / .SetDirty()
```
Approaches, roughly in order of preference:
1. **Temporarily confer warrior status** when a villager is engaged with a whitelisted enemy: ensure
   `_warriorCombatQuest` is added/active (`_warriorQuestAdded`) and revert when the enemy is gone.
   Caveat: warrior status isn't target-specific and carries a buff (`warriorStatusEffect`) + gear
   assumptions — confirm it doesn't make them fight *everything*. May need to pair with the existing
   `ShouldFight` gate so flee is still the default for non-whitelisted targets.
2. **Re-rank quests for the whitelisted case** — patch `FleeCombatQuest.GetPriority`/`TriggerPriority`
   down (or `WarriorCombatQuest` up) only while `_engagedEnemy` is whitelisted, so the AIManager picks
   the fight quest. Requires the villager to actually *have* a `WarriorCombatQuest` in its set (warriors
   get it via gear; regular villagers may not) — verify before relying on this.
3. Investigate whether there's a lighter "villager defends" path (`DefensiveCombatTask`,
   `FSM_SetOnDefendDuty` / `Villager.IsOnDefendDuty`) that fights without full warrior status.

The warrior path is heavier and host-authoritative — gate all of it on `_hasAuthority` and prefer adding
a quest / calling an existing method over writing networked state directly (per the universal gotchas).

## Open questions to resolve at runtime
1. **Is `_GetSpooked` the flee trigger?** If no spook log fires when a Wisp attacks, the flee decision
   may route elsewhere. Candidates to instrument next: `FleeCombatQuestData._OnTargetChanged(IAttackTarget)`,
   `CombatQuestData._OnTakeDamage(DamageData)`, or `VillagerSocial.SendSpookedNotification(IAttackTarget)`.
2. **Is `_engagedEnemy` populated when `ShouldFight` is read?** If frequently null, capture the target in
   `_GetSpooked` and remember it per quest-data instance (keyed by instance), then consult that in the
   getter postfix instead of `_engagedEnemy`.
3. **Wisp faction** — once logged, decide whether a faction-based whitelist is ever worth exposing as a
   default, or names-only stays the recommendation (wisps/wolves may share `Danger`; draugar are `Undead`).
4. **Can an unarmed villager damage a Wisp at all?** If they engage but can't deal damage, the fix needs
   to also ensure a weapon (`FSM_FindWeaponDuringCombat` suggests they grab one — confirm).

## Build / deploy / tooling (same as the other AskaMods)
- Project `VillagerFightBackMod/VillagerFightBackMod.csproj` (net6.0, `CopyToPlugins` target). Already
  builds clean; `dotnet build` redeploys to `BepInEx\plugins\` and the project dir.
- `_explore/` Mono.Cecil scripts for further RE: `search.ps1 -TypeKeywords @(...)` / `-MemberKeywords @(...) -Members`,
  `dump.ps1 -Types @("Full.Name", ...)`.
- Key confirmed API (don't re-derive): see the architecture subsection — the quest hierarchy,
  `IAttackTarget` (`GetTargetName()`, `Faction`), `Faction` enum, and the warrior members above.

## Publishing note
This was a Nexus comment request — when it ships, reply to rondi112's comment. Follow
[`docs/nexus-upload.md`](docs/nexus-upload.md) for the upload pipeline (changelog convention +
main-description update). New page (or fold into DynamicVillagerNeedsMod's page? — user's call).
