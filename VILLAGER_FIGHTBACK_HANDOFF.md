# Mod 7 — VillagerFightBackMod — Handoff (COMPLETE — v1.0.23 WORKING; verified in-game)

## RESOLVED (2026-06-27): v1.0.14 Crash resolved by using C# `as` operator instead of IL2CPP `TryCast`
The native access violation (**`0xc0000005`**) on startup was caused by calling the native `Il2CppObjectBase.TryCast<T>()` on uninitialized/dummy `QuestData` assets during early engine asset loading. 
By replacing the native `TryCast` with standard C# `as` casting (`var cqd = questData as CombatQuest.CombatQuestData`) and validating pointers using `Plugin.PtrOf(cqd) == IntPtr.Zero`, we completely avoid invoking the native IL2CPP casting runtime on uninitialized structures. This resolved the startup crash.

Additionally, to resolve the issue where villagers remained in combat/look-out mode for a long time (~1 minute) after killing the Wisps, we:
- Check `decisionTarget.IsAlive()` and immediately force the combat time remaining to `0f` when the target dies.
- Tightened the in-combat top-up threshold to `8f` seconds (rather than `60f` seconds).
This allows the villager to immediately drop out of combat and restore their suspended work quests within seconds of the fight ending.

**Configuration Cleanup (v1.0.23):**
Removed all experimental debug and behavioral toggles from the BepInEx configuration file (mocking them internally to always be active). The config file now only contains the essential options: `Enabled`, `FightBackAgainst`, `FightBackFactions`, `CombatTopUpSeconds`, and `DebugLogging`.

**Status as of 2026-06-27:** Deployed **v1.0.23**, launched clean, and verified in-game. Villagers stand and fight Wisps and return to their jobs immediately after.

**Bisection plan (fix path):** the config feature-flags only gate behavior *inside* each postfix — the Harmony
**detours install regardless** — so bisect at the **source**: comment out groups of `[HarmonyPatch]` classes in
`Patches/FleeCombatPatch.cs`, rebuild (bump version per SAC), launch, and see which group stops the crash.
Start with the newest/riskiest: **`CombatBehaviourSwapPatch`** (`CombatQuest.GetFSMBehavior`, v1.0.14) and
**`QuestRunnerUpdatePatch`** (`QuestRunner.Update`, v1.0.13). v1.0.12 already hard-crashed via a `Single&`
by-ref param (`QuestRunner.FindNextBestQuest`); the current detours' signatures *look* marshal-safe, but a
startup AV in coreclr is the same class of native-glue failure — re-audit each remaining detour's signature.
(The cooking-hut investigation that interrupted this session is fully resolved and was unrelated to VFB.)

**Status as of 2026-06-23 (session 3→4):** Deployed **v1.0.14**, then **launched → it CRASHES the game (see
🛑 banner above)** — no *behavior* test was possible. The last successful behavior test was **v1.0.13**. Five approaches now; the new one is grounded in a v1.0.13 finding that reframes
the whole problem. Dead-ends so far, each for a distinct (documented) reason:
- v1.0.10 `BoostCombatPriority` (GetPriority→41): fires, no effect (GetPriority isn't the selector).
- v1.0.11 `BoostTriggerPriority` (get_TriggerPriority→40): boost log never fired (getter cached).
- v1.0.12 `ForceCombatQuest` (postfix `QuestRunner.FindNextBestQuest`): **CRASHED** — `Single&` by-ref
  out-param is a HarmonyX/IL2CPP trampoline hazard; NRE'd every call → villagers totally inert.
  **Rule: never Harmony-patch an IL2CPP method with a by-ref primitive param.** (Now in architecture.md.)
- v1.0.13 `SuspendWorkWhileEngaged` (`QuestRunner.Update` poll → `RemoveQuest`/`AddQuest` her work quest):
  **mechanically worked but didn't fix it.** Log showed `Suspended work quest 'TerraformingQuest'` → active
  became `FleeCombatQuest` → `Entered MELEE` — **yet she still ran away** ("in combat" status, running). Also
  a bug: `Restored … enemy gone` fired **mid-fight** (8s engagement window expired during sustained melee).

**THE v1.0.13 REFRAME (key insight):** with her job removed, the run-away **can't** be job-pathing — so the
running **is the flee-combat _behaviour_ itself**, not the job. A villager has TWO combat FSM behaviours on
`VillagerSurvival`: **`fleeCombatBehaviour`** (kite/run) and **`naturalCombatBehaviour`** (stand & fight).
The `FleeCombatQuest` runs the FLEE one. Every prior lever tried to de-fang the flee quest (suppress spook,
block flee flag/run action, force ShouldFight, boost priority, remove job) — but the game's actual
stand-and-fight path is a **different behaviour** the idle villager uses. We were fighting the wrong FSM.

**v1.0.14 — swap the combat behaviour (`UseNaturalCombatBehaviour`, default on):** postfix
`CombatQuest.GetFSMBehavior(QuestData)` — the method that returns which `vFSMBehaviour` the combat FSM runs —
and for a whitelisted enemy return `VillagerSurvival.naturalCombatBehaviour` instead of the flee one. Routes
her into the stand-and-fight FSM directly. `FleeCombatQuest` does **not** override `GetFSMBehavior`, so
patching `CombatQuest`'s covers it; safe sig (QuestData in, vFSMBehaviour out — both reference types). This
patch **also refreshes the engagement window** (it runs throughout the fight), fixing v1.0.13's premature
restore. `SuspendWorkWhileEngaged` is **left on** too, so both axes are covered (no flee-run AND no job-pull).

**Priority-boost levers are EXHAUSTED — both failed in-game:**
- `BoostCombatPriority` (postfix `CombatQuest.GetPriority` → 41): **fired** (`GetPriority boost FIRED` logs)
  but combat still lost to work (15). Confirmed near-no-op.
- `BoostTriggerPriority` (v1.0.11, postfix `FleeCombatQuest.get_TriggerPriority` → 40): the boost log
  **never appeared** in the v1.0.11 run → either the getter isn't called during arbitration (likely
  **cached** — `CombatQuest.SetDirty()` exists) or the per-quest scoping missed. Either way, no effect.
  (v1.0.12 keeps this patch but adds an *unconditional* one-shot `get_TriggerPriority INVOKED` log to settle
  which.) **So the runner does NOT re-read either priority on the path that reclaims her for work.**

**v1.0.12 — patch the arbiter directly (`ForceCombatQuest`, default on):** postfix
`QuestRunner.FindNextBestQuest(out topPriority, excludeActiveQuest)` — the per-villager method that returns
the quest the runner makes active. While this villager has a live whitelisted engagement, override the
return to **her** flee-combat quest (registered per-runner from `_GetSpooked`/`ShouldFight`, which carry
`FleeCombatQuestData.QuestRunner`/`.Quest`); when combat is already active and the runner asks for the best
*other* quest, return `null` ("nothing better") so she stays. This doesn't depend on which priority field
the arbiter reads. Self-reverts ~8s after the enemy is gone. Also emits throttled `[arb]` lines showing the
live arbitration (what the runner wanted vs. what we forced).

> Pointer gotcha encountered & solved: `QuestRunner` is on the UnityEngine.Object chain and the csproj
> references the **stripped** `unity-libs\UnityEngine.CoreModule`, so the compiler can't see
> `QuestRunner → Il2CppObjectBase` (no `.Pointer`, won't cast). `Plugin.PtrOf(object)` casts through
> `object` to `Il2CppObjectBase` at runtime. (Il2CppSystem.Object-derived types like `QuestData`/`Quest`
> expose `.Pointer` fine — only the Unity-chain types need this.)

**Precise current behavior (before the v1.0.11 fix — what we're trying to fix):**
- A villager **idle / not en route to a job**: fights and **kills Wisps fine.** ✅ (this already worked in vanilla)
- A villager **on the way to a job**: enters combat for a moment (crossed swords appear, `FSM_MeleeCombat`
  is reached) but is **pulled back to her job marker and runs off**; she only commits to the fight
  **once the job state resets** (she stops trying to reach the marker). ❌ ← **v1.0.11 targets this.**

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
1. **FIRST: fix the v1.0.14 startup CRASH (see the 🛑 banner at top).** v1.0.14 hard-crashes ASKA on launch
   (native AV in `coreclr.dll`), so **no behavior test is possible until the crash is fixed.** Bisect the
   detours per the banner — comment out `[HarmonyPatch]` groups in `Patches/FleeCombatPatch.cs`, rebuild (bump
   version), launch — starting with `CombatBehaviourSwapPatch` and `QuestRunnerUpdatePatch`. Once it launches
   clean, THEN run the behavior test: job scenario (villager walking to a job, Wisp attacks). **Win
   condition:** she **stands and fights** the Wisp instead of running — no kiting/retreating — then resumes
   work when it's dead.
2. **Read these log lines:**
   - `Swapped combat FSM behaviour flee -> natural (stand & fight) vs 'Wisp'` (one-shot) — **the key signal.**
     Confirms `GetFSMBehavior` was patched and we redirected her to `naturalCombatBehaviour`. If it appears
     and she **stands and fights → SUCCESS.**
   - `Suspended work quest …` / `Restored … enemy gone` — the restore should now fire **only after the Wisp
     is actually dead/gone** (the swap patch refreshes the window during the fight). If it still fires
     mid-combat, the window refresh isn't keeping up — widen it / refresh from another in-combat call.
   - Sanity-check a **draugr/wolf**: must still **flee** (swap only triggers for whitelisted targets — a
     non-whitelisted enemy keeps `fleeCombatBehaviour`).
3. **If the swap log appears but she STILL runs:** `naturalCombatBehaviour` may itself include repositioning,
   or `GetFSMBehavior`'s result is cached after the first call (like `TriggerPriority` was). Check whether the
   one-shot fires once vs repeatedly (temporarily make it throttled, not one-shot). If cached, force a
   re-fetch: call `__instance.SetDirty()` or `cqd.Quest.SetDirty()` from the swap so the behaviour is re-read.
4. **If the swap doesn't take at all → confer real warrior status** (the game's own stand-and-fight path):
   `VillagerSurvival._warriorCombatQuest` (+ `_warriorQuestAdded`, `_warriorBuffAdded`, `_CheckWarriorStatus()`).
   `_warriorCombatQuest` may be null for a non-warrior → may need to create/register a `WarriorCombatQuest`
   (note it carries the `warriorStatusEffect` buff + gear assumptions). `WarriorCombatQuest : CombatQuest`,
   `TriggerPriority` ~ `c_SelfDefense`(42).
5. **If it works:** prune dead levers — `BoostCombatPriority`, `BoostTriggerPriority`, `TreatAsWarrior`,
   `PreventRunMovement` (and the leftover `get_TriggerPriority INVOKED` diag). Keep: `UseNaturalCombatBehaviour`
   (the fix), probably `SuspendWorkWhileEngaged`, `ForceEngage`, `SuppressSpook`/`PreventFlee`, `KeepCombatAlive`.
   Flip `DebugLogging` default to `false`, tune `FightBackAgainst`, ship.

### v1.0.13 run log (reference — the run that produced the reframe)
```
get_TriggerPriority INVOKED: native=40 remembered=null   ← getter IS reachable (but null at startup)
Whitelisted spook … behavior=Work activeQuest=TerraformingQuest activePrio=15.0
Suspended work quest 'SSSGame.AI.TerraformingQuest' …    ← RemoveQuest worked
[diag] ShouldFight … activeQuest=FleeCombatQuest activePrio=40.0   ← combat now active
GetPriority boost FIRED … 40.0 -> 41.0
Blocked flee state … / Entered MELEE state vs 'Wisp'     ← reached melee…
Restored 1 suspended work quest(s) — enemy gone.         ← BUG: fired mid-fight (window expired)
Treating villager as warrior … / Entered MELEE …
```
User: "visually 'in combat' status but ran away." → running is the flee BEHAVIOUR, not the job. Hence v1.0.14.

## ⚠️ IL2CPP patch-safety rule learned this session
**Never Harmony-patch an IL2CPP method with a by-ref primitive parameter** (`Single&`, `Int32&`, `Boolean&`
as out, etc.). The trampoline NREs on every invocation and bricks whatever calls it. `QuestRunner.FindNextBestQuest(Single& topPriority, …)`
did exactly this (v1.0.12) → every villager inert. Patch a sibling method with a safe signature instead
(here: `QuestRunner.Update()`, void/no-args) and call the game's own `Void M(Quest)` methods. Added to
`docs/architecture.md` IL2CPP gotchas.

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
