using System;
using System.Collections.Generic;
using SSSGame.AI;
using UnityEngine;

namespace SupplyChainMod;

// TierCaseController (v0.17.0, DEMAND_MODEL_PLAN.md "Tier arming v0.17.0 spec + dilution/demotion
// decisions") — the FIRST armed lever in the demand-model autopilot. Fed exclusively by
// CaseTracker.Ingest (CaseTracker.OnPoll, called once per WarehouseWatch poll, ~30s cadence): OPEN
// "tier|<item>" cases (a merged BLOCKAGE(priority-shadowed) + SHADOWED finding on the same item —
// see CaseTracker.cs) are candidates for a tier bump (Med/Low -> High) on the item's warehouse
// destination row, via the SAME direct SetPriority+HostUpdateTasks write path EvictionSpike's
// Shift+F6 tier test fire-verified (extracted into TierWriter.cs, shared by both).
//
// State machine per active hold (Holding -> DeferredRevert -> gone, or -> PendingRevert -> gone):
// bump -> judge each poll (responding = the case's finding absent this poll OR its LastStored fell
// below the bump-time baseline) -> RESPONSE (first response only, logged once) -> INEFFECTIVE after
// TierResponsePolls consecutive non-responding polls with no response ever seen (revert + lockout)
// -> or the case RESOLVES (chain-aware: a resolved hold whose item shares a ChainId with another
// still-OPEN case defers its revert until that chain-mate is gone too, DEMAND_MODEL_PLAN.md rail 3)
// -> or a hard abort at TierMaxHoldMinutes regardless of state. Every revert starts a per-item
// TierCooldownMinutes window (oscillation guard); a case re-opening on the same item during cooldown
// is skipped with a "chronic re-open" marker instead of silently vanishing.
//
// Armed state = ArmState.Armed (the SAME F11 flag SupplyController's hotkey flips — see ArmState.cs
// for why that flag was hoisted out of the EnableController-gated tick in v0.17.0). NOT armed: the
// ENTIRE state machine above still runs (budget, cooldowns, lockouts, chain deferral, dilution
// reports) — only the physical write/ledger/BoostedStationKeys actions are replaced with "WOULD
// BUMP"/"WOULD REVERT" log lines, so the dry-run path exercises exactly the same logic the armed
// path will. A hold remembers whether IT was armed at bump time (Hold.WasArmed) — never the live
// Armed flag — so a mid-hold F11 toggle can never strand a real write un-reverted or fake-revert
// something that was never actually written.
//
// Tier dilution (DEMAND_MODEL_PLAN.md): every BUMP logs the settlement-wide count of tier-0 (High)
// true-warehouse rows, and a candidate that's ALREADY High (nothing to bump) emits a DILUTION report
// instead — the demote-eligible list (High, no structural demand, no open case, stored>0) is
// evidence collection ONLY; this version never demotes a competing High row (that's the v0.18
// candidate lever the plan calls out).
//
// v0.17.1 — F11 kill-switch protocol: disarming (ArmState.ToggleArmed, armed->disarmed only) calls
// NoteDisarmed(), which ends every active hold IMMEDIATELY instead of merely stopping new writes and
// leaving live holds to ride out their normal judge/revert cycle — a real revert for a hold that
// actually wrote (live re-resolve + SetPriority(OrigTier)+HostUpdateTasks via TierWriter, ledger
// cleared, BoostedStationKeys released), a silent drop for a dry-run hold, and the normal per-item
// TierCooldownMinutes cooldown either way so re-arming can't instantly re-bump the same items.
//
// v0.17.2 — run-1 findings (first armed run: full bump->response->resolve cycles succeeded, zero
// errors, but every revert failed once and gave up, some rows stranded at High). CONFIRMED root
// cause (DEMAND_MODEL_PLAN.md "Armed run 1 finding 2"): WarehouseTasks.BuildRows EXCLUDES any
// station already in RankActuator.BoostedStationKeys — which is EXACTLY the station a hold's OWN
// revert needs to re-find, since bumping it is what put it in that set. A failed revert therefore
// left the station's BoostedStationKeys claim stuck FOREVER, hiding the WHOLE station (every item on
// it, not just the boosted one) from every later scan — explaining the Rope/Linen Thread "row not
// resolvable" loop at Improved Warehouse 3 (a stuck Onion claim blacked it out) and very likely the
// settlement-wide High-row count oscillating between two different visible-row sets. Six changes:
//   1. Single station walk: OnPoll now receives the SAME `stations` list WarehouseWatch/BudgetPlane
//      already resolved this poll (threaded through CaseTracker.Ingest), instead of calling
//      StationWalker.ResolveStations() a SECOND time here. The high-row count is now computed ONCE
//      per poll (BuildHighRowContext, includeBoosted:true — see below) and shared by both the BUMP
//      line and the DILUTION report, both now also logging `scanned=<structureCount>`.
//   2. Scan-coverage diagnostics: LogScanIfChanged logs a `SCAN n=<count> (+A: names; -B: names)`
//      line whenever the scanned structure SET changes poll-to-poll (silent otherwise) — the
//      streaming detector run 1 lacked (genuine streaming remains a real, separate possibility this
//      now makes visible). Every row/warehouse resolution failure (act/revert/retry) now appends
//      `playerDist=Xm` (parsed from the target's stringified posKey vs. a freshly resolved
//      SSSGame.PlayerCharacter position — never cached, matches the project's no-static-wrapper rule).
//   3. Coverage-conditioned case presence lives in CaseTracker (mirrored here): a hold whose own
//      WarehousePosKey isn't in this poll's scanned set is judged on NEITHER response NOR
//      resolution this poll — it just ages (the TierMaxHoldMinutes hard-abort wall clock still
//      applies unconditionally). Independent of, and additional to, fix 4 below.
//   4. WarehouseTasks.BuildRows gained an `includeBoosted` parameter (default false — every existing
//      caller unchanged) that a revert-resolution path MUST pass true, or its own row is
//      permanently unfindable (the confirmed bug above). TryDirectRevert (used by RevertHold,
//      NoteDisarmed, and RetryPendingReverts alike) now does this. On TOP of that fix: a revert that
//      still can't resolve its row (e.g. a genuine streaming gap) no longer gives up for the session
//      — the hold moves to PendingRevert (a separate collection, so it does NOT count against
//      MaxActiveTierCases) and retries every poll via RetryPendingReverts, which runs before the
//      EnableTierCases/alarm gates even in OnPoll — undoing our own write is always legitimate,
//      armed or not. NoteDisarmed's own failed reverts join the same queue.
//   5. Receiving-warehouse targeting: a BLOCKAGE-only tier case (no SHADOWED contributor) no longer
//      targets the producer's OWN place (bumping a source row does nothing) — it searches the same
//      row scan (includeBoosted:false — never target a station another hold already claims) for the
//      best true-warehouse (`HasTaskContainer`) row for the item, tier in {1,2}, room>0, excluding
//      the producer, largest room wins.
//   6. DemandWatchlist ([Diagnostics]): DemandGraph logs `[demand-watch]` reasoning (structural
//      demand, wantedBy, producedBy) for a configurable item list at every graph build/refresh —
//      diagnostics only, settles which processing-station items the graph actually covers.
//
// Log markers (fire-verification — every path announces itself, prefix "[SupplyChain] [tier-case]"
// unless noted): WOULD BUMP, BUMP applied, RESPONSE first seen after, INEFFECTIVE, HARD-ABORT,
// WRONG-ROW-ABORT, REVERT DEFERRED chain=, REVERT applied, WOULD REVERT, DISARM revert,
// WOULD REVERT (disarm), REVERT PENDING, DILUTION, chronic re-open inside cooldown, ALARM
// stand-down, SCAN n=, target=receiving-wh, no receiving-warehouse row available. Every bump/
// dilution/ineffective line also carries `scanned=<N>`; every resolution failure carries
// `playerDist=<N>m`. (DemandGraph's own `[demand-watch]` marker is documented in DemandGraph.cs.)
//
// Never cache interop wrappers (StationEntry/Workstation/ResourceStorageTaskData/PlayerCharacter)
// across polls (project-wide gotcha) — Hold stores only strings/ints/floats/bools; every touch
// re-resolves the warehouse/row fresh via WarehouseTasks.BuildRows by PosKey+item name (name-based
// warehouse-place matching for a NEW target is a known, accepted fragility — CaseView only retains
// display names, not PosKeys, for the tier family's own warehouse; a duplicate-named structure could
// mis-resolve). stations are threaded in from OnPoll's own caller (v0.17.2); worldId is still
// resolved fresh every use (a cheap StorageManager singleton lookup, never a station walk).
internal static class TierCaseController
{
    private enum HoldState { Holding, DeferredRevert, PendingRevert }

    private sealed class Hold
    {
        public int CaseId;
        public string Item = "?";
        public int? ChainId;
        public string WarehousePosKey = "?";
        public string WarehouseName = "?";
        public int TaskIndex;
        public string SupplyOwner = "-";
        public int OrigTier;
        public float BumpAtTime;
        public int BaselineStored;
        public int PollsSinceBump;
        public bool RespondedEver;
        public float FirstResponseSeconds = -1f;
        public HoldState State = HoldState.Holding;
        // Frozen at bump time — revert must always match what THIS hold actually did, regardless of
        // a later F11 toggle (see file header).
        public bool WasArmed;
        // v0.17.2 — the marker a PendingRevert hold logs once its delayed retry finally succeeds
        // (so the log narrative matches why the hold ended: a normal revert reason, or "DISARM revert").
        public string PendingMarker = "REVERT applied";
    }

    private static Dictionary<int, Hold> _holds = new();
    // v0.17.2 — holds whose real revert write couldn't resolve a row (streaming gap or similar).
    // Deliberately a SEPARATE collection from _holds: these do NOT count against MaxActiveTierCases
    // (their case is done; this is just cleanup), and they retry every poll regardless of gating.
    private static Dictionary<int, Hold> _pendingReverts = new();
    private static Dictionary<string, float> _cooldownUntil = new();   // item -> Time.time expiry
    private static Dictionary<string, float> _lockoutUntil = new();    // item -> Time.time expiry
    private static Dictionary<int, int> _alreadyTopStrikes = new();    // caseId -> strike count
    private static float _alarmLockoutUntil = -1f;
    private static bool _alarmLoggedThisWindow;

    // v0.17.2 — scan-coverage diagnostics (posKey -> display name, the last poll's scanned set).
    private static Dictionary<string, string> _prevScanned = new();
    private static bool _scanInitialized;

    internal static void NoteWorldLeft()
    {
        try
        {
            _holds = new Dictionary<int, Hold>();
            _pendingReverts = new Dictionary<int, Hold>();
            _cooldownUntil = new Dictionary<string, float>();
            _lockoutUntil = new Dictionary<string, float>();
            _alreadyTopStrikes = new Dictionary<int, int>();
            _alarmLockoutUntil = -1f;
            _alarmLoggedThisWindow = false;
            _prevScanned = new Dictionary<string, string>();
            _scanInitialized = false;
        }
        catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] [tier-case] NoteWorldLeft error: {ex}"); }
    }

    // Militia-alarm feed (ComplaintPatches.cs, same call site as SupplyController.NoteAlarm).
    // Unlike SupplyController/ClogController (which only pause NEW boosts while existing ones ride
    // out normally), tier-case holds revert ALL immediately on alarm — DEMAND_MODEL_PLAN.md "Alarm
    // wiring": a fresh combat complaint should never leave a tier bump competing with defense
    // priorities. Reuses the existing AlarmLockoutSeconds config key (no new key).
    internal static void NoteAlarm()
    {
        try { _alarmLockoutUntil = Time.time + Math.Max(0f, Plugin.AlarmLockoutSeconds.Value); }
        catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] [tier-case] NoteAlarm error: {ex}"); }
    }

    // F11 kill-switch protocol (v0.17.1): called from ArmState.ToggleArmed() on the armed->disarmed
    // transition ONLY — disarming ends every active hold IMMEDIATELY rather than just stopping new
    // writes. A WasArmed=true hold gets a REAL revert attempt (TryDirectRevert — same mechanics
    // RevertHold uses); if that can't resolve a row right now, it joins the SAME PendingRevert queue
    // OnPoll retries every poll from then on (v0.17.2 — even while disarmed, since undoing our own
    // write is always legitimate cleanup). A WasArmed=false hold never wrote anything, so it's just
    // dropped with a "WOULD REVERT (disarm)" log line. BOTH start the normal per-item
    // TierCooldownMinutes window. Safe to call outside a poll — resolves stations/worldId fresh
    // itself (ResolveContext, the one place outside OnPoll that still needs both). Every hold is
    // wrapped in its own try/catch so one failure can't strand the rest un-cleared.
    internal static void NoteDisarmed()
    {
        try
        {
            if (_holds.Count == 0) return;

            var (stations, worldId) = ResolveContext();

            foreach (var kv in _holds)
            {
                try
                {
                    var hold = kv.Value;

                    if (!hold.WasArmed)
                    {
                        Plugin.Logger.LogInfo(
                            $"[SupplyChain] [tier-case] WOULD REVERT (disarm): '{hold.Item}' @ '{hold.WarehouseName}' tier 0->{hold.OrigTier} (dry-run).");
                    }
                    else if (stations == null || worldId == null)
                    {
                        // No active world context at all — nothing to even attempt right now.
                        hold.PendingMarker = "DISARM revert";
                        MoveToPendingRevert(hold, "n/a (no active world context)");
                    }
                    else
                    {
                        hold.PendingMarker = "DISARM revert";
                        int? readback = TryDirectRevert(hold, stations, worldId);
                        if (readback != null)
                        {
                            Plugin.Logger.LogInfo(
                                $"[SupplyChain] [tier-case] DISARM revert: '{hold.Item}' @ '{hold.WarehouseName}' tier 0->{hold.OrigTier} readback={readback}.");
                            SupplyChainTracker.ShowMessage(
                                $"Tier-case DISARM revert: '{hold.Item}' back to tier {hold.OrigTier} at '{hold.WarehouseName}'.", 8f);
                        }
                        // else: TryDirectRevert already logged REVERT PENDING and queued it.
                    }

                    _cooldownUntil[hold.Item] = Time.time + Math.Max(0f, Plugin.TierCooldownMinutes.Value) * 60f;
                }
                catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] [tier-case] NoteDisarmed hold #{kv.Key} error: {ex}"); }
            }

            _holds.Clear();
        }
        catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] [tier-case] NoteDisarmed error: {ex}"); }
    }

    // Called once per CaseTracker.Ingest (~WarehousePollSeconds cadence, default 30s) with every
    // currently-OPEN case (all families — chain-deferral needs to see non-TIER chain-mates too) and
    // the SAME `stations` list that poll's own row scan resolved (v0.17.2 — one walk, shared).
    internal static void OnPoll(List<CaseTracker.CaseView> views, List<StationEntry> stations, bool fullTable)
    {
        try
        {
            ArmState.EnsureInitialized();

            // Streaming detector — always runs, regardless of any gate below (cheap: just diffs the
            // posKey set already in `stations`, no extra walk).
            var scannedKeys = LogScanIfChanged(stations);

            // Pending reverts retry EVERY poll, before EnableTierCases/alarm gating could bail —
            // undoing our own write is always legitimate cleanup, armed/enabled or not.
            RetryPendingReverts(stations);

            // Master switch off: bail without reverting new/active holds — mirrors how
            // SupplyController/ClogController simply stop ticking when their own EnableXxx is false
            // (state freezes in place rather than being force-reverted; re-enabling resumes exactly
            // where it left off). Alarm stand-down below is the one deliberate exception to this.
            if (!Plugin.EnableTierCases.Value) return;

            bool alarmActive = Time.time < _alarmLockoutUntil;
            if (alarmActive)
            {
                if (_holds.Count > 0)
                {
                    string? alarmWorldId = ResolveWorldId();
                    if (alarmWorldId != null) RevertAllHolds(stations, alarmWorldId, "ALARM stand-down");
                }
                if (!_alarmLoggedThisWindow)
                {
                    _alarmLoggedThisWindow = true;
                    Plugin.Logger.LogInfo(
                        $"[SupplyChain] [tier-case] ALARM stand-down active — no new holds for {Plugin.AlarmLockoutSeconds.Value}s from the last militia alarm.");
                }
                return;
            }
            _alarmLoggedThisWindow = false;

            string? worldId = ResolveWorldId();
            if (worldId == null) return; // no active world session (defensive — caller already gates this)

            var byId = new Dictionary<int, CaseTracker.CaseView>();
            var openItems = new HashSet<string>();
            foreach (var v in views) { byId[v.Id] = v; openItems.Add(v.Item); }

            UpdateHolds(byId, stations, worldId, scannedKeys);
            StartNewHolds(views, openItems, stations, worldId);
        }
        catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] [tier-case] OnPoll error: {ex}"); }
    }

    // ── Resolve worldId fresh every use (cheap singleton lookup, never a station walk) ─────────
    private static string? ResolveWorldId()
    {
        try
        {
            var storage = UnityEngine.Object.FindAnyObjectByType<SandSailorStudio.Storage.StorageManager>();
            if (storage != null)
            {
                string id = storage.ActiveSessionID;
                if (!string.IsNullOrEmpty(id)) return id;
            }
        }
        catch { }
        return null;
    }

    // Stations+worldId BOTH resolved fresh — used ONLY by NoteDisarmed, the one entry point outside
    // any poll (so there's no `stations` parameter to thread through).
    private static (List<StationEntry>? stations, string? worldId) ResolveContext()
    {
        string? worldId = ResolveWorldId();
        if (worldId == null) return (null, null);

        List<StationEntry>? stations = null;
        try { stations = StationWalker.ResolveStations(); }
        catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] [tier-case] ResolveContext station walk error: {ex}"); }
        return (stations, worldId);
    }

    // ── Streaming detector (v0.17.2) ─────────────────────────────────────────────────────────────
    // Diffs this poll's scanned structure set (posKey -> name) against the previous poll's; logs one
    // line only when the set actually changed (silent otherwise). Returns the current posKey set for
    // reuse as the coverage gate in UpdateHolds.
    private static HashSet<string> LogScanIfChanged(List<StationEntry> stations)
    {
        var current = new Dictionary<string, string>();
        foreach (var s in stations)
        {
            if (string.IsNullOrEmpty(s.PosKey) || s.PosKey == "?") continue;
            current[s.PosKey] = s.StructureName;
        }

        try
        {
            if (!_scanInitialized)
            {
                _scanInitialized = true;
                Plugin.Logger.LogInfo($"[SupplyChain] [tier-case] SCAN n={current.Count} (initial)");
            }
            else
            {
                var added = new List<string>();
                var removed = new List<string>();
                foreach (var kv in current) if (!_prevScanned.ContainsKey(kv.Key)) added.Add(kv.Value);
                foreach (var kv in _prevScanned) if (!current.ContainsKey(kv.Key)) removed.Add(kv.Value);

                if (added.Count > 0 || removed.Count > 0)
                {
                    var parts = new List<string>();
                    if (added.Count > 0) parts.Add($"+{added.Count}: {NamesJoined(added)}");
                    if (removed.Count > 0) parts.Add($"-{removed.Count}: {NamesJoined(removed)}");
                    Plugin.Logger.LogInfo($"[SupplyChain] [tier-case] SCAN n={current.Count} ({string.Join("; ", parts)})");
                }
            }
        }
        catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] [tier-case] LogScanIfChanged error: {ex}"); }

        _prevScanned = current;
        return new HashSet<string>(current.Keys);
    }

    private static string NamesJoined(List<string> names)
    {
        int shown = Math.Min(8, names.Count);
        var quoted = new List<string>();
        for (int i = 0; i < shown; i++) quoted.Add($"'{names[i]}'");
        string s = string.Join(", ", quoted);
        if (names.Count > shown) s += $", +{names.Count - shown} more";
        return s;
    }

    // ── Player-distance diagnostic (v0.17.2) ────────────────────────────────────────────────────
    // Never caches the player wrapper (project-wide gotcha) — resolved fresh every call, same style
    // as GroundItemVacuumMod/SeedScoutMod's own player-position reads. The target posKey is a
    // stringified Vector3 (Common.PosKey's format) — parsed back since a resolution FAILURE means we
    // have no live wrapper for that structure to read a position from directly.
    private static string PlayerDistToPosKeyStr(string posKey)
    {
        try
        {
            if (!TryParseVector3(posKey, out var targetPos)) return "n/a";
            var player = UnityEngine.Object.FindAnyObjectByType<SSSGame.PlayerCharacter>();
            if (player == null) return "n/a";
            Vector3 playerPos = player.transform.position;
            float dist = Vector3.Distance(playerPos, targetPos);
            return $"{dist:F0}m";
        }
        catch { return "n/a"; }
    }

    private static bool TryParseVector3(string s, out Vector3 v)
    {
        v = default;
        try
        {
            if (string.IsNullOrEmpty(s)) return false;
            string t = s.Trim();
            if (t.StartsWith("(", StringComparison.Ordinal) && t.EndsWith(")", StringComparison.Ordinal))
                t = t.Substring(1, t.Length - 2);
            var parts = t.Split(',');
            if (parts.Length != 3) return false;
            float x = float.Parse(parts[0].Trim());
            float y = float.Parse(parts[1].Trim());
            float z = float.Parse(parts[2].Trim());
            v = new Vector3(x, y, z);
            return true;
        }
        catch { return false; }
    }

    // ── Step 1: update active holds ──────────────────────────────────────────────────────────────
    private static void UpdateHolds(Dictionary<int, CaseTracker.CaseView> byId, List<StationEntry> stations, string worldId, HashSet<string> scannedKeys)
    {
        if (_holds.Count == 0) return;
        var ids = new List<int>(_holds.Keys);

        foreach (var id in ids)
        {
            if (!_holds.TryGetValue(id, out var hold)) continue;

            // Hard-abort rail — checked every poll regardless of state OR coverage.
            if (Time.time - hold.BumpAtTime > Math.Max(0f, Plugin.TierMaxHoldMinutes.Value) * 60f)
            {
                RevertHold(hold, stations, worldId, "HARD-ABORT");
                _holds.Remove(id);
                continue;
            }

            // v0.17.2 coverage gate: if this hold's own warehouse wasn't in this poll's scan, we have
            // no evidence either way — age the hold (hard-abort above still applies) but judge
            // NEITHER response NOR resolution this poll (mirrors CaseTracker's own presence freeze).
            if (!scannedKeys.Contains(hold.WarehousePosKey)) continue;

            if (hold.State == HoldState.DeferredRevert)
            {
                bool mateStillOpen = hold.ChainId.HasValue && TryFindChainMate(byId, id, hold.ChainId.Value, out _, out _);
                if (!mateStillOpen)
                {
                    RevertHold(hold, stations, worldId, "resolved (chain clear)");
                    _holds.Remove(id);
                }
                continue;
            }

            bool stillOpen = byId.TryGetValue(id, out var view);
            if (!stillOpen)
            {
                // RESOLVED this poll — chain-aware defer (rail 3): don't revert an effective fix
                // while another OPEN case shares this hold's chain.
                if (hold.ChainId.HasValue && TryFindChainMate(byId, id, hold.ChainId.Value, out int mateId, out string mateItem))
                {
                    hold.State = HoldState.DeferredRevert;
                    Plugin.Logger.LogInfo(
                        $"[SupplyChain] [tier-case] REVERT DEFERRED chain=#{hold.ChainId} (waiting on case #{mateId} '{mateItem}') " +
                        $"item='{hold.Item}' @ '{hold.WarehouseName}'");
                }
                else
                {
                    RevertHold(hold, stations, worldId, "resolved");
                    _holds.Remove(id);
                }
                continue;
            }

            // Still OPEN — judge response.
            hold.PollsSinceBump++;
            bool responding = !view!.PresentThisPoll || view.LastStored < hold.BaselineStored;
            if (responding)
            {
                if (!hold.RespondedEver)
                {
                    hold.RespondedEver = true;
                    hold.FirstResponseSeconds = Time.time - hold.BumpAtTime;
                    Plugin.Logger.LogInfo(
                        $"[SupplyChain] [tier-case] RESPONSE first seen after {hold.FirstResponseSeconds:F0}s item='{hold.Item}' @ '{hold.WarehouseName}'");
                }
            }
            else if (!hold.RespondedEver && hold.PollsSinceBump >= Math.Max(1, Plugin.TierResponsePolls.Value))
            {
                var (_, highRows) = BuildHighRowContext(stations);
                string dryRunSuffix = hold.WasArmed ? "" : " (dry-run — exercises the path, not the lever)";
                Plugin.Logger.LogWarning(
                    $"[SupplyChain] [tier-case] INEFFECTIVE: '{hold.Item}' @ '{hold.WarehouseName}' no response after {hold.PollsSinceBump} polls " +
                    $"highRowsSettlementWide={highRows} scanned={stations.Count}{dryRunSuffix}");
                RevertHold(hold, stations, worldId, "ineffective");
                _holds.Remove(id);
                _lockoutUntil[hold.Item] = Time.time + Math.Max(0f, Plugin.TierLockoutMinutes.Value) * 60f;
            }
        }
    }

    private static bool TryFindChainMate(Dictionary<int, CaseTracker.CaseView> byId, int excludeCaseId, int chainId, out int mateCaseId, out string mateItem)
    {
        foreach (var kv in byId)
        {
            if (kv.Key == excludeCaseId) continue;
            if (kv.Value.ChainId == chainId)
            {
                mateCaseId = kv.Key;
                mateItem = kv.Value.Item;
                return true;
            }
        }
        mateCaseId = -1;
        mateItem = "?";
        return false;
    }

    // ── Step 2: start new holds (budget-limited) ────────────────────────────────────────────────
    private static void StartNewHolds(List<CaseTracker.CaseView> views, HashSet<string> openItems, List<StationEntry> stations, string worldId)
    {
        if (_holds.Count >= Math.Max(0, Plugin.MaxActiveTierCases.Value)) return;

        var candidates = new List<CaseTracker.CaseView>();
        foreach (var v in views)
        {
            if (v.Family != "TIER") continue;
            if (_holds.ContainsKey(v.Id)) continue;
            if (IsLockedOut(v.Item)) continue;
            if (IsCoolingDown(v.Item))
            {
                Plugin.Logger.LogInfo(
                    $"[SupplyChain] [tier-case] chronic re-open inside cooldown: '{v.Item}' case #{v.Id} — skipping.");
                continue;
            }
            candidates.Add(v);
        }
        if (candidates.Count == 0) return;

        // Rank by distress duty cycle (DistressCount/PollsSeenTotal) desc, then structural demand
        // magnitude desc (DemandGraph's existing per-item structural table — never a new query).
        candidates.Sort((a, b) =>
        {
            float da = a.PollsSeenTotal > 0 ? (float)a.DistressCount / a.PollsSeenTotal : 0f;
            float db = b.PollsSeenTotal > 0 ? (float)b.DistressCount / b.PollsSeenTotal : 0f;
            int c = db.CompareTo(da);
            if (c != 0) return c;
            return StructuralMagnitude(b.Item).CompareTo(StructuralMagnitude(a.Item));
        });

        // v0.17.2 — ONE shared row scan for this whole call: the settlement-wide high-row count is
        // computed here once and reused by every candidate's bump/dilution line this poll (was two
        // independent BuildRows calls before, the prime suspect for run 1's oscillating count).
        var (rowsForHighCount, highRows) = BuildHighRowContext(stations);
        int scannedCount = stations.Count;

        foreach (var v in candidates)
        {
            if (_holds.Count >= Math.Max(0, Plugin.MaxActiveTierCases.Value)) break;
            TryStartHold(v, openItems, stations, worldId, rowsForHighCount, highRows, scannedCount);
        }
    }

    private static int StructuralMagnitude(string item)
    {
        try { DemandGraph.TryGetStructuralDemand(item, out int d, out int der, out _); return d + der; }
        catch { return 0; }
    }

    private static void TryStartHold(CaseTracker.CaseView v, HashSet<string> openItems, List<StationEntry> stations, string worldId,
        List<WarehouseTasks.WarehouseTaskRow> rowsForHighCount, int highRows, int scannedCount)
    {
        var allRows = WarehouseTasks.BuildRows(stations);
        WarehouseTasks.WarehouseTaskRow? target = null;

        if (v.ClassSources.TryGetValue("SHADOWED", out var sh))
        {
            // A SHADOWED contributor's own place IS the destination warehouse row — unchanged path.
            foreach (var r in allRows)
            {
                if (!string.Equals(r.Warehouse.StructureName, sh.Place, StringComparison.Ordinal)) continue;
                if (!string.Equals(r.ItemName, v.Item, StringComparison.Ordinal)) continue;
                target = r;
                break;
            }
        }
        else
        {
            // v0.17.2 fix 5 — BLOCKAGE-only tier case: v.Warehouse is the PRODUCER's own place
            // (where the item is stuck), never a bump target. Find the best RECEIVING true-warehouse
            // row instead: tier in {1,2}, room>0, excluding the producer itself, largest room wins.
            string producerPlace = v.Warehouse;
            WarehouseTasks.WarehouseTaskRow? best = null;
            int bestRoom = -1;
            foreach (var r in allRows)
            {
                if (!r.HasTaskContainer) continue; // true-warehouse (kind=wh) rows only
                if (!string.Equals(r.ItemName, v.Item, StringComparison.Ordinal)) continue;
                if (string.Equals(r.Warehouse.StructureName, producerPlace, StringComparison.Ordinal)) continue;
                if (r.Rank != 1 && r.Rank != 2) continue;
                int room = SafeRoom(r);
                if (room <= 0) continue;
                if (best == null || room > bestRoom) { best = r; bestRoom = room; }
            }

            if (best == null)
            {
                _alreadyTopStrikes.TryGetValue(v.Id, out int strikes);
                strikes++;
                _alreadyTopStrikes[v.Id] = strikes;
                Plugin.Logger.LogInfo(
                    $"[SupplyChain] [tier-case] '{v.Item}' — no receiving-warehouse row available (blockage-only, producer='{producerPlace}').");

                if (strikes >= 2)
                {
                    Plugin.Logger.LogWarning(
                        $"[SupplyChain] [tier-case] advisory VERDICT: '{v.Item}' — no receiving-warehouse row found across {strikes} attempts; " +
                        $"cooldown {Plugin.TierCooldownMinutes.Value}m.");
                    SupplyChainTracker.ShowMessage($"Tier-case: '{v.Item}' has no receiving warehouse — see log.", 8f);
                    _cooldownUntil[v.Item] = Time.time + Math.Max(0f, Plugin.TierCooldownMinutes.Value) * 60f;
                    _alreadyTopStrikes.Remove(v.Id);
                }
                return;
            }

            target = best;
            Plugin.Logger.LogInfo(
                $"[SupplyChain] [tier-case] target=receiving-wh (blockage-only case): '{best.Warehouse.StructureName}' room={bestRoom}");
        }

        if (target == null)
        {
            string dist = v.PosKeys.Count > 0 ? PlayerDistToPosKeyStr(FirstOf(v.PosKeys)) : "n/a";
            Plugin.Logger.LogInfo(
                $"[SupplyChain] [tier-case] case #{v.Id} '{v.Item}' — row not resolvable this poll (playerDist={dist}), will retry.");
            return;
        }

        if (RankActuator.BoostedStationKeys.Contains(target.Warehouse.PosKey))
        {
            Plugin.Logger.LogInfo(
                $"[SupplyChain] [tier-case] case #{v.Id} '{v.Item}' — station '{target.Warehouse.StructureName}' already boosted by another actuator, skipping this poll.");
            return;
        }

        if (target.Rank == 0) // already High
        {
            _alreadyTopStrikes.TryGetValue(v.Id, out int strikes);
            strikes++;
            _alreadyTopStrikes[v.Id] = strikes;
            EmitDilutionReport(v.Item, rowsForHighCount, highRows, scannedCount, openItems);

            if (strikes >= 2)
            {
                Plugin.Logger.LogWarning(
                    $"[SupplyChain] [tier-case] advisory VERDICT: '{v.Item}' @ '{target.Warehouse.StructureName}' already High but case persists " +
                    $"— tier lever exhausted, not a priority problem; cooldown {Plugin.TierCooldownMinutes.Value}m.");
                SupplyChainTracker.ShowMessage($"Tier-case: '{v.Item}' already High but still stuck — see log.", 8f);
                _cooldownUntil[v.Item] = Time.time + Math.Max(0f, Plugin.TierCooldownMinutes.Value) * 60f;
                _alreadyTopStrikes.Remove(v.Id);
            }
            return;
        }

        if (target.Rank == 3) // Off — player intent, off-limits
        {
            Plugin.Logger.LogInfo(
                $"[SupplyChain] [tier-case] '{v.Item}' @ '{target.Warehouse.StructureName}' row is Off — player intent, off-limits.");
            _cooldownUntil[v.Item] = Time.time + Math.Max(0f, Plugin.TierCooldownMinutes.Value) * 60f;
            return;
        }

        if (target.Rank != 1 && target.Rank != 2)
        {
            Plugin.Logger.LogInfo($"[SupplyChain] [tier-case] '{v.Item}' @ '{target.Warehouse.StructureName}' unexpected rank={target.Rank} — skipping.");
            return;
        }

        ActOnCandidate(v, target, worldId, stations, highRows, scannedCount);
    }

    private static string FirstOf(HashSet<string> set)
    {
        foreach (var s in set) return s;
        return "?";
    }

    // ── Act (armed: real write; dry-run: WOULD BUMP only) ───────────────────────────────────────
    private static void ActOnCandidate(CaseTracker.CaseView v, WarehouseTasks.WarehouseTaskRow target, string worldId,
        List<StationEntry> stations, int highRows, int scannedCount)
    {
        bool armed = ArmState.Armed;
        int origTier = target.Rank;
        string warehousePosKey = target.Warehouse.PosKey;
        string warehouseName = target.Warehouse.StructureName;

        if (armed)
        {
            bool allowed = true;
            try { allowed = target.Warehouse.Ws.HasStateAuthority; }
            catch (Exception ex)
            {
                Plugin.Logger.LogInfo($"[SupplyChain] [tier-case] HasStateAuthority unreadable ({ex.Message}) — assuming solo/offline, allowing.");
                allowed = true;
            }
            if (!allowed)
            {
                Plugin.Logger.LogWarning(
                    $"[SupplyChain] [tier-case] BUMP blocked: not host (HasStateAuthority=false) for '{warehouseName}' — skipping '{v.Item}' this poll.");
                return;
            }

            var snapshot = TierWriter.SnapshotWarehouse(stations, warehousePosKey);

            var ledgerEntry = new LedgerEntry
            {
                WorldId = worldId,
                PosKey = warehousePosKey,
                StationName = warehouseName,
                TaskIndex = target.TaskIndex,
                ItemName = target.ItemName,
                OrigPriority = origTier,
                BoostPriority = 0,
                UtcTicks = DateTime.UtcNow.Ticks,
                Kind = "tier",
                SupplyOwner = target.SupplyOwner,
            };
            SpikeLedger.Append(ledgerEntry);
            RankActuator.BoostedStationKeys.Add(warehousePosKey);

            int readback = TierWriter.DirectWrite(target.Warehouse, target.Rst, 0);

            Il2CppSystem.Collections.Generic.List<WorkstationTaskData>? freshDatas = null;
            try { freshDatas = target.Warehouse.Ws.GetWorkstationTaskDatas(); }
            catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] [tier-case] post-write GetWorkstationTaskDatas error: {ex}"); }

            var wrongRow = freshDatas != null
                ? TierWriter.DiffAndFindWrongRow(snapshot, target.TaskIndex, 0, freshDatas)
                : null;
            if (wrongRow != null)
            {
                var (wIdx, wItem, wOrigRank) = wrongRow.Value;
                Plugin.Logger.LogError(
                    $"[SupplyChain] [tier-case] WRONG-ROW-ABORT: bump on '{warehouseName}' hit task[{wIdx}] '{wItem}' instead of '{v.Item}' — reverting both.");
                TierWriter.RevertWrongRow(target.Warehouse, freshDatas!, wIdx, wOrigRank);
                TierWriter.DirectWrite(target.Warehouse, target.Rst, origTier);
                SpikeLedger.Remove(ledgerEntry);
                RankActuator.BoostedStationKeys.Remove(warehousePosKey);
                SupplyChainTracker.ShowMessage($"Tier-case: WRONG-ROW abort for '{v.Item}' — reverted.", 8f);
                return;
            }

            Plugin.Logger.LogInfo(
                $"[SupplyChain] [tier-case] BUMP applied: '{v.Item}' @ '{warehouseName}' tier {origTier}->0 readback={readback} " +
                $"highRowsSettlementWide={highRows} scanned={scannedCount}");
            SupplyChainTracker.ShowMessage($"Tier-case BUMP: '{v.Item}' -> High at '{warehouseName}'.", 8f);
        }
        else
        {
            Plugin.Logger.LogInfo($"[SupplyChain] [tier-case] WOULD BUMP: '{v.Item}' @ '{warehouseName}' tier {origTier}->0 (dry-run)");
            SupplyChainTracker.ShowMessage($"Tier-case [dry-run] would bump '{v.Item}' at '{warehouseName}' — press F11 to arm.", 8f);
        }

        _holds[v.Id] = new Hold
        {
            CaseId = v.Id,
            Item = v.Item,
            ChainId = v.ChainId,
            WarehousePosKey = warehousePosKey,
            WarehouseName = warehouseName,
            TaskIndex = target.TaskIndex,
            SupplyOwner = target.SupplyOwner,
            OrigTier = origTier,
            BumpAtTime = Time.time,
            BaselineStored = v.LastStored,
            WasArmed = armed,
        };
    }

    // ── Revert (real write when the hold was armed; log-only otherwise) ────────────────────────
    private static void RevertHold(Hold hold, List<StationEntry> stations, string worldId, string reason)
    {
        float totalSeconds = Time.time - hold.BumpAtTime;
        string respStr = hold.RespondedEver ? $" firstResponse={hold.FirstResponseSeconds:F0}s" : " firstResponse=never";
        bool isVerdictReason = reason == "HARD-ABORT" || reason == "ineffective";
        string dryRunSuffix = (!hold.WasArmed && isVerdictReason) ? " (dry-run — exercises the path, not the lever)" : "";

        if (!hold.WasArmed)
        {
            Plugin.Logger.LogInfo(
                $"[SupplyChain] [tier-case] WOULD REVERT: '{hold.Item}' @ '{hold.WarehouseName}' tier 0->{hold.OrigTier} reason={reason} " +
                $"timeToResolved={totalSeconds:F0}s{respStr}{dryRunSuffix}");
            _cooldownUntil[hold.Item] = Time.time + Math.Max(0f, Plugin.TierCooldownMinutes.Value) * 60f;
            return;
        }

        hold.PendingMarker = "REVERT applied"; // remembered in case this becomes a PendingRevert
        int? readback = TryDirectRevert(hold, stations, worldId);
        if (readback == null)
        {
            _cooldownUntil[hold.Item] = Time.time + Math.Max(0f, Plugin.TierCooldownMinutes.Value) * 60f;
            return; // TryDirectRevert already logged REVERT PENDING and queued it.
        }

        Plugin.Logger.LogInfo(
            $"[SupplyChain] [tier-case] REVERT applied: '{hold.Item}' @ '{hold.WarehouseName}' tier 0->{hold.OrigTier} readback={readback} " +
            $"reason={reason} timeToResolved={totalSeconds:F0}s{respStr}");
        SupplyChainTracker.ShowMessage($"Tier-case REVERT ({reason}): '{hold.Item}' back to tier {hold.OrigTier} at '{hold.WarehouseName}'.", 8f);

        _cooldownUntil[hold.Item] = Time.time + Math.Max(0f, Plugin.TierCooldownMinutes.Value) * 60f;
    }

    private static void RevertAllHolds(List<StationEntry> stations, string worldId, string reason)
    {
        if (_holds.Count == 0) return;
        var ids = new List<int>(_holds.Keys);
        foreach (var id in ids)
        {
            if (!_holds.TryGetValue(id, out var hold)) continue;
            RevertHold(hold, stations, worldId, reason);
            _holds.Remove(id);
        }
    }

    // ── PendingRevert (v0.17.2) ──────────────────────────────────────────────────────────────────
    // Core revert mechanics ONLY: re-resolve the row live by posKey+item, write OrigTier, clear the
    // ledger entry, release BoostedStationKeys. Returns the readback on success; on failure to
    // resolve a row, transitions `hold` into the PendingRevert queue (logging REVERT PENDING with a
    // player-distance hint) and returns null. Never throws.
    //
    // includeBoosted: true is REQUIRED here — WarehouseTasks.BuildRows by default EXCLUDES any
    // station whose posKey is already in RankActuator.BoostedStationKeys, which is EXACTLY the
    // station THIS hold itself boosted. Calling BuildRows with the default here would make a hold's
    // own row permanently unresolvable — the confirmed root cause of run 1's "every revert failed
    // once" AND the cascading Rope/Linen Thread blackout at Improved Warehouse 3 (a stuck Onion
    // claim hid the WHOLE station, posKey-keyed, from every scan — DEMAND_MODEL_PLAN.md "Armed run 1
    // finding 2"). Never pass includeBoosted:true from a target/candidate SELECTION path (only from
    // paths re-resolving a row this exact hold already owns).
    private static int? TryDirectRevert(Hold hold, List<StationEntry> stations, string worldId)
    {
        WarehouseTasks.WarehouseTaskRow? target = null;
        try
        {
            var allRows = WarehouseTasks.BuildRows(stations, includeBoosted: true);
            foreach (var r in allRows)
            {
                if (r.Warehouse.PosKey != hold.WarehousePosKey) continue;
                if (!string.Equals(r.ItemName, hold.Item, StringComparison.Ordinal)) continue;
                target = r;
                break;
            }
        }
        catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] [tier-case] TryDirectRevert row resolve error: {ex}"); }

        if (target == null)
        {
            MoveToPendingRevert(hold, $"playerDist={PlayerDistToPosKeyStr(hold.WarehousePosKey)}");
            return null;
        }

        try
        {
            int readback = TierWriter.DirectWrite(target.Warehouse, target.Rst, hold.OrigTier);
            SpikeLedger.Remove(new LedgerEntry { WorldId = worldId, PosKey = hold.WarehousePosKey, TaskIndex = hold.TaskIndex, ItemName = hold.Item, Kind = "tier" });
            RankActuator.BoostedStationKeys.Remove(hold.WarehousePosKey);
            return readback;
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"[SupplyChain] [tier-case] TryDirectRevert write error: {ex}");
            MoveToPendingRevert(hold, "write threw");
            return null;
        }
    }

    private static void MoveToPendingRevert(Hold hold, string context)
    {
        Plugin.Logger.LogWarning(
            $"[SupplyChain] [tier-case] REVERT PENDING '{hold.Item}' @ '{hold.WarehouseName}' (unresolvable, {context}) — retrying each poll.");
        hold.State = HoldState.PendingRevert;
        _pendingReverts[hold.CaseId] = hold;
    }

    // Retried every OnPoll, BEFORE the EnableTierCases/alarm gates — undoing our own write is always
    // legitimate cleanup regardless of the module's current on/off or armed state.
    private static void RetryPendingReverts(List<StationEntry> stations)
    {
        if (_pendingReverts.Count == 0) return;

        string? worldId = ResolveWorldId();
        if (worldId == null) return; // try again next poll

        var ids = new List<int>(_pendingReverts.Keys);
        foreach (var id in ids)
        {
            try
            {
                if (!_pendingReverts.TryGetValue(id, out var hold)) continue;

                int? readback = TryDirectRevert(hold, stations, worldId);
                if (readback == null) continue; // still pending — TryDirectRevert re-logged/re-queued it

                _pendingReverts.Remove(id);
                Plugin.Logger.LogInfo(
                    $"[SupplyChain] [tier-case] {hold.PendingMarker} (pending retry succeeded): '{hold.Item}' @ '{hold.WarehouseName}' " +
                    $"tier 0->{hold.OrigTier} readback={readback}.");
                SupplyChainTracker.ShowMessage(
                    $"Tier-case revert (delayed): '{hold.Item}' back to tier {hold.OrigTier} at '{hold.WarehouseName}'.", 8f);

                _cooldownUntil[hold.Item] = Time.time + Math.Max(0f, Plugin.TierCooldownMinutes.Value) * 60f;
            }
            catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] [tier-case] RetryPendingReverts hold #{id} error: {ex}"); }
        }
    }

    // ── Dilution report (AlreadyTop path) — diagnostics only, no demotion writes this version ────
    private static void EmitDilutionReport(string triggerItem, List<WarehouseTasks.WarehouseTaskRow> rows, int highCount, int scannedCount, HashSet<string> openItems)
    {
        try
        {
            var demoteEligible = new List<string>();
            var seenItems = new HashSet<string>();

            foreach (var r in rows)
            {
                if (!r.HasTaskContainer || r.Rank != 0) continue;
                if (!seenItems.Add(r.ItemName)) continue; // one demote-eligible check per item name
                if (openItems.Contains(r.ItemName)) continue;

                int d = 0, der = 0;
                try { DemandGraph.TryGetStructuralDemand(r.ItemName, out d, out der, out _); } catch { }
                if (d + der != 0) continue;

                int stored = SafeStored(r);
                if (stored <= 0) continue;

                demoteEligible.Add(r.ItemName);
            }

            int shown = Math.Min(8, demoteEligible.Count);
            string namesStr = shown == 0 ? "" : ": " + string.Join(", ", demoteEligible.GetRange(0, shown));
            Plugin.Logger.LogInfo(
                $"[SupplyChain] [tier-case] DILUTION '{triggerItem}' already High; {highCount} row(s) High settlement-wide, " +
                $"{demoteEligible.Count} demote-eligible{namesStr} scanned={scannedCount}");
        }
        catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] [tier-case] EmitDilutionReport error: {ex}"); }
    }

    private static int SafeStored(WarehouseTasks.WarehouseTaskRow r)
    {
        try
        {
            var inv = r.Warehouse.Ws.GetInventory();
            var info = r.Rst.itemInfoQuantity?.itemInfo;
            if (inv != null && info != null) return inv.GetItemQuantity(info);
        }
        catch { }
        return -1;
    }

    // v0.17.2 — room (remaining capacity) for a warehouse row, same accessor WarehouseWatch's own
    // AllotmentRow.Room uses. WarehouseTasks.WarehouseTaskRow carries no Room field of its own.
    private static int SafeRoom(WarehouseTasks.WarehouseTaskRow r)
    {
        try
        {
            var inv = r.Warehouse.Ws.GetInventory();
            var info = r.Rst.itemInfoQuantity?.itemInfo;
            if (inv != null && info != null) return inv.GetTotalRemainingCapacity(info);
        }
        catch { }
        return -1;
    }

    // v0.17.2 — ONE BuildRows call producing BOTH the row list (for the dilution report's
    // demote-eligible scan) and the settlement-wide High true-warehouse row count (for both the
    // bump line and the dilution report) — see StartNewHolds's own note on why this replaced two
    // independent per-call-site scans. includeBoosted:true is deliberate: this is a SETTLEMENT-WIDE
    // TALLY, not a target search — a row currently boosted (by this controller or any other
    // actuator) is still a real High row right now and must count, or the tally undercounts exactly
    // like the excluded-station bug that caused reverts to fail (see TryDirectRevert's note).
    private static (List<WarehouseTasks.WarehouseTaskRow> rows, int highCount) BuildHighRowContext(List<StationEntry> stations)
    {
        var rows = new List<WarehouseTasks.WarehouseTaskRow>();
        int highCount = 0;
        try
        {
            rows = WarehouseTasks.BuildRows(stations, includeBoosted: true);
            foreach (var r in rows) if (r.HasTaskContainer && r.Rank == 0) highCount++;
        }
        catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] [tier-case] BuildHighRowContext error: {ex}"); }
        return (rows, highCount);
    }

    private static bool IsLockedOut(string item)
    {
        if (_lockoutUntil.TryGetValue(item, out float t))
        {
            if (Time.time < t) return true;
            _lockoutUntil.Remove(item);
        }
        return false;
    }

    private static bool IsCoolingDown(string item)
    {
        if (_cooldownUntil.TryGetValue(item, out float t))
        {
            if (Time.time < t) return true;
            _cooldownUntil.Remove(item);
        }
        return false;
    }
}
