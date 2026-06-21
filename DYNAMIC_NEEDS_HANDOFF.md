# DynamicVillagerNeedsMod — Handoff (IN PROGRESS, linchpin UNVERIFIED)

**Status as of 2026-06-21:** Built and deployed, compiles clean, **not yet confirmed working in-game.**
The whole approach hinges on one unverified assumption (see "The linchpin" below). Do the in-game test
first; everything else flows from whether that assumption holds.

This is Mod 5. It is intentionally **not** written up as "COMPLETE" in `CLAUDE.md` yet — keep notes here
until the in-game test confirms behavior, then promote the confirmed facts into `CLAUDE.md`.

---

## The goal (the user's actual ask, refined over the conversation)

Replace ASKA's clock-based villager **schedule** (you manually assign Sleep/Work/Leisure hours per
villager) with **needs-based behavior**, so villagers self-manage instead of being micromanaged:

- **tired → sleep**
- **needs leisure → take leisure**
- **otherwise → work**

…plus configurable control over **how much sleep and leisure they actually need**. Overarching
principle: **"stop wasting time" without reducing happiness.**

---

## How we got here (so nobody re-walks the dead ends)

1. Original framing was "villagers sleep ~50% of playtime; make them sleep half as much without
   happiness loss." First mod (`VillagerSleepMod`, now **deleted**) scaled the rest-drain /
   rest-recovery rates via a measure-and-rescale poller.
2. **That approach was abandoned.** Runtime logging proved **sleep is schedule-bound, not
   tiredness-bound**: villagers reach `rest = 24.00/24.0` (100%) and then keep `sleeping = True` for
   ~4+ minutes of real time before waking. They sleep the **whole scheduled night** regardless of how
   rested they are. So scaling drain/recovery cannot shorten the dominant nightly sleep. Don't retry it.
3. Good news from that run: **happiness was rock-stable / rising (0.78 → 0.82)** across the session, so
   the rest system itself isn't a happiness risk.
4. The user then clarified the real goal (above): they dislike the schedule system and want needs-based
   behavior. → pivoted to **`DynamicVillagerNeedsMod`**.

---

## Current approach: `DynamicVillagerNeedsMod`

GUID `com.askamods.dynamicneeds`. Files:
- `Plugin.cs` — config binds + registers the controller MonoBehaviour + Harmony patch.
- `Patches/VillagerSurvivalPatch.cs` — Postfix on `VillagerSurvival.Spawned()` registers each villager
  into `Plugin.TrackedSurvivals`.
- `NeedsController.cs` — the poller (`Update()`), same pattern as HealthRegenMod/TorchFuelMod.

**Per tick, for every villager we own (host/authority only):**
1. Read `rest = VillagerSurvival._restVariableAttribute.GetValue()` (0..24) and
   `happiness = Villager.NormalizedHappiness` (0..1).
2. Pick a `Mode` with hysteresis (`NeedsController.Decide`):
   - `prev==Sleep` and `rest < WakeWhenRestAboveHours` → **Sleep** (keep sleeping until rested)
   - else `rest <= SleepWhenRestBelowHours` → **Sleep**
   - else (not sleeping) leisure enabled & `happiness <= LeisureWhenHappinessBelow` (with `…Above`
     hysteresis) → **Leisure**
   - else → **Work**
3. Apply via `NeedsController.Apply`:
   ```
   villager.overrideSchedule = true;
   villager.scheduleOverride  = <ScheduleType>;
   if (villager.CurrentBehaviorType != st) villager.CurrentBehaviorType = st; // force immediate
   ```

**Config section `[DynamicNeeds]`** (file: `BepInEx/config/com.askamods.dynamicneeds.cfg`):
| Key | Default | Meaning |
|---|---|---|
| `Enabled` | `true` | Master switch. |
| `SleepWhenRestBelowHours` | `8.0` | Sleep when rest (0..24) drops below this. Lower = less sleep. |
| `WakeWhenRestAboveHours` | `23.0` | While sleeping, wake once rest reaches this. Lower = shorter sleeps. |
| `LeisureWhenHappinessBelow` | `0.6` | Take leisure when happiness < this. **0 disables leisure.** Self-correcting net. |
| `LeisureUntilHappinessAbove` | `0.78` | Leave leisure once happiness recovers past this. |
| `DebugLogging` | `true` | Per-villager mode-change logs + 5s summary for villager[0]. |

The leisure rule is the **happiness safety net**: there is no separate "leisure need" stat in the game,
so leisure is derived from happiness. If working more / sleeping less ever lowers happiness, villagers
auto-take leisure until it recovers — happiness becomes a feedback loop instead of a hope.

---

## ⚠️ The linchpin (verify this FIRST)

**Unverified assumption:** that setting `Villager.overrideSchedule = true` + `scheduleOverride` +
`CurrentBehaviorType` actually redirects the villager's FSM behavior (i.e. the FSM's `FSM_CheckSchedule`
reads `CurrentBehaviorType`, and `overrideSchedule` stops the clock from overwriting it).

The interop DLLs are native trampolines into `GameAssembly.dll` (Unity 6 IL2CPP, **metadata v39** —
not dumpable by Il2CppDumper), so this could only be confirmed by running. Everything **compiles**, which
proves the members exist with those signatures — it does **not** prove they drive behavior.

**Test (as HOST — villager sim is host-authoritative):** load a save with several villagers, run a day or
two with `DebugLogging=true`, then read `BepInEx/LogOutput.log` for `[DynamicNeeds]` lines, and watch:
1. **Do villagers stop sleeping the whole night?** With defaults they should sleep only when actually
   tired (`rest<=8`), wake at `rest>=23`, and work the rest of the time — including at night.
2. **Does happiness hold?** Watch `happiness=` in summaries and any `Work -> Leisure` transitions (the
   safety net firing).
3. **Anyone stuck idle?** (forced to Work with no job/workstation.)

### If the linchpin FAILS (override doesn't redirect behavior)
Fallback: end the sleep **interaction** directly to force a wake, instead of (or in addition to) the
schedule override. Confirmed available API:
- `Interaction.Finish(IInteractionAgent agent, InteractionSessionState reason)` and
  `Interaction.FinishAllSessions(InteractionSessionState reason)`
- `VillagerRestInteractionConfig/VillagerRestInteractionSession.End(InteractionSessionState lastResult)`
- The sleeping villager's session is reachable via the sleep FSM (`FSM_GoToBedAndSleep/SleepData.session`),
  or by patching `RestInteraction.Use` to capture the session per agent.
Also consider: does `CurrentBehaviorType`'s setter fire `OnBehaviorTypeChanged`? If not, `VillagerHappiness`
/ FSM listeners may not react to our forced changes — may need to invoke whatever the game uses to raise
that event, or set the backing field `_CurrentBehaviorType_k__BackingField` + manually trigger refresh.

---

## Game mechanics reference (what was reverse-engineered)

### Rest / sleep (CONFIRMED at runtime)
- `VillagerSurvival._restVariableAttribute` : `VariableAttribute`, range **0..24** ("hours of rest"),
  drains awake / fills asleep. `.GetValue()`, `.SetValue()`, `.min`, `.max`.
- `VillagerSurvival.IsSleeping` (bool), `.RestRatio` (0..1), `.GetVillager()`, `._hasAuthority` (bool,
  inherited from `CharacterSurvival`; use for authority gating).
- `VillagerDataSheet : CharacterDataSheet` — `fatigueRate` (was **1** at runtime), `restHoursSafe`=**4**,
  `restHoursLow`=**2**, `restHoursCritical`=**0**. (These are the vanilla sleepiness thresholds; the mod
  does not currently touch them.)
- Sleep is **schedule-driven** (villagers sleep the scheduled night even at 100% rest) — the central
  finding that killed the drain/recovery approach.

### Schedule (API surface CONFIRMED present; behavior effect UNVERIFIED — the linchpin)
- `Villager.overrideSchedule` (bool), `Villager.scheduleOverride` (`ScheduleType`).
- `Villager.CurrentBehaviorType` (`ScheduleType`, get+set; backing `_CurrentBehaviorType_k__BackingField`)
  — the current scheduled activity the FSM acts on. `OnBehaviorTypeChanged` (Action<ScheduleType>).
- `Villager.Schedule` (Il2CppStructArray, per-hour), `scheduleMaxHourCount`, `_bitsPerScheduleType`,
  `Rpc_ChangeSchedule(Int64)`, `_ScheduleToNetworkSchedule` / `_NetworkScheduleToSchedule`,
  `GetScheduleModified(int hour)`, `ResetScheduleToDefault()`. (Persistent path — **not used**; we chose
  the runtime override to avoid baking changes into the save / compounding across reloads.)
- `enum SSSGame.UI.ScheduleType { Sleep, Work, Leisure }` (underlying int order unknown — use the enum
  values directly in code, never hardcode ints).
- FSM decision nodes (ScriptableObjects, can't dump bodies): `FSM_CheckSchedule(scheduleType)`,
  `FSM_CheckVillagerSleepiness(Safe/Low/Critical)`, `FSM_CheckVillagerIsSleeping`, `FSM_GoToBedAndSleep`,
  `FSM_SetIsSleeping`.

### Happiness (API surface; relative rates NOT yet measured — see open questions)
- `Villager.NormalizedHappiness` (0..1), `.GetHappinessManager()` → `VillagerHappiness`.
- `VillagerHappiness`: `SleepRate`, `WorkRate`, `LeisureRate` (Single, per-activity happiness rates);
  `GetCurrentHappinessRate(bool)`, `GetHappinessGainPerHour(ScheduleType)`,
  `GetHappinessGainPerDay(ScheduleType, out int matchingHours)`, `_CalculateWorkRateMultiplier()`,
  `workRateTierMultiplier`, `workAnxietyEffect`, `sleep/work/leisureStatusEffect` (StructureStatusEffect).
- **`GetHappinessSummary(bool addDetails)` and `GetOverallHappiness(bool addDetails)` return human-readable
  breakdown strings** (the villager happiness-tab text). Logging these is the fastest way to learn what
  actually drives happiness — do this next if we need to understand/tune the happiness side.
- No "leisure need" VAttr exists; leisure is purely a schedule activity that grants `LeisureRate` happiness.

### Status-effect / attribute system (context)
- `SandSailorStudio.Attributes`: `VariableAttribute` (modifiers + `Tick`), `AttributeManager`
  (ticks VAttrs at `tickInterval`), `CustomOperationVariableAtributeModifier`, `Attribute`, `AttributeConfig`.
- `StatusEffect.table : StatusEffectModifierTable` with `vattrElements[] {modifier: VariableAttributeModifier
  (.value), targetAttribute: AttributeConfig (.attributeId)}` — how effects modify attributes.

---

## Build / deploy / tooling

- Build: `dotnet build "DynamicVillagerNeedsMod/DynamicVillagerNeedsMod.csproj" -c Debug`. The
  `CopyToPlugins` target auto-copies the DLL to `ASKA/BepInEx/plugins/DynamicVillagerNeedsMod/` and to
  the project folder. Targets `net6.0`; references the standard BepInEx core + interop set.
- ASKA install: `D:\SteamLibrary\steamapps\common\ASKA` (paths in the `.csproj` are absolute — adjust on
  the other machine if the install path differs).
- `_explore/` Mono.Cecil inspector tooling (run with PowerShell):
  - `search.ps1 -TypeKeywords @(...) [-MemberKeywords @(...) -Members]` — find types / members by keyword.
  - `dump.ps1 -Types @("Full.Type.Name", ...)` — dump fields/properties/methods of types.
  - `il.ps1 -Type X -Methods @(...)` — dumps IL, but interop methods are just native trampolines
    (won't show real game logic). Useful only to confirm signatures.
  - These read the interop DLLs under `ASKA\BepInEx\interop|core|unity-libs`.

---

## Open questions / next steps
1. **Verify the linchpin** (above). This gates everything.
2. If working, **tune the four thresholds** for "sleep ~half as much" feel and confirm happiness holds.
3. **Measure happiness rates**: log `WorkRate` / `SleepRate` / `LeisureRate` and
   `GetHappinessSummary(true)` for one villager to understand whether Work-vs-Sleep time helps or hurts,
   and whether a happiness compensation lever is even needed.
4. Decide whether to also expose the vanilla sleepiness thresholds (`restHoursSafe/Low/Critical`) or
   `fatigueRate` as config, if finer control is wanted.
5. Once confirmed: promote the verified facts into `CLAUDE.md` as the Mod 5 section, and mark this
   handoff superseded.
