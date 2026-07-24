using System;
using HarmonyLib;
using SSSGame.AI;
using SSSGame.AI.FSM;
using SSSGame.Combat;
using UnityEngine;

namespace VillagerFightBackMod.Patches;

// The villager flee behavior lives in FleeCombatQuest. Its data object decides, via the get-only
// computed property ShouldFight, whether to stand and fight vs run. We post-process that getter:
// if it says "flee" (false) but the currently-engaged enemy is on our whitelist, we flip it to
// "fight" (true). ShouldFight is target-agnostic in vanilla (home-safety / HP / defense score), so
// reading the engaged enemy is what makes this per-enemy. No state write / no RPC -> co-op-safe.
//
// NOTE (unverified until in-game): whether ShouldFight==true actually produces *attacking* for a
// non-warrior villager, vs merely "stop running", can't be read from the IL2CPP binary (FSM state
// bodies aren't dumpable). If they just stand there, the fallback is routing them through the
// warrior combat path. Run with DebugLogging=true first to confirm the flip lands and they swing.
[HarmonyPatch(typeof(FleeCombatQuest.FleeCombatQuestData), nameof(FleeCombatQuest.FleeCombatQuestData.ShouldFight), MethodType.Getter)]
internal static class FleeCombatShouldFightPatch
{
    static void Postfix(FleeCombatQuest.FleeCombatQuestData __instance, ref bool __result)
    {
        try
        {
            if (!Plugin.EnabledCfg.Value) return;
            if (__instance == null || __instance.Pointer == IntPtr.Zero) return;

            // _engagedEnemy is frequently null at getter time, so fall back to the attacker we
            // stashed in _GetSpooked for this quest instance (see Plugin.GetRememberedSpook).
            IAttackTarget? engaged = __instance._engagedEnemy;
            IAttackTarget? remembered = engaged == null ? Plugin.GetRememberedSpook(__instance.Pointer) : null;
            IAttackTarget? decisionTarget = engaged ?? remembered;

            // Diagnostic: only while an engagement/recent spook exists, so it's silent out of combat.
            // Tells us whether the getter is even polled in combat, what it sees, and why it bails.
            bool isAlive = Plugin.SafeIsAlive(decisionTarget);
            bool wl = isAlive && Plugin.IsWhitelisted(decisionTarget!);

            // Diagnostic: only while an engagement/recent spook exists, so it's silent out of combat.
            // Now also reports the active quest + remaining combat time over the whole encounter, to
            // show whether combat is being dropped (quest flips) or simply timing out.
            if (Plugin.DebugLogging.Value && decisionTarget != null && Plugin.ShouldLogDiag())
            {
                var surv0 = __instance._survival;
                string au = surv0 == null ? "null-surv" : surv0._hasAuthority.ToString();
                string aq = "?"; float ct = -1f; float ap = -1f;
                try { var v = __instance._villager; var qr = v != null ? v.GetQuestRunner() : null; if (qr != null) { if (qr.ActiveQuest != null) aq = qr.ActiveQuest.Name; ap = qr._activeQuestPriority; } } catch { }
                try { ct = __instance._combatTimeRemaining; } catch { }
                Plugin.Logger.LogInfo(
                    $"[VillagerFightBack][diag] ShouldFight result={__result} " +
                    $"engaged={(engaged != null ? Plugin.SafeName(engaged) : "null")} " +
                    $"remembered={(remembered != null ? Plugin.SafeName(remembered) : "null")} " +
                    $"auth={au} activeQuest={aq} activePrio={ap:0.0} combatTime={ct:0.0} whitelisted={wl} isAlive={isAlive}");
            }

            // Refresh the "engaging a whitelisted enemy" window from this frequently-polled getter so
            // the TriggerPriority boost (which holds the flee-combat quest selected over work) stays
            // live through the whole fight, not just the instants right after a spook event. Refresh
            // both keys (quest-data ptr + owning-quest ptr) — see _GetSpooked for why both.
            if (wl && decisionTarget != null)
            {
                Plugin.RememberSpook(__instance.Pointer, decisionTarget);
                try
                {
                    var q = __instance.Quest;
                    if (q != null && Plugin.PtrOf(q) != IntPtr.Zero) Plugin.RememberSpook(q.Pointer, decisionTarget);
                    var qr = __instance.QuestRunner;
                    if (qr != null && Plugin.PtrOf(qr) != IntPtr.Zero && q != null && Plugin.PtrOf(q) != IntPtr.Zero)
                        Plugin.RememberVillagerCombat(qr, q, __instance.Pointer, __instance);
                }
                catch { }
            }

            // Keep combat alive while engaged with a whitelisted enemy (authority only). Done here
            // because this getter is polled frequently. If the target is dead, force end the combat timer
            // so they return to work immediately.
            if (Plugin.KeepCombatAlive)
            {
                var survK = __instance._survival;
                if (survK == null || survK._hasAuthority)
                {
                    try
                    {
                        // if (Plugin.DebugLogging.Value)
                        //     Plugin.Logger.LogInfo($"[VillagerFightBack][timer] wl={wl} decisionTarget={Plugin.SafeName(decisionTarget)} isAlive={isAlive} ct={__instance._combatTimeRemaining}");

                        if (wl)
                        {
                            float limit = Plugin.CombatTopUpSeconds.Value;
                            float threshold = limit / 2f;
                            float ct = __instance._combatTimeRemaining;
                            if (ct < threshold) __instance._combatTimeRemaining = limit;
                            else if (ct > limit)
                            {
                                // Vanilla re-arms the combat timer (~60s) mid-fight — e.g. when the
                                // villager takes a hit. The old top-up only ever RAISED the timer, so
                                // whenever the death zero-out below didn't land (ShouldFight often stops
                                // being polled once the target dies), the villager waited out the full
                                // vanilla timer — the observed "stays in combat ~a minute". Clamp it
                                // back down so lingering is always bounded by CombatTopUpSeconds.
                                __instance._combatTimeRemaining = limit;
                                if (Plugin.TimerDiagnostics.Value && Plugin.ShouldLogTimer())
                                    Plugin.Logger.LogInfo($"[VillagerFightBack][timer] clamped combatTime {ct:0.0} -> {limit:0.0} (vanilla re-arm).");
                            }
                        }
                        else if (decisionTarget != null && !isAlive)
                        {
                            __instance._combatTimeRemaining = 0f;
                            if (Plugin.DebugLogging.Value)
                                Plugin.Logger.LogInfo("[VillagerFightBack][timer] Force ended combat time (0).");
                        }
                    }
                    catch (Exception ex) { Plugin.Logger.LogError($"[VillagerFightBack] combatTime adjustment: {ex}"); }
                }
            }

            if (__result) return;          // already going to fight — nothing to override
            if (decisionTarget == null) return;

            // Host/solo authority only: the FSM that consumes this runs on the authoritative sim.
            // Flipping a non-authoritative client's copy would do nothing useful and risks confusion.
            var surv = __instance._survival;
            if (surv != null && !surv._hasAuthority) return;

            if (wl)
            {
                __result = true;
                if (Plugin.DebugLogging.Value && Plugin.ShouldLogFlip())
                    Plugin.Logger.LogInfo(
                        $"[VillagerFightBack] Forced ShouldFight=TRUE vs '{Plugin.SafeName(decisionTarget)}' " +
                        $"(faction={Plugin.FactionName(decisionTarget.Faction)})" +
                        $"{(engaged == null ? " [via remembered spook]" : "")}.");
            }
        }
        catch (Exception ex) { Plugin.Logger.LogError($"[VillagerFightBack] ShouldFight postfix: {ex}"); }
    }
}

// PRIMARY mechanism. _GetSpooked is the per-target entry point that frightens a villager into
// fleeing (it applies the spookedStatusEffect → FSM_SetVillagerIsFleeing → FSM_RunFromTarget). The
// villager's normal chase/attack (FSM_MeleeCombat + FSM_FindWeaponDuringCombat) already works — the
// spook just keeps interrupting it. So for a WHITELISTED attacker we skip _GetSpooked entirely:
// no spook applied → no flee → their existing attack behavior continues. Non-whitelisted attackers
// (draugar, wolves) fall through to vanilla spook/flee. Also doubles as the discovery log + the
// per-instance target memory used by the ShouldFight fallback.
[HarmonyPatch(typeof(FleeCombatQuest.FleeCombatQuestData), "_GetSpooked")]
internal static class FleeCombatGetSpookedPatch
{
    // Return false to skip the original _GetSpooked (suppress the spook); true to run vanilla.
    static bool Prefix(FleeCombatQuest.FleeCombatQuestData __instance, IAttackTarget spookyTarget)
    {
        try
        {
            if (spookyTarget == null) return true;

            // Stash the attacker for this quest instance (fallback target for the ShouldFight getter).
            // Remember it under BOTH the quest-data pointer (used by ShouldFight/GetPriority, which have
            // the data) AND the owning FleeCombatQuest pointer (used by the get_TriggerPriority postfix,
            // which has only the quest). FleeCombatQuest is per-villager, so the quest pointer is a valid
            // per-villager key. This is what lets the trigger-priority boost be scoped to this villager.
            if (__instance != null)
            {
                Plugin.RememberSpook(__instance.Pointer, spookyTarget);
                try
                {
                    var q = __instance.Quest;
                    if (q != null) Plugin.RememberSpook(q.Pointer, spookyTarget);
                    // Register this villager's runner -> flee-combat-quest so the FindNextBestQuest
                    // arbiter patch can force her combat quest while this engagement is live.
                    var qr = __instance.QuestRunner;
                    if (qr != null && q != null) Plugin.RememberVillagerCombat(qr, q, __instance.Pointer, __instance);
                }
                catch { }
            }

            bool whitelisted = Plugin.IsWhitelisted(spookyTarget);

            // Discovery log: localized name + invariant asset name + faction of each distinct spook
            // source, once. The asset name is language-independent — prefer it in FightBackAgainst.
            if (Plugin.DebugLogging.Value && !Plugin.AlreadyLoggedSpook(Plugin.SafeName(spookyTarget)))
            {
                string invName = Plugin.InvariantCreatureName(spookyTarget) ?? "(none)";
                Plugin.Logger.LogInfo(
                    $"[VillagerFightBack] Villager spooked by '{Plugin.SafeName(spookyTarget)}' " +
                    $"(assetName='{invName}', faction={Plugin.FactionName(spookyTarget.Faction)}) — " +
                    $"{(whitelisted ? "ON whitelist" : "not on whitelist; add assetName (language-proof) or the display name to FightBackAgainst, or the faction to FightBackFactions")}.");
            }

            if (!Plugin.EnabledCfg.Value) return true; // mod off → vanilla
            if (!whitelisted) return true;             // flee real threats normally

            // Whitelisted: only the host/solo authority acts (combat target, spook status & flee state
            // are networked/replicated; letting a client diverge would desync). Clients follow.
            var surv = __instance?._survival;
            if (surv != null && !surv._hasAuthority) return true;

            var villager = __instance?._villager;

            // State diag (throttled): proves the job-vs-combat theory — a villager walking to a job
            // shows inCombat=False and only ever flees; encounter-4 (already fighting) shows inCombat=True.
            if (Plugin.DebugLogging.Value && Plugin.ShouldLogFlip() && villager != null)
            {
                string activeQuest = "?"; float activePrio = -1f;
                try { var qr = villager.GetQuestRunner(); if (qr != null) { if (qr.ActiveQuest != null) activeQuest = qr.ActiveQuest.Name; activePrio = qr._activeQuestPriority; } }
                catch { }
                Plugin.Logger.LogInfo(
                    $"[VillagerFightBack] Whitelisted spook from '{Plugin.SafeName(spookyTarget)}' — " +
                    $"villager state: inCombat={villager.IsInCombat} fleeing={villager.IsFleeing} " +
                    $"warrior={villager.IsWarrior} behavior={villager.CurrentBehaviorType} activeQuest={activeQuest} activePrio={activePrio:0.0}.");
            }

            // Force engagement: set the wisp as her combat target so she ENTERS combat and attacks it,
            // rather than ignoring it while doing a job. onlyIfNotPresent=true so we never steal an
            // existing (possibly more dangerous) target.
            if (Plugin.ForceEngage && villager != null)
            {
                try { villager.SetTarget(spookyTarget, true); }
                catch (Exception ex) { Plugin.Logger.LogError($"[VillagerFightBack] SetTarget failed: {ex}"); }
            }

            if (!Plugin.SuppressSpook) return true; // engage but still allow vanilla spook
            return false;                                 // skip _GetSpooked → no spook → no flee
        }
        catch (Exception ex) { Plugin.Logger.LogError($"[VillagerFightBack] GetSpooked prefix: {ex}"); return true; }
    }
}

// THE key lever. QuestRunner runs the single highest-priority quest. A villager with a job drops
// combat the instant it begins because the work quest out-prioritizes un-spooked combat — that's why
// they fight when idle but never when busy. So while the combat quest is engaged with a whitelisted
// enemy, we raise its priority above work (to just over c_Flee) so it HOLDS and the melee branch
// runs. Reverts on its own once _engagedEnemy clears / the remembered spook expires. Pure read +
// number bump, no networked write → co-op-safe.
[HarmonyPatch(typeof(CombatQuest), nameof(CombatQuest.GetPriority))]
internal static class CombatPriorityPatch
{
    static void Postfix(CombatQuest __instance, QuestData questData, ref float __result)
    {
        try
        {
            if (!Plugin.EnabledCfg.Value || !Plugin.BoostCombatPriority) return;
            if (questData == null || questData.Pointer == IntPtr.Zero) return;

            var cqd = questData as CombatQuest.CombatQuestData;
            if (cqd == null || Plugin.PtrOf(cqd) == IntPtr.Zero) return;

            IAttackTarget? enemy = cqd._engagedEnemy ?? Plugin.GetRememberedSpook(questData.Pointer);
            if (enemy == null || !Plugin.IsWhitelisted(enemy)) return;

            float boost = Plugin.GetCombatBoost();
            if (__result < boost)
            {
                float was = __result;
                __result = boost;
                // One-shot proof that this method is actually the priority the QuestRunner arbitrates on.
                if (Plugin.DebugLogging.Value && !Plugin.BoostLoggedOnce)
                {
                    Plugin.BoostLoggedOnce = true;
                    Plugin.Logger.LogInfo(
                        $"[VillagerFightBack] GetPriority boost FIRED vs '{Plugin.SafeName(enemy)}': {was:0.0} -> {boost:0.0}.");
                }
            }
        }
        catch (Exception ex) { Plugin.Logger.LogError($"[VillagerFightBack] GetPriority postfix: {ex}"); }
    }
}

// THE likely real fix (v1.0.14). A villager has TWO combat FSM behaviours on VillagerSurvival:
// `fleeCombatBehaviour` (kite/run) and `naturalCombatBehaviour` (stand & fight). CombatQuest.GetFSMBehavior
// picks which one the combat FSM runs; for the FleeCombatQuest it returns the FLEE behaviour — which is why
// she RUNS even after we strip her job and force ShouldFight/melee (v1.0.13: work suspended, FleeCombat
// active, MELEE reached — yet she still ran). The running IS the flee combat behaviour, not the job. So for
// a whitelisted enemy we swap the result to `naturalCombatBehaviour`, the same stand-and-fight FSM idle
// villagers use to kill Wisps. FleeCombatQuest does NOT override GetFSMBehavior, so patching CombatQuest's
// covers it. Safe sig (QuestData in, vFSMBehaviour out — reference types). Also refreshes the engagement
// window here (this runs throughout the fight) so the work-suspension doesn't restore her job mid-combat.
[HarmonyPatch(typeof(CombatQuest), nameof(CombatQuest.GetFSMBehavior))]
internal static class CombatBehaviourSwapPatch
{
    static void Postfix(CombatQuest __instance, QuestData questData, ref vFSMBehaviour __result)
    {
        try
        {
            if (!Plugin.EnabledCfg.Value) return;
            if (questData == null || questData.Pointer == IntPtr.Zero) return;

            var cqd = questData as CombatQuest.CombatQuestData;
            if (cqd == null || Plugin.PtrOf(cqd) == IntPtr.Zero) return;

            IAttackTarget? enemy = cqd._engagedEnemy ?? Plugin.GetRememberedSpook(questData.Pointer);
            if (enemy == null || enemy.Pointer == IntPtr.Zero || !Plugin.SafeIsAlive(enemy) || !Plugin.IsWhitelisted(enemy)) return;

            // Keep the "engaging a whitelisted enemy" window alive for the whole fight — this method is
            // called while the combat quest runs, so the work-suspension's restore can't fire mid-combat.
            Plugin.RememberSpook(questData.Pointer, enemy);
            try
            {
                var q = cqd.Quest;
                if (q != null && Plugin.PtrOf(q) != IntPtr.Zero) Plugin.RememberSpook(q.Pointer, enemy);
                var qr = cqd.QuestRunner;
                if (qr != null && Plugin.PtrOf(qr) != IntPtr.Zero && q != null && Plugin.PtrOf(q) != IntPtr.Zero)
                    Plugin.RememberVillagerCombat(qr, q, questData.Pointer, cqd);
            }
            catch { }

            if (!Plugin.UseNaturalCombatBehaviour) return; // refresh only; no behaviour swap

            var surv = cqd._survival;
            if (surv == null || Plugin.PtrOf(surv) == IntPtr.Zero) return;
            var natural = surv.naturalCombatBehaviour;
            if (natural == null || Plugin.PtrOf(natural) == IntPtr.Zero) return;

            // Already the stand-and-fight behaviour? nothing to do.
            if (Plugin.PtrOf(__result) == Plugin.PtrOf(natural)) return;

            __result = natural;
            if (Plugin.DebugLogging.Value && !Plugin.BehaviourSwapLoggedOnce)
            {
                Plugin.BehaviourSwapLoggedOnce = true;
                Plugin.Logger.LogInfo(
                    $"[VillagerFightBack] Swapped combat FSM behaviour flee -> natural (stand & fight) vs " +
                    $"'{Plugin.SafeName(enemy)}'.");
            }
        }
        catch (Exception ex) { Plugin.Logger.LogError($"[VillagerFightBack] GetFSMBehavior postfix: {ex}"); }
    }
}

// THE arbitration fix (proven by the v1.0.10 diag run). QuestRunner.FindNextBestQuest selects the
// active quest by each quest's TriggerPriority, NOT GetPriority: in-game we boosted GetPriority to 41
// (it fired) yet the runner still re-selected TerraformingQuest at 15 — impossible for a GetPriority
// arbiter, so eligibility is gated on TriggerPriority. A busy villager's flee-combat TriggerPriority
// only spikes to c_Flee for an instant when freshly spooked; in the gaps it drops below work, so work
// reclaims her and drags her to the job marker (the observed oscillation TerraformingQuest<->FleeCombat).
//
// FleeCombatQuest is per-villager (VillagerSurvival._fleeCombatQuest), and it OVERRIDES TriggerPriority,
// so we patch the override here (patching CombatQuest's would miss it). The getter has no questData /
// target context, so we scope per-villager via the spook we stashed under this quest's pointer in
// _GetSpooked/ShouldFight: only boost while THIS villager has a recent whitelisted engagement. Pinning
// it at c_Flee keeps the flee-combat quest continuously selected (no oscillation) without exceeding real
// emergencies (AvoidImminentDanger=46). Pure read + return-an-int → co-op-safe. Self-reverts when the
// remembered spook expires (~8s after the enemy is gone).
[HarmonyPatch(typeof(FleeCombatQuest), nameof(FleeCombatQuest.TriggerPriority), MethodType.Getter)]
internal static class FleeTriggerPriorityPatch
{
    static void Postfix(FleeCombatQuest __instance, ref int __result)
    {
        try
        {
            if (!Plugin.EnabledCfg.Value || !Plugin.BoostTriggerPriority) return;
            if (__instance == null) return;

            IAttackTarget? remembered = Plugin.GetRememberedSpook(__instance.Pointer);

            // Unconditional one-shot: tells us whether get_TriggerPriority is even invoked during
            // arbitration (in v1.0.11 the boost log never appeared — this disambiguates "getter never
            // called / cached" from "called but the per-quest spook lookup missed").
            if (Plugin.DebugLogging.Value && !Plugin.TriggerGetterLoggedOnce)
            {
                Plugin.TriggerGetterLoggedOnce = true;
                Plugin.Logger.LogInfo(
                    $"[VillagerFightBack][arb] get_TriggerPriority INVOKED: native={__result} " +
                    $"remembered={(remembered != null ? Plugin.SafeName(remembered) : "null")}.");
            }

            if (remembered == null || !Plugin.SafeIsAlive(remembered) || !Plugin.IsWhitelisted(remembered)) return;

            int boost = Plugin.GetTriggerBoost();
            if (__result < boost)
            {
                int was = __result;
                __result = boost;
                // One-shot proof this is the lever that actually holds combat selected.
                if (Plugin.DebugLogging.Value && !Plugin.TriggerBoostLoggedOnce)
                {
                    Plugin.TriggerBoostLoggedOnce = true;
                    Plugin.Logger.LogInfo(
                        $"[VillagerFightBack] TriggerPriority boost FIRED vs '{Plugin.SafeName(remembered)}': " +
                        $"{was} -> {boost} (holds flee-combat selected over work).");
                }
            }
        }
        catch (Exception ex) { Plugin.Logger.LogError($"[VillagerFightBack] TriggerPriority postfix: {ex}"); }
    }
}

// THE arbitration fix, take 2 (safe patch target). Patching QuestRunner.FindNextBestQuest crashed: its
// native sig has a `Single&` by-ref out-param, a HarmonyX/IL2CPP sharp edge that NRE'd in the trampoline
// on every call and left villagers fully disabled. So instead we drive the runner from QuestRunner.Update
// (void, no args → marshal-safe) and use the game's OWN methods (RemoveQuest/AddQuest, both Void+single
// reference-type arg → safe). The mechanism: idle villagers already fight Wisps fine (proven) — the only
// thing dragging her off is her active WORK quest. So while she's engaged with a whitelisted enemy, we
// REMOVE her active work quest (making her effectively idle → she fights), stashing it; when the enemy is
// gone we ADD it back. We never touch survival/combat/idle quests (name-protected). Authority-only.
[HarmonyPatch(typeof(QuestRunner), nameof(QuestRunner.Update))]
internal static class QuestRunnerUpdatePatch
{
    // This postfix runs per-villager per-frame; the combat-state work below is heavy interop and
    // doesn't need 60 Hz (combat engage/disengage is multi-second). Throttle per villager to ~4 Hz.
    private static readonly System.Collections.Generic.Dictionary<System.IntPtr, float> _lastRun = new();
    private const float TickInterval = 0.25f;

    static void Postfix(QuestRunner __instance)
    {
        try
        {
            // Cheap global frame gate FIRST. This postfix fires per-villager per-frame, so skipping 3 of
            // every 4 invocations with a single int op cuts the per-frame ENTRY cost (the interop PtrOf +
            // dict lookup below) ~75% with no behavioral impact: still ~15 Hz global, bodies stay staggered
            // by the per-instance throttle, and combat engage/disengage is a multi-second event.
            if ((Time.frameCount & 3) != 0) return;

            if (!Plugin.EnabledCfg.Value || !Plugin.SuspendWorkWhileEngaged) return;
            if (__instance == null) return;

            System.IntPtr key = Plugin.PtrOf(__instance);
            float now = Time.time;
            if (_lastRun.TryGetValue(key, out var last) && now - last < TickInterval) return;
            _lastRun[key] = now;

            // Is this villager currently engaged with a whitelisted enemy?
            bool engaged = false;
            Quest? combatQuest = null;
            if (Plugin.TryGetVillagerCombat(__instance, out var cq, out var questDataPtr, out var questData) && cq != null)
            {
                var remembered = Plugin.GetRememberedSpook(questDataPtr);
                if (remembered != null && Plugin.IsWhitelisted(remembered))
                {
                    if (Plugin.SafeIsAlive(remembered)) { engaged = true; combatQuest = cq; }
                    else
                    {
                        // The whitelisted enemy just died/despawned. End combat NOW, frame-driven —
                        // don't rely on the ShouldFight getter being polled again after death (it
                        // often isn't, which left _combatTimeRemaining to run out vanilla-style and
                        // held the villager in combat for up to a minute). Zero the timer, collapse
                        // the boost/suspension window, and let the restore branch below run this tick.
                        float ctWas = -1f;
                        try
                        {
                            if (questData != null && Plugin.PtrOf(questData) != IntPtr.Zero)
                            {
                                var surv = questData._survival;
                                if (surv == null || surv._hasAuthority)
                                {
                                    ctWas = questData._combatTimeRemaining;
                                    questData._combatTimeRemaining = 0f;
                                }
                            }
                        }
                        catch (Exception ex) { Plugin.Logger.LogError($"[VillagerFightBack] end-combat zero: {ex}"); }

                        Plugin.ForgetSpook(questDataPtr);
                        try { Plugin.ForgetSpook(cq.Pointer); } catch { }
                        Plugin.ForgetVillagerCombat(__instance);
                        Plugin.StartPostCombatWatch(__instance, questData);
                        if (Plugin.TimerDiagnostics.Value)
                            Plugin.Logger.LogInfo(
                                $"[VillagerFightBack][timer] enemy '{Plugin.SafeName(remembered)}' down — combat ended " +
                                $"(combatTime {ctWas:0.0} -> 0), restoring work.");
                    }
                }
            }

            if (engaged)
            {
                // Suspend her active quest if it's a work quest (not combat/survival/idle). One per tick;
                // over a few ticks she runs out of work to do and drops to idle → her existing combat
                // behavior takes the Wisp. Capped so we can't strip her whole quest list.
                Quest? active = null;
                try { active = __instance.ActiveQuest; } catch { }
                if (active != null && combatQuest != null && active.Pointer != combatQuest.Pointer
                    && !Plugin.IsProtectedQuest(active) && Plugin.SuspendedCount(__instance) < 8
                    && Plugin.StashSuspended(__instance, active))
                {
                    try { __instance.RemoveQuest(active); }
                    catch (Exception ex) { Plugin.Logger.LogError($"[VillagerFightBack] RemoveQuest: {ex}"); }

                    if (Plugin.DebugLogging.Value && Plugin.ShouldLogArb())
                    {
                        string an = "?"; try { an = active.Name; } catch { }
                        Plugin.Logger.LogInfo(
                            $"[VillagerFightBack] Suspended work quest '{an}' while engaging a whitelisted enemy " +
                            $"(so she drops to idle and fights). Will restore when the enemy is gone.");
                    }
                }
            }
            else
            {
                // Not engaged: restore anything we suspended for this villager.
                if (Plugin.TryRestoreSuspended(__instance, out var restored) && restored != null)
                {
                    int n = 0;
                    foreach (var q in restored)
                    {
                        if (q == null) continue;
                        try { __instance.AddQuest(q); n++; }
                        catch (Exception ex) { Plugin.Logger.LogError($"[VillagerFightBack] AddQuest restore: {ex}"); }
                    }
                    if ((Plugin.DebugLogging.Value || Plugin.TimerDiagnostics.Value) && n > 0)
                        Plugin.Logger.LogInfo($"[VillagerFightBack] Restored {n} suspended work quest(s) — enemy gone.");
                }

                // Post-combat watch (diagnostic): for a few seconds after a fight ends, show which
                // quest actually runs and what the combat timer reads. If lingering persists, these
                // lines name the quest that holds the villager (e.g. a separate defensive task).
                if (Plugin.TimerDiagnostics.Value && Plugin.TryGetPostCombatWatch(__instance, out var watchData) && Plugin.ShouldLogTimer())
                {
                    string aq = "?"; float ap = -1f; float ct = -1f;
                    try { if (__instance.ActiveQuest != null) aq = __instance.ActiveQuest.Name; ap = __instance._activeQuestPriority; } catch { }
                    try { if (watchData != null && Plugin.PtrOf(watchData) != IntPtr.Zero) ct = watchData._combatTimeRemaining; } catch { }
                    Plugin.Logger.LogInfo($"[VillagerFightBack][postcombat] activeQuest={aq} prio={ap:0.0} combatTime={ct:0.0}");
                }
            }
        }
        catch (Exception ex) { Plugin.Logger.LogError($"[VillagerFightBack] QuestRunner.Update postfix: {ex}"); }
    }
}

// THE actual fix. FSM_SetVillagerIsFleeing is the FSM action that flips Villager.IsFleeing — the flag
// that drives the run-away behavior (suppressing _GetSpooked didn't stop it because that's only the
// notification, not the flag). The action instance with fleeingState=true is "start fleeing". When
// the villager's current combat target is whitelisted, we skip that action so the flag never goes
// true and their combat FSM falls through to the melee branch (the same one that already runs when
// idle). The FSM only ticks on the authoritative sim, and skipping sets less state → co-op-safe.
[HarmonyPatch(typeof(FSM_SetVillagerIsFleeing), nameof(FSM_SetVillagerIsFleeing.OnStateEnter))]
internal static class BlockFleeingPatch
{
    static bool Prefix(FSM_SetVillagerIsFleeing __instance, IFSMBehaviourController fsmBehaviour)
    {
        try
        {
            if (!Plugin.EnabledCfg.Value || !Plugin.PreventFlee) return true;
            if (__instance == null || !__instance.fleeingState) return true; // only block "start fleeing"
            if (fsmBehaviour == null) return true;

            var targeting = fsmBehaviour.AiTargeting;
            IAttackTarget? target = targeting != null ? targeting.CurrentTarget : null;
            if (target == null || !Plugin.IsWhitelisted(target)) return true; // flee real threats

            if (Plugin.DebugLogging.Value && Plugin.ShouldLogBlock())
                Plugin.Logger.LogInfo(
                    $"[VillagerFightBack] Blocked flee state vs '{Plugin.SafeName(target)}' — villager should melee instead.");
            return false; // skip OnStateEnter → IsFleeing not set true → no run-away
        }
        catch (Exception ex) { Plugin.Logger.LogError($"[VillagerFightBack] BlockFleeing prefix: {ex}"); return true; }
    }
}

// THE fix. The villager combat FSM branches on FSM_CheckVillagerWarrior: warriors melee
// (FSM_MeleeCombat), non-warriors run (FSM_RunFromTarget). Removing the fear (above) wasn't enough —
// the FSM still routes a non-warrior down the run branch. So while the villager's current target is
// whitelisted, we answer this decision "yes", sending them into the existing melee behavior. Scoped
// to whitelisted targets, so they're only "warriors" for the duration of fighting a wisp.
[HarmonyPatch(typeof(FSM_CheckVillagerWarrior), nameof(FSM_CheckVillagerWarrior.Decide))]
internal static class WarriorCheckPatch
{
    static void Postfix(IFSMBehaviourController fsmBehaviour, ref bool __result)
    {
        try
        {
            if (__result) return; // already a warrior — nothing to do
            if (!Plugin.EnabledCfg.Value || !Plugin.TreatAsWarrior) return;
            if (fsmBehaviour == null) return;

            var targeting = fsmBehaviour.AiTargeting;
            IAttackTarget? target = targeting != null ? targeting.CurrentTarget : null;
            if (target == null || !Plugin.IsWhitelisted(target)) return; // non-warriors flee real threats

            __result = true;
            if (Plugin.DebugLogging.Value && Plugin.ShouldLogWarrior())
                Plugin.Logger.LogInfo(
                    $"[VillagerFightBack] Treating villager as warrior vs '{Plugin.SafeName(target)}' — should melee.");
        }
        catch (Exception ex) { Plugin.Logger.LogError($"[VillagerFightBack] WarriorCheck postfix: {ex}"); }
    }
}

// FSM_RunFromTarget is the action that performs the actual run-away movement. Even with the fear flag
// suppressed, the combat FSM is still entering its run state — so we block this action while the
// villager's current target is whitelisted. Also logs entry (a truth-probe: confirms she's in the
// run state and what's driving it).
[HarmonyPatch(typeof(FSM_RunFromTarget))]
internal static class RunFromTargetPatch
{
    [HarmonyPrefix]
    [HarmonyPatch(nameof(FSM_RunFromTarget.OnStateEnter))]
    static bool OnEnter(IFSMBehaviourController fsmBehaviour) => !BlockRun(fsmBehaviour, true);

    [HarmonyPrefix]
    [HarmonyPatch(nameof(FSM_RunFromTarget.OnStateUpdate))]
    static bool OnUpdate(IFSMBehaviourController fsmBehaviour) => !BlockRun(fsmBehaviour, false);

    // Returns true if the run should be blocked.
    static bool BlockRun(IFSMBehaviourController fsmBehaviour, bool isEnter)
    {
        try
        {
            if (fsmBehaviour == null) return false;
            var targeting = fsmBehaviour.AiTargeting;
            IAttackTarget? target = targeting != null ? targeting.CurrentTarget : null;
            if (target == null || !Plugin.IsWhitelisted(target)) return false;

            bool block = Plugin.EnabledCfg.Value && Plugin.PreventRunMovement;
            if (isEnter && Plugin.DebugLogging.Value && Plugin.ShouldLogRun())
                Plugin.Logger.LogInfo(
                    $"[VillagerFightBack] FSM_RunFromTarget entered vs '{Plugin.SafeName(target)}' (block={block}).");
            return block;
        }
        catch (Exception ex) { Plugin.Logger.LogError($"[VillagerFightBack] RunFromTarget: {ex}"); return false; }
    }
}

// Truth-probe (log-only): fires if the villager ever enters the melee state while targeting a
// whitelisted enemy. If we see this, the melee branch is reachable; if we never do while she runs,
// her active combat quest simply has no melee branch (→ she needs the warrior combat quest).
[HarmonyPatch(typeof(FSM_MeleeCombat), nameof(FSM_MeleeCombat.OnStateEnter))]
internal static class MeleeEntryProbePatch
{
    static void Postfix(IFSMBehaviourController fsmBehaviour)
    {
        try
        {
            if (!Plugin.DebugLogging.Value || fsmBehaviour == null) return;
            var targeting = fsmBehaviour.AiTargeting;
            IAttackTarget? target = targeting != null ? targeting.CurrentTarget : null;
            if (target == null || !Plugin.IsWhitelisted(target)) return;
            if (Plugin.ShouldLogWarrior())
                Plugin.Logger.LogInfo($"[VillagerFightBack] Entered MELEE state vs '{Plugin.SafeName(target)}'.");
        }
        catch { }
    }
}
