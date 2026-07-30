# CraftFromStorageMod — per-unit gating for workshop add-ons

Open plan. The confirmed behaviour, the `AnvilInteraction` discriminator and the run results all live
in [`docs/mods/craft-from-storage.md`](../docs/mods/craft-from-storage.md); this file holds only the
work still to do.

## The model this must produce (user, 2026-07-30)

| Level | Who | Where |
|---|---|---|
| Default | player | crafting tables |
| Toggle 1 | villagers too | crafting tables, incl. the **armorsmith** |
| Toggle 2 (needs toggle 1) | villagers | workshop **add-on units**, **bloomeries**, **coal makers** |

Cooking and cheesemaking are deliberately excluded (user, 2026-07-30) — never a problem in play, and
they have their own quest family, so leaving them out costs nothing and adds no special case.

**Toggle 2 is two separate builds.** The workshop add-ons already run through the crafting-supplies
fetch the mod hooks, so they need only the per-unit change below. Bloomeries and coal makers do NOT:
they have their own supply quests and state machines — `FSM_FetchBloomerySupplies`,
`FSM_FetchKilnSupplies`, `SSSGame.CoalmakerSupplyQuest` (Cecil 2026-07-30) — which the mod does not
hook at all, so they need new hooks and their own test. That half is unstarted.

The standalone `SSSGame.ForgeInteraction` is almost certainly the bloomery's own hearth rather than a
loose forge building: `FSM_UseBloomeryAnvil` and `FSM_UseBloomeryBellows` both exist, so a bloomery
carries its own anvil and bellows.

**Do not touch the logic that decides what gets made when.** That is SupplyChainMod's job. Reporting a
recipe craftable when storage covers it IS this mod's mechanism, so it cannot be narrowed to solve a
downstream problem. Two fixes already rejected under this rule: excluding recipes whose missing
ingredient exists in storage in a more finished form, and excluding recipes such as Wood Shaft by name.

## The gap

The gate works per BUILDING. A workshop containing any add-on unit is skipped whole when toggle 2 is
off, so a villager at that same workshop's ordinary table gets no help either. That breaks toggle 1 for
every workshop with an add-on attached, and it is the only thing standing between the current build and
the model above.

Confirmed in-game 2026-07-30: one station object owns four surfaces, none of them on the station
itself — `all=[CraftInteraction@descendant; AnvilInteraction@descendant; CarpenterInteraction@descendant;
CarpenterInteraction@descendant]`. The mod picks one of those four and applies the answer to the whole
building.

## The fix — let the RECIPE decide, and keep the station test only as a fallback

The recipe gate the mod already has is per-unit by nature: a forging recipe means the anvil, an
ordinary craft recipe means the table. It was doing the right thing before the station gate was added
in front of it. The station gate is the blunt one, and it currently runs FIRST and overrides a
perfectly resolvable recipe answer.

So the fix is an ordering change plus a fallback, not a new resolution mechanism:

1. Resolve the recipe family first. When it resolves to a transform family, consult toggle 2. When it
   resolves to anything else, proceed under toggle 1 regardless of what else the building contains.
2. Only when the family cannot be resolved, fall back to the station test as it behaves today.
3. Log which of the two routes decided, so a run shows how often the fallback is reached.

This uses only mechanisms already confirmed working in-game, and it needs no new hierarchy search.
The fallback still answers per building, so an unresolvable recipe inside a workshop holding an add-on
is still treated bluntly — acceptable, because it is strictly narrower than today.

**Why the fallback matters.** 170 of 336 stock attempts in the 2026-07-29 run could not resolve a
recipe family, which is exactly why the station test was added. Removing it outright would reopen the
carpenter stall for those attempts.

**Available if the fallback proves too coarse**, not needed for this change:
`SSSGame.CraftingQuest/CraftingQuestData` carries `CraftInteraction _ci` naming the exact unit a job
targets, and `CraftingStation` keeps `_craftingTables`, `_anvils` and `_studyInteractions` as separate
typed lists so membership in `_anvils` is a positive test for an add-on. Prefer that narrow positive
test over inferring "table" from `_craftingTables`, since a base-typed list may hold derived entries.

## Naming to land with this change

Rename `Transfer/StockTransformStationMaterials` to match the model: it is toggle 2, "villagers may also
craft at workshop add-on units". Its current description frames it as the mod acting somewhere it was
told to leave alone, which reads like an escape hatch rather than a feature. Reword accordingly, and
state the toggle-1 dependency that the stocker already enforces.

## What must not change

The two availability paths must keep standing aside at transform units unconditionally, and must never
consult toggle 2. Telling the game a transform recipe is already satisfied is what stalled villagers
entirely, and the asymmetry is what keeps vanilla in charge of when a villager fetches.

## Verification

One in-game run with toggle 2 OFF and a villager crafting at the ordinary table inside the workshop that
holds the sawhorses and anvil. Success is that villager being served from storage while the add-on units
next to her are not. The `stationProbe` line should name the single unit the job targets rather than
listing the whole building.

## Risks

- **A base-typed list may hold derived entries.** `_craftingTables` is typed `List<CraftInteraction>`,
  and every add-on type derives from `CraftInteraction`, so it may contain add-ons too. Test membership
  in `_anvils` positively; never infer "table" from presence in `_craftingTables`.
- **The job may not always name a unit.** Half of all stock attempts in the 2026-07-28 run could not
  resolve a recipe family from the quest, so the same chain may be incomplete here. The fallbacks exist
  for that case and their use must be logged, not silent.
