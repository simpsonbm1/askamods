# SupplyChainMod — Demand Model & Phase 2d Redesign Plan

Status: DRAFT for user review (2026-07-15). No code gets built from this until approved
(working agreement 2026-07-15: propose → approve → build). Supersedes the reactive v0.8.0–v0.9.1
classifier-patching approach; those versions' in-game findings feed this plan.

## Goal (user-stated, incl. 2026-07-15 additions)

Steady-state flow autopilot: "player creates buildings, assigns workers, sets basic material
floors; the mod works out the rest." **Long-term the mod CREATES tasks for needed items** — which
closes the demand loop: a distress signal for item X becomes a mod-created production task, which
makes X *structurally* demanded, which flow measurement then confirms, which clears the distress.
Demand assessment is the cornerstone; it must be designed forward from "what is need?", not
accreted backward from false-positive vetoes (how v0.8.0–v0.9.1 grew).

## The demand loop (target architecture)

Three layers plus the actuation feedback that ties them into a loop:

1. **Structural demand (intent)** — what the settlement is *configured* to consume, read from the
   task graph. Player-created and (later) mod-created tasks are the same thing here by design:
   the mod's own task creation lands in the same source of truth it reads demand from.
   Per-category sources (confirmation status in brackets):
   - Crafting/cooking recipe inputs: station task lists × blueprint manifests
     [BomDump proven in-game 2026-07-12: iterate `_localBlueprints` dictionaries;
     `KnowsBlueprintForItem` is a dead-end].
   - Food: population × eating; cooking/dining task configuration [partially known via DVN/
     cooking-pipeline docs; needs a per-item mapping pass].
   - Seeds: FarmingStation crop selections [census shows 16-task farm datas; task type unprobed].
   - Ammo: archer/patrol consumption [VillagerAmmoMod knowledge; consumption rate measurable].
   - Fuel: fire structures [TorchFuelMod knowledge: fuel categories].
   - Build materials: Buildstation pending projects [DVN lead, unprobed].
   Output: per-item `wantedBy` list + rough magnitude (task quota × recipe qty — user-approved as
   sufficient for v1). Rebuilds on a slow cadence or on task-change detection — it only changes
   when tasks change. "Recipes" here means crafted items (armor/weapons/tools) — user-confirmed
   that reading makes the category list complete.

   **Long-term extension — chain-capacity rollup (user-specified 2026-07-15):** structural demand
   gains a TIME element. The BOM graph is a dependency chain (hammer ← hammer head + wood shaft;
   hammer head ← iron bloom; iron bloom ← iron ore + coal; ore mined at x/min, coal at y/min,
   bloom produced at z/min, shafts at w/min) — so end-to-end DELIVERY CAPACITY per end item
   (n hammers/min) is computable by rolling measured per-link production rates up the chain, and
   comparable against that item's consumption. A breakdown anywhere in the chain (ore stops, bloom
   slows) propagates to the end item's capacity — that per-link bottleneck attribution is a data
   point the autopilot will eventually need. Builds on exactly the two spikes below: BOM extraction
   (the graph) + metabolic rates (the per-link flows). Not v1 scope; the spikes must not preclude
   it (keep per-item rates queryable, keep the BOM graph walkable both directions).
2. **Measured flow (sizing + confirmation)** — the existing metabolic net-rate + churn series,
   demoted from "the demand gate" to what they measure well: how big a buffer/quota needs to be,
   and whether a structural demand is actually being exercised. Known blind spots stay documented:
   summed series cancels internal hauling; balanced produce/consume reads near zero; stalled
   chains read zero exactly when intervention matters.
3. **Distress (urgency + creation trigger)** — game-declared failure: noProduction map (item +
   needed-by) [wired v0.9.1; add/remove semantics UNMAPPED — observation item], villager item
   complaints [Phase 1 demand model exists, never wired into BudgetPlane], storage-full/STALL
   verdicts [Phase 2a, proven]. Near-term: urgency ranking for proposals. Long-term: the trigger
   for task creation — distress on X with no structural producer of X ⇒ create the producing task.

Decision shape: **structural demand gates** (no configured consumer → never tier-bump, never
protect beyond a floor, eviction allowed), **flow sizes**, **distress prioritizes** (and later
triggers creation).

### Task creation (long-term lever, research status)
Leads only, nothing verified: `IFilterTaskStation.CreateFilterTask(taskId)` (Cecil-verified
2026-07-12), task-preset machinery `SerializeTasksPreset`/`DeserializeTasksPresetData` (Cecil).
Runtime creation of `WorkstationTaskData`/`ResourceStorageTaskData` instances is UNRESEARCHED —
⚠️ research item. Constraint inherited from VillagerAmmoMod: never Harmony-patch methods with
inventory-family parameter types (native crash); probing must use direct calls from polling
context, not detours. Mod-created tasks must be ledger-tagged for revert/cleanup.

## Container ground truth (hog correctness)

User confirmation (2026-07-15): **shared-pool hogs exist and matter.** The v0.9.x failure was
inference, not concept — SupplyOwner display names are not container identity, and hut rows'
SupplyOwner = the hut itself, which fabricated shared pools out of separate storage units
(Resin/sticks/fibers at Woodcutter's House 2, seeds vs mushrooms/eggs at Gatherer's Hut 4,
feathers at Gatherer's House 2 — all falsely mutual). Requirement: a full per-structure inventory
of which containers are SHARED (multi-item) vs DEDICATED (single-item).

**Container semantics (user-confirmed 2026-07-15):** `ItemContainer.capacity` = INVENTORY SQUARES
(slots), not item units. Each slot holds one stack of a single item type, and — the wrinkle —
**stack size is per-(container, item)**: the same item can stack differently in different
containers (which is why the API is the instance method `container.GetStackSize(ItemInfo)`, and
why no formula may ever use a global per-item stack constant). Example: Hunter's House meat/bone
container = 25 slots × 50 bones/slot there = 1250 bones. `GetFillRatio()` = slots used / slots.
Consequences for sizing math (all reads per-container):
- Effective unit capacity for item X in container C = free slots × `C.GetStackSize(X)`.
- Settlement capacity for X = Σ over compatible containers of (slots × that container's stack
  size for X) — never squares × a shared constant. (This is presumably what
  `GetMaximumStorageCapacity` computes, ignoring occupancy — explains its "theoretical" reads.)
- Deficit-sized eviction is SLOT-GRANULAR and container-local: to admit N units of blocked victim
  V into container C, free `ceil(N / C.GetStackSize(V))` slots of C; freeing a slot means removing
  a whole stack (up to `C.GetStackSize(hog)` units) of the hog — evicting fewer units than empties
  a stack frees nothing. "Just barely enough" = the smallest number of whole hog stacks IN THAT
  CONTAINER that yields the victim's needed slots.

**Probe (v0.9.2, read-only — RAN IN-GAME 2026-07-15, all objectives met):** zero errors across 19
structures / 116 containers; every row resolved via `storageSupply.interaction.Container` (hut and
warehouse alike — `taskContainer` is a NetworkTaskContainer, NOT the physical container);
`GetAcceptedItemTypes()`/`CanStoreItemType()` CONFIRMED working at runtime on all 116. Ground
truth: sharing is common (59/116 containers multi-item) and follows item-family lines — confirmed
shared pools incl. Raw Red Meat+Bone Fragments (Hunter's House, fill 1.00 — the founding case),
Fibers+Resin+Pine Cone+Reed Seeds (Woodcutter's), Feathers+Fibers+9 seed types (gatherer huts),
9-way seed containers (warehouse). Player Off-dedication observed live (Gatherer's House 2 mixed
container: Flax/Reed Seeds + Pine Cone at rank 3). Every structure also has one walk-only
`capacity=9999, acceptedTypes=0` internal catch-all container no rows touch — exclude from maps.
Original probe spec (kept for reference):
- Warehouse rows: group by `taskContainer` POINTER (true physical identity). The de-facto
  accepted-item set falls out of the rows themselves (vanilla makes a row per compatible item per
  container). Log capacity + fill per container.
- Hut rows (taskContainer null): try `storageSupply.interaction?.Container` per row; fallback
  child-hierarchy walk collecting `ItemContainer` per node (singular GetComponent — plural is the
  known-missing trampoline). First runtime use of `GetAcceptedItemTypes()`/`CanStoreItemType()`
  as cross-check (Cecil-confirmed 2026-07-14, never yet called).
- No wrapper caching across polls (project gotcha): build the map fresh per dump; if a persistent
  identity is needed later, derive a stable string key (container transform path/index), never a
  held pointer.
- Verification targets (user can eyeball in-game): Woodcutter's House 2, Gatherer's Hut 4,
  Gatherer's House 2, Hunter's House 2, Improved Warehouse 3 seed storage.
- Ride-along observation: log noProduction map ADD/REMOVE transitions with timestamps to start
  mapping its semantics.

**Effective vs physical sharing (user insight 2026-07-15):** Off (rank 3) is also the PLAYER'S
manual dedication lever — e.g. iron ore and iron bloom share a warehouse container type; building
two such containers and turning ore off on one, bloom off on the other, makes each effectively
dedicated, unable to hog the other. Therefore container dedication is NOT purely physical:
`effective accepted set = physical accepted set − items whose row on that container is Off`.
The hog classifier must evaluate EFFECTIVE sharing, recomputed per poll (Off states change live);
the probe's physical map is the stable substrate, rows' rank supplies the Off overlay (already
captured per row — no new reads).

**Hog v3 (post-probe):** hog and victim must share a verified EFFECTIVELY-shared container that
accepts both; victim must be structurally demanded; hog must not be.
**Eviction policy (user-decided 2026-07-15):** evict ONLY when a demanded item is actually
blocked, and only JUST ENOUGH to meet the blocked item's computed need (deficit-based formula
from the demand model). Flat reductions are retired — v0.8.0–v0.9.1's "target = MinQuota +
headroom" behaved as a ~90% flat cut and is exactly what not to do.
**Separate class — producer-output blockage** — for dedicated-container
producer stations: output container full of an undemanded item while a demanded sibling output is
stalled/noProd (the Hunter's House bone case *if* the probe shows its storage is dedicated;
if shared, it's a plain hog). Same remedy (drain/evict, deficit-sized), honest evidence either
way.

## Lever map (constraints now known)

| Lever | Status | Constraints learned |
|---|---|---|
| Tier write (SetPriority + HostUpdateTasks) | proven 2026-07-15 | Primary flow lever. Winner-take-all service order. NEVER touch rank 3 = Off (player intent). Rpc_ChangeTaskPriority is inert (dead-end). |
| Quota write | proven 2026-07-14 | Intake shutoff / floor sizing only; raise on unmet rows is a no-op; defaults are quota=max everywhere. |
| Evict (DropItem to ground) | proven 2026-07-15 | Last-resort space recovery; co-op client replication ⚠️ unverified. |
| Task creation | unresearched | Closes distress→structure loop (Phase 3+); see research status above. |

All writes ledgered (write-ahead, restore on load) — unchanged policy.

## Step-2 findings (v0.10.0 DemandGraph, in-game-verified 2026-07-15)

47/51 crafting tasks matched across 5 CraftingStations (410 blueprints), 23 structural inputs.
Table read TRUE on audit: Feathers structurally demanded (55, three arrow tasks) despite zero
flow — the intent-vs-flow layering works; Bone Fragments absent (legit eviction target); Seeds
absent (farming category, correct v1 scope). Three user-confirmed game facts (2026-07-15):

1. **Intermediates break the flat name-join — demand must propagate transitively.** The bloomery
   makes Iron Bloom (ore+coal); the metalworker HEATS it into 'Hot Iron Bloom' and crafts from
   that. Recipes reference the intermediate, so flat wantedBy terminates at Hot Iron Bloom and
   never reaches Iron Bloom/ore/coal. Fix (step 3): transitive BOM propagation — when a demanded
   input itself has a blueprint (in the union of ALL stations' tables), propagate its structural
   qty down to its own inputs, recursively, cycle-guarded. This is also the skeleton of the
   long-term chain-capacity rollup.
2. **Non-blueprint transforms exist (curing rack).** Curing happens at the leatherworker's
   Curing Rack: leather items are placed in it and process into cured variants over time. There
   is NO blueprint — hence Cured Leather Hide/Pelt/Heavy Pelt tasks are unmatched at their own
   station and a blueprint-union fallback will NOT resolve them (it WILL resolve Rope @ Workshop
   Pit 3, whose blueprint exists at Workshop House 4). Demand for raw hides implied by
   cured-item tasks therefore needs a small TRANSFORM MAP (cured X ← raw X), hardcoded or config.
   The rack DOES present as a container (user-corrected 2026-07-15 vs an earlier recollection —
   screenshot: 10-slot "MEDIUM ITEMS" grid, in-progress items show progress bars), so rack stock
   is likely reachable by the standard ItemContainerComponent walk — but the leatherworker is a
   CraftingStation, OUTSIDE the current WarehouseClassList scan scope, so visibility is
   ⚠️ unverified until a probe pass covers crafting stations. On the "MEDIUM ITEMS" UI header
   (user-clarified 2026-07-15): item SIZE CLASSES exist and the UI groups slots by them, but
   acceptance is NOT size-based — the curing rack shows "MEDIUM ITEMS" yet accepts only curable
   hides/pelts, while a transport cart's medium slots accept any medium item. Size class is a
   per-container display/slot dimension; the authoritative accepted-set remains
   `GetAcceptedItemTypes()`/`CanStoreItemType()` per container. Size categories: noted as
   existing, NOT load-bearing for this design.
3. **Batch recipes: task quota counts OUTPUT ITEMS.** A '10x Iron Tipped Arrow' task incremented
   twice shows quota=20 (2 crafts). Manifest input qtys are per CRAFT, so
   `structuralQty = inputQty × quota` OVERSTATES by the batch factor. Correct formula:
   `inputQty × ceil(quota / outputPerCraft)`. outputPerCraft needs a source: check
   `CraftBlueprintInfo` for a produced-quantity member (Cecil, at step-3 build time); fallback =
   parse the `Nx ` name prefix.

## Step 3 — classifier rebuild (PROPOSAL, not yet approved to build)

New dry-run classifier (v0.11.0, read-only, replaces BudgetPlane's rules) on the two verified
layers. Per poll: build the physical container map (interaction.Container per row — probe-proven,
~35 ms full; effective accepted set = physical set − Off rows, recomputed per poll), join
structural demand by item name via DemandGraph (+ transitive propagation + batch correction +
transform map; rebuilt on world load, F9, and a slow cadence — tasks change rarely).

Classes (each proposal line carries its full evidence):
1. **HOG** — X and victim Y effectively share container C; Y structurally demanded AND blocked at
   C (zero free slots, no partial Y stack); X not structurally demanded (transitively). Proposal:
   evict the SMALLEST number of whole X stacks from C yielding
   `ceil(Yneed / C.GetStackSize(Y))` slots, where `Yneed = min(structural deficit, Y's unmet row
   quota at the structure)` — the user-decided "just barely enough".
2. **SHADOWED** — item structurally demanded; destination row unmet + room, rank Med/Low (never
   Off); stock piled at producer self-storages; flow static. ONE destination per item.
3. **PRODUCER-BLOCKAGE** — producer output container 100% full of undemanded X while a demanded
   sibling output is stalled/noProd (for DEDICATED-container producers; shared-container cases
   are plain HOGs — e.g. Hunter's House bones+meat).
4. **OFF-CONFLICT** (informational) — item demanded + piling, but its row here is player-Off.
5. **STARVED** (informational) — demanded, already High, unmet, source exists, not filling.
Flow (net/churn) sizes headroom and confirms; distress (noProd/complaints/storage-full) ranks
proposal urgency. Still zero writes — same audit loop as steps 1–2.

Decisions (closed 2026-07-15):
- ~~Transform map~~ REFRAMED then resolved: "transforms" were defined by data representation
  (recipe undiscoverable), but wood shafts prove bench-process crafts ARE ordinary blueprints
  (`3x Wood Shaft ← Hardwood Long Stick` matched in step 2). The cured-hide gap is therefore a
  LOOKUP-SCOPE gap, not a category: step 3 widens DemandGraph to (a) a blueprint-UNION fallback
  across all read tables, and (b) walking ALL structures for `CraftInteraction` tables directly
  (not gated on a CraftingStation component — also brings Bloomstation/cooking recipe sources
  into reach). A hardcoded map returns ONLY if cured items still have no recipe anywhere.
- Eviction sizing (user-approved): `min(structural deficit, victim's unmet row quota)` when
  structural demand is computable, CAPPED at `MaxSlotsPerCycle` (config, default 1–2 slots per
  proposal cycle); pure incremental (one slot per cycle, re-evaluate next poll) as the automatic
  fallback when the victim's demand is distress-only (no magnitude). Dry-run lines log both the
  computed target and the cap so sizing is auditable before arming.

**Eviction is RESPONSE-GATED, never cadence-gated (user-identified pathology, 2026-07-15):**
a per-cycle slot cap on a free-running proposal cycle is a dump rate in disguise (a 1 s cycle
would empty the container before production/hauling of the victim could respond). State machine
per (container, victim): evict ONE quantum → LOCK the pair and observe → victim inflow observed
AND container re-blocks ⇒ next quantum justified; response window expires with NO inflow ⇒ the
freed slot sat empty, eviction was not the binding constraint ⇒ STOP + verdict + lockout (never
continue). Eviction is the one lever with NO REVERT (ground drops don't come back), so its
failure mode is the strictest: one quantum, observe, stop on silence. Reuses the proven
ineffective-cycles → verdict + lockout pattern (rank/clog controllers, fire-verified). Net:
eviction rate self-paces to the victim's real replenishment rate; cycle frequency drops out.

**Actuation timing (user-flagged variable, 2026-07-15):** every lever cycle has an EVALUATION
window (evidence persistence before acting), a RESPONSE window (how long to wait before judging
effect), and revert timing — and the right values depend on the process being corrected (a
hauling response is ~13 s — Phase 2b datum; a multi-link production chain responds far slower).
Phasing: v1 = per-class config constants (the proven ClogController pattern); every ARMED cycle
must additionally LOG its per-item response latency (time-to-first-response, time-to-target) so
an empirical latency base accumulates; chain-rollup era derives windows from measured link
process times (the "hammer process time" value) instead of constants. Faster/slower lever
cycling becomes a tunable, eventually per-item.
- ~~Anything else the classifier must never touch besides Off rows?~~ ANSWERED 2026-07-15:
  **crafting-station task quotas are player-declared MATERIAL FLOORS** ("always have 6 iron
  plates" = plate quota 6; worker maintains local stock ≥ quota) — they are the demand INPUT
  surface and must NEVER be written by the mod. Discriminator between deliberate and generated
  lines on crafting stations: auto-listed recipe lines sit at quota 0 (Armorsmith x0 lines in
  the step-2 dump); nonzero quota = player intent. (Opposite of storage rows, where quota
  defaults to max and carries no intent.) This also means Phase 3 likely needs NO custom
  floor-config surface — the game's own crafting-quota UI is the floor declaration. Refinement
  for the chain-rollup era: structural demand = standing floor (quota) + active deficit
  (quota − local stored); v1 uses the standing floor as magnitude (user-approved rough v1).
  Further untouchables: deferred to playtest observations (user, 2026-07-15).

## Proposed sequence (each step gated on user approval)

1. **v0.9.2 container probe** (read-only): container inventory + accepted-types API verification +
   noProduction semantics observation. Test cost: load, F9, exit.
2. **Structural-demand spike** (read-only): BOM/task-graph extraction → per-item `wantedBy` table
   on F9, starting with crafting/cooking recipes (proven path), then farms/ammo/fuel/food
   incrementally. Test cost: load, F9, exit; user sanity-checks the wantedBy table.
3. **Demand model v2 in BudgetPlane**: structural gate + flow sizing + distress urgency; hog v3 on
   the container map; producer-output blockage class. Dry-run re-audit with user.
4. **Arming design** (separate proposal): which proposal classes may auto-apply, rails, duty
   cycles — only after the dry-run proposals read as consistently sane.
5. **Phase 3 / task creation research** — after the read-side is trustworthy.

## User decisions (2026-07-15, all four open questions answered)

1. **Magnitude:** task quota × recipe qty is enough for v1; long-term add the chain-capacity
   rollup (time element) described in the structural-demand section.
2. **Eviction:** only when something demanded is actually blocked, sized to just barely meet the
   blocked item's computed need. No flat reductions ever.
3. **Off rows:** propose-only, never override — AND Off doubles as the player's manual
   container-dedication lever (ore/bloom two-container example), hence the effective-sharing rule
   in the container section.
4. **Coverage:** "recipes" = crafted items (armor/weapons/tools); with that reading the
   structural category list (crafted items, food, seeds, ammo, fuel, build materials) is complete.
