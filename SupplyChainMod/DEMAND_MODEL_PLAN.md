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
**Eviction policy (SHIPPED v0.12.0–v0.13.0 — supersedes the deficit-based note):** evict ONLY when a
demanded, NON-surplus item is actually blocked in a SHARED container by a SURPLUS co-resident (HOG).
Sizing is DEMAND-GROUNDED, not victim-deficit: reduce the surplus hog toward
max(CoProductFloorSlots × stack, its own demand). Flat reductions stay retired.
**"Producer-output blockage" — RETIRED as invalid (2026-07-15).** The idea (a full DEDICATED
container of undemanded X stalls a demanded SIBLING output elsewhere at the same station) rests on a
false premise: items in SEPARATE containers cannot compete for slots, so one cannot block another
(user-confirmed in-game). The Hunter's House bones case turned out SHARED (bones+meat) → a plain HOG,
which is what shipped. **This misleading definition drove a wrong build** — "BLOCKAGE" was reused for
a completely different (and correct) meaning in v0.12.0: a routing diagnostic (see the classes below).

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

Classes (SHIPPED v0.13.0 — each proposal line carries its full evidence):
1. **HOG** (eviction) — a SURPLUS, static item crowds a demanded, NON-surplus, blocked item in a
   SHARED container. Surplus = settleStored > structural × SurplusFactor (config 2.0) — the hog gate,
   replacing binary demanded/undemanded (which misfiled over-supplied-but-demanded items like Resin).
   Reduce the hog toward max(CoProductFloorSlots × stack, its own demand), paced at MaxSlotsPerCycle.
2. **BLOCKAGE** (diagnostic) — a demanded, NON-surplus material SITTING (stored>0) in a full, static
   container while downstream BOM consumers rely on it. Routing/priority problem (lever tier/priority),
   NOT eviction. One line per item, cause token no-route / destination-full / priority-shadowed.
3. **SURPLUS** (the hog-eligibility pool made visible — never mere information) — a surplus item
   over-produced but not currently crowding a victim yet.
4. **SHADOWED** — item structurally demanded; destination row unmet + room, rank Med/Low (never
   Off); stock piled at producer self-storages; flow static. ONE destination per item.
5. **OFF-CONFLICT / STARVED** (informational) — demanded+piling but Off here; demanded+High+unmet+source.
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

## v0.14.x — SURPLUS v2: flow-aware surplus + riders (v0.14.1–v0.14.2 in-game-verified 2026-07-16)

Replaces v0.13.0's single magnitude predicate (`IsSurplus`) with two orthogonal predicates. Closes
former open items 1 (base-material keep-target) and 2 (demand-blind window); rides item 4 (clog
retirement) plus the per-container Off fix found in the 2026-07-16 code audit.

- **OverTarget(item)** = settleStored > keepTarget, keepTarget = RATCHETED structural ×
  SurplusFactor. Ratchet = session high-water mark of StructTotal (task-completion dips must not
  condemn stock; errs toward keeping; cleared on world-leave).
- **Verdict(item)** — directional and threshold-free (per-item rate constants rejected: rates
  differ by resource — user decision 2026-07-16). Over the last InertMinutes of AWAKE time:
  - **ALIVE** — any observed DECREASE of the settlement-wide total (consumption or in-flight
    hauling; either means the item participates in flow). Never surplus. Re-evaluated every poll:
    one decrease instantly rescues a dead/growing item — dead is NEVER a permanent judgment.
    **Consumption memory (v0.14.2):** a REAL observed decrease keeps the item ALIVE for
    6 × InertMinutes of awake time (internal multiplier, no config key), not just one window.
    Why (v0.14.1 test finding, 2026-07-16): accumulation is continuous but consumption is
    EVENTFUL — a base material's consumers run on task cadence, so a window short enough to
    condemn a dead pile misses slow consumers (Fibers read GROWING: no fiber-consuming craft ran
    within the window while gatherers trickled a few in; same pattern on mushrooms/sticks/eggs).
    The load-time "assumed alive" seed still expires after ONE window (warmup unchanged), and
    never-consumed co-products (bones) are never rescued by it.
  - **GROWING** — increases only (co-products arriving with no consumer: bones, resin trickle).
  - **DEAD** — no movement at all.
  - AWAKE gate: verdict clocks advance only on polls where ≥1 item's settlement total moved, so
    vanilla night/sleep, pauses, and menu time age nothing. Behavior-adaptive — correct for both
    vanilla and DVN sleep patterns with no mod detection (user requirement 2026-07-16).
  - Warmup: at world load every item initializes "assumed alive" → no surplus/HOG verdict can
    exist until InertMinutes of observed awake inactivity. This closes the demand-blind post-load
    window (former item 2) with no separate gate.
- **Gate reassignment:** hog eligibility + SURPLUS class = OverTarget AND (DEAD or GROWING);
  HOG-victim neediness = NOT OverTarget; BLOCKAGE/SHADOWED/pass-3 exclusions = OverTarget.
  Flow-awareness applies ONLY to "what may be evicted"; "what deserves rescuing" stays
  magnitude-based — preserving v0.13.0's verified false-hog suppression (seeds→Fibers stays gone).
- **Config:** user-facing tuning = SurplusFactor alone (user decision 2026-07-16); InertMinutes
  (default 20, awake-minutes) is the only other knob. The draft's ActiveChurnPerMin/GrowthNetPerMin
  are DROPPED (never built); orphaned DemandDrainPerMin/DemandChurnPerMin keys deleted.
- **Riders:** clog-detector retirement (Clog/STALL toast vocabulary deleted; storage-full registry
  becomes a `distress=storage-full` severity-boost tag on HOG/BLOCKAGE/SHADOWED lines; forge
  harness kept; EnableClogController default → false); per-container Off overlay (HOG effective
  set + BLOCKAGE stuck-scan use per-(container,item) row rank, not structure-level MinRank — an
  Off'd container is player-frozen, not stuck); DemandGraph zero-match rebuild retry at 60 s
  (world-streaming race).
- Side effect: actively-planted seeds show decreases → ALIVE → protected. True farming demand
  modeling (open item 1 below) is still required before arming seed eviction (seasonality).

## Arming design — failure-mode → resolution routing (case-grounded, user-reviewed 2026-07-16)

Grounded in the v0.14.2 verification run: every classifier class fired at least once, and the
~30 s background polls caught transients the F9 snapshots never showed. Each failure class routes
to exactly one resolution path:

| Failure mode | Real-world meaning | Lever | End state |
|---|---|---|---|
| HOG | surplus dead/growing stock crowds a demanded item in a shared container | ground-drop eviction, response-gated quantum machine (spec above) | victim flows; hog held at floor |
| BLOCKAGE priority-shadowed / SHADOWED | goods pile at producer while the receiving warehouse row idles at Med rank | tier bump (SetPriority + HostUpdateTasks), hold, revert | haulers service the row; pile drains |
| BLOCKAGE destination-full | every destination row for a demanded item at capacity | check destination containers for a HOG (→ eviction case); else advisory | player builds storage / hog evicted |
| BLOCKAGE no-route | no storage row anywhere accepts the item | advisory only (config/build problem) | player adds storage/allotment |
| SURPLUS | over-produced, crowding nobody | NEVER acts — eviction-eligibility precondition only | escalates to HOG if it crowds |
| STARVED | demanded, High rank, room, source stock — still static | advisory only (labor/logistics-bound; no lever fits) | player adds haulers |
| OFF-CONFLICT | demanded but row is player-Off | advisory only (Off = player intent, locked) | player decides |

### Case evidence (v0.14.2 run 2026-07-16 unless dated otherwise)

1. **HOG 'Bone Fragments' vs 'Raw Red Meat' @ Hunter's House 2** (persistent ~25 min of polls):
   the proposal line already carries every input the response-gated eviction machine needs
   (surplus slots, per-cycle quantum, keep floor, victim row deficit, distress tag as the
   success signal). Ready to arm once the rails below exist; solo-only until co-op drop
   replication is verified.
2. **Transient HOG 'Wild Egg' vs 'Berries' @ Gatherer's House 2 (ONE 30 s poll).** HEADLINE
   (user): food demand is at best partially modeled — eggs feed pie tasks sitting high on the
   cooking priority list (crockpot-recipe demand read 33; population-eating demand is invisible),
   so dumping eggs would have been actively harmful. Secondary lesson: one-poll findings must
   never actuate (rail 1).
3. **Tier cases:** 'Rope' + 'Linen Thread' @ Improved Warehouse 3 (shared container, both
   demanded, cause=priority-shadowed, rich downstream lists) — the Phase 2c coal pattern, the
   tier lever's home turf. 'Berries'/'Mushrooms'/'Onion': BLOCKAGE(priority-shadowed) at
   Gatherer's Hut 4 + SHADOWED (propose tier 1→0) at Improved Warehouse 3 are two classifier
   views of ONE failure → merge rule (rail 2).
4. **destination-full chain: 'Leather Hide' → 'Cured Leather Hide'.** Cured hides (54, demand 29)
   legitimately fill their container — consumers not keeping pace; upstream raw hides jam with
   distress=storage-full. No honest lever → emit a CHAIN advisory. Full diagnosis (which link is
   slow) needs chain-capacity rollup + crafting-station container visibility (the curing rack is
   outside the current scan scope — pre-arm blocker 4).
5. **SURPLUS 'Resin' verdict=growing (2068 vs demand 3):** the tempting quota-clamp would confine
   resin to the Woodcutter container it SHARES with Fibers/Pine Cone/Reed Seeds — manufacturing
   a producer HOG. SURPLUS therefore stays permanently report-only — it IS the hog-eligibility
   pool made visible; escalation to HOG is the designed remediation path.
6. **Equipment SURPLUS noise (~50 of 56 lines: tools/weapons/clothes ×1–9, keepTarget=0):**
   structural demand is blind to loadout demand. Near-term SHIPPED (v0.15.0 rider B):
   MinSurplusSlots slot-footprint floor (flat MinSurplusUnits scrapped — stack sizes are
   per-(container,item)), default 10 since 2026-07-16. Lead (user, not high priority): equipment
   need is derivable — villagers assigned per task type × required equipment × observed breakage
   rate (4 woodcutters ⇒ ≥4 axes at the axe-breakage rate); the game itself raises an
   equipment-unavailable warning (LoadoutsComplaint — ComplaintWatcher already sees it;
   missingItems carries no item name, native class only — known limitation).
7. **STARVED fired for one poll — item UNKNOWN: starved/off classes emitted NO proposal line, only
   summary counts** (found 2026-07-16). Rider SHIPPED v0.15.0: both now emit item-named lines.
   The save has a known ore bottleneck (user), so ore/bloom is plausible but unconfirmable from
   this run.
8. Positive control observed live: 'Leather scraps' entered SURPLUS (300 vs demand 92), then a
   real consumption event granted ALIVE (consumption memory) and it dropped off mid-run — the
   v0.14.2 rescue mechanism working beyond the Fibers case it was built for.

### Slow-carry contention (user-flagged 2026-07-16; diagnosis-hard, keep on the radar)
The mine-hut case (Phase 2e in docs/mods/supply-chain.md → Design Direction): Large Rocks and
Iron Ore both High → miners alternate a couple of ore with one slow giant-rock carry, collapsing
ore throughput. The LEVER is proven (tier-DOWN the bulky competitor's row so it stops competing);
the DIAGNOSIS is the hard part: nothing jams (both items flow, no class fires) — the signal is a
throughput RATIO, not a stall. Leads: per-trip carry observation; or, once chain rollup exists,
flow-rate comparison of co-gathered items vs their demand gaps. Genuinely impactful (user); may
stay out of scope on difficulty, but the design must not preclude a future "de-prioritize
competitor" advisory/lever.

### Arming rails (user-reviewed 2026-07-16)
1. **Evidence persistence:** a finding enters the actuation queue only after N consecutive polls
   (suggest 3–4 ≈ 2 min); kills the one-poll transients (cases 2 and 7).
2. **Case identity = item, with merge:** BLOCKAGE(priority-shadowed) + SHADOWED on the same item
   form one case, one action, one cooldown.
3. **Chain-aware case management (user rule 2026-07-16):** one-action-per-item must NOT serialize
   fixes within one BOM chain. If steps 2 and 3 of a chain both have active problems, never fix
   step 2, revert on success, then fix step 3 while step 2 re-jams behind it. Cases sharing a
   chain get coordinated handling: concurrent levers allowed (within the budget), and — the
   critical part — an effective fix is NOT reverted while other active cases remain in the same
   chain; its revert defers until the whole chain reads healthy. Chain membership comes from the
   existing BOM graph (wantedBy walk).
4. **Response-gated everything:** every lever cycle = act → observe → justify-or-stop, per-class
   response windows (hauling ~13 s datum; production chains slower), measured latency logged
   (actuation-timing section above).
5. **Actuation budget:** ≤1 eviction case + 1–2 tier cases active settlement-wide; queue ranked
   by distress tag, then victim demand size.
6. **Untouchables carry over:** Off rows, crafting-station quotas (player floors), militia-alarm
   lockout, warmup + demand-built gate before any armed action, write-ahead ledger on every
   write.

### Pre-arm blockers (dependency order) + arming order
1. **Case layer build (dry-run):** persistence gate + item/chain case merge + case lifecycle log
   lines, with riders (starved/off emit proposal lines; SURPLUS reporting floor). The natural
   next build — pure code on top of the current classifier, testable dry-run.
2. **Food demand modeling** — blocks arming eviction of any food item or seed (case 2);
   seeds additionally read structural=0 (FarmingStation crop demand unmodeled) and planting is
   seasonal, so ALIVE protection alone is not sufficient to arm seed eviction. Modeling priority
   (user, 2026-07-16): the hunting/gathering→cooking pipeline outranks seeds/farming→cooking.
   As a food SOURCE a farm is a more deterministic duplicate of the gatherer huts (it just grows
   more of what gatherers pick up anyway), so model raw-food demand (cooking-recipe inputs +
   population eating) against gathered/hunted items first; farming stays first-class only as the
   SEED CONSUMER (the fix for seeds reading structural=0 is FarmingStation crop selections, a
   separable sub-task).
3. **Co-op drop replication test** — blocks eviction outside solo.
4. **Crafting-station container visibility** — blocks chain advisories (case 4); not needed for
   tier or HOG arming.

Arming order: **tier first** (whole loop proven, reversible, ledgered), **eviction second**
(solo-only), advisories throughout.

### Player-facing HUD panel (stretch goal — user idea + mockup, 2026-07-16)
Far-future, after everything works: an in-game "Supply Chain" HUD panel where each OPEN case
appears as a card — problem sentence, the concrete proposed change (e.g. `bone_fragments
1250 → 50`, `long_sticks priority Med → High`), and per-card controls: approve (apply the lever),
reject (dismiss + lockout), snooze (defer). Panel header carries an "auto-approve" toggle = the
armed mode. This defines a THIRD operating mode between dry-run and armed: **manual-approve** —
the mod proposes, the player clicks. The case layer is deliberately the substrate: a case's
lifecycle + proposed-action payload is exactly what a card renders, so nothing needs redesign to
add the panel later. Mockup vocabulary (BLOCKED/STALLED/STARVED cards) is illustrative — real
cards use the routing-table classes. ⚠️ UI research unresearched: toasts are proven, but panel
injection into the game HUD is unexplored territory (research item when the time comes; keep
expectations gated on that spike).

## Food-demand probe findings (2026-07-16, Cecil + v0.15.1 live log — build not yet approved)

Probe pass for pre-arm blocker 2 (user priority: hunting/gathering→cooking first; farming =
seed consumer). Game facts recorded in architecture.md (Cooking Station Pipeline → "Cooking
recipe classes"; Settlement Hauling → "Gatherer/Hunting/Fishing/Dining huts are
ResourceStorage"). Design implications:
1. **Cob oven is free.** Pies/cheese are `CrockpotRecipeInfo` — already fully read (19/19
   Cooking House + 3/3 Cheesemaker matchedViaCooking in the v0.15.1 run). No work needed.
2. **Barbecue is the one real coverage gap.** Campfire products (Cooked Red/Fish Meat, Cooked
   Mushrooms ×3 — 9 tasks) are plain `CookingRecipeInfo` → all unmatched before v0.16.0. Fixed:
   BlueprintInfo `parts` fallback in TryMatchCookingRecipe — confirmed in-game 2026-07-16, parts
   populated for all 9 barbecue AND the 3 curing recipes (matchedViaParts=12,
   matchedViaTransform=0; TransformMap now a redundant safety net).
3. **Table ('?') requirements are resolvable** via `tableConfig.GetItemsList()` /
   `_occurenceTableManifest`. Any-of semantics DECIDED (user 2026-07-16): protection-only — every
   accepted item counts as demanded for eviction PROTECTION (never selectable as a HOG), but an
   any-of requirement creates hard tier-bump demand for no single item (demand would otherwise
   inflate across the whole table).
4. **Gathered-item supply lever confirmed, no new pipeline needed.** Gatherer huts are plain
   `ResourceStorage`: supply levers are their per-item quota rows + rank — the warehouse scan
   already reads these, and BLOCKAGE cause=priority-shadowed at 'Gatherer's Hut 4' already names
   them as tier targets. Hunting = HuntingStation's unique prey-filter task (gear/proficiency
   gated per entry).
5. **Seed demand is an interpretation rule away.** FarmingStation production tasks already
   enumerate seed items + quotas (16/farm in the run, currently all 'unmatched') → rule: a farm
   task = structural demand for its seed item. The crop-production edge (Beetroot Seeds →
   Beetroot) stays deferred — farms duplicate gatherer supply (user call 2026-07-16).
   KNOWN UNMODELED CONSUMER (user, 2026-07-16): animal-pen feed — smolkrs eat seeds at the
   Smolkr Pen (`AnimalPen : Kennel`; its feed selection is a FILTER task, correctly skipped by
   the production-task reader; its one production task 'Smolkr' is unmatched). Later pens' diets
   unknown (user hasn't progressed there). Coverage today is flow-protection only (an observed
   decrease → ALIVE for 6×InertMinutes), which is consumer-agnostic; structural pen-feed demand
   is DEFERRED until the user reaches later pens — probe AnimalPen's feed config/filter then.
   Accepted seed posture (user): keep working stock (farm demand × ratchet), let genuinely
   hoarded mountains (respawn-mod gathering can reach thousands) become hog-eligible for
   eventual cleanup — the ALIVE-vs-DEAD split already encodes this.
6. **Population eating: model as measured drain, not first principles.** No per-item nutrition
   exists on ItemInfo; eat pacing lives in `VillagerDiningInteractionConfig`. v1 stance: the
   metabolic layer's measured per-item drain rates ARE the eating term; note DVN's
   `HungerRateMultiplier` scales villager hunger drain and thus functions as a scalar on the
   entire food pipeline (user note 2026-07-16).
v0.16.0 build APPROVED (user 2026-07-16, all three riders): barbecue BlueprintInfo-parts
fallback (+ probe line; TransformMap stays as the safety net — user note: the barbecue is
mechanically a transform like the curing racks, and curing items are indeed the SAME plain
CookingRecipeInfo shape, so the parts path auto-derives both if populated); table-req resolution
with protection-only any-of semantics; farm-task seed demand. User-confirmed chain (2026-07-16):
the barbecue worker pulls ANY raw food from storage and cooks by the station's per-item task
priority; the downstream consumer is the tavern (DiningStation), which stocks cooked food for
villagers to eat — so with the barbecue edge added, gathering/hunting→barbecue→tavern→eating
becomes structurally connected end to end.

## Status & open next steps (updated 2026-07-16)

SHIPPED + in-game-verified: sequence steps 1–3 (v0.9.2 probe → v0.10.0 DemandGraph → v0.11.x
classifier v3 → v0.12.0 HOG sizing + BLOCKAGE rewrite → v0.13.0 IsSurplus/SURPLUS/cause tokens)
and SURPLUS v2 (v0.14.1 tested 2026-07-16 with the Fibers bursty-consumption finding; v0.14.2
consumption-memory fix in-game-verified 2026-07-16 — Fibers absent from SURPLUS, all markers
passed, zero errors). Still 100% dry-run. Details/version-history in docs/mods/supply-chain.md.

Case layer (pre-arm blocker 1) SHIPPED: v0.15.0–v0.15.1 in-game-verified 2026-07-16 — core spec
passed (opens at exactly 4/6, bones HOG opened once, merge/chain tags + RESOLVED observation
summaries live, zero errors); tuning: MinSurplusSlots default 4→10, CaseClosePolls kept 4 (churn
accepted). Recipe + version history in docs/mods/supply-chain.md.

Open next steps (arming design above approved 2026-07-16):
1. **Food demand modeling** (pre-arm blocker 2) — hunting/gathering→cooking pipeline first,
   farming demoted to seed-consumer role (user call 2026-07-16; see pre-arm blockers below).
2. **Arming implementation** — tier first, per the routing table + rails above.
3. **Phase 3 / task creation research.**

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
