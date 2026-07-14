# SupplyChainMod — Mod 26

**Status:** WIP v0.6.0, Phase 0 + Phase 1 + Phase 2a + Phase 2b complete (in-game-verified),
Phase 2c (metabolic plane + clog controller) built ⚠️ pending in-game confirmation, dev tool
NOT for Nexus

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

**Phase 2c metabolic plane + clog controller (v0.6.0, ⚠️ pending in-game confirmation):**

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
  (warehouse stored ≥ storedAtBoost+delta), clog-cleared (station's storage-full complaint not
  seen for 60 s), no-response (ClogResponseWindowSeconds elapsed, default 60 s), hold-elapsed
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
WarehouseClassList=ResourceStorage  # comma-sep exact native class names treated as warehouses
ClogDominantSharePercent=40 # dominant-item share threshold for clog verdicts
ClogRealertMinutes=10       # per-(station,item) toast dedupe window
ClogStallMinutes=3          # unchanged stored-count window before a STALL diagnosis fires

[Phase2Spike]
EnableQuotaSpike=true       # F7 micro-test + Shift+F7 forge mode
QuotaSpikeHotkey=F7         # Typing-guarded hotkey (Shift+hotkey = forge)
QuotaBoostMaxDelta=50       # max single-row quota delta per spike
QuotaSpikeMaxHoldSeconds=300  # hold timeout before auto-revert (F7 micro mode)
QuotaMinStationStock=10     # minimum source-station stock to trigger spike
ClogForgeItem=              # empty = forge disabled; item name to forge as permanent clog

[Phase2Clog]
EnableClogController=true   # Phase 2c clog automation
ClogRetainSeconds=240       # VERDICT A staleness threshold before entry removed
ClogHysteresisSeconds=45    # min window between samples for rate calculation
ClogBoostMaxDelta=50        # max single-row quota delta per clog boost
ClogResponseWindowSeconds=60  # elapsed time without response = no-response revert trigger
ClogHoldMaxSeconds=600      # hold duration before auto-revert after boost
ClogCooldownSeconds=120     # cooldown duration before next clog boost eligible
ClogMaxCyclesPerItem=2      # max cycles with clog persisting before VERDICT
ClogCapacityLockoutMinutes=15  # lockout duration after capacity VERDICT
MaxConcurrentClogBoosts=1   # max simultaneous clog boosts (mutual exclusion)

[Metabolic]
EnableMetabolicPlane=true   # Phase 2d future data layer (ring buffer samples)
MetabolicWindowSeconds=180  # default window for rate-per-minute calculations
MetabolicStatusMinutes=5    # throttle window for "top movers" log line
```

## Dead-Ends

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
- **v0.6.0** (2026-07-14, Phase 2c) — MetabolicPlane + ClogController + WarehouseTasks hoist;
  build-verified only (0 errors/0 warnings), no game run yet. ⚠️ pending in-game confirmation.
