# SupplyChainMod — Mod 26

**Status:** WIP v0.14.2 — SURPLUS v2 flow-aware classifier (ratcheted keep-target ×
ALIVE/GROWING/DEAD verdicts) + clog-toast retirement + per-container Off fix, per
`SupplyChainMod/DEMAND_MODEL_PLAN.md` → "v0.14.x — SURPLUS v2". v0.14.1 in-game-tested 2026-07-16
(9/10 expectations passed; Fibers-GROWING finding, see Dead-Ends); v0.14.2 consumption-memory fix
⚠️ pending in-game test. Still 100% dry-run/read-only. Open next steps: farming demand modeling,
arming design — see DEMAND_MODEL_PLAN.md. Dev tool NOT for Nexus

**Goal:** Phase 0 read-only diagnostics + Phase 1 demand-driven priority actuation + Phase 2a
warehouse diagnostics + Phase 2b quota-raise actuation. Phase 0 observes game state (villager
complaints, settlement issues, task data, inventory composition), never writes; findings absorbed
into architecture.md. Phase 1 implements automatic crafting-task priority bumps triggered by
complaint-based demand, with automatic revert and duty-cycling. Phase 2a provides read-only
warehouse/storage-drain diagnostics and clog detection. Phase 2b implements quota-raise actuation
with hauler-response verification and a permanent clog-forge regression harness.

## Design & Components

**Phase 0 diagnostic modules (all v0.1.0→v0.1.3 in-game-verified 2026-07-12):**

1. **ComplaintWatcher** — `VillagerSocial.AddComplaint`/`RemoveComplaint` postfixes,
   transition-based logging (ADD-new / CLEARED with after=Ns + readds=N), periodic stats line
   (active/newAdds/reAdds/clears/unmatchedRemoves/suppressed, only when nonzero). Gated by config
   `[Diagnostics] PatchVillagerComplaints` (safety valve: set false if plugin-load crash). Filter
   ignores configured complaint messages (default "feelUnsafe") via exact string key match +
   per-world id map deduplication.
2. **WidgetWatcher** — polls `SettlementIssueTrackerWidget.Instance` for deltas in six
   aggregation maps; fire-verification postfixes. Confirmed in-game: maps populate under real
   problems (storageFull cycles per-workstation as haulers drain; noProd holds constant with item
   + needed-by string; others cycle or stay 0).
3. **TaskDump** — F9 hotkey (typing-guarded) + auto ~60 s after world load. Per-station output:
   workstation class, inventory count, per-task item/quota/priority (Int32 = 0-based rank index),
   `quantityRange`, FiltersTaskData per-item tiers.
4. **CompositionSweep** — rolling [Perf]-instrumented scan via `ItemCollection.GetItemInfos()` +
   `GetItemQuantity()`. Per-station 0.2–5.1 ms, full settlement 6–20 ms (31 stations) to 10.8 ms
   (61 stations under invasion). No performance ceiling hit.
5. **BomDump** — table-level blueprint/recipe dump via `CraftInteraction._localBlueprints` +
   per-blueprint manifests. Logs per-task `KnowsBlueprintForItem` probe results (all false/null —
   dead-end confirmed; dictionaries are the reliable path).

**Phase 1a actuation spike (v0.2.0→v0.2.3, all in-game-verified 2026-07-12):**

- **Hotkey-triggered boost/revert** (F10, configurable via `[Phase1Spike] SpikeHotkey`, default
  F10): select a station (via config `SpikeStationFilter` or auto-select first ≥2-task
  CraftingStation, else CookingStation, else any ≥2-task) and task (via config `SpikeTaskIndex`
  or auto-select last non-pinned task, index -1 = auto). Boost = move task to rank 0 (via
  `RemoveAt`/`Insert` into live `Workstation._taskDatas` + `RefreshTaskDataPriorities()` renumber
  + `HostUpdateTasks()` on the GetComponent-probed `NetworkCraftingStation`); revert = move back
  to original rank. Pinned tasks rejected as boost targets (both modes; revert/restore paths
  exempt).
- **Write-ahead crash-restore ledger** (`BepInEx\config\SupplyChainMod_ledger.dat`, pipe-separated
  format): logs every boost/revert, reads and restores stranded boosts at world load (verified
  in-game 2026-07-12).
- **Authority gated** (`ws.HasStateAuthority`); fallback: `null NetworkObject.Runner` treated as
  solo and allowed, `IsSinglePlayer` accepted per config `[Phase1Spike] ForceAllowRefresh` (default
  false; MineRefresh host-gate pattern).
- **Config reference** (`[Phase1Spike]`): `EnableSpike=true`, `SpikeHotkey=F10`,
  `SpikeStationFilter=""` (empty = auto-select), `SpikeTaskIndex=-1` (auto = last non-pinned),
  `SpikeNewPriority=1` (⚠️ inert since v0.2.3; rank-move actuator replaced value writes),
  `SpikeAutoRevertSeconds=120` (auto-revert timeout, 0 = manual revert only),
  `ForceAllowRefresh=false` (allow null/solo contexts).

**Phase 1b controller (v0.3.0→v0.3.5, all in-game-verified 2026-07-12):**

- **Complaint-driven demand model:** feeds `ItemComplaint` + `ItemManifestComplaint` payloads
  through `ComplaintWatcher.AddComplaint` postfix; per-item demand map tracks maturity, span,
  sightings (burst-tolerant — items re-created within maturity window resume span credit). Config
  gates: `DemandRetainSeconds=180` (complaint re-add = fresh sighting within window), optional
  `MinDemandSpanSeconds=30` (span gate = first-to-last sighting window threshold), `DemandMemoryMinutes=15`
  (maturity memory for re-created items); config key RENAMED `DemandStaleSeconds` → `DemandRetainSeconds`
  in v0.3.1 so new default beats stale cfg values.
- **Dry-run + arm toggle** (F11 toggles `[Phase1Controller] Armed=false` default; dry-run mode
  applies all gates/decisions but no actual boosts, fires watching toasts + verdicts diagnostically).
- **State machine per demand:** Pending → (hysteresis 45 s, freshness, <2 concurrent boosts,
  no militia alarm) → Boosting (rank 0 via shared `RankActuator`, ledger recorded) → hold 180 s →
  revert (demand-cleared early release: complaints stop during hold = boost succeeded) → Cooldown
  120 s. Re-boost eligible if demand persists; max 2 cycles → CAPACITY VERDICT + 15 min lockout.
- **Target resolution:** filter by station class (allow-list: CraftingStation, CookingStation,
  BloomStation), skip pinned/already-boosted/no-authority stations. **Already-top precondition
  (v0.3.3+):** skip candidates at/above target rank (EffectiveTopIndex = never boost above
  FiltersTaskData), AlreadyTop outcome fires strike 1 (120 s backoff) then strike 2 (VERDICT toast
  + CapacityLockout 15 min — diagnosis: "production limited by inputs/capacity, not priority").
  **StationBusy outcome (v0.3.5):** same-station demands get 20 s retry + truthful logs instead of
  no-task mislabel + 60 s backoff. Tie-break: StationBusy > AlreadyTop > None.
- **Demand watching toasts:** on new demand creation, one-off TryFindTarget peek (NOT in postfix,
  pure observability) — if craftable, toast `watching '<item>' — craftable at '<station>'`; already-top
  variant labels it truthfully; else log-only.
- **Militia-alarm lockout** (`Complaint.c_militia_alarm` message-key string match = raid active):
  90 s lockout during/after raid, no boosts.
- **Shared ledger + collision-safe registry:** `SpikeLedger` (write-ahead, same for spike +
  controller), `BoostedStationKeys` (per-station boost tracking, neither spike nor controller can
  conflict).
- **Config reference** (`[Phase1Controller]`): `EnableController=true`, `Armed=false` (F11 toggles
  live), `DemandRetainSeconds=180`, `MinDemandSpanSeconds=30`, `DemandMemoryMinutes=15`,
  `ControllerTickSeconds=5` (state machine polling).
- Per-demand status line (when active + ≤8 demands): lists maturity gate / span gate / sightings /
  maturity-credit state / craftable flag per demand.

**Phase 2a warehouse diagnostics (v0.4.0–v0.4.3, in-game-verified 2026-07-13):**

- **TaskDump extension** — per-task `ResourceStorageTaskData` probe: supplyOwner / taskMaxQuota /
  defaultTaskPriority / supplyAvailable / hasTaskContainer.
- **WarehouseWatch** (new module, read-only) — polls stations whose native class matches
  `WarehouseClassList` (default `ResourceStorage`): per-warehouse allotment table (item, qty,
  warehouse-wide stored, met=stored>=qty, rank, taskMaxQuota, supplyOwner, gathered=quota echo,
  room=GetTotalRemainingCapacity), one-time-per-world network-component probe
  (NetworkCompositeResourceStorage vs NetworkSimpleResourceStorage_<N>), met-flag transition logs
  between full tables (full table on F9 + first poll per world).
- **Dry-run clog detector** — fed by a storage-full registry (Add/RemoveStorageFullComplaint widget
  postfixes, strings only). Storage-full station → dominant-item composition scan → if share ≥
  ClogDominantSharePercent: VERDICT A (all matching allotments met → drain-boost candidate, delta
  = dominant count; duplicate rows grouped 'N×'), VERDICT B (no allotment task exists), or
  watching (unmet allotment). Watching cases escalate to a STALL diagnosis after ClogStallMinutes
  with an unchanged stored-count: room=0 → 'capacity-blocked'; room>0 → 'likely priority-shadowed'
  (logs rank). Toasts deduped per (station,item) by ClogRealertMinutes; log lines fire every poll.
- **Known gap (feeds Phase 2b preconditions):** VERDICT A does not yet split on room — run 2 showed
  allotment-met clogs with room=0 (Coal, Pike) are capacity problems a quota raise cannot fix; the
  room>0 check becomes a Phase 2b actuation precondition.
- Perf: steady poll 8.5–9.5 ms / 15 warehouses (30 s cadence); F9 full pass 66–107 ms.

**Phase 2b quota spike (v0.5.0–v0.5.2, in-game-verified 2026-07-14):**

- **QuotaActuator module — quota write path (NEW confirmed recipe, 2026-07-14):** writing
  `ResourceStorageTaskData.itemInfoQuantity.quantity` directly WORKS — readback-confirmed,
  immediately visible in the warehouse UI, and gameplay-effective (villager intake stopped when
  clamped, resumed when restored). Full recipe: write `quantity`, call
  `rst._RefreshQuantityRange()`, then `HostUpdateTasks()` on the network component obtained via
  `GetComponent<NetworkCompositeResourceStorage>()` on the warehouse's own GameObject (found
  there in-game; succeeded every call). `Rpc_ChangeTaskQuantity(taskIndex, newQuantity)` exists
  (Cecil) as an untried fallback — never needed. There is NO renumber/squash mechanism for quota
  (unlike priority's RefreshTaskDataPriorities). Ledger records every quota write + restore pair.
- **F7 micro-test mode:** auto-selects a (warehouse item-group, source station) pair —
  true-warehouse rows only (`storageSupply.taskContainer != null`; hut self-storage rows excluded);
  group
  considered "met" when warehouse-wide stored >= SUM of the item's rows' quotas; requires room>0
  (`GetTotalRemainingCapacity`) and a station elsewhere holding >= QuotaMinStationStock of the
  item; raises ONE row by delta = min(sourceStock, room, QuotaBoostMaxDelta); response watcher
  self-reports hauler latency, auto-reverts on target-met or QuotaSpikeMaxHoldSeconds timeout.
  Never fired a real boost yet in-game (both test saves too well-hauled: breakdown groups=323,
  hutRowsExcluded=130, met=17, room0=7, noSourceStock=10; near-misses all failed sourceStock<min
  with best stocks 6/2/2 on qty=1–2 tool/armor allotments). Non-blocking: its experimental purpose
  (hauler-response proof) was fulfilled by the forge loop; logs top-3 near-miss diagnostics when
  it finds no pair.
- **Shift+F7 forge mode** (`ClogForgeItem` config): clamps ALL rows of that item's group in the
  best-stocked true warehouse so the sum equals stored (base/remainder split), shutting villager
  intake to deliberately build a clog — the permanent regression harness. No auto-revert; Shift+F7
  (or F7) again restores every row and transitions into a read-only **drain watch** that
  self-reports hauler latency and a target-met/timeout verdict. Verified end-to-end in-game
  2026-07-14 (forged real storage-full complaints, hauler response 13 s, target met).
- **Ledger schema extension (backward-compatible):** entries now carry `Kind` (rank|quota) +
  `SupplyOwner` (10 pipe-separated fields); legacy 8-field lines parse as rank entries; quota
  entries reuse the OrigPriority/BoostPriority columns as orig/boosted QUOTA values; one ledger
  entry per clamped row, written BEFORE each mutation; world-load restore handles rank and quota
  kinds separately.
- **Safety rails:** room>0 precondition, delta cap, pinned-task guard, HasStateAuthority gate,
  write-ahead ledger, shared BoostedStationKeys mutual exclusion with the rank spike/controller,
  no interop wrappers cached across ticks.

**Phase 2b factual findings (in-game-verified 2026-07-14):**

- **Quota gates villagers only:** a warehouse task quota throttles VILLAGER collect/intake tasks
  only — PLAYER deposits bypass it entirely (observed: stored rose 133→168 while the group quota
  was
  clamped to 133 because the player hand-deposited).
- **Hauler response latency:** after a clamped Firewood allotment group on 'Warehouse 3' was
  restored (sum 133→180 re-opening intake), haulers responded in 13 seconds and filled to the
  target (stored 168→180). Single datum; sizes the future Phase 2c measurement window.
- **Composite multi-row clamp confirmed:** the forge clamp distributed one item's group quota
  across all 4 of its per-sub-storage rows (45,45,45,45 → 34,33,33,33 so the sum equals the
  warehouse-wide stored count 133) and all 4 restored cleanly — consistent with (and further
  confirming) the documented "effective allotment = SUM of per-supply quotas" fact.

**Phase 2c metabolic plane + clog controller (v0.6.0–v0.6.1, in-game-verified 2026-07-14):**

- **MetabolicPlane module** — per-key ring buffer (fixed capacity 16) of (Time.time, count)
  samples, key format `<posKey>|<itemName>`. Fed by every WarehouseWatch poll: one sample per
  (warehouse, item) stored count, plus each storage-full station's dominant-item count from
  the clog detector. `TryGetRatePerMinute(key, window)`: endpoint-delta rate over samples in
  the window, requires ≥2 samples spanning ≥45 s. Throttled "top movers" log line (top 5 by
  |rate| ≥ 0.1/min, log prefix `[SupplyChain] [metabolic]`). `DescribeRate(key)` helper for
  other modules' log lines.
- **ClogController module** — automates the Phase-2b-proven quota-raise drain. Per-ITEM state
  machine Pending → Boosting → Cooldown → CapacityLockout, fed by WarehouseWatch VERDICT A via
  `NoteVerdictA(stationPosKey, stationName, item, dominantQty)` call. Shares: SupplyController
  .Armed (F11 toggle — dry-run until armed), militia-alarm lockout (SupplyController
  .AlarmLockoutActive), SpikeLedger (Kind="quota", write-ahead before every write), RankActuator
  .BoostedStationKeys (mutual exclusion with spikes + rank controller), QuotaActuator.ApplyQuota
  (single shared quota write path). Actuation: picks the unlocked true-warehouse row group for
  the item with the LARGEST room (GetTotalRemainingCapacity), raises ONE row by delta = min
  (clogged station's current item count, room, ClogBoostMaxDelta); authority-gated (HasStateAuthority
  , unreadable → allow). Response detection every 5 s MasterTick: warehouse stored rises OR clogged
  station's count falls; logs hauler latency. Revert triggers (priority order): target-met
  (warehouse stored ≥ storedAtBoost+delta), clog-cleared (v0.6.1 fix: station's storage-full
  complaint is NOT present in registry within detector's 10-min staleness via new
  `WarehouseWatch.IsStorageFullActive(posKey)` — replaces v0.6.0's 60-s gate that starved on
  persistent non-cycling complaints; gate-reopen logs ONE line per stall via `ClogEntry.FreshGateLogged
  ` flag), no-response (ClogResponseWindowSeconds elapsed, default 60 s), hold-elapsed
  (ClogHoldMaxSeconds). Then Cooldown; ClogMaxCyclesPerItem cycles with clog persisting →
  capacity VERDICT toast + ClogCapacityLockoutMinutes lockout; 3 consecutive no-target outcomes
  (no unlocked warehouse with room) → same verdict path. Entry staleness: VERDICT A refreshes
  LastSeen each ~30 s warehouse poll; no refresh for ClogRetainSeconds → entry removed (reverting
  first if Boosting). Revert ledger policy mirrors rank controller's: warehouse-not-found KEEPS
  ledger entry (restored at next world load), task-not-found DROPS it. Stranded boosts restored
  by QuotaSpike.OnWorldReady's existing Kind="quota" pass (MasterTick gate widened to
  EnableQuotaSpike OR EnableClogController). Log prefix `[SupplyChain] [clog-ctl]`.
- **WarehouseTasks module** — warehouse row scan (BuildRows, was QuotaSpike.BuildWarehouseTaskRows
  ) + FindQuotaTask + WarehouseTaskRow + GetSupplyOwner hoisted into shared static class so spike
  and controller use one implementation; QuotaSpike delegates — zero behavior change.

## Configuration

**`<Mod>.cfg` after first launch:**

```
[General]
TickSeconds=5               # Sweep and widget-poll interval (Phase 0)
DumpHotkey=F9              # Typing-guarded hotkey for TaskDump (Phase 0)

[Diagnostics]
EnableDiagnostics=true     # Developer mode (deliberately true; flip false before ship)
PatchVillagerComplaints=true  # Safety valve — set false if load crash
WidgetPollSeconds=10       # Widget map sample rate
SweepStationsPerTick=1     # Rolling station quota per tick
IgnoredComplaintMessages=feelUnsafe  # Suppress listed key-exact messages (comma-sep)

[Phase1Spike]
EnableSpike=true           # F10 hotkey actuation
SpikeHotkey=F10            # Typing-guarded hotkey
SpikeStationFilter=        # Empty = auto-select; prefix-match station name to filter
SpikeTaskIndex=-1          # Task index to boost (-1 = auto = last non-pinned)
SpikeNewPriority=1         # ⚠️ Inert since v0.2.3 (rank-move actuator)
SpikeAutoRevertSeconds=120 # Auto-revert timeout (0 = manual only)
ForceAllowRefresh=false    # Allow null/solo contexts (MineRefresh pattern)

[Phase1Controller]
EnableController=true      # Complaint-driven demand management
Armed=false                # F11 toggles live (dry-run default)
DemandRetainSeconds=180    # Re-sighting window (burst tolerance)
MinDemandSpanSeconds=30    # Span maturity gate (first→last sighting)
DemandMemoryMinutes=15     # Maturity credit on re-create
ControllerTickSeconds=5    # State machine polling interval

[Phase2Warehouse]
EnableWarehouseWatch=true   # Phase 2a read-only warehouse diagnostics + clog detector
WarehousePollSeconds=30     # WarehouseWatch poll cadence
WarehouseClassList=ResourceStorage,HuntingStation,FishingStation,CaveResourceStorage  # comma-sep exact native class names treated as warehouses
ClogDominantSharePercent=40 # dominant-item share threshold for clog verdicts

[Phase2Spike]
EnableQuotaSpike=true       # F7 micro-test + Shift+F7 forge mode
QuotaSpikeHotkey=F7         # Typing-guarded hotkey (Shift+hotkey = forge)
QuotaBoostMaxDelta=50       # max single-row quota delta per spike
QuotaSpikeMaxHoldSeconds=300  # hold timeout before auto-revert (F7 micro mode)
QuotaMinStationStock=10     # minimum source-station stock to trigger spike
ClogForgeItem=              # empty = forge disabled; item name to forge as permanent clog

[Phase2Clog]
EnableClogController=false  # retired lever (v0.14.x): off unless deliberately re-enabled
ClogRetainSeconds=240       # VERDICT A staleness threshold before entry removed
ClogHysteresisSeconds=45    # min window between samples for rate calculation
ClogBoostMaxDelta=50        # max single-row quota delta per clog boost
ClogResponseWindowSeconds=60  # elapsed time without response = no-response revert trigger
ClogHoldMaxSeconds=600      # hold duration before auto-revert after boost
ClogCooldownSeconds=120     # cooldown duration before next clog boost eligible
ClogMaxCyclesPerItem=2      # max cycles with clog persisting before VERDICT
ClogCapacityLockoutMinutes=15  # lockout duration after capacity VERDICT
MaxConcurrentClogBoosts=1   # max simultaneous clog boosts (mutual exclusion)

[Phase2dSpike]
EnableEvictSpike=true       # v0.7.0 fire-verify spike (hotkey + tier ledger restore)
EvictSpikeHotkey=F6         # plain = ground-drop test; Shift+key = tier test
EvictItem=                  # empty = auto-select best-stocked true-warehouse row
EvictDropCount=2            # units per press (clamped 1-5, capped by stack + container count)
TierItem=                   # empty = auto-select first Med-tier true-warehouse row
TierRpcTimeoutSeconds=15    # per-phase confirmation window before fallback/conclusion
TierHoldSeconds=15          # hold at High after confirmed apply before auto-revert
TierAbortSeconds=180        # absolute abort rail for the tier state machine

[Metabolic]
EnableMetabolicPlane=true   # Phase 2d future data layer (ring buffer samples)
MetabolicWindowSeconds=180  # default window for rate-per-minute calculations
MetabolicStatusMinutes=5    # throttle window for "top movers" log line

[Phase2dBudget]
EnableBudgetPlane=true      # v0.8.0 read-only budget analytics (also gates MaxCapacity reads)
BudgetWindowSeconds=480     # rate lookback; ceiling = metabolic ring depth (16 @ 30 s)
MinQuota=20                 # floor of every proposed quota target
HeadroomMinutes=30          # proposed target holds this many minutes of measured drain
MaxQuotaShareOfMaxPct=50    # proposed targets capped at this % of item physical max
HogMinStored=100            # hog requires at least this stockpile
HogMaxAbsRate=0.2           # |rate|/min at or below = static stockpile
ShadowMaxFillRate=0.2       # |rate|/min at or below on unmet+room group = not filling
ShadowMinSourceStock=30     # SHADOWED needs this much piled at producer self-storages
VictimMaxRoom=0             # HOG victim: unmet group sharing a container with room <= this
MaxProposalLinesPerPoll=12  # severity-sorted proposal-line cap per poll (F9 = full set)
MaxSlotsPerCycle=2          # eviction quantum cap (slots per proposal cycle, response-gated at arming)
CoProductFloorSlots=1       # v0.12.0 HOG sizing: reduce a surplus hog toward max(this×stack, its demand)
SurplusFactor=2.0           # v0.14.x surplus gate: over-target when settleStored > RATCHETED structural × this
InertMinutes=20             # v0.14.x verdict window (awake-minutes); real decreases protect 6× this (internal)

[Phase2dDemand]
EnableDemandGraph=true      # v0.10.0 structural-demand extractor (wantedBy table)
DemandRefreshMinutes=5      # auto-rebuild cadence outside F9 dumps
TransformMap=Cured Leather Hide<-Leather Hide;Cured Pelt<-Pelt;Cured Heavy Pelt<-Heavy Pelt

[Phase2dProbe]
EnableContainerProbe=true   # v0.9.2 container-layout probe on each F9/first-poll full dump
```

## Phase 2c Findings & Dead-Ends

**Confirmed (in-game 2026-07-14, run 3 armed test):**
- MetabolicPlane mover lines live and report correct rates (e.g. Firewood +13.2/min over 150 s
  sample).
- ClogController demand creation from VERDICT A fires; strikes and capacity verdict function.
- Quota-kind ledger auto-restore at world load works — clamped allotment rows restore to
  original values on load.
- Single-warehouse quota clamps redirect flow to other storages' open allotments + consumption
  rather than forging clogs for multi-sink items — only single-source items reliably clog.
- Armed F11 reaches ClogController (armed=True in status lines).
- v0.6.1 fix: persistent non-cycling complaints no longer starve via the `IsStorageFullActive`
  gate — verified in-game run 2 (Pike completed its strike sequence instead of hanging).
- Armed BOOST→response→REVERT cycle FIRED end-to-end mechanically (run 3): clog demand
  ('Coal' at 'Improved Coal Maker 2' and '4', x200) → BOOST quota write 300+50→350 on
  'Improved Warehouse 4' (room=600) with readback → no hauler response → REVERT
  (clog-cleared trigger) → quota restored 350→300 with readback, ledger clean, zero errors.
- Bonus: capacity verdict via 3 consecutive no-target strikes live-fired (item 'Pike',
  roomBlocked ×3 → "no warehouse can absorb" VERDICT + lockout), edge case previously untested.

**Wrong-lever finding (run 3, design impact):**
The armed boost worked mechanically but targeted the wrong lever: newly-built storage units
default EVERY compatible item's quota = unit physical max (coal unit = 300). Therefore the
boosted row was already UNMET (stored=0, qty=300): intake was already open, so raising the
quota changed nothing; 350 exceeds physical capacity — double no-op. The REAL constraint was
priority TIER: new coal rows were Med, shadowed by High-tier rows on the SAME warehouse.
Haulers service High-tier rows first; Med row serviced only when High rows full. The Phase 2a
detector diagnosed this live: STALL "unmet allotment NOT filling despite room — likely
priority-shadowed". Diagnosis correct; quota-raise insufficient. **Law: quota-RAISE valid only
when target group ACTUALLY MET (intake genuinely closed) AND raise ≤ physical capacity.** Organically
rare — requires player-lowered quota + free room. Quota-raise RETIRED as core clog lever.

**Lessons for future phases:**
1. (Dead-end, fixed in v0.6.1) Never gate on short freshness windows over the storage-full
   registry — persistent non-cycling complaints starve. Gate on registry-active within the
   detector's own 10-min staleness.
2. (Lesson, confirmed 2026-07-14) Single-warehouse quota clamps do NOT reliably forge clogs for
   items with many sinks — flow redirects to other allotments + consumption. Multi-sink clog
   tests need either single-group items or all-groups clamped.
3. (Dead-end, 2026-07-14) Quota-raise on UNMET rows is a no-op (intake already open); raise
   beyond physical capacity is a no-op; new storage units default quota=physical max. Organic
   boost preconditions too rare to justify quota-raise as core lever.

## Dead-Ends

- **Quota-raise as core clog lever (2026-07-14):** quota raise on an UNMET row is a no-op since
  intake is already open; raise beyond physical capacity is a no-op. Newly-built storage units
  default every compatible item's quota = physical max, so organic rows sit unmet-at-max. Preconditions
  too rare to justify raise as primary drain mechanism (confirmed in-game 2026-07-14,
  SupplyChainMod Phase 2c run 3). Retired; see "Design Direction" below.
- **`Rpc_ChangeTaskPriority` is inert from a host-side mod call (2026-07-15):** fired without
  exception on the GetComponent-probed NetworkCompositeResourceStorage but the row's priority
  never changed (readback Med for 15 s); direct SetPriority + HostUpdateTasks applied
  synchronously. Prefer direct host-side state writes + HostUpdateTasks over the task RPC
  surface.
- **`Workstation.NetworkWorkstations` interop property lies:** returns null/empty at runtime even
  when the `NetworkCraftingStation` component exists on the same GameObject (GetComponent finds
  it; confirmed in-game 2026-07-12). Reach network components via GetComponent, not cached link
  fields.
- **Value writes via `SetPriority` are squashed:** `RefreshTaskDataPriorities()` renumbers priority
  from list order, overwriting any direct value write (setter itself works, readback confirmed).
  Real actuation = list reorder via RemoveAt/Insert.
- **`KnowsBlueprintForItem` false on valid items:** confirmed dead-end (architecture.md → Blueprint
  lookup section). Always iterate `_knownBlueprints`/`_localBlueprints` dictionaries directly.
- **`LoadoutsComplaint.missingItems` has no name:** is `List<IItemFilter>` with no name member —
  logs native class names instead.
- `ResourceStorage.GetGatheredItemQuantity(supply, info)` returns the task's QUOTA, not fill
  progress (gathered==qty on every row, in-game 2026-07-13) — no per-supply stored count exists on
  this API; warehouse-wide GetItemQuantity is the only stored measure found so far.
- **SupplyOwner display names as container identity (2026-07-15):** hut rows' OwnerStructure is
  the hut itself, so name-grouping collapses a hut's SEPARATE physical containers into one
  pseudo-container (fabricated resin→sticks "victims" at Woodcutter's House 2); warehouse-side,
  multiple sub-storages can share a display name. Physical identity = the row's
  `storageSupply.interaction.Container` object, never a name.
- **`storageSupply.taskContainer` is NOT the physical container (Cecil, 2026-07-15):** it is
  `SSSGame.Network.NetworkTaskContainer`, a task-sync object with no capacity/accepted-types
  surface. Non-null taskContainer remains valid ONLY as the true-warehouse-row discriminator.
- **Flat/floor quota targets (2026-07-15):** `MinQuota + headroom×drain` collapses to ~MinQuota
  for static items — behaves as a ~90% flat cut. Eviction must be victim-deficit-sized and
  SLOT-GRANULAR (see DEMAND_MODEL_PLAN.md container semantics).
- **Supply-push-only SHADOWED gate (2026-07-15):** hut-stock piling without demand-pull evidence
  proposes tier bumps nobody needs (Feathers@Outpost1, incl. flipping rank 3 = Off — player
  intent). Demand-pull (drain/churn/noProd) is mandatory; Off rows are never proposed.
- **`HogQuotaShareOfMaxPct` (2026-07-15):** carried no signal — vanilla defaults every quota to
  physical max, so the test was near-always true. Config key removed in v0.9.1.
- **maxCap-vs-(stored+room) cross-check (answered 2026-07-15):** `GetMaximumStorageCapacity` is
  THEORETICAL (Σ over compatible containers of slots × that container's per-item stack size),
  ignoring occupancy — mismatched on all 517 groups; check removed in v0.9.0.
- **Storage/filter tasks as production demand (2026-07-15):** Workstation-generic task reading
  swept warehouse collect tasks + FiltersTaskData into DemandGraph (929 "tasks", quota=max
  allotments became fake production orders; Fibers structural inflated 21204 vs true ~810 and
  exploded transitively). Production tasks ONLY: exclude ResourceStorageTaskData/FiltersTaskData
  casts and skip WarehouseClassList-class structures entirely.
- **`ItemContainer.HasSpace(info, 1)` is NOT "has a free slot" (2026-07-15):** returned false on
  containers at fill 0.94–0.98 with free slots (semantics unknown). Blocked-for-item primitive =
  `GetRemainingCapacity(ItemInfo) == 0` (⚠️ v0.11.1 replacement pending in-game confirmation).
- **`cookingRequirements` lives ONLY on `CrockpotRecipeInfo`** (Cecil 2026-07-15): type is
  `Il2CppReferenceArray<CrockpotRecipeInfo.CookingRecipeRequirement>` (a NESTED type); base
  CookingRecipeInfo has no requirements member and no other subclass exists. Requirement enum
  `CookingRecipeRequirementType { Specific=0, Table=1, Unfiltered_Table=2 }` — Table types imply
  category/table acceptance (acceptedInfo may be empty; ⚠️ attribution behavior pending in-game
  data). Plain CookingRecipeInfo meals (grill/barbecue style) fall through to the TransformMap.
- **Cured leather items are recipe-less in data (2026-07-15):** unmatched even via the blueprint
  UNION across 10 table-bearing stations — the curing rack has no blueprint; the TransformMap
  config exists because of this.
- **Blocking requires SHARED storage (2026-07-15, v0.12.0):** two items in SEPARATE containers can't
  compete for slots, so one can't block the other. The old "producer-blockage" concept (a full
  DEDICATED container of undemanded X stalls a demanded SIBLING output elsewhere at the same station)
  was therefore invalid — every such proposal paired non-sharing items. Real crowding exists only
  within a shared container (that's HOG). BLOCKAGE was redefined to a single-material routing
  diagnostic (a demanded material stuck while its downstream consumers wait), never a cross-container
  stall. User-confirmed in-game; the misleading definition had lived in DEMAND_MODEL_PLAN.md and drove
  the wrong build.
- **`structural × SurplusFactor` mislabels base-material buffers as surplus (2026-07-15, v0.13.0):**
  structural demand = current active task quota × recipe — a good target for END PRODUCTS but a poor
  one for BASE MATERIALS, which a settlement legitimately buffers far above current crafting orders
  (Fibers 4900 stored vs demand 484 → flagged surplus, but likely a healthy buffer; contrast Resin
  2050 vs demand 3 → genuine over-production). The SURPLUS keep-target must become consumption-rate/
  flow-aware (the deferred chain-capacity rollup) before SURPLUS/HOG eviction is armed, or it would
  dump useful stock. Related: the demand-blind post-load window (DemandGraph builds after ~5 min / F9)
  reads EVERYTHING as surplus (keepTarget=0) — arming must be gated until demand has built.
- **Single-window flow verdicts misread bursty consumers (2026-07-16, v0.14.1):** consumption is
  EVENTFUL (crafts consume in task-cadence bursts) while accumulation is continuous — with a
  10-min window, Fibers (4903 stored, demand 484) read GROWING because no fiber-consuming craft
  ran inside the window while gatherers trickled arrivals (same pattern: Gray Mushrooms, Stick,
  Wild Egg). Fix: a REAL observed decrease grants ALIVE for 6 × InertMinutes (v0.14.2 consumption
  memory). A settlement genuinely idle on an item for longer still reads growing/dead — the full
  fix remains consumption-rate-aware demand (chain-capacity rollup, deferred).

## Design Direction (user-decided 2026-07-14)

**End goal:** a steady-state flow autopilot. "Player creates buildings, assigns workers, sets basic
material floors; the mod works out the rest." Stages:

- **Phase 2c (current):** episodic closed loop with conservative rails — player manually triggers
  the clog system; the mod detects and resolves known patterns under safety guards.
- **Phase 2d (promoted to CORE of Phase 2):** continuous SOFT quota budgeting. Vanilla's default
  (every item's quota = unit physical max) means any single item can crowd out whole storage — the
  mod counters by sizing per-item quotas from measured need (metabolic-plane rates) + headroom for
  future boosts, with per-item caps below unit max; the sum MAY exceed capacity (soft, keeps
  flexibility). Common organic failure: bone-fragment shape (low/no-demand item maxes out storage).
  Remedy = quota REDUCE plus NEW down-to-quota EVICTION routine: `ItemContainer.DropItem(Item,
  Int32 count, Vector3 pos, ...)` drop-to-ground by default (mirrors player X-press drop;
  GroundItemVacuumMod picks litter up); direct deletion via `ItemCollection.RemoveItems` deferred.
  Tier lever stays critical: diagnose/rebalance priority-shadow starvation (the coal case from Phase
  2c) and deliberate surge boosts. Budgeting mitigates shadowing: with sane quotas, High rows reach
  met and haulers proceed down the tier list, so tier lever becomes rare corrective not primary
  actuator. Bounds via `ItemCollection.GetMaximumStorageCapacity(ItemInfo)` (per-item physical max
  warehouse-wide). Cecil-confirmed primitives recorded in architecture.md.
- **Phase 3:** invert ownership — player-declared per-material floors, mod owns quotas/priorities
  continuously, reverts only on world unload/mod disable (write-ahead ledger handles restore).
  Capacity verdict keeps its diagnosis role but evolves from timed lockout to persistent advisory.

**Phase 2e research candidate (failure mode 3, observed 2026-07-14):** producer-side priority
contention. Mine hut gathers Large Rocks at EQUAL priority with Iron Ore; large rocks are slow,
bulky carries, so ~1:1 hauling collapses ore throughput. Mechanism question answered (CaveResourceStorage
rows carry WorkstationTaskPriority TIER values, confirmed in-game 2026-07-14); remaining opens =
reliable demand signal + lever design. Research before design — see docs/architecture.md Settlement
Hauling section for details.

**Phase 2d fire-verify spike (v0.7.0, in-game-verified 2026-07-15):**

Fire-verify spike hotkey F6, config section `[Phase2dSpike]`. Plain F6 = ground-drop test:
auto-selects the best-stocked true-warehouse row (or config `EvictItem`), resolves the
container via `rst.storageSupply.interaction.Container`, calls `ItemContainer.DropItem(item,
count, pos, Quaternion.identity, null)` (count = min(EvictDropCount clamped 1–5, stack
count, container count); pos = interaction transform + forward*1.2 + up*0.6), host-gated,
no ledger, immediate + one-tick-delayed count readback. Shift+F6 = tier test: picks the
first Med-tier true-warehouse row (or config `TierItem`), write-ahead SpikeLedger entry
Kind="tier" (OrigPriority/BoostPriority columns carry orig/boosted TIER values), tries
`Rpc_ChangeTaskPriority(taskIndex, 0)` on the GetComponent-probed storage network component,
falls back to direct `rst.SetPriority(0)` + `HostUpdateTasks()` after a 15 s window, holds
15 s, auto-reverts via the proven path (retry via the other path if unconfirmed), whole-
warehouse rank-snapshot diff detects wrong-row application, 180 s abort rail,
BoostedStationKeys mutual exclusion, world-load restore of stranded tier entries
(warehouse-not-found KEEPS the ledger entry, task-not-found DROPS it).

**Finding 1 — ground-drop eviction CONFIRMED (in-game 2026-07-15):** two F6 presses on
auto-selected item Feathers: container 500→498→496, warehouse-wide stored 700→698→696,
delayed re-check 3–4 s later showed the decrement held both times, and the user visually
confirmed 2 dropped feather items on the ground outside the storage after each press
(recoverable by GroundItemVacuum). Solo host only — co-op CLIENT replication of the spawned
ground item remains ⚠️ unverified.

**Finding 2 — tier lever CONFIRMED via DIRECT WRITE; the RPC is a DEAD-END (in-game
2026-07-15):** target Beetroot task[0] on 'Improved Warehouse 2'; network probe found
`NetworkCompositeResourceStorage` via gameObject/composite. `Rpc_ChangeTaskPriority(0, 0)`
fired without exception but was INERT — readback stayed Med=1 for the full 15 s window.
Fallback direct write `rst.SetPriority(0)` + `HostUpdateTasks()` applied SYNCHRONOUSLY
(immediate readback 0 — tier values are NOT squashed by any renumber mechanism, unlike
crafting-station rank). Revert `SetPriority(1)` + `HostUpdateTasks()` likewise confirmed.
Log verdict: `TIER CYCLE COMPLETE: boost + revert both confirmed (path=direct)`. Ledger
empty after the session. Conclusion: the warehouse tier lever recipe = direct value write +
`HostUpdateTasks()` (the exact same pattern as the proven quota write); `Rpc_ChangeTaskPriority`
called from a host-side mod joins the interop dead-end family (like the lying
`NetworkWorkstations` property).

**Phase 2d step 1 — BudgetPlane dry-run classifier (v0.8.0–v0.9.1, SUPERSEDED 2026-07-15):**

Read-only classifier riding WarehouseWatch's poll; two in-game audits (2026-07-15) showed its
classification logic wrong at each iteration (see Dead-Ends: name-based container identity,
supply-push-only shadowing, flat quota targets). Retained machinery that IS current: per-item
settlement series `ALL|<item>` fed into MetabolicPlane (net rate + gross churn via
`TryGetChurnPerMinute`), the game's noProduction map exposed via
`WidgetWatcher.TryGetNoProductionNeededBy` (latent demand), rank 3 = Off never proposed,
severity-sorted proposal lines capped per poll. Classification rules are being rebuilt per
`SupplyChainMod/DEMAND_MODEL_PLAN.md` (demand loop: structural demand gates, flow sizes,
distress prioritizes; hog/victim requires verified effectively-shared containers).

**ContainerProbe (v0.9.2, in-game-verified 2026-07-15):** read-only container-layout probe on
each full-table dump (F9/first poll), config `[Phase2dProbe] EnableContainerProbe`. Per
WarehouseClassList structure: resolves every storage row's PHYSICAL container via
`storageSupply.interaction.Container` (uniform for warehouse AND hut rows — 0 unresolved rows
across 19 structures / 116 containers on the big save), groups rows by container pointer
(Il2CppObjectBase boxing pattern, never cached across polls), logs per container: items+ranks,
`capacity`, `GetFillRatio()`, `GetAcceptedItemTypes()` (+ one `CanStoreItemType` cross-check),
plus an independent hierarchy-walk cross-check collecting `ItemContainerComponent.container`
per node. Perf ~35-37 ms per dump.

**DemandGraph (v0.10.0–v0.11.1) — the structural-demand layer (plan step 2):** per structure,
reads production tasks (Workstation-generic; SKIPS structures whose class is in
WarehouseClassList and tasks castable to ResourceStorageTaskData/FiltersTaskData — storage and
filter tasks are NOT production demand, see Dead-Ends) and joins each task's output item against
recipe sources in order: station-local blueprint tables (`_craftingTables→_localBlueprints`,
BomDump-proven) → global blueprint UNION across all read tables (also found via a
CraftInteraction hierarchy walk on stations without a CraftingStation component) → cooking
(task item's native class chain = CrockpotRecipeInfo → read `cookingRequirements[]`) → the
`TransformMap` config (non-blueprint conversions, default = the curing trio) → unmatched
(logged). structuralQty = inputQty × ceil(quota / outputPerCraft); outputPerCraft primary source
= `BlueprintInfo.quantity` (in-game-confirmed == batch size on all 418 blueprints, 2026-07-15),
name-prefix `Nx ` parse as fallback. Transitive propagation carries demand down chains (Hot Iron
Bloom → Iron Bloom → ore/coal; breadth-first, cycle-guarded, depth 8; derived tracked separately
from direct, `derivedVia=` logged). Auto-rebuild via `EnsureFresh()` every DemandRefreshMinutes +
F9 full dump; accessor `TryGetStructuralDemand(item, out direct, out derived, out wantedByCount)`.
v0.10.0 in-game-verified 2026-07-15: 47/51 tasks matched, 23 inputs, table audited TRUE
(Feathers demanded by arrow tasks despite zero flow; Bone Fragments/Seeds correctly absent).

**Classifier (v0.11.0 → v0.14.2, plan step 3):** BudgetPlane rebuilt on
a per-poll container map (interaction.Container per row, transient same-call-stack only; AllotmentRow
carries ContainerKey/Info) + structural demand. It reasons about ONE central signal and separates
three storage pathologies:

- **OverTarget × Verdict** (v0.14.x, replaces v0.13.0's single IsSurplus): OverTarget(item) =
  settleStored > RatchetTotal × `SurplusFactor`, where RatchetTotal = session HIGH-WATER mark of
  structural demand (task-completion dips don't condemn stock; cleared on world-leave).
  Verdict(item), directional and threshold-free over the last `InertMinutes` of AWAKE time
  (clocks advance only on polls where ≥1 item's settlement total moved — night/sleep/pause-proof,
  DVN-agnostic): ALIVE = any observed decrease (real decreases grant ALIVE for 6 × InertMinutes —
  v0.14.2 consumption memory, internal constant); GROWING = increases only; DEAD = no movement.
  Items initialize "assumed alive" at world load (expires after ONE window) → no surplus/HOG
  verdict before InertMinutes of observed awake inactivity (closes the demand-blind post-load
  window). Hog eligibility + SURPLUS class = OverTarget AND (DEAD or GROWING); HOG-victim
  neediness and the BLOCKAGE/pass-3 exclusions stay OverTarget-only (magnitude-based — preserves
  the verified seeds→Fibers false-hog suppression). SURPLUS/HOG lines carry `verdict=` tokens;
  storage-full complaints add a `distress=storage-full` tag + severity boost on HOG/BLOCKAGE/
  SHADOWED lines. HOG/BLOCKAGE read player Off-dedication per (container,item) row rank
  (v0.14.x), not structure-wide MinRank.
- **HOG** (eviction) — a SURPLUS, static item crowds a demanded, NON-surplus, blocked item in a
  SHARED container (real physical crowding). Blocked = `GetRemainingCapacity(victim)==0` (fill>=0.999
  fallback; blk=cap|fill token — v0.11.0's HasSpace was wrong, see Dead-Ends). Sizing DEMAND-GROUNDED
  (v0.12.0): reduce the hog toward max(CoProductFloorSlots × stack, its own RATCHETED demand), paced at
  MaxSlotsPerCycle; full target + per-cycle step both logged (bones 1250→50).
- **BLOCKAGE** (diagnostic, v0.12.0 rewrite) — a demanded, NON-surplus material SITTING (stored>0) in
  a full, static container while its downstream BOM consumers rely on it (Hardwood Long Stick jammed
  while Shafts/Beams demand it). A routing/priority problem; lever tier/priority, NOT eviction. One
  line per ITEM (deduped, most-piled location) with a cause token from the item's warehouse rows:
  no-route / destination-full / priority-shadowed. The pre-v0.12.0 "undemanded X stalls a demanded
  SIBLING in a DIFFERENT container" logic was WRONG (separate containers can't block — Dead-Ends), retired.
- **SURPLUS** (informational, v0.13.0) — a surplus item NOT currently crowding a victim: over-produced
  and just sitting. One line per item.
- **SHADOWED / OFF-CONFLICT / STARVED** — unchanged demand-pull warehouse classes.

Still 100% dry-run (no DropItem, no quota/tier write). v0.13.0 in-game-verified 2026-07-15: loaded
clean, zero errors; Resin (2050 stored vs demand 3) correctly reclassified surplus→HOG-eligible (was
mislabeled BLOCKAGE); 5 clean cause-tokened BLOCKAGEs; the false seed→Fibers hogs correctly suppressed
(Fibers over-supplied settlement-wide, so not a needy victim). Finding: `structural × SurplusFactor`
mislabels heavily-buffered BASE MATERIALS as surplus (Fibers 4900 vs demand 484) — keep-target must
become consumption-aware before SURPLUS/HOG eviction is armed (see Dead-Ends + DEMAND_MODEL_PLAN.md).
v0.14.1 re-test 2026-07-16: warmup gate, Resin dead, bones HOG + distress tag, cause tokens, clog
vocabulary gone, streaming retry all verified; Fibers finding → v0.14.2 (Dead-Ends).

## Research spike (Cecil, 2026-07-14)

Eviction/capacity machinery for Phase 2d, recorded with minimal filtering for implementation.
See architecture.md for full API surface.
- **Drop-to-ground eviction:** `ItemContainer.DropItem(Item item, Int32 count, Vector3
  position, Quaternion rotation, Transform parent)` recipe via
  `ResourceStorageTaskData.storageSupply.interaction` (StorageInteraction) → `.Container :
  ItemContainer`. Recipe confirmed in-game 2026-07-15 (v0.7.0 spike); ⚠️ co-op CLIENT
  replication of the spawned ground item still unverified.
- **Per-item capacity reads:** `ItemCollection.GetMaximumStorageCapacity(ItemInfo[, bool
  includeHidden])` = warehouse-wide physical max per item; per-container: `ItemContainer.
  capacity`, `GetRemainingCapacity(ItemInfo)`, `GetStackSize(ItemInfo)`, `IsFull(bool)`,
  `GetFillRatio()`, `HasSpace(ItemInfo, Int32)`.
- **Compatible-item sets:** `ItemContainer.GetAcceptedItemTypes() : List`, `CanStoreItemType
  (ItemInfo)`; collection-level `GetMatchingContainers(...)`, `FindLargestMatchingContainer
  (...)`, `HasMatchingContainer(...)`, `GetFreeContainer(...)`.
- **Tier write path:** the warehouse tier lever = direct `rst.SetPriority(tier)` +
  `HostUpdateTasks()` (recipe confirmed in-game 2026-07-15, applies synchronously, no squash,
  revert identical). `Rpc_ChangeTaskPriority` fired host-side without exception but was
  INERT (readback unchanged for 15 s) — dead-end, same family as the lying
  `NetworkWorkstations` property.
- **taskMaxQuota hypothesis (superseding "unknown"):** likely per-spawned-task carry count
  (items one villager trip moves), irrelevant to budgeting bounds either way. Live on
  `StorageSupply` (per sub-storage), not per-task.
- **Station self-storages (Woodcutter's House, Gatherer's House, Outhouse, Stonecutter's
  Hut):** use the SAME ResourceStorage row machinery as warehouses (confirmed in-game
  2026-07-14) — quota/tier levers uniform across warehouses AND station storages.
- **Villager cleanup FSM exists:** `FSM_CleanupInventory` with `StorageToDropIntoPredicate`
  — study lead ONLY IF villager-labor eviction is ever wanted (not needed for ground-drop v1).
- **Direct deletion:** `ItemCollection.RemoveItems(ItemInfo, Int32)` = deletion path (deferred).
- **Events (do NOT subscribe — IL2CPP gotcha):** `ItemContainer.OnItemAdded/OnItemRemoved/
  OnItemCountChanged` — poll instead.

## Version History (compressed)

- **v0.1.0–v0.1.3** (2026-07-12, Phase 0) — scaffold + all diagnostics modules, 4 test runs.
  Confirmed: complaint ADD/CLEARED/re-add flow, widget maps, task data (priority = rank index),
  composition sweep perf, BomDump dead-end.
- **v0.2.0–v0.2.3** (2026-07-12, Phase 1a spike) — hotkey-triggered list-move boost/revert,
  write-ahead ledger, auto-select, crash-restore, 3 test runs. Confirmed: list-move actuation,
  rank reorder, ledger restore, GetComponent NetworkCraftingStation probe, NetworkWorkstations
  property lies.
- **v0.3.0–v0.3.5** (2026-07-12, Phase 1b controller) — complaint-driven demand model, dry-run,
  burst tolerance, already-top precondition, maturity credit, StationBusy outcome, 6 test runs.
  Confirmed: loop closure via demand-cleared early release, already-top→capacity verdict,
  filter-aware rank, maturity memory, alarm lockout mechanism (untested in-game). ⚠️ Untested
  edges: StationBusy rotation (no same-station contention in tests), capacity verdict via 2
  ineffective cycles (all real boosts self-cleared early).
- **v0.4.0–v0.4.3** (2026-07-13, Phase 2a) — warehouse allotment diagnostics + dry-run clog
  detector, 2 test runs, zero errors. Confirmed: ResourceStorageTaskData structure, composite
  multi-task-per-item allotments, Composite/Simple network split, room semantics
  (GetTotalRemainingCapacity), STALL discriminator (capacity-blocked vs priority-shadowed) both
  branches live-fired, GetGatheredItemQuantity dead-end. (v0.4.2 was a build-only SAC bump, never
  tested.)
- **v0.5.0–v0.5.2** (2026-07-13/14, Phase 2b) — quota spike + ledger extension; run 1 (v0.5.1)
  clean but no eligible pair → v0.5.2 added the true-warehouse filter, near-miss diagnostics,
  multi-row forge clamp, forge→drain-watch transition, Shift+F7 split; run 2 (v0.5.2) verified
  the full closed loop (clamp → forged storage-full complaints → revert → 13 s hauler response →
  target met), zero errors across both runs. Confirmed: quota write path works, player deposits
  bypass quota, composite multi-row clamp distributes correctly, hauler response latency 13 s.
  (v0.5.0 was a build-only casualty of the SAC bump guard, never tested.)
- **v0.6.0–v0.6.1** (2026-07-14, Phase 2c) — MetabolicPlane + ClogController + WarehouseTasks
  hoist. v0.6.0 run 1: starvation bug found (short gate window on persistent non-cycling
  complaints); v0.6.1 fix: IsStorageFullActive gate on detector's 10-min staleness + FreshGateLogged
  one-shot line. Run 2 (v0.6.1) verified: metabolic plane rates live, clog demand/strike/verdict
  paths live, quota ledger restore live, gate fix confirmed (Pike completed strike sequence instead
  of starving). Run 3 (v0.6.1) armed cycle fired mechanically but exposed wrong-lever finding
  (quota-raise on unmet rows is no-op); triggered design pivot (Phases 2d/2e reshaped, quota-raise
  retired as core lever). Lessons + Phase 2e research findings + Cecil spike recorded above.
- **v0.7.0** (2026-07-15, Phase 2d fire-verify spike) — EvictionSpike (F6 drop test + Shift+F6
  tier test), 1 test run, zero errors. Confirmed: DropItem ground-drop eviction recipe (spawn +
  exact decrement, solo host), tier lever = direct SetPriority + HostUpdateTasks (synchronous,
  no squash), Rpc_ChangeTaskPriority inert from host-side call (dead-end).
- **v0.8.0** (2026-07-15, Phase 2d step 1) — BudgetPlane dry-run brain (read-only need-based
  quota targets + hog/shadowed/healthy classification from metabolic rates; proposals logged,
  nothing written), AllotmentRow HasTaskContainer/MaxCapacity extension. Built clean, ⚠️
  pending in-game confirmation (test: play ≥10 min on a real save, F9 once, review proposals).
- **v0.9.0** (2026-07-15) — classifier redesign #1: hut groups included, multi-type gate via row
  composition, settlement `ALL|item` net+churn, victim requirement, severity-capped proposals;
  maxCap cross-check removed, HogQuotaShareOfMaxPct retired. In-game same day: noise 230→~26
  standing proposals, but victims/demand still wrong (name-based containers, no demand-pull).
- **v0.9.1** (2026-07-15) — demand-pull gates (drain/churn/noProd accessor), rank 3 = Off
  respected (+`off` class), one-destination-per-item (+`shadowed-alt`), victim demand gate,
  WarehouseClassList default widened (+HuntingStation,FishingStation,CaveResourceStorage),
  nonStorageTasks line gated to F9. Loaded clean in-game; classifier logic superseded by the
  DEMAND_MODEL_PLAN redesign before a rate-bearing audit.
- **v0.9.2** (2026-07-15, in-game-verified) — ContainerProbe module + [Phase2dProbe] config;
  all container APIs confirmed at runtime; container ground truth mapped (59/116 shared,
  family-line sharing, meat+bones@Hunter's House confirmed shared pool; capacity = slots,
  stack size per-(container,item)).
- **v0.10.0** (2026-07-15, in-game-verified) — DemandGraph structural-demand extractor (plan
  step 2): blueprint-manifest join, wantedBy rollup, TryGetStructuralDemand accessor. Audit read
  TRUE (47/51 matched; Feathers demanded, bones/seeds absent).
- **v0.11.0** (2026-07-15, tested in-game, logic superseded) — classifier v3 (plan step 3):
  per-poll container map, HOG/BLOCKAGE/SHADOWED/OFF/STARVED on structural demand + transitive
  propagation + widened recipe discovery. Ran clean; audit found demand pollution (storage tasks)
  + HasSpace blocked-detection bugs.
- **v0.11.1** (2026-07-15, tested in-game) — production-task filter, GetRemainingCapacity blocked
  primitive + evict clamp, BlueprintInfo.quantity primary batch source, TransformMap config (curing
  trio default), CrockpotRecipeInfo cooking-requirements demand, derivedVia labels. Inflation FIXED
  (Fibers direct 21204→100); audit found the two classifier-semantics bugs that v0.12.0/v0.13.0 fix.
- **v0.12.0** (2026-07-15, in-game-verified) — (1) demand-grounded HOG sizing: reduce the hog toward
  a kept floor, not the victim's tiny deficit (bones 1250→50, was would-evict 50); (2) BLOCKAGE
  REWRITTEN from the invalid "undemanded X stalls a same-station sibling in a DIFFERENT container"
  (all such proposals were false — separate containers can't block) into a diagnostic naming a stuck
  DEMANDED material + its downstream consumers. Config CoProductFloorSlots. Both verdicts passed
  in-game; the user's Hardwood Long Stick→Wood Beam/Shaft case appeared verbatim.
- **v0.13.0** (2026-07-15, in-game-verified) — taxonomy reconciliation (HOG/BLOCKAGE/SURPLUS): NEW
  IsSurplus(item) = settleStored > structural × SurplusFactor (config 2.0) as the hog gate (replaces
  binary demanded/undemanded); NEW SURPLUS informational class (over-produced, not crowding); BLOCKAGE
  gains cause tokens (no-route/destination-full/priority-shadowed) + per-item dedup + stored>0 gate;
  victim must be non-surplus. Loaded clean, zero errors: Resin (2050 vs demand 3) → SURPLUS (was
  mislabeled BLOCKAGE); seed→Fibers hogs correctly vanished (Fibers over-supplied). Surfaced the
  base-material keep-target gap (Dead-Ends + DEMAND_MODEL_PLAN.md next steps).
- **v0.14.0–v0.14.1** (2026-07-16, in-game-tested) — SURPLUS v2 (DEMAND_MODEL_PLAN.md "v0.14.x"):
  OverTarget (ratcheted keep-target) × directional ALIVE/GROWING/DEAD verdicts on awake-gated
  clocks replace magnitude-only IsSurplus as the hog/SURPLUS gate (victim + BLOCKAGE exclusions
  stay magnitude-based); riders: clog VERDICT/STALL toast vocabulary retired (storage-full →
  `distress=storage-full` severity tag; EnableClogController default false), per-container Off
  overlay, DemandGraph zero-match 60 s retry; +InertMinutes config, 4 orphaned keys removed.
  (v0.14.0 superseded pre-test by a cosmetic rebuild bump — no behavior difference.) Test 9/10:
  warmup gate, Resin dead, bones HOG + distress, cause tokens, no clog vocabulary, streaming
  retry, zero errors; FINDING: Fibers read GROWING (bursty consumption — see Dead-Ends).
- **v0.14.2** (2026-07-16) — consumption memory: a REAL observed decrease keeps an item ALIVE for
  6 × InertMinutes (internal constant); warmup seed still expires after one window. ⚠️ pending
  in-game test.
