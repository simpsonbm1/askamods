# CraftFromStorageMod — per-unit gating for workshop add-ons

Open plan. The confirmed behaviour, the `AnvilInteraction` discriminator and the run results all live
in [`docs/mods/craft-from-storage.md`](../docs/mods/craft-from-storage.md); this file holds only the
work still to do.

## The model this must produce (user, 2026-07-30)

| Level | Who | Where |
|---|---|---|
| Default | player | workshop tables |
| Toggle 1 | villagers too | workshop tables |
| Toggle 2 (needs toggle 1) | villagers | workshop **add-on units** — the physical transforms |

A possible third layer covering bloomeries is undecided and out of scope here.

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

## The fix — read the unit off the crafting job

`SSSGame.CraftingQuest/CraftingQuestData` carries `CraftInteraction _ci`, naming the exact unit a job
will use (Cecil 2026-07-30). The stocker already walks the quest chain to resolve a recipe family, so
this is a field read on a path it already follows, and it replaces the hierarchy search that caused the
v0.10.0 and v0.10.1 defects.

Gate on THAT unit rather than on the building. When it is a transform unit, consult toggle 2. When it is
an ordinary table, proceed under toggle 1 regardless of what else the building contains.

**Fallback when the job does not name a unit.** Ask the workshop instead: `CraftingStation` keeps
`_craftingTables` (`List<CraftInteraction>`), `_anvils` (`List<AnvilInteraction>`) and
`_studyInteractions` (`List<StudyInteraction>`) as separate typed lists, so membership in `_anvils` is a
positive test for an add-on. `CraftInteraction.craftStationHost` navigates back the other way. Prefer
the narrow positive test over inferring from `_craftingTables`, since a base-typed list may hold derived
entries.

Keep the existing hierarchy walk only as a last resort, and log which of the three routes answered so a
future run can show whether the fallbacks are ever reached.

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
