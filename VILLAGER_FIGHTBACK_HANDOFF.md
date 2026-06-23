# Mod 7 — VillagerFightBackMod — Handoff (IN PROGRESS, NOT WORKING YET)

**Status as of 2026-06-22 (session 2):** Deployed **v1.0.10**. The mod **loads and every patch fires**,
but the goal is **not achieved yet**. Precise current behavior:

- A villager **idle / not en route to a job**: fights and **kills Wisps fine.** ✅ (this already worked in vanilla)
- A villager **on the way to a job**: enters combat for a moment (crossed swords appear, `FSM_MeleeCombat`
  is reached) but is **pulled back to her job marker and runs off**; she only commits to the fight
  **once the job state resets** (she stops trying to reach the marker). ❌

**The remaining problem is QUEST ARBITRATION (job-quest vs combat-quest), not fear.** We have fully
removed the spook/fear path; what's left is the work quest out-competing combat. The next session's job
is to read the `activePrio` diagnostic added in v1.0.10 and implement the arbitration fix (most likely:
**suspend her work task while a whitelisted enemy is engaged**).

Source of truth for subsystem facts: the **"Villager Combat / Fight-vs-Flee System"** section in
[`docs/architecture.md`](docs/architecture.md#villager-combat--fight-vs-flee-system) (updated this session)
and the mod recipe [`docs/mods/villager-fight-back.md`](docs/mods/villager-fight-back.md).

## The request (Nexus comment on DynamicVillagerNeedsMod, user rondi112, 2026-06-22)
> "it pisses me off that the villagers can't fight back against the Wisps. I'd like a mod that doesn't
> scare them into killing them."

Goal: regular (non-warrior) villagers **stand and fight Wisps** instead of fleeing to their deaths,
while still fleeing genuine threats (draugar/wolves). Per-enemy **name** whitelist. Villager-only.

---

## ⚠️ Operational gotcha first: Smart App Control blocks fresh builds (this machine)
This cost two test rounds — **read before building.** Windows **Smart App Control is ENFORCED** here
(`HKLM\SYSTEM\CurrentControlSet\Control\CI\Policy\VerifiedAndReputablePolicyState = 1`). It will
**intermittently block a freshly-built unsigned mod DLL** at load:
```
Error loading [VillagerFightBackMod ...]: System.IO.FileLoadException: ... An Application Control
policy has blocked this file. (0x800711C7)
```
- The other already-deployed mods load fine (their hashes are known/grandfathered).
- **.NET SDK builds are deterministic** → rebuilding identical source produces the **same hash** → the
  **same block**. **Relaunching does not help.**
- **Fix that works:** bump the version (`MyPluginInfo.PLUGIN_VERSION` **and** `<Version>` in the csproj),
  which changes the DLL hash. SAC re-evaluates the new unknown hash and (so far, always) lets it load.
  This is why every iteration this session bumped the patch version (1.0.1 → 1.0.10).
- Turning SAC off is the only permanent fix but is **irreversible** (needs a Windows reinstall) — don't.
- **Always confirm the loaded version in the log** (`Loading [VillagerFightBackMod 1.0.X]`) before trusting
  a test — we once tested a stale 1.0.6 thinking it was 1.0.7. Fully quit the game before relaunch.

---

## What we proved this session (the dead-ends + the breakthroughs)
Each lever below is a **config flag** in `com.askamods.villagerfightback.cfg` (all default `true` except
`FightBackFactions`). Keep them while iterating; **prune the confirmed no-ops once it works.**

| Lever / patch | What it does | Result |
|---|---|---|
| **(original) `ShouldFight` postfix → force true** | flip `FleeCombatQuestData.get_ShouldFight` to true | ❌ **Dead end.** `ShouldFight` is **already `True`** here; forcing it does nothing. Flee is **not** gated on it. (patch is now inert) |
| **`SuppressSpook`** | prefix-skip `_GetSpooked` for whitelisted | ⚠️ Removes the spook *notification/visuals* (icon/fanfare) but **not** the flee. `_GetSpooked` is only the notification. |
| **`PreventFlee`** | block `FSM_SetVillagerIsFleeing.OnStateEnter` (fleeingState=true) for whitelisted | ⚠️ Clears the `IsFleeing` flag (no more fear behavior) but she **still runs**. |
| **`ForceEngage`** | `Villager.SetTarget(wisp, onlyIfNotPresent)` | ✅ Puts her **into combat** (crossed swords appear). |
| **`BoostCombatPriority`** | postfix `CombatQuest.GetPriority` → `max(_, 41)` for whitelisted | ❓ **No observed effect.** Suspect `GetPriority` is **not** the priority the `QuestRunner` arbitrates on (`TriggerPriority` may be). v1.0.10 adds a one-shot "GetPriority boost FIRED" log to confirm. |
| **`TreatAsWarrior`** | postfix `FSM_CheckVillagerWarrior.Decide` → true for whitelisted | ❌ **Fires (logged) but no behavior change.** So `FSM_CheckVillagerWarrior` is **not** the melee-vs-run branch in her active FSM. |
| **`PreventRunMovement`** | block `FSM_RunFromTarget.OnStateEnter/Update` for whitelisted | ❌ **`FSM_RunFromTarget` never fired** in the job case → her "running" is **not** the flee-run action; it's **work-quest pathing to the job marker**. Moot for the job case. |
| **`KeepCombatAlive`** | top `_combatTimeRemaining` to 60 in the polled `ShouldFight` postfix | ✅ **Works** (observed `combatTime=63.8`). So **combat is not timing out** — duration is not the blocker. |
| **(probe) `FSM_MeleeCombat.OnStateEnter` log** | log-only | ✅ **`Entered MELEE state vs 'Wisp'` fires** → a **regular villager CAN reach melee.** Huge: the fight behavior exists for non-warriors. |

### Root cause (current best understanding)
The villager's **work quest** (observed: `TerraformingQuest`; also `BuildAndSupplyQuest`, etc.) competes
with `FleeCombatQuest` in the `QuestRunner`. While she has an active job objective (pathing to a job
marker), the job keeps winning/oscillating and drags her to the marker; combat only fully takes over
**once the job state resets**. Idle villagers (no competing job) fight fine. The user's own words:
> "she ran away **until her job state reset**, then ran back into combat to fight the Wisp **once she was
> no longer actively trying to reach the job marker**."

### The contradiction v1.0.10 must resolve
Confirmed `QuestPriority` scale (read at runtime, logged at load):
```
Flee=40  SelfDefense=42  AvoidImminentDanger=46  WorkstationWork=15  ImportantWork=22  Idle=0
```
Combat (40–42) **should trivially beat work (15–22)** — yet work wins in practice and she oscillates
(`activeQuest` flips `TerraformingQuest` ↔ `FleeCombatQuest`). So **either**:
- (a) the `QuestRunner` arbitrates on **`TriggerPriority`**, not `GetPriority` (our boost = no-op), **or**
- (b) `FleeCombatQuest`'s *effective* priority drops when she's not actively fleeing/spooked, letting work
  win in the gaps between Wisp hits.

**v1.0.10 was deployed but NOT yet tested.** It changes no behavior; it adds:
- `activePrio=` in the `[diag]` and spook-state log lines = `QuestRunner._activeQuestPriority`.
  **Read it when `activeQuest=...TerraformingQuest`** — that's work's effective priority in the arbitration.
- A one-shot `GetPriority boost FIRED vs 'Wisp': X -> 41` — if it **never appears**, `GetPriority` is not
  the arbiter (→ it's `TriggerPriority`).

## Pick up here (next session)
1. **Re-test v1.0.10** (confirm `1.0.10` in the log), do the job scenario, capture the `[diag]` run.
   - Note `activePrio` when `activeQuest=…TerraformingQuest`, and whether `GetPriority boost FIRED` appears.
2. **Implement the arbitration fix** based on what you see. In order of preference:
   1. **Suspend/abort her work task while a whitelisted enemy is engaged** — directly matches the observed
      cause and is target-scoped. Candidate APIs: `Villager.AbortMatchTarget()`, `Villager.GetTaskRunner()`
      (→ a `Task.Abort()`/pause), `QuestRunner.RemoveQuest`/`ReevaluateQuest`, `FSM_QuestSetPausedState`,
      `FSM_QuestStop`. Re-enable when the enemy is gone. **This is the leading hypothesis.**
   2. If the arbiter is `TriggerPriority`: boost `CombatQuest`/`FleeCombatQuest` `get_TriggerPriority`. **But**
      it has no target context (can't scope to whitelisted) → would make villagers fight *everything*.
      Mitigate with a per-villager "engaging-whitelist" flag set in the `_GetSpooked` prefix and read in the
      `get_TriggerPriority` postfix (key by villager/targeting pointer).
   3. **Confer real warrior status** (add `WarriorCombatQuest` to her `QuestRunner` / set up
      `VillagerSurvival._warriorCombatQuest` + `_warriorQuestAdded`). Heaviest; warriors stand & fight and
      aren't pulled by jobs. Caveat: `_warriorCombatQuest` may be **null** for a non-warrior → may need to
      create/register one, plus it carries the `warriorStatusEffect` buff and gear assumptions.
3. Once it works: **prune the inert levers** (ShouldFight flip, `TreatAsWarrior`, `PreventRunMovement`, and
   `BoostCombatPriority` if confirmed no-op), keep what's load-bearing (`ForceEngage`, `KeepCombatAlive`,
   probably the job-suspension), flip `DebugLogging` default to `false`, tune `FightBackAgainst`, ship.

## Confirmed API facts (don't re-derive)
- **Wisp:** `IAttackTarget.GetTargetName()` == `"Wisp"`, `.Faction` == `Undead`. Use a **name** whitelist —
  draugar are also `Undead`, so a faction whitelist would make them fight draugar.
- **Flee is FSM-driven, not a single bool:** `FleeCombatQuestData._GetSpooked` = *notification only*;
  `FSM_SetVillagerIsFleeing.OnStateEnter` sets `Villager.IsFleeing`; `FSM_RunFromTarget` does the run
  movement. `ShouldFight` is unrelated to the actual flee (always `True` in this scenario).
- **Get a villager's current target inside any FSM action:** `IFSMBehaviourController.AiTargeting.CurrentTarget`
  (`ITargetAI.CurrentTarget : IAttackTarget`). This is the clean way to scope FSM-action patches by target.
- **`CombatQuestData._combatTimeRemaining`** is a plain settable field (not networked) — top it to hold combat.
- **`QuestRunner`**: `_activeQuestPriority`, `.ActiveQuest.Name`, `FindNextBestQuest`, `ReevaluateQuest`,
  `AddQuest`/`RemoveQuest`. Reach it via `Villager.GetQuestRunner()`.
- **`QuestPriority.c_*`** constants are runtime-readable (scale above).
- **Quests:** non-warrior combat = `FleeCombatQuest`; warrior = `WarriorCombatQuest` (priority `SelfDefense`=42);
  work examples = `TerraformingQuest`, `BuildAndSupplyQuest`, `WorkstationQuest`, …
- **Melee is reachable for non-warriors** (`FSM_MeleeCombat.OnStateEnter` fires for a regular villager).

## Build / deploy / tooling
- `dotnet build VillagerFightBackMod/VillagerFightBackMod.csproj -c Release` → `CopyToPlugins` deploys to
  `ASKA\BepInEx\plugins\VillagerFightBackMod\` and the project dir. **Bump the version each build** (SAC, above).
- `_explore/` Mono.Cecil scripts for RE: `search.ps1 -TypeKeywords @(...) [-MemberKeywords @(...) -Members]`,
  `dump.ps1 -Types @("Full.Name", ...)`. (Method bodies aren't dumpable in IL2CPP — signatures only.)
- Read the live log directly with the Grep tool on `ASKA\BepInEx\LogOutput.log` (pattern `VillagerFightBack`)
  — faster and more complete than pasted excerpts.

## Publishing note
Nexus comment request — when it ships, reply to **rondi112**'s comment. Follow
[`docs/nexus-upload.md`](docs/nexus-upload.md). New page, or fold into DynamicVillagerNeedsMod's page (user's call).
