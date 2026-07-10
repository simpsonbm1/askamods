using System;
using System.Collections.Generic;
using System.Text;
using Il2CppInterop.Runtime.InteropTypes;
using SSSGame;
using SSSGame.AI;
using UnityEngine;

namespace DynamicVillagerNeedsMod;

// v1.6.0 "builder-fill diagnostics" — a diagnostics-only spike. Purpose: test whether an assigned
// villager can be "lent" to the settlement's Buildstation (the builder/unassigned-labor pool)
// WITHOUT changing Villager._workstation (the field the UI job icon reads). Hypothesis:
// Buildstation.SetTaskAgent(agent) alone injects build quests into the villager's QuestRunner.
//
// Everything here is gated on Plugin.BuilderDiagnostics.Value and wrapped in try/catch at every
// level, so a diagnostic failure can NEVER break NeedsController's control loop. Log prefix:
// "[DynamicNeeds] BLDdiag". NeedsController.Update() calls Tick() every frame (unthrottled, before
// the 4 Hz gate) because Input.GetKeyDown is frame-scoped and would otherwise be missed.
//
// IL2CPP interop notes (see CLAUDE.md "managed casts lie" / stub-chain gotchas):
//   - Villager and Workstation/Buildstation both compile through the unity-libs MonoBehaviour stub,
//     so `.Pointer` doesn't resolve directly on them — box via `(object) is Il2CppObjectBase` (PtrOf).
//   - Interop Villager does NOT managed-implement ITaskAgent (empty interface list, confirmed via
//     Cecil) — SetTaskAgent/ReleaseTaskAgent need an explicit `new ITaskAgent(pointer)` rewrap.
//     Same story for IWorkstation (used only by the SWAP variant, via Villager.AssignToWorkstation).
//   - ITaskAgent/IWorkstation/Quest ARE Il2CppObjectBase directly at compile time, so `.Pointer`
//     works on them without boxing.
//
// v1.6.1: v1.6.0's in-game test showed SetTaskAgent alone (AGENT) and SetTaskAgent+AddToTaskDatas
// (AGENT+TASKDATA) both inject build quests into the runner, but the HOME station's quests stay in
// the runner too and out-priority them, so the villager never actually builds. Plain-key variant is
// now FULL_LOAN: release from home (so its quests leave the runner), join the Buildstation for real
// (SetTaskAgent + AddToTaskDatas), and paint an all-Work schedule so nothing else competes. Also adds
// a quest-priority-table dump to the probes (to see the contest directly), a Buildstation task-data
// assignee-count dump, and top-3 nearest-candidate logging on lend (v1.6.0's pick was unexplainable
// without it). NeedsController's control loop now skips a villager for the whole time it's lent
// (BuilderDiag.IsLent) so DVN's own mode/schedule writes can't fight the experiment.
internal static class BuilderDiag
{
    private enum Variant { FullLoan, AgentTaskData, Swap }

    private const float BaselineDumpDelaySeconds = 10f;
    private const float ProbeIntervalSeconds = 5f;
    private const float PostRestoreDurationSeconds = 30f;

    // --- baseline dump (once per world session) ---
    private static bool _dumped;
    private static float _firstSeenTime = -1f;

    // --- active experiment state (cleared whenever the tracked list empties — never held across
    //     a world reload; see CLAUDE.md "never cache interop wrappers of per-world native objects") ---
    private static bool _active;
    private static bool _postRestore;
    private static VillagerSurvival? _surv;
    private static Villager? _villager;
    private static ITaskAgent? _agent;              // null for the SWAP variant
    private static Workstation? _homeWorkstation;
    private static Buildstation? _buildstation;
    private static Variant _variant;
    private static Vector3 _lendStartPos;
    private static float _probeTimer;
    private static float _postRestoreTimer;
    // FULL_LOAN only: the villager's painted per-hour schedule, snapshotted before the all-Work
    // write, restored verbatim on the reverse leg.
    private static int[]? _scheduleSnapshot;
    // Logs the "skipping lent villager" note once per lend (not every tick DVN's control loop runs).
    private static bool _skipLogged;

    // One-shot warnings so a persistently-failing probe doesn't spam the log every 5s.
    private static bool _membershipErrorLogged;
    private static bool _assignedErrorLogged;
    // Interop Dictionary enumeration (_questDataTable) is untested territory — if it throws, log once
    // and stop trying for the rest of the session rather than spamming every 5s probe.
    private static bool _questDumpErrorLogged;
    private static bool _questDumpDisabled;

    internal static void Tick(List<VillagerSurvival> tracked)
    {
        if (!Plugin.BuilderDiagnostics.Value) return;
        try { TickInner(tracked); }
        catch (Exception ex) { Plugin.Logger.LogError($"[DynamicNeeds] BLDdiag tick error: {ex}"); }
    }

    // Called from NeedsController.RestoreAll() (OnDestroy/OnApplicationQuit) so quitting or a
    // plugin unload never leaves a lend dangling.
    internal static void RestoreIfActive()
    {
        if (!_active) return;
        try { TryRestore("shutdown"); } catch (Exception ex) { Plugin.Logger.LogError($"[DynamicNeeds] BLDdiag shutdown restore failed: {ex}"); }
        ClearExperimentState();
    }

    // Called from NeedsController's per-villager control loop: while a lend is active for this
    // villager, DVN must not make any mode/schedule decisions for them (it would fight the
    // experiment's own schedule write and task-agent membership). Logs one info line the first time
    // it skips a given lend, not every tick.
    internal static bool IsLent(VillagerSurvival surv)
    {
        if (!_active || surv == null || !ReferenceEquals(_surv, surv)) return false;
        if (!_skipLogged)
        {
            _skipLogged = true;
            Plugin.Logger.LogInfo($"[DynamicNeeds] BLDdiag control loop skipping lent villager {SafeName(_villager!)}");
        }
        return true;
    }

    private static void TickInner(List<VillagerSurvival> tracked)
    {
        if (tracked.Count == 0)
        {
            // World session ended (or hasn't started) — reset the baseline-dump gate so a new world
            // re-dumps, and drop any experiment state WITHOUT touching its wrappers (they're stale/
            // gone, not safe to call native methods on across a world boundary).
            _dumped = false;
            _firstSeenTime = -1f;
            if (_active || _postRestore)
            {
                Plugin.Logger.LogWarning("[DynamicNeeds] BLDdiag: tracked list emptied mid-experiment (world change) — dropping state without a restore call (stale wrappers).");
                ClearExperimentState();
            }
            return;
        }

        if (!_dumped)
        {
            if (_firstSeenTime < 0f) _firstSeenTime = Time.time;
            if (Time.time - _firstSeenTime >= BaselineDumpDelaySeconds)
            {
                DumpBaseline(tracked);
                _dumped = true;
            }
        }

        if (_active)
        {
            CheckExperimentValidity();
        }

        if (Input.GetKeyDown(Plugin.BuilderTestKeyCode))
        {
            if (_active) RestoreActive();
            else StartLend(tracked);
        }

        if (_active)
        {
            _probeTimer += Time.deltaTime;
            if (_probeTimer >= ProbeIntervalSeconds)
            {
                _probeTimer = 0f;
                Probe(postRestore: false);
            }
        }
        else if (_postRestore)
        {
            _probeTimer += Time.deltaTime;
            if (_probeTimer >= ProbeIntervalSeconds)
            {
                _probeTimer = 0f;
                Probe(postRestore: true);
            }
            _postRestoreTimer += Time.deltaTime;
            if (_postRestoreTimer >= PostRestoreDurationSeconds)
                ClearExperimentState();
        }
    }

    // ---------------------------------------------------------------------------------------
    // A. Baseline dump (read-only, once per world session)
    // ---------------------------------------------------------------------------------------
    private static void DumpBaseline(List<VillagerSurvival> tracked)
    {
        Plugin.Logger.LogInfo("[DynamicNeeds] BLDdiag baseline dump ---");
        Buildstation? bs = null;

        foreach (var surv in tracked)
        {
            if (surv == null) continue;
            Villager? villager = null;
            try { villager = surv.GetVillager(); } catch { }
            if (villager == null) continue;

            string name = SafeName(villager);
            string wsName = "?";
            try { wsName = villager.GetWorkstation()?._structure?.GetName() ?? "(none)"; } catch { }

            if (bs == null)
            {
                try { bs = villager._GetDefaultBuildstationToAssign(); } catch { }
            }
            string member = bs != null ? MembershipStatus(bs, villager) : "?";

            Plugin.Logger.LogInfo($"[DynamicNeeds] BLDdiag baseline villager={name} workstation={wsName} inBuildstationAgents={member}");
        }

        if (bs != null)
        {
            string bsName = "?"; int agentsCount = -1, agentsCap = -1;
            string assignedCount = "?", taskAgentsCount = "?";
            try { bsName = bs.GetName(); } catch { }
            try { agentsCount = bs.GetAgentsCount(); } catch { }
            try { agentsCap = bs.GetAgentsCapacity(); } catch { }
            try { assignedCount = bs.GetAssignedVillagers()?.Count.ToString() ?? "?"; } catch { }
            try { taskAgentsCount = bs.GetWorkstationTaskAgents()?.Count.ToString() ?? "?"; } catch { }
            Plugin.Logger.LogInfo(
                $"[DynamicNeeds] BLDdiag baseline buildstation={bsName} agentsCount={agentsCount} " +
                $"agentsCapacity={agentsCap} assignedVillagers={assignedCount} taskAgents={taskAgentsCount}");
        }
        else
        {
            Plugin.Logger.LogInfo("[DynamicNeeds] BLDdiag baseline: no Buildstation resolved (no tracked villager returned one).");
        }
    }

    // ---------------------------------------------------------------------------------------
    // B. Hotkey lend/restore experiment
    // ---------------------------------------------------------------------------------------
    private static Buildstation? ResolveBuildstation(List<VillagerSurvival> tracked)
    {
        foreach (var surv in tracked)
        {
            if (surv == null) continue;
            try
            {
                var v = surv.GetVillager();
                if (v == null) continue;
                var bs = v._GetDefaultBuildstationToAssign();
                if (bs != null) return bs;
            }
            catch { }
        }
        return null;
    }

    private static void StartLend(List<VillagerSurvival> tracked)
    {
        var cam = Camera.main;
        if (cam == null)
        {
            Plugin.Logger.LogWarning("[DynamicNeeds] BLDdiag lend: Camera.main is null, aborting.");
            return;
        }

        var bs = ResolveBuildstation(tracked);
        if (bs == null)
        {
            Plugin.Logger.LogWarning("[DynamicNeeds] BLDdiag lend: could not resolve a default Buildstation from any tracked villager, aborting.");
            return;
        }
        IntPtr bsPtr = PtrOf(bs);
        Vector3 camPos = cam.transform.position;

        // Collect every eligible candidate (not just the nearest) so an unexpected pick is explainable
        // from the log (v1.6.0 chose a villager the tester wasn't standing next to).
        var candidates = new List<(VillagerSurvival surv, Villager villager, float dist)>();
        foreach (var surv in tracked)
        {
            if (surv == null) continue;
            try
            {
                if (!surv._hasAuthority) continue;
                var villager = surv.GetVillager();
                if (villager == null) continue;
                var ws = villager.GetWorkstation();
                if (ws == null) continue;
                if (PtrOf(ws) == bsPtr) continue; // already at the buildstation

                float d = Vector3.Distance(villager.GetPosition(), camPos);
                candidates.Add((surv, villager, d));
            }
            catch { }
        }

        if (candidates.Count == 0)
        {
            Plugin.Logger.LogWarning("[DynamicNeeds] BLDdiag lend: no eligible villager found (need authority + a non-Buildstation workstation).");
            return;
        }

        candidates.Sort((a, b) => a.dist.CompareTo(b.dist));
        VillagerSurvival bestSurv = candidates[0].surv;
        Villager bestVillager = candidates[0].villager;

        var top = new StringBuilder();
        for (int k = 0; k < Math.Min(3, candidates.Count); k++)
        {
            if (k > 0) top.Append(", ");
            top.Append(SafeName(candidates[k].villager)).Append('=').Append(candidates[k].dist.ToString("F1"));
            if (k == 0) top.Append("(chosen)");
        }
        Plugin.Logger.LogInfo($"[DynamicNeeds] BLDdiag lend candidates (nearest 3): {top}");

        Workstation? homeWs = null;
        try { homeWs = bestVillager.GetWorkstation(); } catch { }

        Variant variant =
            (Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl)) ? Variant.Swap :
            (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift)) ? Variant.AgentTaskData :
            Variant.FullLoan;

        string villagerName = SafeName(bestVillager);
        string homeName = "?"; try { homeName = homeWs?._structure?.GetName() ?? "(none)"; } catch { }
        string bsName = "?"; try { bsName = bs.GetName(); } catch { }

        Plugin.Logger.LogInfo(
            $"[DynamicNeeds] BLDdiag lend START variant={VariantName(variant)} villager={villagerName} home={homeName} buildstation={bsName}");

        bool result;
        ITaskAgent? agent = null;
        int[]? scheduleSnapshot = null;
        try
        {
            if (variant == Variant.Swap)
            {
                var bsAsIWorkstation = new IWorkstation(bsPtr);
                result = bestVillager.AssignToWorkstation(bsAsIWorkstation);
                Plugin.Logger.LogWarning(
                    "[DynamicNeeds] BLDdiag lend: SWAP variant reassigns Villager._workstation directly — " +
                    "if ZeroTaskWorkersMod is active, restoring a SWAP may leave the villager with zero tasks " +
                    "at the home station (its zero-task gate only fires on assignment, not on this restore path).");
            }
            else if (variant == Variant.FullLoan)
            {
                // v1.7.0: release+join+all-Work-schedule now lives in the shared BuilderLoan helper (also
                // used by NeedsController's OffWindowFill=Builder feature) — this hotkey variant just
                // drives it and keeps its own before/after diagnostics around the call.
                var loanState = BuilderLoan.Lend(bestVillager, bs, homeWs, out var failReason);
                if (loanState == null)
                {
                    Plugin.Logger.LogError($"[DynamicNeeds] BLDdiag lend FULL_LOAN failed: {failReason}");
                    return;
                }
                agent = loanState.Agent;
                scheduleSnapshot = loanState.ScheduleSnapshot;
                result = true;
                Plugin.Logger.LogInfo("[DynamicNeeds] BLDdiag lend FULL_LOAN: release+join+all-Work-schedule via shared BuilderLoan helper succeeded.");
            }
            else // AgentTaskData
            {
                agent = new ITaskAgent(PtrOf(bestVillager));
                result = bs.SetTaskAgent(agent);
                bs.AddToTaskDatas(bestVillager);
                Plugin.Logger.LogInfo("[DynamicNeeds] BLDdiag lend: AddToTaskDatas called.");
            }
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"[DynamicNeeds] BLDdiag lend start failed: {ex}");
            return;
        }

        Plugin.Logger.LogInfo($"[DynamicNeeds] BLDdiag lend result setTaskAgent/assign={result}");
        LogPostCallState(bestVillager, homeWs, bs, "lend-start");
        LogTaskDataCounts(bs, "lend-start");

        _active = true;
        _postRestore = false;
        _surv = bestSurv;
        _villager = bestVillager;
        _agent = agent;
        _homeWorkstation = homeWs;
        _buildstation = bs;
        _variant = variant;
        _scheduleSnapshot = scheduleSnapshot;
        _skipLogged = false;
        try { _lendStartPos = bestVillager.GetPosition(); } catch { _lendStartPos = Vector3.zero; }
        _probeTimer = 0f;
    }

    // ---------------------------------------------------------------------------------------
    // C. Probes while a lend (or the post-restore window) is active
    // ---------------------------------------------------------------------------------------
    private static void Probe(bool postRestore)
    {
        if (_villager == null || _surv == null || _buildstation == null) return;
        try
        {
            string tag = postRestore ? "probe post-restore" : "probe";
            string behavior = "?";
            try { behavior = _villager.CurrentBehaviorType.ToString(); } catch { }
            bool sleeping = false;
            try { sleeping = _surv.IsSleeping; } catch { }
            string questName = "?";
            try { questName = _villager.GetQuestRunner()?.GetActiveQuest()?.Name ?? "(none)"; } catch { }
            string pendingName = "?";
            try { pendingName = _villager.GetQuestRunner()?._pendingQuest?.Name ?? "(none)"; } catch { }
            string curWs = "?";
            try { curWs = _villager.GetWorkstation()?._structure?.GetName() ?? "(none)"; } catch { }
            string moved = "?";
            try { moved = Vector3.Distance(_villager.GetPosition(), _lendStartPos).ToString("F1"); } catch { }
            string member = MembershipStatus(_buildstation, _villager);

            Plugin.Logger.LogInfo(
                $"[DynamicNeeds] BLDdiag {tag} villager={SafeName(_villager)} variant={VariantName(_variant)} " +
                $"behavior={behavior} sleeping={sleeping} quest={questName} pendingQuest={pendingName} " +
                $"workstation={curWs} moved={moved} inBuildstationAgents={member}");

            LogQuestTable(_villager);
        }
        catch (Exception ex) { Plugin.Logger.LogError($"[DynamicNeeds] BLDdiag probe error: {ex}"); }
    }

    // ---------------------------------------------------------------------------------------
    // D. Restore
    // ---------------------------------------------------------------------------------------
    private static void RestoreActive()
    {
        TryRestore("hotkey");
        _active = false;
        _postRestore = true;
        _postRestoreTimer = 0f;
        _probeTimer = 0f;
    }

    // Shared restore logic (also used by the invalid-target safety path and the shutdown path).
    // Best-effort: returns false (and logs) on any failure rather than throwing.
    private static bool TryRestore(string reasonTag)
    {
        if (_villager == null || _buildstation == null)
        {
            Plugin.Logger.LogWarning($"[DynamicNeeds] BLDdiag restore ({reasonTag}): no active target, nothing to do.");
            return false;
        }
        try
        {
            bool ok;
            if (_variant == Variant.Swap)
            {
                if (_homeWorkstation == null)
                {
                    Plugin.Logger.LogWarning($"[DynamicNeeds] BLDdiag restore ({reasonTag}): no home workstation captured, cannot SWAP back.");
                    return false;
                }
                var homeAsIWorkstation = new IWorkstation(PtrOf(_homeWorkstation));
                ok = _villager.AssignToWorkstation(homeAsIWorkstation);
            }
            else if (_variant == Variant.FullLoan)
            {
                // v1.7.0: release/rejoin-home/schedule-restore now lives in the shared BuilderLoan helper.
                if (_agent == null)
                {
                    Plugin.Logger.LogWarning($"[DynamicNeeds] BLDdiag restore ({reasonTag}) FULL_LOAN: no agent captured, cannot restore.");
                    ok = false;
                }
                else
                {
                    var state = new BuilderLoan.State
                    {
                        Agent = _agent,
                        HomeWorkstation = _homeWorkstation,
                        Buildstation = _buildstation,
                        ScheduleSnapshot = _scheduleSnapshot,
                    };
                    ok = BuilderLoan.Restore(_villager, state, out var failReason);
                    if (!ok)
                        Plugin.Logger.LogError($"[DynamicNeeds] BLDdiag restore ({reasonTag}) FULL_LOAN failed: {failReason}");
                    else
                        Plugin.Logger.LogInfo($"[DynamicNeeds] BLDdiag restore ({reasonTag}) FULL_LOAN: release+rejoin-home+schedule via shared BuilderLoan helper succeeded.");
                }
            }
            else // AgentTaskData
            {
                try { _buildstation.RemoveFromTaskDatas(_villager); }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[DynamicNeeds] BLDdiag RemoveFromTaskDatas failed: {ex.Message}"); }
                ok = _agent != null && _buildstation.ReleaseTaskAgent(_agent);
            }
            Plugin.Logger.LogInfo($"[DynamicNeeds] BLDdiag restore ({reasonTag}) variant={VariantName(_variant)} villager={SafeName(_villager)} result={ok}");
            LogPostCallState(_villager, _homeWorkstation, _buildstation, "post-restore");
            LogTaskDataCounts(_buildstation, $"restore ({reasonTag})");
            return ok;
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"[DynamicNeeds] BLDdiag restore ({reasonTag}) failed: {ex}");
            return false;
        }
    }

    // ---------------------------------------------------------------------------------------
    // E. Safety / lifecycle
    // ---------------------------------------------------------------------------------------
    private static void CheckExperimentValidity()
    {
        if (_surv == null) { ClearExperimentState(); return; }
        bool invalid;
        try
        {
            invalid = !Plugin.TrackedSurvivals.Contains(_surv)
                      || _surv.GetVillager() == null
                      || !_surv._hasAuthority;
        }
        catch { invalid = true; }

        if (invalid)
        {
            Plugin.Logger.LogWarning("[DynamicNeeds] BLDdiag experiment villager became invalid mid-experiment — attempting restore.");
            try { TryRestore("invalid-target"); }
            catch (Exception ex) { Plugin.Logger.LogError($"[DynamicNeeds] BLDdiag invalid-target restore failed: {ex}"); }
            ClearExperimentState();
        }
    }

    private static void ClearExperimentState()
    {
        _active = false;
        _postRestore = false;
        _surv = null;
        _villager = null;
        _agent = null;
        _homeWorkstation = null;
        _buildstation = null;
        _probeTimer = 0f;
        _postRestoreTimer = 0f;
        _scheduleSnapshot = null;
        _skipLogged = false;
    }

    // ---------------------------------------------------------------------------------------
    // Shared helpers
    // ---------------------------------------------------------------------------------------

    // Schedule snapshot/write helpers moved to BuilderLoan.cs (v1.7.0, shared with NeedsController's
    // OffWindowFill=Builder feature) — SnapshotSchedule/WriteSchedule/WriteScheduleArray now live there.

    // Buildstation task-data assignee counts (VillagersInCharge.Count per task) — shows whether
    // AddToTaskDatas actually populated the checkbox list (ZeroTaskWorkersMod's Buildstation exemption
    // on _CanAddVillagerToTaskData is what should let this succeed).
    private static void LogTaskDataCounts(Buildstation bs, string tag)
    {
        try
        {
            int? build = null, repair = null, terraform = null;
            try { build = bs.BuildTask?.VillagersInCharge?.Count; } catch { }
            try { repair = bs.RepairTask?.VillagersInCharge?.Count; } catch { }
            try { terraform = bs.TerraformTask?.VillagersInCharge?.Count; } catch { }
            Plugin.Logger.LogInfo(
                $"[DynamicNeeds] BLDdiag {tag} bs taskdatas: build={FmtCount(build)} repair={FmtCount(repair)} terraform={FmtCount(terraform)}");
        }
        catch (Exception ex) { Plugin.Logger.LogWarning($"[DynamicNeeds] BLDdiag {tag} bs taskdatas failed: {ex.Message}"); }
    }

    private static string FmtCount(int? v) => v.HasValue ? v.Value.ToString() : "?";

    // Quest-priority-table dump: what's actually contesting the runner's active-quest pick right now.
    // Il2Cpp Dictionary<Quest,QuestData> enumeration is untested here — if it throws, log once and
    // disable the dump for the rest of the session rather than spamming every 5s probe.
    private static void LogQuestTable(Villager villager)
    {
        if (_questDumpDisabled) return;
        try
        {
            var runner = villager.GetQuestRunner();
            var table = runner?._questDataTable;
            if (table == null)
            {
                Plugin.Logger.LogInfo("[DynamicNeeds] BLDdiag probe quests: (no quest table)");
                return;
            }

            var sb = new StringBuilder();
            foreach (var kv in table)
            {
                var quest = kv.Key;
                if (quest == null) continue;
                string name = "?";
                try { name = quest.Name ?? "?"; } catch { }
                int dot = name.LastIndexOf('.');
                if (dot >= 0 && dot < name.Length - 1) name = name.Substring(dot + 1);
                string prio = "?";
                try { prio = quest.GetPriority(kv.Value).ToString("F1"); } catch { }
                if (sb.Length > 0) sb.Append(' ');
                sb.Append(name).Append('=').Append(prio);
            }
            Plugin.Logger.LogInfo($"[DynamicNeeds] BLDdiag probe quests: {sb}");
        }
        catch (Exception ex)
        {
            if (!_questDumpErrorLogged)
            {
                _questDumpErrorLogged = true;
                _questDumpDisabled = true;
                Plugin.Logger.LogWarning($"[DynamicNeeds] BLDdiag quest table dump failed, disabling for the rest of the session: {ex.Message}");
            }
        }
    }

    private static void LogPostCallState(Villager villager, Workstation? homeWs, Buildstation bs, string tag)
    {
        string curWs = "?";
        try { curWs = villager.GetWorkstation()?._structure?.GetName() ?? "(none)"; } catch { }
        int agentsCount = -1;
        try { agentsCount = bs.GetAgentsCount(); } catch { }
        string member = MembershipStatus(bs, villager);
        string stillAssignedHome = homeWs != null ? AssignedVillagersContains(homeWs, villager) : "?";

        Plugin.Logger.LogInfo(
            $"[DynamicNeeds] BLDdiag {tag} state: workstation={curWs} buildstationAgentsCount={agentsCount} " +
            $"inBuildstationAgents={member} stillInHomeAssigned={stillAssignedHome}");
    }

    // bs.GetWorkstationTaskAgents() elements are ITaskAgent (Il2CppObjectBase directly — .Pointer
    // works without boxing). Best-effort; on exception logs once and reports "?".
    private static string MembershipStatus(Buildstation bs, Villager villager)
    {
        try
        {
            IntPtr vPtr = PtrOf(villager);
            var agents = bs.GetWorkstationTaskAgents();
            if (agents == null) return "no";
            for (int i = 0; i < agents.Count; i++)
            {
                var a = agents[i];
                if (a != null && a.Pointer == vPtr) return "yes";
            }
            return "no";
        }
        catch (Exception ex)
        {
            if (!_membershipErrorLogged)
            {
                _membershipErrorLogged = true;
                Plugin.Logger.LogWarning($"[DynamicNeeds] BLDdiag membership check failed: {ex.Message}");
            }
            return "?";
        }
    }

    // ws.GetAssignedVillagers() elements are Villager (needs PtrOf boxing, same stub-chain issue).
    private static string AssignedVillagersContains(Workstation ws, Villager villager)
    {
        try
        {
            IntPtr vPtr = PtrOf(villager);
            var list = ws.GetAssignedVillagers();
            if (list == null) return "no";
            for (int i = 0; i < list.Count; i++)
            {
                var v = list[i];
                if (v != null && PtrOf(v) == vPtr) return "yes";
            }
            return "no";
        }
        catch (Exception ex)
        {
            if (!_assignedErrorLogged)
            {
                _assignedErrorLogged = true;
                Plugin.Logger.LogWarning($"[DynamicNeeds] BLDdiag assigned-villagers check failed: {ex.Message}");
            }
            return "?";
        }
    }

    private static string VariantName(Variant v) => v switch
    {
        Variant.FullLoan => "FULL_LOAN",
        Variant.AgentTaskData => "AGENT+TASKDATA",
        Variant.Swap => "SWAP",
        _ => "?"
    };

    private static string SafeName(Villager v)
    {
        try { return v.GetName(); } catch { return "?"; }
    }

    // Villager/Workstation/Buildstation compile through the unity-libs MonoBehaviour stub chain, so
    // `.Pointer` isn't directly accessible — box through `object` first (documented CLAUDE.md
    // "managed casts lie" gotcha; same pattern as RefreshCohorts' ws.Pointer workaround).
    private static IntPtr PtrOf(object o) => o is Il2CppObjectBase b ? b.Pointer : IntPtr.Zero;
}
