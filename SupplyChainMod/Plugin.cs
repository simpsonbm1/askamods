using BepInEx;
using BepInEx.Configuration;
using BepInEx.Unity.IL2CPP;
using BepInEx.Logging;
using HarmonyLib;
using Il2CppInterop.Runtime.Injection;
using UnityEngine;

namespace SupplyChainMod;

// SupplyChainMod v0.1.x — Phase 0 of the demand-driven supply-chain autopilot
// (NEW_MOD_IDEAS_PLAN.md idea 12). Phase 0 (ComplaintWatcher, WidgetWatcher, TaskDump,
// CompositionSweep, BomDump) is READ-ONLY: it observes complaints, workstation task datas, and
// station inventory composition, and logs them. Writes NOTHING — no priority changes, no task
// creation, no inventory writes.
//
// v0.1.1 — first in-game run follow-up: GenericMessageComplaint discrimination + an ignore filter
// for "feels unsafe" complaint spam (IgnoredComplaintMessages), FailedObjectiveComplaint detail,
// REMOVE-log dedupe aggregation, a SettlementIssueTrackerWidget.Instance fallback
// (FindAnyObjectByType), and unambiguous BomDump input/output instrumentation.
//
// v0.1.2 — run 2 showed complaints are a LEVEL signal (the game re-ADDs a held complaint every
// ~50-100ms): ComplaintWatcher now logs TRANSITIONS only (ADD (new) / CLEARED), collapsing the
// v0.1.1 REMOVE-dedupe into a periodic stats line (active/newAdds/reAdds/clears/unmatchedRemoves/
// suppressed). Also: run 2 confirmed CraftingStation.KnowsBlueprintForItem returns false+null for
// every valid item on every station, so BomDump now additionally probes the crafting TABLES
// (CraftInteraction._localBlueprints) directly — a second, more promising BOM path.
//
// v0.1.3 — run 3: transition logging + table BOM dump both confirmed working (434 lines vs. 3,580;
// every table recipe manifest printed cleanly), but KnowsBlueprintForItem STILL returns false+null
// at both station and table level even when the SAME table's _localBlueprints demonstrably held
// the item's blueprint. Two small diagnostics added: WidgetWatcher now logs the six complaint
// maps' raw sizes (once on connect, thereafter on change) to tell "genuinely empty this save" from
// "our enumeration sees nothing silently"; BomDump now also logs a pointer-identity comparison
// between a task item's ItemInfo and the name-matched blueprint's produced ItemInfo, to tell
// "different native instance" from "the method's semantics are just something else".
//
// v0.2.0 — Phase 1a actuation spike: the FIRST write-capable machinery in this mod. A hotkey
// (SpikeHotkey) toggles a manual "boost" of one workstation task's priority (ActuationSpike),
// with guaranteed revert (same hotkey, or an auto-revert timer) and a crash-safe write-ahead file
// ledger (SpikeLedger) that restores a stranded boost at the next load of the same world. Purpose:
// an in-game spike to learn (a) what the priority Int32 actually means and (b) whether
// Rpc_ChangeTaskPriority is reachable at runtime. Phase 0 diagnostics above are unchanged and
// still read-only; actuation only ever fires on the hotkey or a ledger restore, and EnableSpike
// fully disarms the write machinery when set false.
//
// v0.2.1 — single-press auto-target: an empty SpikeStationFilter now auto-selects a station
// (CraftingStation, then CookingStation, then any, each requiring >= 2 tasks) and a SpikeTaskIndex
// of -1 (new default) auto-selects the last non-pinned task, so one F10 press with untouched
// config picks a target and applies the boost — no runtime config reload, by design (established
// project position: the reload-cadence cost isn't worth it unless required for mod function).
//
// v0.2.2 — on-screen toast feedback for every user-relevant spike outcome (boost/revert/blocked/
// no-target/ledger-restore), ported from MineRefreshMod's proven ShowMessage/OnGUI pattern, so the
// player doesn't have to alt-tab to the log to see which station got boosted.
//
// v0.2.3 — test run 1 findings: priority is a RANK POSITION, continuously renumbered from list
// order (priority == list index, 0 = top) — not an arbitrary value; ws.NetworkWorkstations was
// null/empty at runtime, so the rpc value-write path never fired; and the direct value write
// (WorkstationTaskData.SetPriority) got squashed the instant RefreshTaskDataPriorities()
// re-derived priority from list order. Actuation switched to LIST-MOVE (RemoveAt+Insert on the
// live Workstation._taskDatas backing list, then RefreshTaskDataPriorities to renumber), with a
// setter probe and a NetworkCraftingStation component probe added for diagnostics.
//
// v0.3.0 — Phase 1a CONFIRMED in-game (boost/revert/ledger crash-restore all verified). Phase 1b:
// the complaint-driven controller (SupplyController) — a per-item demand state machine fed by
// ComplaintLog's ADD/re-add signal, actuating through the SAME RankActuator write path the spike
// uses. Starts in DRY-RUN (logs/toasts intended actions only); ControllerArmHotkey (default F11)
// arms it for live actuation. Per demand: duty-cycle boost/hold/cooldown, a capacity VERDICT +
// lockout after MaxBoostCyclesPerDemand cycles with the demand still persisting (capacity-limited,
// not a priority problem), and a militia-alarm lockout that pauses new boosts during combat. The
// spike and the controller share RankActuator.BoostedStationKeys so they never target the same
// station. The now-inert SpikePath config (its rpc/value-write path was superseded by list-move
// actuation in v0.2.3) is removed.
//
// v0.3.1 — in-game test 1 (co-op save, controller ran clean, zero errors): demands WERE captured
// but item complaints arrive in BURSTS with >20s gaps between bursts, so every demand went stale
// under the old DemandStaleSeconds=20 before ever reaching the 45s hysteresis — zero demands ever
// became boost-eligible. DemandStaleSeconds is renamed DemandRetainSeconds (default 180s, spans the
// observed burst gaps) — the KEY is renamed, not just the default, so the new default takes effect
// despite BepInEx keeping a stale value for any config that still has the old key (an orphaned old
// key line is harmless). New MinDemandSpanSeconds (default 30s) requires a demand's first and last
// sightings to span at least that long before it's boost-eligible — a burst-tolerant filter that
// still excludes one-off transient complaints. Also added: a "demand created" log line and a
// compact per-demand status detail (when <= 8 demands) for visibility into what's being tracked.
//
// v0.3.2 — in-game test 2: no bug — state machine correct, zero errors. All matured demands
// correctly hit "no boostable task" (gathered raw materials, nothing crafts them); the CRAFTABLE
// demands seen (Iron Bar, Large Iron Pickaxe Head) were created too late in the session to mature.
// Run-2 lesson: input-manifest complaints mostly demand raw materials; craftable demands need
// longer soaks before they mature through hysteresis/span. Purely observability — a creation-time
// craftable peek (advisory, never gates actuation) plus a "watching" toast, so the player isn't
// left guessing for minutes whether anything actionable is maturing. No behavior change to the
// eligibility gates or actuation path; no config changes.
//
// v0.3.3 — in-game test 3 succeeded end-to-end (watching -> dry-run -> armed -> real BOOST -> hold
// -> release, ledger clean), but the player had ALREADY top-ranked the starving item themselves —
// priority wasn't the bottleneck, so the boost was a 0->0 no-op that still burned a cycle toward a
// bogus capacity verdict. Added the mod's first real F4 "verify the action can take effect"
// precondition: TryFindTarget now returns a three-way outcome (Found/AlreadyTop/None) and never
// selects an already-top-ranked task, instead backing off with a strike counter and issuing its own
// VERDICT (limited by inputs/capacity, not priority) after 2 strikes — a diagnosis, so it fires in
// both armed and dry-run. Also added demand maturity memory: a slow-cadence demand (bursts minutes
// apart) that goes stale mid-hold no longer has to re-mature from zero on re-creation — new
// DemandMemoryMinutes (default 15) config controls the credit window.
//
// v0.3.4 — the creation-time peek now labels already-top demands truthfully (still craftable, just
// maxed) instead of collapsing them into "no crafting task" and dropping their watching toast.
//
// v0.3.5 — run 5 milestone: control loop closed end-to-end (boost -> production resumed ->
// complaints stopped -> early release on demand-cleared, zero errors). Filter-aware target rank:
// the controller never boosts above a hidden FiltersTaskData (fuel/filler) — confirmed in-game,
// Campfire task[0] is a hidden fuel-filter task the UI never shows; the effective target is now the
// first non-filter position when that's lower than BoostToRank. StationBusy outcome: a demand
// whose only matching station is locked by another boost now rotates in (retry in 20s, no
// strike/backoff) instead of being mislabeled "no boostable task" and going stale — fixes a
// starvation case where a mature demand never got a turn while another demand held the station.
//
// v0.4.0 — Phase 2a: warehouse (ResourceStorage) allotment diagnostics + a dry-run clog detector.
// TaskDump gained a ResourceStorageTaskData probe (storageSupply's taskMaxQuota/defaultTaskPriority
// /IsAvailable()/taskContainer). New WarehouseWatch (read-only): builds each warehouse's allotment
// table (task item/qty/stored/met/rank/maxQuota/supplyOwner) on a poll cadence, probes for a
// NetworkCompositeResourceStorage/NetworkSimpleResourceStorage component once per warehouse per
// world (fire-verification for a future write path), and — fed by the storage-full widget events
// (AddStorageFullComplaint/RemoveStorageFullComplaint) — runs a dry-run clog detector: when a
// complaining station's inventory is dominated by one item AND that item has a warehouse allotment
// task, it logs/toasts a VERDICT (all allotments met = drain-boost candidate; no allotment task =
// a different problem than priority). No actuation — read-only, same as every other diagnostic.
//
// v0.4.2 — first in-game run (v0.4.1, 2026-07-13) follow-up: a composite warehouse can hold
// MULTIPLE tasks for the same item (one per child sub-storage supply), so a warehouse-wide
// GetItemQuantity compared per-task was too coarse (duplicate rows for the same item/warehouse).
// WarehouseWatch now also reads the per-supply ResourceStorage.GetGatheredItemQuantity and the
// warehouse's ItemCollection.GetTotalRemainingCapacity every poll (both logged raw, semantics
// still being learned), groups duplicate (Warehouse, Qty, Stored) rows in clog VERDICT text
// instead of listing them separately, and adds a STALL escalation for the "watching" case (unmet
// allotment, haulers should be draining): if the unmet row's Stored hasn't moved for
// ClogStallMinutes, it diagnoses capacity-blocked (room=0) vs. likely priority-shadowed (room>0)
// instead of leaving the player to guess. Still fully read-only.
//
// v0.4.3 — same v0.4.2 changeset; a nullable-reference warning (CS8602) surfaced by the STALL
// escalation code was fixed after the v0.4.2 DLL had already deployed, so the SAC bump guard
// required a further version bump to redeploy the corrected build.
//
// v0.5.0 — Phase 2b: quota-raise drain actuator (hotkey spike). New QuotaSpike/QuotaActuator write
// a warehouse ResourceStorageTaskData's QUOTA (itemInfoQuantity.quantity) instead of its rank —
// F7 hotkey, gated by EnableQuotaSpike. Two modes on ClogForgeItem: empty = micro-test (auto-picks
// a met+room>0 warehouse allotment, raises its quota by min(donor stock, room, QuotaBoostMaxDelta),
// watches for hauler response, auto-reverts on target-met or QuotaSpikeMaxHoldSeconds timeout);
// non-empty = forge (clamps that item's quota DOWN to its current stored count to deliberately shut
// intake for other diagnostics — no auto-revert, F7-only). SpikeLedger's schema is extended with
// Kind ("rank"|"quota") and SupplyOwner, backward-compatible with pre-v0.5.0 8-field lines (parsed
// as Kind="rank"). Rails: room>0 required, delta capped, pinned tasks skipped, HasStateAuthority
// gate, write-ahead ledger with crash-safe restore at next world load — same discipline as every
// other actuator in this mod.
//
// v0.5.1 — same v0.5.0 changeset; an unrelated pre-existing unused-variable warning (CS0219, an
// `isNew` local in ComplaintPatches.cs never read) surfaced by the "0 warnings" build requirement
// was fixed after the v0.5.0 DLL had already deployed, so the SAC bump guard required a further
// version bump to redeploy the corrected build (same pattern as v0.4.3).
//
// v0.5.2 — run-1 findings (v0.5.1 loaded clean, zero errors, but the micro-test found no eligible
// pair: "groups=452 met=32 room=0=22 noSourceStock=10"). Five changes: (1) true-warehouse filter —
// gathering-hut self-storage rows (storageSupply.taskContainer == null) were diluting the candidate
// pool and can never be real drain targets, so they're now excluded from both micro-test and forge
// candidate selection (taskContainer != null distinguishes a real warehouse row, confirmed in-game
// 2026-07-13); (2) a "no candidate" outcome now logs the top 3 near-miss groups with full
// gate-failure detail for data-driven threshold tuning; (3) forge now clamps an ENTIRE multi-row
// group (a composite warehouse can hold several rows for the same item) so the group sum equals
// stored exactly, instead of aborting as a false "already shut" when no single row's qty exceeded
// warehouse-wide stored; (4) reverting a forge clamp now transitions into a read-only "drain watch"
// (same response/target-met/timeout semantics as micro-test) instead of just clearing, so the
// artificial stockpile's drain can still be observed; (5) forge moved off the plain hotkey onto
// Shift+<QuotaSpikeHotkey> — the plain key is now unambiguously always micro-test. Also confirmed:
// warehouse task priority is a VALUE (tier: observed 0/1/2/3, NOT list-position-derived like
// crafting-station priority) — no rank-reorder logic exists or is added to any warehouse path.
//
// v0.6.0 — Phase 2c: MetabolicPlane (per-warehouse-item stored-count ring buffers + net-rate
// derivatives, the Phase 2d rate-based-calibration data layer) + ClogController (automates the
// 2b-proven quota-raise drain: WarehouseWatch's VERDICT A feed -> a duty-cycled single-row quota
// boost through the SAME QuotaActuator/SpikeLedger/BoostedStationKeys write path QuotaSpike uses,
// with a hauler-response window sized from the measured 13s response latency, early release on
// clog-cleared/target-met, and capacity VERDICTs after 2 ineffective cycles or 3 no-target strikes).
// ClogController shares SupplyController's F11 Armed flag and militia-alarm lockout — dry-run until
// armed, exactly like the other actuators in this mod. New WarehouseTasks.cs hoists the warehouse
// row scan (BuildRows/FindQuotaTask/GetSupplyOwner) out of QuotaSpike so ClogController can reuse it
// without duplicating the WarehouseClassList filter logic — no behavior change to QuotaSpike itself.
//
// v0.6.1: clog-ctl storage-full gates use registry-active (10 min) instead of 60/90 s freshness;
// one-shot gate-skip log.
//
// v0.8.0 — Phase 2d dry-run budget engine (BudgetPlane): the READ-ONLY brain the v0.7.0 spike
// fire-verified write levers were built for. Per (warehouse, item) true-warehouse group (hut
// self-storage rows excluded — never a budgeting target), it computes a need-based quota target
// from MetabolicPlane's measured drain rate (MinQuota floor + HeadroomMinutes of drain, capped at
// MaxQuotaShareOfMaxPct of the item's physical max via GetMaximumStorageCapacity(info, false) —
// only the 2-arg overload exists) and classifies the group: HOG (quota already crowding storage
// far beyond what's consumed — proposes a quota reduce + the excess to evict), SHADOWED (unmet,
// room to fill, low/no rank, but not filling — proposes a tier bump to High), or healthy/no-data.
// Every proposal is LOGGED ONLY, throttled to when the proposal set actually changes (or on a full
// table dump) to keep steady-state log volume sane — no arming path exists in this version at all;
// nothing is written, no quota, no DropItem eviction, no SetPriority. WarehouseWatch grew the two
// new AllotmentRow fields BudgetPlane needs (HasTaskContainer, MaxCapacity — the latter read-once-
// per-item-per-warehouse-poll and gated behind EnableBudgetPlane so the capacity call is skipped
// entirely when the plane is off) and now calls BudgetPlane.Evaluate after its own clog detector
// each poll. The v0.7.0-proven write levers (quota write, DropItem eviction, tier
// SetPriority+HostUpdateTasks) are wired to this brain in a later version.
//
// v0.7.0 — Phase 2d fire-verify spike: the reshaped Phase 2d design (soft quota budgeting +
// down-to-quota eviction + a tier lever, docs/mods/supply-chain.md "Design Direction") depends on
// two runtime unknowns that were only Cecil-confirmed, never fire-verified: (a) does
// ItemContainer.DropItem actually spawn a ground item and decrement the container when reached from
// a warehouse row's storageSupply.interaction.Container, and (b) does Rpc_ChangeTaskPriority
// actually move a warehouse row's TIER (High=0/Med=1/Low=2/None=3 VALUE — never a rank/list-move).
// New EvictionSpike answers both behind one hotkey (EvictSpikeHotkey, default F6): the plain key
// runs a one-shot, unledgered drop test (auto-picks the best-stocked true-warehouse row, drops
// EvictDropCount units, schedules a one-tick-later delayed re-check); Shift+key runs a stateful,
// write-ahead-ledgered (Kind="tier") tier test that boosts a Med row to High, watches which write
// path actually lands (RPC first, direct SetPriority fallback, with a WRONG-ROW detector), holds,
// then reverts — logging every phase transition LOUDLY so both unknowns come back unambiguously
// proven or disproven. No new Harmony patches (DropItem/Setup have inventory-family params, a
// forbidden patch target) — this is a pure hotkey/tick call-and-observe spike, same discipline as
// every other actuator in this mod.
[BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
public class Plugin : BasePlugin
{
    internal static ManualLogSource Logger = null!;

    // ── [General] ────────────────────────────────────────────────────────────────────────────
    internal static ConfigEntry<float> TickSeconds = null!;
    internal static ConfigEntry<string> DumpHotkey = null!;

    // ── [Diagnostics] ────────────────────────────────────────────────────────────────────────
    internal static ConfigEntry<bool> EnableDiagnostics = null!;
    internal static ConfigEntry<bool> PatchVillagerComplaints = null!;
    internal static ConfigEntry<float> WidgetPollSeconds = null!;
    internal static ConfigEntry<int> SweepStationsPerTick = null!;
    internal static ConfigEntry<string> IgnoredComplaintMessages = null!;

    // ── [Phase1Spike] ────────────────────────────────────────────────────────────────────────
    internal static ConfigEntry<bool> EnableSpike = null!;
    internal static ConfigEntry<string> SpikeHotkey = null!;
    internal static ConfigEntry<string> SpikeStationFilter = null!;
    internal static ConfigEntry<int> SpikeTaskIndex = null!;
    internal static ConfigEntry<int> SpikeNewPriority = null!;
    internal static ConfigEntry<float> SpikeAutoRevertSeconds = null!;

    // ── [Phase1Controller] ───────────────────────────────────────────────────────────────────
    internal static ConfigEntry<bool> EnableController = null!;
    internal static ConfigEntry<bool> ControllerStartArmed = null!;
    internal static ConfigEntry<string> ControllerArmHotkey = null!;
    internal static ConfigEntry<float> HysteresisSeconds = null!;
    internal static ConfigEntry<float> DemandRetainSeconds = null!;
    internal static ConfigEntry<float> MinDemandSpanSeconds = null!;
    internal static ConfigEntry<float> DemandMemoryMinutes = null!;
    internal static ConfigEntry<float> BoostHoldSeconds = null!;
    internal static ConfigEntry<float> CooldownSeconds = null!;
    internal static ConfigEntry<int> MaxBoostCyclesPerDemand = null!;
    internal static ConfigEntry<float> CapacityLockoutMinutes = null!;
    internal static ConfigEntry<int> MaxConcurrentBoosts = null!;
    internal static ConfigEntry<int> BoostToRank = null!;
    internal static ConfigEntry<string> StationClassAllowList = null!;
    internal static ConfigEntry<float> AlarmLockoutSeconds = null!;

    // ── [Phase2Warehouse] ────────────────────────────────────────────────────────────────────
    internal static ConfigEntry<bool> EnableWarehouseWatch = null!;
    internal static ConfigEntry<float> WarehousePollSeconds = null!;
    internal static ConfigEntry<string> WarehouseClassList = null!;
    internal static ConfigEntry<int> ClogDominantSharePercent = null!;
    internal static ConfigEntry<float> ClogRealertMinutes = null!;
    internal static ConfigEntry<float> ClogStallMinutes = null!;

    // ── [Phase2Spike] ────────────────────────────────────────────────────────────────────────
    internal static ConfigEntry<bool> EnableQuotaSpike = null!;
    internal static ConfigEntry<string> QuotaSpikeHotkey = null!;
    internal static ConfigEntry<int> QuotaBoostMaxDelta = null!;
    internal static ConfigEntry<float> QuotaSpikeMaxHoldSeconds = null!;
    internal static ConfigEntry<int> QuotaMinStationStock = null!;
    internal static ConfigEntry<string> ClogForgeItem = null!;

    // ── [Phase2Clog] ─────────────────────────────────────────────────────────────────────────
    internal static ConfigEntry<bool> EnableClogController = null!;
    internal static ConfigEntry<float> ClogRetainSeconds = null!;
    internal static ConfigEntry<float> ClogHysteresisSeconds = null!;
    internal static ConfigEntry<int> ClogBoostMaxDelta = null!;
    internal static ConfigEntry<float> ClogResponseWindowSeconds = null!;
    internal static ConfigEntry<float> ClogHoldMaxSeconds = null!;
    internal static ConfigEntry<float> ClogCooldownSeconds = null!;
    internal static ConfigEntry<int> ClogMaxCyclesPerItem = null!;
    internal static ConfigEntry<float> ClogCapacityLockoutMinutes = null!;
    internal static ConfigEntry<int> MaxConcurrentClogBoosts = null!;

    // ── [Metabolic] ──────────────────────────────────────────────────────────────────────────
    internal static ConfigEntry<bool> EnableMetabolicPlane = null!;
    internal static ConfigEntry<float> MetabolicWindowSeconds = null!;
    internal static ConfigEntry<float> MetabolicStatusMinutes = null!;

    // ── [Phase2dSpike] ───────────────────────────────────────────────────────────────────────
    internal static ConfigEntry<bool> EnableEvictSpike = null!;
    internal static ConfigEntry<string> EvictSpikeHotkey = null!;
    internal static ConfigEntry<string> EvictItem = null!;
    internal static ConfigEntry<int> EvictDropCount = null!;
    internal static ConfigEntry<string> TierItem = null!;
    internal static ConfigEntry<float> TierRpcTimeoutSeconds = null!;
    internal static ConfigEntry<float> TierHoldSeconds = null!;
    internal static ConfigEntry<float> TierAbortSeconds = null!;

    // ── [Phase2dBudget] ──────────────────────────────────────────────────────────────────────
    internal static ConfigEntry<bool> EnableBudgetPlane = null!;
    internal static ConfigEntry<float> BudgetWindowSeconds = null!;
    internal static ConfigEntry<int> MinQuota = null!;
    internal static ConfigEntry<float> HeadroomMinutes = null!;
    internal static ConfigEntry<int> MaxQuotaShareOfMaxPct = null!;
    internal static ConfigEntry<int> HogQuotaShareOfMaxPct = null!;
    internal static ConfigEntry<int> HogMinStored = null!;
    internal static ConfigEntry<float> HogMaxAbsRate = null!;
    internal static ConfigEntry<float> ShadowMaxFillRate = null!;

    public override void Load()
    {
        Logger = base.Log;

        TickSeconds = Config.Bind(
            section: "General",
            key: "TickSeconds",
            defaultValue: 5.0f,
            description: "Master tick interval (seconds) for the SupplyChain tracker — drives the rolling composition-sweep pacing and the ~60s post-load auto TaskDump.");

        DumpHotkey = Config.Bind(
            section: "General",
            key: "DumpHotkey",
            defaultValue: "F9",
            description: "Hotkey (UnityEngine.KeyCode name) that triggers a full on-demand dump: task datas (TaskDump), the crafting BOM probe (BomDump), and an immediate full composition sweep (CompositionSweep). Read-only.");

        EnableDiagnostics = Config.Bind(
            section: "Diagnostics",
            key: "EnableDiagnostics",
            defaultValue: true,
            description: "Master switch for SupplyChain diagnostic logging. Shipped default TRUE (Phase 0 diagnostic mod, per project convention) — flip to false once you're done measuring.");

        PatchVillagerComplaints = Config.Bind(
            section: "Diagnostics",
            key: "PatchVillagerComplaints",
            defaultValue: true,
            description: "Whether the VillagerSocial.AddComplaint/RemoveComplaint Harmony patches (ComplaintWatcher) are applied at load. Safety valve: if this patch pair ever crashes the game at plugin load, flip this to false — the rest of the mod (WidgetWatcher, TaskDump, CompositionSweep, BomDump) keeps working.");

        WidgetPollSeconds = Config.Bind(
            section: "Diagnostics",
            key: "WidgetPollSeconds",
            defaultValue: 10.0f,
            description: "How often (seconds) WidgetWatcher polls SettlementIssueTrackerWidget's complaint maps for deltas (entries appearing/disappearing).");

        SweepStationsPerTick = Config.Bind(
            section: "Diagnostics",
            key: "SweepStationsPerTick",
            defaultValue: 1,
            description: "How many stations CompositionSweep's rolling pass processes per master tick (TickSeconds). Higher = faster full coverage but more per-tick cost. A full pass also runs immediately on DumpHotkey.");

        IgnoredComplaintMessages = Config.Bind(
            section: "Diagnostics",
            key: "IgnoredComplaintMessages",
            defaultValue: "feelUnsafe",
            description: "Comma-separated, case-insensitive substrings. A GenericMessageComplaint whose message contains any of these (or exactly matches the game's own 'feels unsafe at work/home' complaint text) is NOT logged — neither its ADD nor its matching REMOVE. v0.1.1 default suppresses the 'feels unsafe' combat complaints that scrolled the console on a defenseless test save. Empty = suppress nothing.");

        EnableSpike = Config.Bind(
            section: "Phase1Spike",
            key: "EnableSpike",
            defaultValue: true,
            description: "Master switch for the v0.2.0 actuation spike (ActuationSpike/SpikeLedger) — shipped default TRUE (dev/diagnostic convention). Actuation only ever fires on the SpikeHotkey press or a ledger restore at world load; set false to fully disarm the write machinery.");

        SpikeHotkey = Config.Bind(
            section: "Phase1Spike",
            key: "SpikeHotkey",
            defaultValue: "F10",
            description: "Hotkey (UnityEngine.KeyCode name) that toggles a boost/revert of the configured target task's priority.");

        SpikeStationFilter = Config.Bind(
            section: "Phase1Spike",
            key: "SpikeStationFilter",
            defaultValue: "",
            description: "Substring (case-insensitive) of the target station's display name. Empty = auto-select the first CraftingStation (then CookingStation, then any) station with >= 2 tasks. Set a substring to force a specific station instead.");

        SpikeTaskIndex = Config.Bind(
            section: "Phase1Spike",
            key: "SpikeTaskIndex",
            defaultValue: -1,
            description: "-1 = auto (last non-pinned task — the informative choice for the priority-semantics experiment); >= 0 = explicit task index.");

        SpikeNewPriority = Config.Bind(
            section: "Phase1Spike",
            key: "SpikeNewPriority",
            defaultValue: 1,
            description: "Target RANK INDEX (list position, 0 = top). Test finding: priority int == list index; the spike now MOVES the task to this position and lets the game renumber.");

        SpikeAutoRevertSeconds = Config.Bind(
            section: "Phase1Spike",
            key: "SpikeAutoRevertSeconds",
            defaultValue: 120f,
            description: "Seconds after a boost is applied before it is automatically reverted, even if the hotkey is never pressed again.");

        EnableController = Config.Bind(
            section: "Phase1Controller",
            key: "EnableController",
            defaultValue: true,
            description: "Master switch for the v0.3.0 complaint-driven autopilot (SupplyController) — shipped default TRUE (dev/diagnostic convention). Even when enabled, the controller only OBSERVES and logs/toasts intended actions until armed (ControllerStartArmed or the ControllerArmHotkey) — set false to fully disarm it (including dry-run logging).");

        ControllerStartArmed = Config.Bind(
            section: "Phase1Controller",
            key: "ControllerStartArmed",
            defaultValue: false,
            description: "true = skip dry-run and actuate from world load — leave false until the dry-run has been reviewed. Also the value the controller resets to on every world-leave, regardless of in-session ControllerArmHotkey toggles.");

        ControllerArmHotkey = Config.Bind(
            section: "Phase1Controller",
            key: "ControllerArmHotkey",
            defaultValue: "F11",
            description: "Hotkey (UnityEngine.KeyCode name) that toggles the autopilot between armed (live actuation) and dry-run (logging/toasting only). Works even with no active world session.");

        HysteresisSeconds = Config.Bind(
            section: "Phase1Controller",
            key: "HysteresisSeconds",
            defaultValue: 45f,
            description: "How long (seconds) a demand must be continuously fresh (since first seen) before the controller will consider boosting it — avoids reacting to a complaint that clears itself almost immediately.");

        DemandRetainSeconds = Config.Bind(
            section: "Phase1Controller",
            key: "DemandRetainSeconds",
            defaultValue: 180f,
            description: "How long (seconds) a demand stays live after its last complaint sighting. Item complaints arrive in bursts (villager re-attempts the recipe), so this must exceed the typical gap between bursts; a demand not seen for this long is dropped (with an early revert if it was being boosted).");

        MinDemandSpanSeconds = Config.Bind(
            section: "Phase1Controller",
            key: "MinDemandSpanSeconds",
            defaultValue: 30f,
            description: "A demand only becomes boost-eligible once its first and last sightings are at least this many seconds apart — filters one-off transient complaints without requiring continuous re-complaining.");

        DemandMemoryMinutes = Config.Bind(
            section: "Phase1Controller",
            key: "DemandMemoryMinutes",
            defaultValue: 15f,
            description: "If a matured demand goes stale and the same item is demanded again within this many minutes, it re-matures almost immediately (span credit) instead of restarting from zero — needed for slow-cadence complaints (e.g. cooking) whose bursts are minutes apart.");

        BoostHoldSeconds = Config.Bind(
            section: "Phase1Controller",
            key: "BoostHoldSeconds",
            defaultValue: 180f,
            description: "How long (seconds) an armed boost is held before it's automatically reverted (moving the demand to Cooldown), even if the demand is still active.");

        CooldownSeconds = Config.Bind(
            section: "Phase1Controller",
            key: "CooldownSeconds",
            defaultValue: 120f,
            description: "How long (seconds) after a revert before a still-fresh demand is eligible to be boosted again (up to MaxBoostCyclesPerDemand).");

        MaxBoostCyclesPerDemand = Config.Bind(
            section: "Phase1Controller",
            key: "MaxBoostCyclesPerDemand",
            defaultValue: 2,
            description: "How many boost/revert cycles a single persistent demand gets before the controller concludes it's capacity-limited (not a priority problem) and issues a VERDICT alert + lockout instead of boosting again.");

        CapacityLockoutMinutes = Config.Bind(
            section: "Phase1Controller",
            key: "CapacityLockoutMinutes",
            defaultValue: 15f,
            description: "Minutes a demand stays in CapacityLockout (no further boost attempts, just an earlier alert) after exhausting MaxBoostCyclesPerDemand, before the controller resets its cycle count and re-evaluates it from scratch.");

        MaxConcurrentBoosts = Config.Bind(
            section: "Phase1Controller",
            key: "MaxConcurrentBoosts",
            defaultValue: 2,
            description: "Maximum number of demands the controller will have actively Boosting at the same time.");

        BoostToRank = Config.Bind(
            section: "Phase1Controller",
            key: "BoostToRank",
            defaultValue: 0,
            description: "Target rank index (list position, 0 = top) the controller moves a demanded item's task to when it boosts. The controller never boosts above hidden filter tasks (fuel/filler) — the effective target is the first non-filter position when that is lower.");

        StationClassAllowList = Config.Bind(
            section: "Phase1Controller",
            key: "StationClassAllowList",
            defaultValue: "CraftingStation,CookingStation,BloomStation",
            description: "Comma-separated native-class-name prefixes (Common.NativeClassName(entry.Ws)) the controller is allowed to target. Stations whose class doesn't start with any listed prefix are never chosen as a boost target.");

        AlarmLockoutSeconds = Config.Bind(
            section: "Phase1Controller",
            key: "AlarmLockoutSeconds",
            defaultValue: 90f,
            description: "Seconds after a militia alarm (Complaint.c_militia_alarm) during which the controller starts NO new boosts — existing Boosting demands still advance/revert normally. Avoids competing with combat/defense priorities.");

        EnableWarehouseWatch = Config.Bind(
            section: "Phase2Warehouse",
            key: "EnableWarehouseWatch",
            defaultValue: true,
            description: "Master switch for the v0.4.0 warehouse (ResourceStorage) allotment diagnostics + dry-run clog detector (WarehouseWatch) — shipped default TRUE (Phase 2a read-only diagnostic, dev convention). Read-only: no game-state writes.");

        WarehousePollSeconds = Config.Bind(
            section: "Phase2Warehouse",
            key: "WarehousePollSeconds",
            defaultValue: 30.0f,
            description: "How often (seconds) WarehouseWatch polls warehouses' allotment tables and re-checks the dry-run clog detector.");

        WarehouseClassList = Config.Bind(
            section: "Phase2Warehouse",
            key: "WarehouseClassList",
            defaultValue: "ResourceStorage",
            description: "Comma-separated, case-sensitive, EXACT native class names (Common.NativeClassName(entry.Ws)) treated as warehouses.");

        ClogDominantSharePercent = Config.Bind(
            section: "Phase2Warehouse",
            key: "ClogDominantSharePercent",
            defaultValue: 40,
            description: "A complaining station's inventory is considered 'dominated' by its single largest item when that item's share of total stored quantity is at least this percent (0-100).");

        ClogRealertMinutes = Config.Bind(
            section: "Phase2Warehouse",
            key: "ClogRealertMinutes",
            defaultValue: 10.0f,
            description: "Minutes between repeat on-screen toasts for the same clog VERDICT (station+item). The log line still fires every poll regardless — this only throttles the toast.");

        ClogStallMinutes = Config.Bind(
            section: "Phase2Warehouse",
            key: "ClogStallMinutes",
            defaultValue: 3f,
            description: "Minutes a watching-case unmet allotment must show an unchanged warehouse stored-count before a STALL diagnosis line/toast fires — distinguishes capacity-blocked (warehouse has no room) from priority-shadowed (room exists, haulers just aren't going).");

        EnableQuotaSpike = Config.Bind(
            section: "Phase2Spike",
            key: "EnableQuotaSpike",
            defaultValue: true,
            description: "Master switch for the v0.5.0 quota-raise drain actuator spike (QuotaSpike/QuotaActuator) — shipped default TRUE (dev/diagnostic convention). Gates the hotkey (plain + Shift) AND its ledger restore at world load; set false to fully disarm the write machinery.");

        QuotaSpikeHotkey = Config.Bind(
            section: "Phase2Spike",
            key: "QuotaSpikeHotkey",
            defaultValue: "F7",
            description: "Hotkey (UnityEngine.KeyCode name) for the quota spike. Plain key = micro-test boost/revert. Shift+key = forge clamp/revert (requires ClogForgeItem set). Either combination reverts/clears whatever is currently active (boost, forge, or a post-forge drain watch).");

        QuotaBoostMaxDelta = Config.Bind(
            section: "Phase2Spike",
            key: "QuotaBoostMaxDelta",
            defaultValue: 50,
            description: "Cap on how far a warehouse task's quota is raised above its current value in micro-test mode. Quota raises can't be un-hauled — the extra stock occupies warehouse capacity until consumed — so this is never 'boost to lots'.");

        QuotaSpikeMaxHoldSeconds = Config.Bind(
            section: "Phase2Spike",
            key: "QuotaSpikeMaxHoldSeconds",
            defaultValue: 300f,
            description: "Micro-test mode auto-revert timeout (seconds) when no hauler response (stored count never reaches the boosted quota).");

        QuotaMinStationStock = Config.Bind(
            section: "Phase2Spike",
            key: "QuotaMinStationStock",
            defaultValue: 10,
            description: "Minimum station-side stock of an item for that station to count as a viable donor source in micro-test mode's auto-selection.");

        ClogForgeItem = Config.Bind(
            section: "Phase2Spike",
            key: "ClogForgeItem",
            defaultValue: "",
            description: "Item display name for forge mode, fired by Shift+<QuotaSpikeHotkey> (plain key is always micro-test — this no longer replaces the plain-key mode). Empty = Shift+hotkey does nothing (logs/toasts a reminder to set this). When set: clamps ALL of that item's rows in its best-stocked warehouse DOWN so their sum equals the warehouse's current stored count exactly, deliberately shutting intake to reproduce a clog for other diagnostics (test harness only — no auto-revert; reverting transitions into a read-only drain watch).");

        EnableClogController = Config.Bind(
            section: "Phase2Clog",
            key: "EnableClogController",
            defaultValue: true,
            description: "Master switch for the v0.6.0 automated clog drain controller (ClogController) — shipped default TRUE (dev/diagnostic convention). Only ever actuates when armed (shares SupplyController's ControllerStartArmed/ControllerArmHotkey F11 flag) — writes nothing in dry-run.");

        ClogRetainSeconds = Config.Bind(
            section: "Phase2Clog",
            key: "ClogRetainSeconds",
            defaultValue: 240f,
            description: "How long (seconds) a tracked clog stays live after its last VERDICT A sighting. WarehouseWatch's VERDICT A refreshes roughly every WarehousePollSeconds (~30s) while a clog persists, so this must comfortably exceed that poll cadence; a clog not re-sighted for this long is dropped (with an early revert if it was being boosted) — stale means the clog is gone.");

        ClogHysteresisSeconds = Config.Bind(
            section: "Phase2Clog",
            key: "ClogHysteresisSeconds",
            defaultValue: 45f,
            description: "How long (seconds) a clog must be continuously tracked (since first VERDICT A) before the controller will consider boosting it — avoids reacting to a clog that clears itself almost immediately.");

        ClogBoostMaxDelta = Config.Bind(
            section: "Phase2Clog",
            key: "ClogBoostMaxDelta",
            defaultValue: 50,
            description: "Cap on how far a warehouse task's quota is raised above its current value. Quota raises can't be un-hauled — the extra stock occupies warehouse capacity until consumed — bounded the same way QuotaSpike's QuotaBoostMaxDelta is.");

        ClogResponseWindowSeconds = Config.Bind(
            section: "Phase2Clog",
            key: "ClogResponseWindowSeconds",
            defaultValue: 60f,
            description: "Seconds after a boost before the controller reverts as 'no-response' if no hauler activity has been detected yet. Sized from the measured ~13s hauler response latency (QuotaSpike test runs) with margin.");

        ClogHoldMaxSeconds = Config.Bind(
            section: "Phase2Clog",
            key: "ClogHoldMaxSeconds",
            defaultValue: 600f,
            description: "Absolute maximum seconds a boost is held before it is force-reverted (moving the clog to Cooldown), even if haulers responded and are still working on it.");

        ClogCooldownSeconds = Config.Bind(
            section: "Phase2Clog",
            key: "ClogCooldownSeconds",
            defaultValue: 120f,
            description: "How long (seconds) after a revert before a still-fresh clog is eligible to be boosted again (up to ClogMaxCyclesPerItem).");

        ClogMaxCyclesPerItem = Config.Bind(
            section: "Phase2Clog",
            key: "ClogMaxCyclesPerItem",
            defaultValue: 2,
            description: "How many boost/revert cycles a single persistent clog item gets before the controller concludes it's a warehouse capacity/hauler-shortage problem (not a quota problem) and issues a VERDICT alert + lockout instead of boosting again.");

        ClogCapacityLockoutMinutes = Config.Bind(
            section: "Phase2Clog",
            key: "ClogCapacityLockoutMinutes",
            defaultValue: 15f,
            description: "Minutes a clog stays in CapacityLockout (no further boost attempts) after exhausting ClogMaxCyclesPerItem or 3 no-target strikes, before the controller resets its cycle/strike counts and re-evaluates it from scratch.");

        MaxConcurrentClogBoosts = Config.Bind(
            section: "Phase2Clog",
            key: "MaxConcurrentClogBoosts",
            defaultValue: 1,
            description: "Maximum number of clog items the controller will have actively Boosting at the same time.");

        EnableMetabolicPlane = Config.Bind(
            section: "Metabolic",
            key: "EnableMetabolicPlane",
            defaultValue: true,
            description: "Master switch for the v0.6.0 metabolic plane (MetabolicPlane) — per-(warehouse,item) stored-count ring buffers feeding net-rate derivatives for the future Phase 2d rate-based quota calibration. Shipped default TRUE (dev/diagnostic convention); purely a data layer, never writes game state.");

        MetabolicWindowSeconds = Config.Bind(
            section: "Metabolic",
            key: "MetabolicWindowSeconds",
            defaultValue: 180f,
            description: "Lookback window (seconds) used when computing a key's net rate (endpoint-delta, not a linear fit) from its sample ring buffer.");

        MetabolicStatusMinutes = Config.Bind(
            section: "Metabolic",
            key: "MetabolicStatusMinutes",
            defaultValue: 5f,
            description: "How often (minutes) the metabolic plane logs its top 5 fastest-moving (|rate| >= 0.1/min) keys. 0 = disable the movers status line entirely (sampling itself is unaffected).");

        EnableEvictSpike = Config.Bind(
            section: "Phase2dSpike",
            key: "EnableEvictSpike",
            defaultValue: true,
            description: "Master switch for the v0.7.0 Phase 2d fire-verify spike (EvictionSpike) — shipped default TRUE (dev/diagnostic convention). Gates the hotkey (plain + Shift) AND the tier test's ledger restore at world load; set false to fully disarm the write machinery.");

        EvictSpikeHotkey = Config.Bind(
            section: "Phase2dSpike",
            key: "EvictSpikeHotkey",
            defaultValue: "F6",
            description: "Hotkey (UnityEngine.KeyCode name) for the Phase 2d spike. Plain key = one-shot ground-drop eviction test (ItemContainer.DropItem). Shift+key = stateful tier test (Rpc_ChangeTaskPriority boost/revert) — pressing Shift+key again while a tier test is active reverts it early.");

        EvictItem = Config.Bind(
            section: "Phase2dSpike",
            key: "EvictItem",
            defaultValue: "",
            description: "Item display name for the drop test. Empty = auto-select the best-stocked true-warehouse row's container.");

        EvictDropCount = Config.Bind(
            section: "Phase2dSpike",
            key: "EvictDropCount",
            defaultValue: 2,
            description: "Units to drop per drop-test press. Clamped to 1-5 at use time, and further capped by the target stack's actual count.");

        TierItem = Config.Bind(
            section: "Phase2dSpike",
            key: "TierItem",
            defaultValue: "",
            description: "Item display name for the tier test. Empty = auto-select the first Med-tier true-warehouse row.");

        TierRpcTimeoutSeconds = Config.Bind(
            section: "Phase2dSpike",
            key: "TierRpcTimeoutSeconds",
            defaultValue: 15f,
            description: "Per-phase confirmation window (seconds) before the tier test falls back to the next write path, or concludes a write/revert was ineffective.");

        TierHoldSeconds = Config.Bind(
            section: "Phase2dSpike",
            key: "TierHoldSeconds",
            defaultValue: 15f,
            description: "How long (seconds) the tier test holds the boosted row at High before automatically reverting it back to its original tier.");

        TierAbortSeconds = Config.Bind(
            section: "Phase2dSpike",
            key: "TierAbortSeconds",
            defaultValue: 180f,
            description: "Absolute abort rail (seconds) for the tier test state machine — if the test is still running after this long (any phase), it attempts one direct-write revert and force-clears, regardless of confirmation.");

        EnableBudgetPlane = Config.Bind(
            section: "Phase2dBudget",
            key: "EnableBudgetPlane",
            defaultValue: true,
            description: "Master switch for the v0.8.0 dry-run budget engine (BudgetPlane) — shipped default TRUE (dev/diagnostic convention). v0.8.0 is READ-ONLY analytics: no arming path exists in this version, it only computes and logs quota/tier proposals. Also gates WarehouseWatch's per-item MaxCapacity reads (GetMaximumStorageCapacity), which are otherwise skipped entirely.");

        BudgetWindowSeconds = Config.Bind(
            section: "Phase2dBudget",
            key: "BudgetWindowSeconds",
            defaultValue: 480f,
            description: "Lookback window (seconds) used when reading a group's net rate from MetabolicPlane. Ceiling is the metabolic ring's ~8 minute depth (16 samples at the ~30s WarehousePollSeconds cadence) — a larger window just means fewer samples land inside it.");

        MinQuota = Config.Bind(
            section: "Phase2dBudget",
            key: "MinQuota",
            defaultValue: 20,
            description: "Floor of every proposed quota target, regardless of measured drain rate.");

        HeadroomMinutes = Config.Bind(
            section: "Phase2dBudget",
            key: "HeadroomMinutes",
            defaultValue: 30f,
            description: "A proposed quota target holds this many minutes of measured drain above MinQuota — sizing the buffer so a quota isn't shaved down to exactly current consumption.");

        MaxQuotaShareOfMaxPct = Config.Bind(
            section: "Phase2dBudget",
            key: "MaxQuotaShareOfMaxPct",
            defaultValue: 50,
            description: "Proposed quota targets are capped at this percent (0-100) of the item's physical max (ItemCollection.GetMaximumStorageCapacity) — keeps a single item's proposed quota from ever crowding out the whole warehouse.");

        HogQuotaShareOfMaxPct = Config.Bind(
            section: "Phase2dBudget",
            key: "HogQuotaShareOfMaxPct",
            defaultValue: 90,
            description: "A group only counts as a HOG when its quotaSum is at least this percent (0-100) of the item's physical max — catches vanilla's default behavior of quota == unit max.");

        HogMinStored = Config.Bind(
            section: "Phase2dBudget",
            key: "HogMinStored",
            defaultValue: 100,
            description: "Minimum stockpile (stored count) for a group to count as a HOG — a maxed-out quota on a barely-stocked item isn't a real problem yet.");

        HogMaxAbsRate = Config.Bind(
            section: "Phase2dBudget",
            key: "HogMaxAbsRate",
            defaultValue: 0.2f,
            description: "A group only counts as a HOG when |rate|/min is at or below this — a static stockpile, not one still actively filling or draining.");

        ShadowMaxFillRate = Config.Bind(
            section: "Phase2dBudget",
            key: "ShadowMaxFillRate",
            defaultValue: 0.2f,
            description: "A group only counts as SHADOWED when |rate|/min is at or below this on an unmet allotment with room to fill — the stall signal that haulers aren't going despite room and demand (rank >= Med).");

        ClassInjector.RegisterTypeInIl2Cpp<SupplyChainTracker>();

        var go = new GameObject("SupplyChainMod_Tracker");
        UnityEngine.Object.DontDestroyOnLoad(go);
        go.AddComponent<SupplyChainTracker>();

        var harmony = new Harmony(MyPluginInfo.PLUGIN_GUID);

        // WidgetWatcher's push-method postfixes (safe param types: Workstation, HarvestMarkerComplaint)
        // are always patched — they're fire-verification only, no inventory-family params.
        Harmony.CreateAndPatchAll(typeof(Patches.AddStorageFullComplaintPatch));
        Harmony.CreateAndPatchAll(typeof(Patches.RemoveStorageFullComplaintPatch));
        Harmony.CreateAndPatchAll(typeof(Patches.AddMarkerComplaintPatch));
        Harmony.CreateAndPatchAll(typeof(Patches.AddMineExhaustedComplainPatch));

        // ComplaintWatcher is gated: AddComplaint(Complaint)/RemoveComplaint(Int32) are believed safe
        // (Complaint is Il2CppSystem.Object-derived, not inventory-family), but AddComplaint is
        // non-virtual (AOT-inlining risk) and this is new, unverified ground — PatchVillagerComplaints
        // is the safety valve if patch setup ever proves fatal at load.
        if (PatchVillagerComplaints.Value)
        {
            Harmony.CreateAndPatchAll(typeof(Patches.AddComplaintPatch));
            Harmony.CreateAndPatchAll(typeof(Patches.RemoveComplaintPatch));
            Logger.LogInfo("[SupplyChain] ComplaintWatcher armed.");
        }
        else
        {
            Logger.LogInfo("[SupplyChain] ComplaintWatcher DISABLED via PatchVillagerComplaints=false.");
        }

        Logger.LogInfo(
            $"[SupplyChain] SupplyChainMod v{MyPluginInfo.PLUGIN_VERSION} loaded — Phase 0 read-only flow diagnostic + Phase 1a actuation spike + Phase 1b controller + Phase 2a warehouse diagnostics + Phase 2b quota spike + Phase 2c metabolic plane/clog controller. " +
            $"TickSeconds={TickSeconds.Value} DumpHotkey='{DumpHotkey.Value}' EnableDiagnostics={EnableDiagnostics.Value} PatchVillagerComplaints={PatchVillagerComplaints.Value} " +
            $"WidgetPollSeconds={WidgetPollSeconds.Value} SweepStationsPerTick={SweepStationsPerTick.Value} IgnoredComplaintMessages='{IgnoredComplaintMessages.Value}' " +
            $"EnableSpike={EnableSpike.Value} SpikeHotkey='{SpikeHotkey.Value}' SpikeStationFilter='{SpikeStationFilter.Value}' SpikeTaskIndex={SpikeTaskIndex.Value} " +
            $"SpikeNewPriority={SpikeNewPriority.Value} SpikeAutoRevertSeconds={SpikeAutoRevertSeconds.Value} " +
            $"EnableController={EnableController.Value} ControllerStartArmed={ControllerStartArmed.Value} ControllerArmHotkey='{ControllerArmHotkey.Value}' " +
            $"HysteresisSeconds={HysteresisSeconds.Value} DemandRetainSeconds={DemandRetainSeconds.Value} MinDemandSpanSeconds={MinDemandSpanSeconds.Value} " +
            $"DemandMemoryMinutes={DemandMemoryMinutes.Value} BoostHoldSeconds={BoostHoldSeconds.Value} " +
            $"CooldownSeconds={CooldownSeconds.Value} MaxBoostCyclesPerDemand={MaxBoostCyclesPerDemand.Value} CapacityLockoutMinutes={CapacityLockoutMinutes.Value} " +
            $"MaxConcurrentBoosts={MaxConcurrentBoosts.Value} BoostToRank={BoostToRank.Value} StationClassAllowList='{StationClassAllowList.Value}' " +
            $"AlarmLockoutSeconds={AlarmLockoutSeconds.Value} " +
            $"EnableWarehouseWatch={EnableWarehouseWatch.Value} WarehousePollSeconds={WarehousePollSeconds.Value} WarehouseClassList='{WarehouseClassList.Value}' " +
            $"ClogDominantSharePercent={ClogDominantSharePercent.Value} ClogRealertMinutes={ClogRealertMinutes.Value} ClogStallMinutes={ClogStallMinutes.Value} " +
            $"EnableQuotaSpike={EnableQuotaSpike.Value} QuotaSpikeHotkey='{QuotaSpikeHotkey.Value}' QuotaBoostMaxDelta={QuotaBoostMaxDelta.Value} " +
            $"QuotaSpikeMaxHoldSeconds={QuotaSpikeMaxHoldSeconds.Value} QuotaMinStationStock={QuotaMinStationStock.Value} ClogForgeItem='{ClogForgeItem.Value}' " +
            $"EnableClogController={EnableClogController.Value} ClogRetainSeconds={ClogRetainSeconds.Value} ClogHysteresisSeconds={ClogHysteresisSeconds.Value} " +
            $"ClogBoostMaxDelta={ClogBoostMaxDelta.Value} ClogResponseWindowSeconds={ClogResponseWindowSeconds.Value} ClogHoldMaxSeconds={ClogHoldMaxSeconds.Value} " +
            $"ClogCooldownSeconds={ClogCooldownSeconds.Value} ClogMaxCyclesPerItem={ClogMaxCyclesPerItem.Value} ClogCapacityLockoutMinutes={ClogCapacityLockoutMinutes.Value} " +
            $"MaxConcurrentClogBoosts={MaxConcurrentClogBoosts.Value} " +
            $"EnableMetabolicPlane={EnableMetabolicPlane.Value} MetabolicWindowSeconds={MetabolicWindowSeconds.Value} MetabolicStatusMinutes={MetabolicStatusMinutes.Value} " +
            $"EnableEvictSpike={EnableEvictSpike.Value} EvictSpikeHotkey='{EvictSpikeHotkey.Value}' EvictItem='{EvictItem.Value}' EvictDropCount={EvictDropCount.Value} " +
            $"TierItem='{TierItem.Value}' TierRpcTimeoutSeconds={TierRpcTimeoutSeconds.Value} TierHoldSeconds={TierHoldSeconds.Value} TierAbortSeconds={TierAbortSeconds.Value} " +
            $"EnableBudgetPlane={EnableBudgetPlane.Value} BudgetWindowSeconds={BudgetWindowSeconds.Value} MinQuota={MinQuota.Value} HeadroomMinutes={HeadroomMinutes.Value} " +
            $"MaxQuotaShareOfMaxPct={MaxQuotaShareOfMaxPct.Value} HogQuotaShareOfMaxPct={HogQuotaShareOfMaxPct.Value} HogMinStored={HogMinStored.Value} " +
            $"HogMaxAbsRate={HogMaxAbsRate.Value} ShadowMaxFillRate={ShadowMaxFillRate.Value}.");
    }
}
