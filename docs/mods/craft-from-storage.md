# CraftFromStorageMod (Mod 28) — craft from settlement storage

**GOAL:** crafting at a station draws on materials sitting in ANY non-blacklisted settlement
storage — counted as available AND actually consumed — instead of requiring them to already be in
the station's bin or the crafter's inventory. Applies to the **player** AND to **villager
crafters**, one independent config toggle each. For villagers this means **deleting the supply
walk**: the villager crafts immediately rather than hauling materials to her station first.
(This line is quoted into `SESSION_HANDOFF.md`'s `## GOAL GROUNDING` section — see CLAUDE.md.)

**Status: COMPLETE v1.2.0, host/solo only, published on Nexus.** Phase 1 and Phase 2 confirmed
in-game 2026-07-31. The v1.1.0 source-node allow-list confirmed in-game 2026-08-11 on smolkr
horns; the station-qualified form added in v1.2.0 loads clean but its admission path is
⚠️ pending in-game.
Origin: Nexus request from rondi112 (2026-07-20). Plan entry: NEW_MOD_IDEAS_PLAN.md
→ idea 17. Subsystem facts: docs/architecture.md → "Player crafting pipeline".

## What it does (Phase 1)
Crafting at a station pulls missing ingredients from any non-blacklisted settlement storage into
the player's inventory just in time, then returns anything unconsumed. The crafting menu's
per-ingredient `have/need` counts show settlement-wide totals so the numbers agree with the
now-enabled Craft button.

## How it works
Vanilla already consumes from `{agent inventory} ∪ {station composite}`, choosing by where the
items physically are. **The mod is a reach extension, not a new mechanism** — it puts the materials
somewhere vanilla already looks, then lets vanilla consume normally.

1. **Availability postfix** on `CheckOwnedRequirements`, gated to `PlayerInteractionAgent`, flips
   the gate true when a cached settlement snapshot covers the shortfall.
2. **Pull** on the `BeginCraftingSequence` prefix — consumption lands ~1.0 s later, so there is
   ample headroom. Records a ledger of (source container by world position, ItemInfo, qty moved).
3. **Fail-closed verify**: re-runs vanilla's own gate behind a reentrancy flag (without it, the
   mod's own postfix answers "yes" using storage not yet moved, so the check could never fail).
4. **Abort** if not satisfied: sweep the whole ledger back, cancel the craft, show an on-screen
   message. Letting a partially-supplied craft proceed would hand out a free or half-price item —
   `RemoveOwnedItemManifest` consumes only what is present.
5. **Sweep-back** on the `_OnCraftingSuccess` postfix:
   `sweepBackQty = clamp(packQtyAfterCraft − packQtyBeforePull, 0, ledgerQty)`. This returns the
   pack to its pre-pull level and never claws back the player's own stock. Worked example: player
   had 3, mod pulled 4 (pack 7), craft ate 1 (pack 6) → sweep 3, player keeps their original 3.
6. **Requirement UI** — a `CraftUiPoller` MonoBehaviour (0.2 s tick) rewrites each row's
   `have` number to `vanillaHave + max(0, settlementQty − activeStationQty)`.

## Config
### [1. Features]
| Key | Default | Notes |
|---|---|---|
| `EnablePlayerPull` | `true` | Master switch for the whole Phase 1 behavior |
| `EnableForVillagers` | `false` | Villager crafters draw on settlement storage (fresh installs player-only) |
| `StockTransformStationMaterials` | `false` | Extend stocking to workshop add-on units (physical transforms) |
| `ShowSettlementStockInUI` | `true` | Master switch for the requirement-count rewrite |

### [2. Tuning]
| Key | Default | Notes |
|---|---|---|
| `SweepBackLeftovers` | `true` | Return unconsumed pulled items to their sources |
| `SnapshotTtlSeconds` | `5.0` | Settlement stock snapshot cache lifetime |
| `StockStationOnFetch` | `true` | Stock stations on villager fetch-start (Phase 2c) |
| `UiPollSeconds` | `0.2` | UI poller tick; also observed update latency |
| `BlacklistContainerTypes` | see below | Never-drain source containers, by type name |
| `SourceNodeAllowlist` | see below | Per-node override re-admitting output bins on a blacklisted type |
| `SkipBlueprintClasses` | see below | Never-stock auxiliary-station recipes, by class |
| `SkipStationClasses` | see below | Station classes never receive deliveries |

### [3. Diagnostics]
| Key | Default | Notes |
|---|---|---|
| `TransferDiagnostics` | `false` | Per-pull / per-sweep logging, incl. zero-move stock lines |
| `UiDiagnostics` | `false` | Requirement-UI scoping and rewrite logging |
| `EnableBloomeryTrace` | `false` | Bloomery/kiln fetch trace + slot/manifest dumps |
| `EnableVillagerFetchTrace` | `false` | Villager fetch/craft cycle tracing (`[CFS-P2]`) |
| `TraceStorageWhitelist` | `false` | Villager fetch storage-whitelist probe logging |
| `MaxWhitelistLogsPerCycle` | `40` | Distinct-site log cap per villager fetch cycle |
| `EnableCraftWatcher` | `false` | Ingredient-collection delta watcher (arms per craft) |
| `WatchWindowSeconds` | `20` | Watcher sampling window after craft start |
| `PollIntervalSeconds` | `0.1` | Watcher sampling cadence while armed |
| `TraceCheckOwnedRequirements` | `false` | Availability-gate trace + rollup summary line |
| `TraceBeginCraftingSequence` | `false` | Per-craft `BeginCraftingSequence` log line |
| `TraceOnCraftingSuccess` | `false` | Station-inventory snapshots around craft success |
| `TraceActivateBlueprint` | `false` | Blueprint-activation log line |
| `TraceRecipeListUI` | `false` | Recipe-list-open blueprint counts |
| `VerboseGateLogging` | `false` | Per-call gate logging on top of the rollup |
| `CensusHotkey` | `F12` | Read-only settlement storage census dump |
| `IncludeEquipmentProbe` | `true` | Scan equipment containers in census |
| `IncludeWorkstationStock` | `true` | Include station bins in census |

### Blacklist defaults:
- **`2. Tuning/BlacklistContainerTypes`:** `CharacterFlask`, `CharacterBuilder`, `ArmorRack*`
  family, `Storage_Core`, `Storage_DecorationsTop`, `Storage_SmallItems_Outhouse`,
  `Storage_MediumItems_L1` (medium materials bin at workshop tables and co-located stations,
  confirmed 2026-07-30), `Storage_SmallItems_L1` (small items bin, capacity 20, used by
  workshops AND farms — census-confirmed 2026-07-31; farms' small bins are therefore also
  excluded as pull sources), `Storage_SmallItems_Kiln` (kiln loading container; blacklisted so
  one bloomery cannot drain another's loaded kiln — soak-confirmed effective 2026-07-31). All
  entries including the kiln one are in the Plugin.cs bind default as of v1.0.0. The armor-rack
  entries are belt-and-braces only — user-confirmed 2026-07-20 that armor racks hold finished
  products and no ASKA recipe consumes finished gear as an input, so the racks are not a real
  drain risk.
- **`2. Tuning/SourceNodeAllowlist`** (v1.1.0, station-qualified form v1.2.0): container NODE
  names (`ItemContainerComponent.gameObject.name`) admitted as pull sources even when their
  container TYPE is blacklisted. An entry is either a bare node name, admitted on any building,
  or `Node@StationClass`, admitted only when the owning structure's `Workstation` reports that
  native class. The 23-entry default covers the output bins of hunter huts, fishing houses,
  woodcutters, gatherers, stonecutters, mines, farms, animal pens, charcoal makers and
  bloomeries: `StorageHorns`, `HideStorage`, `Fish`, `Bark`, `Firewood`, `Sticks`, `Thatch`,
  `FiberResin`, `FruitsStorage`, `MaterialsStorage`, `VegetablesStorage`, `StoneSmall`, `Ore`,
  `CrawlerResources`, `FoodStorage`, `FibersStorage`, `SeedsStorage`, `SticksStorage`,
  `SmolkrHornContainerArea`, `CoalStorageInteraction`, `coalPileContainer`, `StorageBloom`,
  `Scraps@HuntingStation`. Deliberately omitted are the bloomery's `StorageOre` and
  `StorageCoal` and the fishing hut's `Bait`, which are inputs a neighbouring station must not
  steal. An empty value restores type-only exclusion. Node names come from the prefab, so a new
  building type is a config edit and never a rebuild. Not yet covered because no census has shown
  their node names: forestry huts, and plain (non-cave) miner huts.
- **`2. Tuning/SkipBlueprintClasses`:** `ForgingBlueprintInfo,ForgingBlueprint,DyeingBlueprintInfo,PaintingBlueprintInfo,KnowledgeBlueprintInfo`.
  These are the auxiliary-station families (forge, dye, paint, study) that craft at specialty
  stations rather than from a crafting table's own bin. Matched exactly and case-insensitively;
  listing a subclass can never catch its parent. User chose this five-entry default on
  2026-07-28.

**Configuration gotcha:** Changing a bind's default does NOT rewrite an existing `.cfg` file.
A deployed machine needs its config edited by hand for a new entry to take effect.

### Removed settings (v1.0.0)
Three settings were removed from the config surface and hardcoded off to prevent game
breakage (per user ruling 2026-07-31: config options whose own description warns they
can break the game must not exist):
- **`CensusTryQuerySettlementResources`** — the underlying call hangs the game
  (`AppHangB1`). Settlement storage is now read via `GetStructures()` walk.
- **`SuppressFetchQuestPriority` / `FetchQuestSuppressedPriority`** — enabling
  suppression stalls villager crafting entirely (confirmed in-game 2026-07-27). The
  patch code remains in the tree but is never attached.
- **`TraceCheckOwnedBlueprintManifest`** — a crash-risk trace patch that was never
  attached live. Code remains; logging never enabled.

Re-enabling any of these requires a code change; they are NOT configurable. This blocks
accidental game-breaking tuning at the cost of a manual code edit for deep investigation.

## Phase 2 — the villager half (in progress)
Villager crafts run the **same pipeline** as the player's (confirmed in-game 2026-07-21):
`BeginCraftingSequence` fires with session `VillagerCraftSession` / agent `Villager`, and
`_OnCraftingSuccess` consumes ~865 ms later from the **station** collections while the crafted
output goes to the villager. v0.9.0 stocks the station at fetch-start rather than suppressing the
quest: let the fetch quest be chosen as vanilla intends, then intercept it the moment it starts
and teleport the materials into the crafting station, so the walk becomes unnecessary through
vanilla's own scheduling path.

**How v0.9.0 works:** a postfix on `FSM_FetchCraftingSupplies.OnStateEnter` in
`Patches/StationStockPatches.cs` immediately stocks the station. The resolution chain is:
`GetQuestData(fsmBehaviour)` gives a base-typed `QuestData`; verify native class is
`CrafterFetchQuestData` and rewrap by pointer; `.Quest` gives `CrafterFetchQuest`;
`.craftingStation` gives `CraftingStation`; `GetNeededSuppliesManifest()` is CALLED (never patched
— its `ItemManifest` return is the inventory-family patch-crash risk); `station.GetInventory()`
gives the destination `ItemCollection`. The shortfall is computed against the station inventory
only, then moved by the new `CraftTransfer.StockStation`, which deliberately writes NO ledger
entry — hauling those items to the station is exactly what the villager's own fetch walk would
have done, so they belong there whether or not the craft completes, and sweeping them back would
recreate the v0.8.0 stall. The success metric is a single unconditional log line per fetch:
`[CFS] [CFS-SS] STOCKED villager=<name> station=<name> wanted=<n> short=<n> itemsMoved=<n>
qtyMoved=<n> stillShort=<n>`. The villager does not walk the now-pointless route: cycle verdicts
run overwhelmingly `DIRECT` once the station is stocked (354 of 381 in the 2026-07-28 run).

**v0.9.1 diagnostics** (run in-game 2026-07-28): Two additions to
station-stocker logging. First, a shortfall log line in `CraftTransfer.StockStation`,
emitted when an item is still short after the candidate loop, gated on
`TransferDiagnostics`: `[CFS] [CFS-SS] StockStation SHORT: '<item>' need <n>, moved
<n>, still short <n> (villager=<name> station=<name>, settlementCandidates=<n>).`
Before v0.9.1, a shortfall logged nothing, so a failed stock attempt gave no clue
which item was missing. The `settlementCandidates` count is the metric: zero means
settlement storage holds none of that item; a count above zero with nothing moved
means the destination collection refused the items. Second, a `stationObj=` field
inserted directly after `station=` in the unconditional `STOCKED` summary line, with
`station.gameObject.name` value, because `GetName()` returns only the building name
and cannot separate a crafting table from its auxiliary stations.

**Scope ruling (user, 2026-07-27): the stocker must NEVER autofill auxiliary stations —
crafting tables only.** A transform's demand for its input material is standing rather than
bounded by a recipe, so an unbounded stocker could drain a settlement's raw stock of one
material into a single auxiliary station's bin. The user placed metalworkers in this class
explicitly (2026-07-28): a metalworker is a transform add-on to a workshop, and its inputs are
out of scope.

**The discriminator is the blueprint's own class** (Cecil-confirmed 2026-07-28). A fetch quest
reaches its recipe through an all-public chain: the `CrafterFetchQuest` the stocker already holds
has native class `CrafterSpecificFetchQuest`, which exposes `craftingProject` (`CraftingProject`)
→ `craftingQuest` (`CraftingQuest`) → `BlueprintInfo`. Recipe families are separate
`BlueprintInfo` subclasses, so the station kind is one native-class read:

```
SandSailorStudio.Inventory.BlueprintInfo
  └─ SSSGame.CraftBlueprintInfo            (P: SSSGame.CraftInteraction interaction)
       ├─ SSSGame.ForgingBlueprintInfo     ← metalworker / forge
       ├─ SSSGame.DyeingBlueprintInfo → SSSGame.PaintingBlueprintInfo
       └─ SSSGame.WorkshopBlueprintInfo → SSSGame.KnowledgeBlueprintInfo
```

`SSSGame.ForgingBlueprint : CraftBlueprint` mirrors it on the item side. Which class the ordinary
workshop crafting-table recipes report — `CraftBlueprintInfo` or `WorkshopBlueprintInfo` — is
⚠️ pending a run that logs it; excluding the wrong one would disable the working half.

**v0.9.0 first-run result (confirmed in-game 2026-07-27):** The station stocker worked
in its first in-game run. At `Workshop House 2`, 21 of 28 stock attempts moved items
(sample: `[CFS-SS] StockStation: -1 'Hardwood Long Stick' from Improved Warehouse 4 ->
station (villager=Barne station=Workshop House 2, still need 0).`). The user watched a
villager craft at the table with no supply walk. Across the run, 122
`_OnCraftingSuccess` events fired (zero in v0.8.0), and cycle verdicts split 315
DIRECT to 20 TOURED. Against that, `Workshop House 4` logged 280 consecutive stock
attempts all reading `wanted=1 short=1 itemsMoved=0 qtyMoved=0` with `stillShort` of
1 or 10 (villagers Emmeline, Majvi, Harald), and the user observed thrashing there.

**v0.9.1 run result (confirmed in-game 2026-07-28):** 476 stock attempts, 26 fully satisfied
(`stillShort=0`), 33 moving a nonzero quantity; 365 `_OnCraftingSuccess` events; cycle verdicts
354 `DIRECT` to 27 `TOURED`, and every `TOURED` cycle carries `modPulls=0` (the walk survives
only where the stocker moved nothing). No managed exceptions. The three shortfall causes,
separated by the new `settlementCandidates` count:
- **Settlement genuinely holds none** — 439 of the 457 `SHORT` lines are `'Heavy Pelt'`, all at
  `Workshop House 4`, all `settlementCandidates=0`, zero counter-examples. Heavy Pelt drops from
  bears, which the user's villagers cannot hunt (user, 2026-07-28), so the mod is a **bystander**
  to that loop: vanilla villagers retry an unfulfillable craft identically. Not a defect.
  `Iron Hammer Head` (6) and `Stone Blade` (2) are the same case.
- **Out of scope** — 8 `'Hot Iron Bloom'` lines at `Workshop House 2` with
  `settlementCandidates` of 2 or 4 and nothing moved. Hot Iron Bloom is a forge transform of
  Iron Bloom, so these are metalworker inputs the scope ruling above excludes.
- **Real defect** — `'Bark'` (need 45, moved 40 from one container, `settlementCandidates=7`)
  and `'Stick'` (need 12, moved 10, `settlementCandidates=16`) at `Workshop Hut 6`, in a
  settlement holding large quantities of both (user, 2026-07-28). The candidate loop tried the
  remaining 6 and 15 containers and every one returned zero.

`stationObj=` does NOT separate a crafting table from an auxiliary: it reports the prefab clone
name, and `Workshop House 2` and `Workshop House 4` both read `Workshop_L2(Clone)` while
`Workshop Hut 6` reads `Workshop_L1(Clone)`. It identifies workshop TIER only.

**v0.9.2 run result (confirmed in-game 2026-07-28):** The forge gate fires and its fail-open
path works. There were 12 `SKIP blueprintClass=ForgingBlueprintInfo` lines (6 at Workshop House
2, 6 at Workshop House 5), and zero `[CFS]` exceptions across 899 STOCKED events. The gate was
blind to roughly half of all fetch quests: 447 of the 899 STOCKED lines logged `bpClass=?` with
`notSpecific:CrafterFetchQuest` as the only failure reason. Plain `CrafterFetchQuest` carries no
`craftingProject` link, so the blueprint class cannot be resolved from the quest — this is exactly
what v0.9.3's station-based fallback chain addresses. Two distinct move failures showed up,
separable for the first time because v0.9.2 logs `removed=` and `added=` independently. The
first is a destination at capacity, seen five times as `'Bark' … removed=5 added=0 stationHad=40
stationNow=40 (villager=Alva station=Workshop Hut 6)` when vanilla's quest manifest needed 45
bark but the station bin capped at 40. The second is a destination refusing an add into an EMPTY
inventory, seen six times as `'Hot Iron Bloom' … removed=1 added=0 stationHad=0 stationNow=0`
with container type `Storage_HotItemsSmall`, where the mod was shuffling blooms between
metalworkers and back. Duplicate candidate entries are confirmed real: `CANDIDATES 'Bark' n=6`
lists `Improved Warehouse 3`, `Warehouse Extension`, and `Bark Storage` all at position
`(136.26, 47.98, 424.94)` with identical container type and `qty=51`. Candidate counts run
roughly 1.5 to 2 times the physical container count. Both ordinary crafting-table blueprint
classes appeared and neither is skip-listed: `CraftBlueprintInfo` from Alva at Workshop Hut 6,
and `WorkshopBlueprintInfo` from Siv at Workshop House 2. Workshop House 4's Heavy Pelt loop
reports `CookingRecipeInfo`.

**Phase 2 diagnostic instrument (v0.6.0, keep enabled while Phase 2 is open):** five read-only
postfixes on `FSM_FetchCraftingSupplies.OnStateEnter`/`.OnStateExit`,
`FSM_UseCraftingStation.OnStateEnter`, `FSM_ReturnCraftingSupplies.OnStateEnter` and
`CrafterFetchQuestData.IsWhitelistedByStorage`. Its `[CFS-P2] CYCLE SUMMARY villager=<name>
verdict=DIRECT|TOURED … modPulls=<n>` line is the success metric. **Baselines to beat (villager
Alva): TOURED at 46.1 s / 21.3 s (v0.6.0) and 42.7 / 45.3 / 20.2 / 28.8 s (v0.7.1).**

**v0.12.0 run result (confirmed in-game 2026-07-30):** Recipe-first ordering works. A forging
recipe is identified and refused per-recipe, logged as `[CFS] [CFS-SS] SKIP blueprintClass=
ForgingBlueprintInfo villager=Jonte station=Workshop House 2`. Metalworkers and carpenters
fetch their materials by hand (intended behaviour with toggle off). The armorsmith is served by
toggle 1 and reports an ordinary recipe class: `[CFS] [CFS-SS] routeDecision station=Improved
Armorsmith 2 route=recipe detail=WorkshopBlueprintInfo outcome=proceed`. The user watched leg
armor crafted from settlement storage; the earlier concern about armorsmith reporting
`ForgingBlueprintInfo` did not hold. A defect was found in the station-fallback path: when the
fallback cannot resolve a single recipe family, it skips the whole fetch. A workshop running
several recipe families makes that happen: `[CFS] [CFS-SS] routeDecision station=Workshop House
2 route=station-fallback detail=fallbackAmbiguous:3 outcome=skip`. The user observed villagers
thrashing at workshop crafting tables, told a recipe was craftable while materials never
arrived. **Reading caveat:** `routeDecision` lines are rate-limited to five per station name, so
log counts are a positional sample, never frequency. Counter-example showing the recipe route
working: `[CFS] [CFS-SS] STOCKED villager=Gro station=Workshop House 2 wanted=5 short=4
itemsMoved=4 qtyMoved=36 stillShort=0 bpClass=CraftBlueprintInfo`. Zero `[CFS]` exceptions
across the whole log.

**v0.13.1 run result (confirmed in-game 2026-07-30):** The per-item filter worked as
designed. Only two item names were ever denied all run, `Hardwood Long Stick` and
`Hardwood Log`, across 15 DENIED ITEM lines, all at Workshop House 2. The `route=per-item`
decision fired at both problem workshops, all `outcome=proceed`, e.g. `routeDecision
station=Workshop House 2 route=per-item detail=ordinary=9 transform=7 denied=2
outcome=proceed`. Wood Shafts were never denied and flowed normally: five StockStation
moves delivered 41 shafts from `Improved Warehouse 3`, e.g. `StockStation: -4 'Wood Shaft'
from Improved Warehouse 3@(131.54, 48.82, 434.47) -> station (villager=Alaric
station=Workshop House 2, still need 0).` Completed crafts consumed them (a battle-axe
success watch mark shows `Wood Shaft 28->24`). Hardwood still moved by hand — station watch
deltas show `Hardwood Long Stick 0->1` arrivals and `1->0` consumption — so the toggle split
(deny transform INPUTS, deliver transform OUTPUTS) is coherent and the carpenters were not
starved. Zero `[CFS]` exceptions.

The run exposed a QUANTITY defect, the reason for v0.14.0: the stocker delivered the fetch
quest's aggregate manifest covering every queued craft. At Workshop House 5 one fetch moved
105 items (`STOCKED villager=Svend station=Workshop House 5 wanted=6 short=6 itemsMoved=4
qtyMoved=105 stillShort=48`), filling the bin to hard capacity. The next needed part was
then refused entry: `MoveContainerToAgent SKIP: 'Large Iron Axe Head' - destination
GetTotalRemainingCapacity<=0`, with `settlementCandidates=4`. The one axe that completed
consumed the single head already in the bin (`Large Iron Axe Head 1->0`); after that
villagers cycled at the table (observed in-game as thrashing) because heads could not enter
the full bin. This was the first run in which the v0.10.0 capacity pre-check ever refused a
move. Root cause of the difference from vanilla: vanilla delivers one villager carry-load
per trip so a bin cannot saturate instantly; the stocker teleported the entire aggregate need
in one tick, in list order.

**v0.14.0 run result (confirmed in-game 2026-07-30):** Run 2026-07-30 (late), loaded v0.14.0, zero
`[CFS]` errors. The one-craft cadence worked — deliveries arrived as small repeated packets (a
villager's sequence: fetch enter, `STOCKED ... itemsMoved=3 qtyMoved=9`, fetch exit, repeating
with 10 and 9). The bin-capacity lockout from v0.13.1 shrank to one early 5-line cluster. Three
residual causes were separated: (1) axe production limited by genuinely scarce Large Iron Axe
Heads, which are forged at the hand-fed metalworker — intended behaviour with toggle 2 off, not a
defect; (2) workshops drained each other's staged bins — evidenced by `ZERO-MOVE: 'Large Iron Axe
Head' from Workshop House 5 ... (villager=Alaric station=Workshop House 2)`, one workshop's fetch
pulling from another workshop's own bin; (3) the shared-fetch plan could deliver far more than the
game requested — `plan=projects planQty=80 aggQty=1`.

**v0.14.1 run result (confirmed in-game 2026-07-31):** Run 2026-07-31, loaded v0.14.1, zero
`[CFS]` errors (the ~56 TypeLoadException warnings in that log are HarmonyX UnityEngine.CoreModule
noise, not this mod). All three v0.14.1 mechanisms verified: the snapshot dedupe skipped 119
duplicate container listings on every one of 41 rebuilds; zero `planQty > aggQty` cases across 186
STOCKED lines (`plan=recipe` 95 / `plan=projects` 88 / `plan=aggregate-nooverlap` 3); zero
capacity refusals, zero ZERO-MOVE lines, zero duplicate candidate pairs, zero
`Storage_MediumItems_L1` pull sources. User-confirmed in-game: table counts no longer inflated,
villagers more effective. The 173 zero-move stocking events are the known vanilla Heavy Pelt loop
at Workshop House 4 (173 of 174 SHORT lines, all `settlementCandidates=0`; the remaining one is a
single Iron Hammer Head) — third consecutive run, not a defect.

The v0.14.0 and v0.14.1 runs revealed the root cause of the count-doubling defect from v0.13.1:
the settlement snapshot walk listed the same physical container once per structure reaching it.
Candidate dumps showed pairs at identical world positions with identical quantities under two
structure names: `Improved Warehouse 4` / `Broken Metal Parts` at (140.70, 47.50, 436.06), and
`Workshop House 5` / `Improved Metalworker` at (170.72, 42.97, 446.73). Roughly half of all
listings were duplicates (119 of ~253), doubling every count shown to the player and fed to the
villager availability check (user saw 6 axe heads when 3 existed). Confirmed in-game 2026-07-30,
fixed by the v0.14.1 dedupe.

**Physical-transform station gate (v0.10.0+)** — Villagers at a carpenter
station stood idle instead of working. Confirmed in-game 2026-07-29 by
running with `EnableForVillagers=false`: carpenters worked normally without
the mod and hovered doing nothing with it on. The carpenter is a physical
transform: stick on sawhorses, struck with an axe, not a recipe from a
station bin. A long stick is large and cannot enter a crafting bin. The mod
reported the requirement covered anyway, so the game saw no reason to send
the villager fetching, so materials never arrived and the craft never began.
This reproduces the same stall the retired v0.8.0 fetch-quest-priority lever
produced deliberately (recorded 2026-07-27).

The discriminator (Cecil 2026-07-29): exactly three types derive from
`SSSGame.CraftInteraction`: `SSSGame.AnvilInteraction`,
`SSSGame.CarpenterInteraction`, and `SSSGame.DyeingInteraction`. Deriving
from `AnvilInteraction` identifies a physical-transform station; plain
`CraftInteraction` identifies ordinary bin crafting. The mod matches the
interaction's native class name and every ancestor class name, so a future
subclass is caught automatically. Managed casts lie for interop objects
under a base declared type, so this must be a native class-name read and
never a cast.

A workshop building owns several work surfaces. Confirmed in-game 2026-07-30,
one station object reported four of them and none on the station itself:
`all=[CraftInteraction@descendant; AnvilInteraction@descendant;
CarpenterInteraction@descendant; CarpenterInteraction@descendant]`. Because
interactions sit on descendant objects, the v0.10.0 lookup checking only the
station's own GameObject found nothing; v0.10.1 replaced it with a walk over
the station itself, its ancestors to depth 10, then descendants to 200 nodes.

Config and confirmed behaviour: `1. Features/StockTransformStationMaterials` is
a bool defaulting to false, read in exactly one place, the station stocker.
The two availability paths never consult it and always stand aside at
transform stations, because reporting a transform recipe already satisfied
is what stalled villagers entirely. Confirmed in-game 2026-07-30 with OFF:
metalworkers walked to warehouse to collect iron bloom themselves, the mod
logged zero bloom moves, and recipe-family skip lines dropped from 20 to 0,
because the station gate now runs before the recipe gate and returns first.
Confirmed with ON: 41 override lines fired, 50 moves across three buildings
covering fourteen materials, and five iron bloom deliveries arrived with no
villager walking. The availability check still stood aside six times, so
vanilla decided when to fetch and the mod only delivered.

The intended model (user, 2026-07-30): by default the PLAYER crafts from
storage at workshop tables. The first toggle lets VILLAGERS do the same at
workshop tables. The second toggle additionally lets villagers craft at
workshop ADD-ON UNITS, the physical transforms, and requires the first
toggle on. The stocker already enforces that dependency because it refuses
to run unless the villager toggle is set. A possible third layer covering
bloomeries is undecided.

Known limit against that model: the gate currently works per BUILDING, not
per unit. A workshop containing any add-on unit is skipped whole when the
second toggle is off, so a villager at that same workshop's ordinary table
gets no help either, which breaks the first toggle for that workshop. The
armorsmith is unaffected because it is a separate building holding a single
ordinary work surface, reported in-game 2026-07-30 as `count=1`.

Ground truth for the per-unit fix (Cecil 2026-07-30): `SSSGame.CraftingStation`
keeps its units in separate typed lists: `_craftingTables` as
`List<CraftInteraction>`, `_anvils` as `List<AnvilInteraction>`, and
`_studyInteractions` as `List<StudyInteraction>`. So the game already
distinguishes tables from add-ons. `SSSGame.CraftInteraction.craftStationHost`
is a back-reference from a unit to its workshop. `SSSGame.CraftingQuest/
CraftingQuestData` carries a `CraftInteraction _ci` property naming the
exact unit a crafting job will use, alongside `FindFreeCraftInteraction()`
and `UsableCraftingTablePredicate(CraftInteraction)`. Reading the unit off
the crafting job is therefore a per-unit route that needs no hierarchy search.

Diagnostics added, all defaulting on: a `stationProbe` line reports every
work surface found for a station, with each one's native class name and
whether it was found on the station, an ancestor or a descendant, plus which
was selected and whether it matched. A `destinationProbe` line reports the
collection the stocker writes to, its `canAddItems` value and the remaining
capacity for the item about to move. A `standing aside` line fires at each
of the two availability paths naming the station class. These exist because
the v0.10.0 gates returned silently, which made a working gate and an absent
workload indistinguishable in the log. Also record that the capacity
pre-check added in v0.10.0, which asks a destination how much it can accept
before touching the source, has never refused a move in any run so far.

**v0.14.0 quantity rule** — `StationStocker.BuildOneCraftPlan` replaces the aggregate
manifest as the primary quantity source. A project-specific fetch (`CrafterSpecificFetchQuest`)
gets one craft's worth of its own recipe, resolved through craftingProject → craftingQuest →
`Blueprint` → `FillPartsManifest` (the same call the Phase 1 player path has always used) —
logged `plan=recipe`. The shared fetch gets one craft's worth per non-transform project on
the station, merged by item name with quantities summed — logged `plan=projects`.
Transform-family projects are included only when `Transfer/StockTransformStationMaterials`
is true. When no plan resolves at all, the aggregate manifest is the fallback, logged
`plan=aggregate` — fail-open preserved. The `STOCKED` line now ends `denied=<n>
plan=<src> planQty=<n> aggQty=<n>`; `planQty` far below `aggQty` at a busy workshop is the
fix operating. User ruling 2026-07-30: no capacity-headroom reserve — it only shrinks the
working bin; the one-craft cap is the chosen mechanism.

**Bloomery delivery (v0.15.0 spike + v0.16.0, toggle 2)** — Spike run results (v0.15.0,
2026-07-31): the bloomery supply fetch (`FSM_FetchBloomerySupplies`) always carries quest data
`BloomstationSupplyQuestData` (51/51); the kiln-tending fetch (`FSM_FetchKilnSupplies`)
always carries `KilnkeeperQuestData` (132/132) and belongs to the bloomery's own kilnkeeper,
not the charcoal maker. Zero `CoalmakerSupplyQuestData` in 183 fetches — the charcoal maker's
supply path uses neither FSM (its hook discovery is backburnered, user 2026-07-31). All three
bloomery slots (ore/coal/bloom) are `Storage_SmallItems_L1` capacity 15. `GetKilnRecipeManifest()`
= `'Iron Ore'x5 + 'Coal'x20`; `_kilnContainerManifest` = `'Coal'x20`. The game's own
substitution confirms `'Iron Ore' -> 'Metal Scraps'` (`TryGetPartSubstitute`, 6/6 probes).

How v0.16.0 works: `BloomeryTrace.TryStockBloomery`, called from both OnStateEnter postfixes.
Resolution is guess-free: quest data rewrapped as `BloomstationQuest/BloomstationQuestData`
(base of both observed classes) → `.Quest` → `.bloomstation`, so the correct bloomery is
found even with several. Wanted = kiln recipe manifest; shortfall netted against
`GetInventory()` counting the substitute's stock too; delivery via the existing no-ledger
`CraftTransfer.StockStation` (blacklist + capacity pre-check apply); a second pass pulls the
substitute for any remainder. Gates: `EnableForVillagers` + `StockStationOnFetch` + toggle 2 +
host/solo. 5-second per-station cooldown (the kiln hook fires very often). Success marker:
the unconditional `[CFS] [CFS-BLM] BLOOMERY STOCKED ...` line with pass-split
`subItemsMoved`/`subQtyMoved` fields; a post-stock slot dump shows where items landed.

First run (v0.16.0, 2026-07-31, log-verified): 34 stocking events, 15 with qtyMoved>0 including
full `qtyMoved=5 stillShort=0` deliveries at both bloomeries; 10 events used the scraps
substitute pass; deliveries land in the ORE slot (post-stock dump: `slot=ore ...
first='Iron Orex2'`) and the bloom output slot stayed empty in every dump; warehouse sourcing
worked (4 Iron Ore moves from Improved Warehouse 3); `kilnSupplied` flipped True at both
bloomeries, which never happened pre-feature; zero errors, zero capacity refusals. User observed
bloomeries receiving ore in groups of 5 into ore storage.

Defect found and patched in config: 28 of the run's pulls drained `Storage_SmallItems_Kiln` —
the kiln's internal loading container, a FOURTH container type not on the blacklist (the slot
blacklist held: zero L1/MediumItems candidates all run). 17 were self-shuffles, 11 drained a
NEIGHBORING bloomery's loaded kiln. `Storage_SmallItems_Kiln` is in the Plugin.cs bind default
as of v1.0.0, and a desktop soak run on 2026-07-31 confirmed the blacklist holds — the
container name appeared exactly once in the whole session log and only as another mod's
capacity survey line, never as a pull source, while bloomeries were still fed from warehouse
stock.

## Dead-ends and traps
- **`Settlement.QuerySettlementResources()` HANGS the game** (`AppHangB1`; no managed rescue). Use
  the `GetStructures()` walk.
- **`_OnCraftingSuccess` IS the consumption site** — the ~3–6 ms between its prefix and postfix.
  Snapshotting only `CraftInteraction.ItemInventory` reads PRE==POST whenever the craft draws from
  the agent side, which makes the site look absent; watch both collections.
- **A postfix on `_UpdateAvailablility` / `_UpdateAvailabilityStatus` cannot see the requirement
  text.** At postfix time the label is still the prefab placeholder `"99"`; the real `0/2` is
  written later by other code. Poll instead — no field-name guess can fix a timing problem.
- **`ItemThumbnailPanel.availability` is always null and `checkAvailability` always False** on
  requirement rows. Neither identifies a requirement row. The row's number lives at
  `.../ItemThumbnailMaterial_Medium/Fitter/Quantity`.
- **The details/preview panel's subtree CONTAINS the material rows** (`Quantity` at depth 5), so a
  naive subtree walk from it resolves another row's label and rewrites it with the wrong item's
  stock. Stop descending at any node owning its own `ItemThumbnailPanel`, and cap the walk depth.
- **An unanchored `\d+/\d+` match hits `"Durability 100/100"`.** Selection must require the whole
  detagged string to be the pair.
- **A poller that re-reads its own output compounds** (`0/2` → `20/2` → `40/2`). Record the exact
  string written per label; skip byte-identical text; recompute only when vanilla overwrites it.
- **The EquipPoint structural probe tagged 0 of 651 containers** — blacklist by container type.
- **✗ `CheckOwnedRequirements` does NOT schedule or suppress the villager fetch quest** (confirmed
  in-game 2026-07-21, v0.7.1). The availability postfix widened the gate **5619 times** for
  villagers in one session and behavior was unchanged — Alva still toured 4× at 42.7/45.3/20.2/
  28.8 s, matching her pre-change baseline. `modPulls=0` on every cycle, because she walks first
  and arrives stocked, leaving no shortfall by craft time. **Widening availability is necessary
  but nowhere near sufficient for the villager half.** The fetch is driven by the station's own
  supply manifests (`CraftingStation._minimumFetchManifest`, `GetMinimumFetchManifest()`,
  `FetchRequirementManifest`) consumed via `CrafterFetchQuest.GetNeededSuppliesManifest()`.
- **✗ Widening the fetch-REACH fields is pointless** (confirmed in-game 2026-07-21).
  `FSM_FetchCraftingSupplies` is already permissive at runtime — `searchStorages=True`,
  `storageSearchRange=100`, `worldSearchRange=20`, `searchWorld=True`, `maxSearchDepth=0` — and
  villagers already probe 144–149 distinct containers per cycle across ~16 building types.
  Vanilla villagers reach settlement storage fine; the cost is the WALK, not the reach.
- **The FSM state actions are SHARED ScriptableObjects** (confirmed in-game 2026-07-21): one
  instance pointer served all five villagers across 231 state entries
  (`FSM_QuestAction : vStateAction : UnityEngine.ScriptableObject`; per-villager data lives in
  `QuestData` via `FSM_QuestAction.GetQuestData`). Any field write on one applies settlement-wide.
- **✗ Suppressing the crafter fetch quest's `GetPriority` score stalls crafting completely**
  (confirmed in-game 2026-07-27). In a 5 minute 49 second run, 704 of 708 `[CFS-P2] CYCLE SUMMARY`
  lines logged `verdict=DIRECT fetchEnters=0` — the walk really was suppressed — yet all 708
  logged `modPulls=0`, and no crafting-success marker appeared anywhere in the log. Villagers
  visibly alternated between sitting down and standing up; villager Gro logged 218 cycles in 349
  seconds, consecutive cycles 0.6–2.4 seconds apart. The root cause is that the just-in-time pull
  hangs off `BeginCraftingSequence` (Point C), which the AI scheduler never reaches for a villager
  standing at an empty station — so suppressing the walk removed the only thing that would ever
  have stocked it. Record explicitly that the lever itself worked mechanically and the failure was
  a design problem, not a tuning one: the priority rollup reported `min=-999.9 max=15.5` across
  the run, so vanilla priorities sit in roughly the −1 to 15.5 range and the mod's −1000 was
  decisively on the correct end. Suppression fired about 139,000 times across the run (rollup
  counters `suppressed=39058` of 135113 calls, and `suppressed=100183` of 283665 calls).
- **Diagnostic rate-limiting note:** the `[CFS-FQ] PRIORITY OBSERVE` lines are rate-limited to the
  first 20 calls after each 1500 ms idle gap, so the handful of raw lines in a log is a positional
  sample of the earliest calls per burst and must never be read as representative of the overall
  suppression rate. Only the `PRIORITY rollup` counters carry that.
- **⚠️ Never patch the fetch-depth methods** — `CraftingStation.GetFetchDepth()`,
  `GetPersonalFetchDepth()`, `CraftingQuest.TryGetFetchDepth`. All take by-ref primitives,
  the project's known-fatal trampoline-NRE family. Read, never detour.
- **Cecil cannot answer "who calls this" for this game** — interop method bodies are native
  trampolines (`Workstation`: 138 methods, 3153 IL instructions, **2** game-to-game calls). Use
  Cpp2IL or a runtime probe; see architecture.md → IL2CPP interop gotchas.
- **Confirmed API facts (Cecil 2026-07-27):** `WorkstationQuestData` is nested as
  `SSSGame.AI.WorkstationQuest/WorkstationQuestData`. `CraftingStation.GetItemManifest()` crashed
  OuthouseComposterMod, so it stays off-limits. Fetch-depth methods carry by-ref primitives and
  remain read-only. The actual `ItemManifest` sources are `CrafterFetchQuest.
  GetNeededSuppliesManifest()`, and on `CraftingStation`: `GetMinimumFetchManifest()`,
  `get_FetchRequirementManifest()`, `get__minimumFetchManifest()`. `Workstation.
  GetItemsNeededFromSettlement()` returns `Il2CppSystem.Collections.Generic.List<ItemCategoryInfo>`,
  not an `ItemManifest`.
- **Vanilla's displayed `have` already includes the station's own storage**, and the settlement
  snapshot walks that same station, so the station quantity must be netted out or every row
  inflates.
- **`CraftingStationType` cannot serve as a forge-versus-table discriminator (Cecil 2026-07-28):**
  The enum only reports GROUP or INDIVIDUAL, not station kind. See docs/architecture.md →
  Workshop structure section for the working discriminator (blueprint class).
- **⚠️ Availability widening impact on villager behavior is UNTESTED (as of 2026-07-28):** The
  mod's availability-widening lever logged 18,700 widenings across 13 villagers in a single run,
  meaning it told the game a recipe was craftable that many times where vanilla considered it
  uncraftable. Whether that widening drives observed villager behaviour — a villager chopping a
  Hardwood Long Stick into Wood Shafts while 389 sticks sat in settlement storage, and bark being
  consumed five per craft while rope and fibers were already stocked — is unconfirmed. A baseline
  run with `EnableForVillagers=false` would settle it; the user decided on 2026-07-29 to ship
  fixes first and skip that baseline.
- **Work unit not reachable from fetch hook (Cecil 2026-07-30).** `SSSGame.CraftingQuest/
  CraftingQuestData` carries a `CraftInteraction _ci` property naming the exact work unit a
  crafting job targets, but the station stocker cannot reach it. The stocker hooks
  `FSM_FetchCraftingSupplies.OnStateEnter`, where the quest data is `CrafterFetchQuestData`,
  whose complete property list is `fetchData`, `returnData`, `_cfQuest`, `_qItmManEvData`,
  `_qEvData`, `_noStorageSpace`, `_noPartsFound`, `_noCarryCapacity` and `Quest`. There is no
  link from it to the craft quest, to `CraftingQuestData`, or to any `CraftInteraction`. This
  is not merely a missing link: a workshop shares one fetch quest across all of its concurrent
  projects, so at fetch time there is no single work unit to name. Any per-unit gate on the
  fetch path must therefore decide per ITEM, not per quest. The plan file
  `CraftFromStorageMod/TRANSFORM_STATION_PLAN.md` previously listed reading `_ci` as the
  available next step; the per-item filter in v0.13.0 was built instead because of this
  constraint. Scope this dead-end to the `_ci` link only — it does NOT mean the fetch path
  cannot identify a work unit at all. Two untried primitives remain, both Cecil-confirmed
  2026-07-30 and neither instrumented live: `CraftingStation._anvils` is a
  `List<AnvilInteraction>`, a positive membership list of the add-on units, and
  `AnvilInteraction.KnowsAnvilProcessForThisItem(ItemInfo, BlueprintInfo&)` is a public method
  answering whether a given anvil makes a given item. Together they are a per-unit test keyed on
  the item rather than on the recipe class, and they are the first thing to try if the v0.13.0
  per-item filter proves too coarse.

## Known limits
- **Host/solo only** (`IsHostOrSolo()` gates the availability, pull and sweep paths); non-host
  clients fall back to vanilla gating, failing closed. Multiplayer client support is requested.
- No timeout for a craft abandoned after the pull — self-heals on the player's next craft via the
  stale-ledger sweep, or the items simply stay with the player on world-leave.
- A rewritten requirement row holds its value until vanilla itself repaints the row.
- The ledger is one flat list, so two players pulling in overlapping windows could cross-attribute
  (single-player unaffected).
- Diagnostics all default `false` as of v1.0.0; the per-move and candidate-dump lines that name a
  source container sit behind `TransferDiagnostics`, so a reporter's log carries none of them
  unless that flag is switched on first.
- **A failed move is invisible in the log.** `CraftTransfer.MoveContainerToAgent` returns 0 for a
  failed remove and a refused add alike, and the per-move log line only fires when `moved > 0`,
  so a candidate that yields nothing leaves no trace. Two distinct failure causes are behind it,
  both confirmed in the v0.9.2 run: a destination at capacity (returning 0 on `AddItems`), and a
  destination refusing an add into an empty inventory. Both appeared as unexplained zero-moves in
  the v0.9.1 run; v0.9.2's split of `removed=` and `added=` counters is what separated a failed
  source removal from a refused destination add.

## Version history
- **v1.2.0** — station-qualified allow-list entries (`Node@StationClass`), plus
  `StorageCensus.ResolveStationClass` reusing the census's own
  `FindComponent<Workstation>` + `Plugin.NativeClassName` pair. The class is resolved lazily and
  at most once per structure, so only a qualified entry pays for the extra hierarchy walk. Every
  unresolvable case refuses the container, so the gate fails closed. Ships one qualified entry,
  `Scraps@HuntingStation`. Loaded clean in-game 2026-08-11 with zero mod errors; the admission
  path itself is ⚠️ pending in-game.
- **v1.1.0–v1.1.2** — the `SourceNodeAllowlist` per-node override, its `NodeName` field on the
  snapshot record, and `node=`/`type=` fields on all four log lines that name a source candidate
  (`PullShortfall`, `StockStation`, `StockStation ZERO-MOVE`, `StockStation CANDIDATES`).
  Confirmed in-game 2026-08-11: smolkr horns reached the crafting table, the settlement total
  showed in the crafting menu, and villagers crafted on a newly assigned task.
- **v1.0.0** — release: config restructured into three numbered sections ([1. Features],
  [2. Tuning], [3. Diagnostics]) so feature toggles lead the file; `EnableForVillagers`
  default flipped to false (fresh installs player-only per user decision 2026-07-31);
  three unsafe settings removed from config surface and hardcoded off
  (`CensusTryQuerySettlementResources`, `SuppressFetchQuestPriority` /
  `FetchQuestSuppressedPriority`, `TraceCheckOwnedBlueprintManifest`) — re-enabling
  requires code changes; all diagnostic defaults flipped to false (eighteen flags:
  Trace-family, EnableCraftWatcher, TransferDiagnostics, UiDiagnostics,
  EnableVillagerFetchTrace, TraceStorageWhitelist, EnableBloomeryTrace);
  `Storage_SmallItems_Kiln` folded into `2. Tuning/BlacklistContainerTypes` bind default.
  EnableBloomeryTrace decoupled from delivery feature: four bloomery/kiln FSM patches
  attach when flag OR (EnableForVillagers AND StockStationOnFetch) is true; flag gates
  only diagnostic logging in BloomeryTrace.cs. Defect fixed: OnStateEnter patches opened
  with early `return` on trace flag — with new false default would skip TryStockBloomery
  delivery entirely — guard now wraps trace call only. Second defect fixed:
  `[CFS-SS] STOCKED` summary line logs unconditionally when qtyMoved>0; zero-move lines
  sit behind TransferDiagnostics (desktop 2026-07-31, 128 total [CFS] log lines vs. 1,218
  before fix, 19 STOCKED lines all with qtyMoved>0, 2 BLOOMERY STOCKED with warehouse
  sourcing, zero errors). Soak run (desktop 2026-07-31, v0.16.0): kiln blacklist held,
  bloomeries reached supplied, zero errors, villagers did not walk up to crafting tables
  and leave — the walk-away question is closed.
- **v0.16.0** — bloomery delivery behind toggle 2 (`TryStockBloomery`; recipe-manifest
  shortfall, scraps-substitute second pass, 5 s cooldown; `Storage_SmallItems_L1` folded into
  the blacklist bind default). First run 2026-07-31 verified: correct-slot delivery, kilns
  reached supplied, zero errors; exposed the kiln-container drain patched in config (⚠️
  untested).
- **v0.15.0** — read-only bloomery/kiln fetch spike (`[CFS-BLM]`, `Probe/EnableBloomeryTrace`).
  Run 2026-07-31: identified both quest-data classes, slot types, recipe manifest, and the
  ore→scraps substitution.
- **v0.14.1** — snapshot dedupe by container identity, delivery plan capped by the fetch request
  (`plan=aggregate-nooverlap` fallback), `Storage_MediumItems_L1` added to the source-blacklist
  default. Run in-game 2026-07-31: all mechanisms verified, count-doubling fix user-confirmed.
- **v0.14.0** — one-craft-per-fetch quantity rule (`BuildOneCraftPlan`; `plan=`/`planQty=`/`aggQty=`
  fields on STOCKED). Routing, denial filter and StockStation untouched. Run in-game 2026-07-30:
  cadence confirmed, exposed the over-delivery and bin-stealing defects fixed in v0.14.1.
- **v0.13.1** — made `BuildDeniedItemNames` reset both of its out-parameter counts when
  its outer catch fires. Without the reset, a classification abandoned partway could report
  a nonzero project count alongside an empty denied set, which would send the caller down
  the per-item route with nothing to deny and stock transform units while the toggle was
  off. Run in-game 2026-07-30; per-item filter confirmed working; quantity defect found.
- **v0.13.0** — replaced the station-fallback path's whole-quest skip with a per-item
  filter. When the blueprint class cannot be resolved from the quest and the transform
  toggle is off, the mod now walks the station's own `craftingProjects`, classifies each
  project's parts as transform or ordinary by its blueprint class, and denies only those
  item names wanted exclusively by transform projects. An item wanted by both a transform
  project and an ordinary project is never denied, so the ordinary table's need wins. The
  whole-building `IsSkippedStation` test survives only for stations where no project at all
  could be read. The recipe route for a `CrafterSpecificFetchQuest` is unchanged, and the
  transform toggle set to ON keeps its previously confirmed behaviour unchanged. New
  diagnostics: a `route=per-item` value on the `routeDecision` line with an
  `ordinary=`/`transform=`/`denied=` detail, a rate-limited `DENIED ITEM` line, and a
  `denied=` field appended to the `STOCKED` summary line. Run in-game 2026-07-30.
- **v0.11.0** — added `Transfer/StockTransformStationMaterials` (default false, stocker
  only) plus the `stationProbe` per-surface listing and the `destinationProbe` line.
  Confirmed in-game 2026-07-30 in both positions.
- **v0.10.1** — fixed the station lookup, which had checked only the station's own
  GameObject and so never resolved anything, and added fire-verification logging to
  the two availability gates that previously returned silently. Confirmed in-game
  2026-07-30.
- **v0.10.0** — added the physical-transform station gate keyed on `AnvilInteraction`
  ancestry, the `Transfer/SkipStationClasses` config, and a destination capacity
  pre-check before every move. Confirmed in-game 2026-07-30 to unstall carpenters.
  The older `SkipBlueprintClasses` recipe gate was deliberately left in place as a
  second gate.
- **v0.9.5** — corrected the dropped-recipe count in the new per-villager log line. It had
  counted every repeat widening of an already-dropped recipe while labeling the number as
  distinct recipes, so it now tracks a set of recipe names instead and the reported number
  matches the label. ⚠️ Not yet run in-game.
- **v0.9.4** — added a widened-recipe diagnostic answering a question the earlier rollup could
  not: the rollup reported how many times the mod widened the craft-availability gate per
  villager, but never which recipe was widened. The per-widening raw line now carries the
  recipe name and the missing-item shortfall. Each rollup flush emits one line per villager
  listing that villager's widened recipes with per-recipe counts and shortfall for each,
  ordered by descending count and capped at top 8, with explicit suffixes reporting anything
  omitted so nothing truncates silently. The original `TryReportAvailable rollup:` line is
  unchanged. One reading caveat: the shortfall recorded per recipe is the FIRST one seen in a
  flush burst, so a recipe whose missing set changed mid-burst shows only its earliest state.
  ⚠️ Not yet run in-game.
- **v0.9.3** — added a station-based fallback resolution chain so the blueprint-class gate also
  covers plain `CrafterFetchQuest` quests (which lack a direct `craftingProject` link), stopped
  the candidate retry loop once a destination refuses an item (logged as `destinationRefused=true`
  on the SHORT line), blacklisted `Storage_HotItemsSmall` as a pull source, and added a
  diagnostic `stationType=` field to separate workshop tier from station kind. ⚠️ Not yet run
  in-game.
- **v0.9.2** — added the blueprint-class gate (`Transfer/SkipBlueprintClasses`) with a
  fail-open resolution chain, added a `bpClass=` field to the STOCKED log line, and added
  zero-move and candidate-list diagnostics that separate a failed source removal from a refused
  destination add. Run in-game 2026-07-28; confirmed the forge gate fires.
- **v0.9.1** — diagnostics only: `StockStation SHORT` line with `settlementCandidates`, and
  `stationObj=` on the `STOCKED` summary line. Run in-game 2026-07-28; separated the three
  shortfall causes and showed `stationObj` reports workshop tier, not station kind.
- **v0.9.0** — Phase 2c: stock the station at fetch-start instead of suppressing the fetch. New
  `StationStocker.cs` + `Patches/StationStockPatches.cs` postfix on
  `FSM_FetchCraftingSupplies.OnStateEnter`; new no-ledger `CraftTransfer.StockStation`; new
  `Transfer/StockStationOnFetch` (default true) and `Transfer/SuppressFetchQuestPriority` (default
  false, retiring the v0.8.0 lever). Built 2026-07-27, run in-game 2026-07-28; worked in first run
  with 122 `_OnCraftingSuccess` events.
- **v0.8.0** — Phase 2 lever 2: suppress the crafter fetch quest. Postfixes `GetPriority()` on
  `CrafterFetchQuest` AND `CrafterSpecificFetchQuest` (the subclass re-declares it), setting
  `Transfer/FetchQuestSuppressedPriority` (default −1000) **only when the cached settlement
  snapshot covers the entire needed-supplies manifest** — a villager needing something the mod
  cannot supply stays free to fetch. `GetNeededSuppliesManifest()` is CALLED, never patched
  (`ItemManifest` return = the risky family). Logs vanilla priority values. Also rate-limits the
  Point A villager line (5619 → a few + rollup). Confirmed in-game 2026-07-27 to stall crafting
  entirely; retired behind `SuppressFetchQuestPriority` (default false).
- **v0.7.1** — per-villager correlation: `villager=<name>` on every `[CFS-V]` line, plus
  `modPulls=`/`modItemsPulled=` appended to the `[CFS-P2] CYCLE SUMMARY` line.
- **v0.7.0** — Phase 2 lever 1: `Villager` branch on all four Phase 1 points under
  `Transfer/EnableForVillagers`; villager pulls land in the station inventory; ledger re-keyed to
  `Dictionary<IntPtr, List<LedgerEntry>>` (7 concurrent villager crafts observed). Confirmed
  in-game to be **insufficient on its own** — see dead-ends.
- **v0.6.0** — read-only Phase 2a villager-fetch diagnostic spike (five postfixes; the
  `verdict=DIRECT|TOURED` cycle summary that is now the Phase 2 success metric).
- **v0.5.1** — nested-panel boundary + depth cap on the label walk (details panel could otherwise
  rewrite a material row). UI confirmed in-game.
- **v0.5.0** — requirement-UI write moved from the postfix to `CraftUiPoller`; strict whole-string
  have/need selection; idempotency guard.
- **v0.4.0–v0.4.3** — requirement-UI feature plus the diagnostics that located the label
  (fire-verification, scoping evidence, hierarchy dump).
- **v0.3.2** — zero-pull no longer skips the fail-closed verify; confirmed in-game.
- **v0.3.0/v0.3.1** — Phase 1 pull/verify/sweep-back; agent gate added to the sweep path so a
  villager craft cannot consume the player's ledger.
- **v0.2.0** — craft delta watcher + census v2; resolved the consumption site.
- **v0.1.x** — read-only diagnostic spike (gate trace + storage census).
