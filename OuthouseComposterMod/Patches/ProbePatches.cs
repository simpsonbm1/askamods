using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes;
using SSSGame;
using SSSGame.AI;
using UnityEngine;

namespace OuthouseComposterMod.Patches;

// v1.3.99 — READ-ONLY diagnostic probe build, NEVER shipped to Nexus (see MyPluginInfo.PLUGIN_VERSION
// / csproj <Version> = 1.3.99, deliberately never a real release number). We get exactly one Nexus
// upload shot for the "villagers still steal from the outhouse" fix, so this probe exists to gather
// facts locally before committing to a fix.
//
// The shipped mod (Patches/HaulGatePatches.cs, Patches/EatGatePatches.cs) covers exactly ONE
// declarer each of two much larger method families — a Cecil static sweep
// (_explore/cecil_outhouse_manifest_impl_out.txt, _explore/cecil_outhouse_gate_family_out.txt)
// found the rest, but Il2CppInterop models NO interface implementations at all, so which of the
// many concrete IResourceStorageSite-shaped types the Outhouse's own storage site actually IS can't
// be determined statically. This probe patches every remaining declarer with a POSTFIX that only
// observes:
//   Probe B — SSSGame.AI.*QuestData.IsWhitelistedByStorage(IResourceStorageSite), 14 declarers not
//             already covered by EatGatePatches.cs's SatisfyObjectiveQuestData patch, plus
//             WorkstationQuestData's IsWhitelistedByStorage(ResourceOutlet) overload.
// (The old "Probe A" postfix group was REMOVED (v1.3.105): both the full 14-declarer 'manifest'
// group and the 2-declarer 'manifest-single' group crashed the game natively and must never exist
// in a shipped build. See docs/mods/outhouse-composter.md for the confirmed dead-end.)
// Every postfix here NEVER assigns __result — this file only reads and logs.
//
// Manual patching (a loop over AccessTools.TypeByName + AccessTools.Method) is used instead of
// [HarmonyPatch] attributes because the target list is long and uniform, and because a
// runtime-resolved miss must be a logged skip, never a build error or a load-time throw — see
// ApplyGateProbes below.
internal static class ProbePatches
{
    // ── Probe B target list (FACT 2 — IsWhitelistedByStorage(IResourceStorageSite) declarers NOT
    // already covered by the shipped EatGatePatches.cs, which only patches SatisfyObjectiveQuestData.
    // SSSGame.ICookingFetchQuestData is an INTERFACE — deliberately excluded, not patchable) ────────
    private static readonly string[] GateSiteDeclarers =
    {
        "SSSGame.AI.CleanupInventoryQuest+CleanupInventoryQuestData",
        "SSSGame.AI.DressUpQuest+DressUpQuestData",
        "SSSGame.AI.FSM.FSM_Fetch+GatherData",
        "SSSGame.AI.FSM.FSM_FetchExpeditionSupplies+ActionData",
        "SSSGame.AI.GatherAndHarvestQuest+GatherAndHarvestData",
        "SSSGame.AI.HomeStockpileQuest+HomeStockpileQuestData",
        "SSSGame.AI.PaintEquipmentQuest+PaintEquipmentQuestData",
        "SSSGame.AI.WorkstationQuest+WorkstationQuestData",
        "SSSGame.CookingQuest+CookingQuestData",
        "SSSGame.CookingStockpileQuest+CookingStockpileQuestData",
        "SSSGame.CrafterFetchQuest+CrafterFetchQuestData",
        "SSSGame.DyeingQuest+DyeingQuestData",
        "SSSGame.KennelFetchQuest+KennelFetchQuestData",
        "SSSGame.TroughWaterSupplyQuest+TroughWaterSupplyQuestData",
    };

    private const string WorkstationQuestDataTypeName = "SSSGame.AI.WorkstationQuest+WorkstationQuestData";

    // ── Probe B state ───────────────────────────────────────────────────────────────────────────
    private static readonly HashSet<string> _aliveLoggedGate = new();
    private static readonly HashSet<string> _hitLoggedGate = new();
    private static readonly Dictionary<string, int> _gateCalls = new();
    private static readonly Dictionary<string, int> _gateHits = new();
    private static bool _loggedGateSiteError;
    private static bool _loggedGateOutletError;

    // ── Villager attribution state (v1.3.102) ──────────────────────────────────────────────────
    // The gate postfixes above see the asking QuestData/GatherData instance directly (Harmony
    // __instance) — that object belongs to a villager, so it's the only place in this probe that
    // CAN know who took something. ComposterDiag's Probe D tripwire is a pure polling diff and
    // can never know the actor on its own; the ring buffer below is how the two correlate.
    //
    // _gatesActive records whether the 'gates' probe group was selected in Apply() — the
    // ring buffer only ever gets entries when the gate postfixes are actually wired, so the
    // tripwire needs to tell "no asker in the last 10s" apart from "gate probes weren't even on".
    private static bool _gatesActive;

    // Actor resolution result, cached by the asking instance's native pointer (project-wide
    // gotcha: never re-resolve per call on a hot path — one gate type alone logged 18,885 calls
    // in a short session). Cleared on world-leave via ClearProbeState() below, called from
    // ComposterDiag.NoteWorldLeft() alongside OuthouseGate.ClearCache().
    private readonly struct ActorInfo
    {
        internal readonly string VillagerName;
        internal readonly string WorkstationName;
        internal ActorInfo(string villagerName, string workstationName)
        {
            VillagerName = villagerName;
            WorkstationName = workstationName;
        }
        internal static readonly ActorInfo Unresolved = new("unresolved", "none");
    }
    private static readonly Dictionary<IntPtr, ActorInfo> _actorCache = new();

    // Fixed-size ring buffer of recent outhouse asks, pushed on EVERY outhouse hit (no logging —
    // must stay cheap) so the tripwire can correlate a LOST line against who recently asked.
    // Oldest-first order: appended at the end, dropped from the front once full.
    private readonly struct AskRecord
    {
        internal readonly float TimeSeconds;
        internal readonly string QuestType;
        internal readonly string VillagerName;
        internal readonly string WorkstationName;
        internal AskRecord(float timeSeconds, string questType, string villagerName, string workstationName)
        {
            TimeSeconds = timeSeconds;
            QuestType = questType;
            VillagerName = villagerName;
            WorkstationName = workstationName;
        }
    }
    private const int RecentAsksCapacity = 32;
    private const float RecentAskWindowSeconds = 10f;
    private const int RecentAskersMaxShown = 5;
    private static readonly List<AskRecord> _recentAsks = new(RecentAsksCapacity);

    // ── Group selection (v1.3.100) ─────────────────────────────────────────────────────────────
    // Independent of ProbeDiagnostics (which gates LOG OUTPUT of an already-attached postfix) — this
    // gates whether the postfixes are attached to the game AT ALL. A skipped group must not touch
    // AccessTools for its targets at all.
    // v1.3.105 — the 'manifest'/'manifest-single'/'both' values were REMOVED along with the old
    // "Probe A" postfix group they wired (both crashed the game natively). Only 'none' and 'gates'
    // remain.
    private enum ProbeGroupSelection { None, Gates }

    private static ProbeGroupSelection ParseGroups(string? raw)
    {
        string v = (raw ?? string.Empty).Trim().ToLowerInvariant();
        switch (v)
        {
            case "none": return ProbeGroupSelection.None;
            case "gates": return ProbeGroupSelection.Gates;
            default:
                Plugin.Logger.LogWarning($"[OuthouseComposter][probe] ProbeGroups: unrecognised value '{raw}' — treating as 'none' (safe mode). Valid values: none, gates.");
                return ProbeGroupSelection.None;
        }
    }

    // ── Wiring ──────────────────────────────────────────────────────────────────────────────────
    internal static void Apply(Harmony harmony)
    {
        ProbeGroupSelection selection = ParseGroups(Plugin.ProbeGroups.Value);
        bool doGates = selection == ProbeGroupSelection.Gates;

        // Distinct-type-name cache, scoped to this single Apply() call — a type name used by
        // several targets (e.g. WorkstationQuestData, which appears in both the gate-site loop and
        // the separate gate-outlet lookup below) is only looked up via AccessTools.TypeByName once.
        var typeCache = new Dictionary<string, Type?>();

        int gateWired = doGates ? ApplyGateProbes(harmony, typeCache) : 0;

        // v1.3.102 — the tripwire's recentAskers=[...] field needs to distinguish "no asker in the
        // last 10s" from "the gate probes weren't even wired" (see ActorInfo/AskRecord state above).
        _gatesActive = doGates;

        switch (selection)
        {
            case ProbeGroupSelection.None:
                Plugin.Logger.LogInfo("[OuthouseComposter][probe] ProbePatches: groups='none' — no probe patches applied (safe mode).");
                break;
            case ProbeGroupSelection.Gates:
                Plugin.Logger.LogInfo($"[OuthouseComposter][probe] ProbePatches: groups='gates' wired gate={gateWired}/{GateSiteDeclarers.Length + 1}.");
                break;
        }
    }

    private static int ApplyGateProbes(Harmony harmony, Dictionary<string, Type?> typeCache)
    {
        int wired = 0;
        foreach (var typeName in GateSiteDeclarers)
        {
            Type? t = ResolveTypeCached(typeCache, typeName, "gate");
            if (t == null) continue;

            MethodInfo? m = ResolveMethod(t, "IsWhitelistedByStorage", new[] { typeof(IResourceStorageSite) }, typeName, "gate");
            if (m == null) continue;

            if (PatchSafely(harmony, m, nameof(WhitelistSitePostfix), typeName, "gate"))
                wired++;
        }

        // WorkstationQuestData additionally overloads IsWhitelistedByStorage(ResourceOutlet) —
        // patch that too (Cecil-confirmed, _explore/cecil_outhouse_gate_family_out.txt section 5).
        // Already resolved once above (it's also a GateSiteDeclarers entry) — the cache serves it.
        Type? wqd = ResolveTypeCached(typeCache, WorkstationQuestDataTypeName, "gate");
        if (wqd != null)
        {
            MethodInfo? m = ResolveMethod(wqd, "IsWhitelistedByStorage", new[] { typeof(ResourceOutlet) }, WorkstationQuestDataTypeName, "gate");
            if (m != null && PatchSafely(harmony, m, nameof(WhitelistOutletPostfix), WorkstationQuestDataTypeName, "gate"))
                wired++;
        }

        return wired;
    }

    private static Type? ResolveTypeCached(Dictionary<string, Type?> typeCache, string typeName, string probeLabel)
    {
        if (typeCache.TryGetValue(typeName, out Type? cached))
            return cached;

        Type? t = ResolveType(typeName, probeLabel);
        typeCache[typeName] = t;
        return t;
    }

    private static Type? ResolveType(string typeName, string probeLabel)
    {
        Type? t = null;
        try { t = AccessTools.TypeByName(typeName); }
        catch (Exception ex) { Plugin.Logger.LogError($"[OuthouseComposter][probe][{probeLabel}] TypeByName('{typeName}') threw: {ex}"); }

        if (t == null)
            Plugin.Logger.LogWarning($"[OuthouseComposter][probe][{probeLabel}] target type not resolved: {typeName}");
        return t;
    }

    private static MethodInfo? ResolveMethod(Type t, string methodName, Type[] parameterTypes, string typeName, string probeLabel)
    {
        MethodInfo? m = null;
        try { m = AccessTools.Method(t, methodName, parameterTypes); }
        catch (Exception ex) { Plugin.Logger.LogError($"[OuthouseComposter][probe][{probeLabel}] Method resolve for {typeName}.{methodName}() threw: {ex}"); }

        if (m == null)
            Plugin.Logger.LogWarning($"[OuthouseComposter][probe][{probeLabel}] target method not resolved: {typeName}.{methodName}()");
        return m;
    }

    private static bool PatchSafely(Harmony harmony, MethodInfo target, string postfixName, string typeName, string probeLabel)
    {
        try
        {
            harmony.Patch(target, postfix: new HarmonyMethod(typeof(ProbePatches), postfixName));
            return true;
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"[OuthouseComposter][probe][{probeLabel}] failed to patch {typeName}.{target.Name}(): {ex}");
            return false;
        }
    }

    // ── Probe B postfixes ───────────────────────────────────────────────────────────────────────
    private static void WhitelistSitePostfix(object __instance, IResourceStorageSite rss, bool __result, MethodBase __originalMethod)
    {
        if (!Plugin.ProbeDiagnostics.Value) return;
        try
        {
            string typeName = SafeDeclaringTypeName(__originalMethod);
            Increment(_gateCalls, typeName);

            if (_aliveLoggedGate.Add(typeName))
                Plugin.Logger.LogInfo($"[OuthouseComposter][probe][gate] alive: {typeName}");

            bool isOuthouse = false;
            try { isOuthouse = OuthouseGate.IsOuthouseSite(rss); } catch { }
            if (!isOuthouse) return;

            Increment(_gateHits, typeName);

            ActorInfo actor = ActorInfo.Unresolved;
            try { actor = ResolveActor(__instance); } catch { }

            PushAsk(typeName, actor.VillagerName, actor.WorkstationName);

            string hitKey = typeName + "|" + actor.VillagerName;
            if (_hitLoggedGate.Add(hitKey))
                Plugin.Logger.LogInfo($"[OuthouseComposter][probe][gate] OUTHOUSE ASKED: type={typeName} villager='{actor.VillagerName}' workstation='{actor.WorkstationName}' nativeResult={__result}");
        }
        catch (Exception ex)
        {
            LogOnce(ref _loggedGateSiteError, "gate(site)", ex);
        }
    }

    private static void WhitelistOutletPostfix(object __instance, ResourceOutlet resourceOutlet, bool __result, MethodBase __originalMethod)
    {
        if (!Plugin.ProbeDiagnostics.Value) return;
        try
        {
            string typeName = SafeDeclaringTypeName(__originalMethod);
            Increment(_gateCalls, typeName);

            if (_aliveLoggedGate.Add(typeName))
                Plugin.Logger.LogInfo($"[OuthouseComposter][probe][gate] alive: {typeName}");

            bool isOuthouse = false;
            try { isOuthouse = OuthouseGate.IsOuthouseOutlet(resourceOutlet); } catch { }
            if (!isOuthouse) return;

            Increment(_gateHits, typeName);

            ActorInfo actor = ActorInfo.Unresolved;
            try { actor = ResolveActor(__instance); } catch { }

            PushAsk(typeName, actor.VillagerName, actor.WorkstationName);

            string hitKey = typeName + "|" + actor.VillagerName;
            if (_hitLoggedGate.Add(hitKey))
                Plugin.Logger.LogInfo($"[OuthouseComposter][probe][gate] OUTHOUSE ASKED: type={typeName} villager='{actor.VillagerName}' workstation='{actor.WorkstationName}' nativeResult={__result}");
        }
        catch (Exception ex)
        {
            LogOnce(ref _loggedGateOutletError, "gate(outlet)", ex);
        }
    }

    // ── Villager attribution (v1.3.102) ────────────────────────────────────────────────────────
    // Resolves the villager behind a gate postfix's `object __instance` (the asking QuestData/
    // GatherData), caching by native pointer since this sits on the same hot path as the gate
    // postfixes themselves. Since __instance here is the ACTUAL Harmony-provided instance of the
    // exact type the postfix was patched onto (not a base-typed materialization from a list/field),
    // direct `is`/`as` downcasts against its own type hierarchy are safe and the "managed casts
    // lie" gotcha doesn't apply.
    private static ActorInfo ResolveActor(object instance)
    {
        IntPtr ptr = IntPtr.Zero;
        try { if (instance is Il2CppObjectBase b) ptr = b.Pointer; } catch { }
        if (ptr == IntPtr.Zero) return ActorInfo.Unresolved;

        if (_actorCache.TryGetValue(ptr, out var cached)) return cached;

        ActorInfo info = ResolveActorUncached(instance);
        _actorCache[ptr] = info;
        return info;
    }

    // Four-step resolution, each step independently try/caught so one failing cast/call can never
    // take down the gate postfix that called us:
    //   1. WorkstationQuest.WorkstationQuestData.GetVillager() — also covers every type that
    //      inherits it (CleanupInventoryQuestData, GatherAndHarvestData, CookingStockpileQuestData,
    //      CrafterFetchQuestData — Cecil-confirmed, see delegation spec).
    //   2. DressUpQuest.DressUpQuestData.GetVillager() — its own accessor, not inherited from (1).
    //   3. QuestData.QuestRunner → QuestRunner.GetComponent<Villager>() (singular generic only —
    //      the plural/non-generic GetComponents family throws MissingMethodException through the
    //      interop trampoline, project-wide gotcha).
    //   4. Unresolved — e.g. FSM_Fetch+GatherData, whose base is Object with no QuestData/
    //      GetVillager at all (Cecil-confirmed).
    private static ActorInfo ResolveActorUncached(object instance)
    {
        Villager? villager = null;

        try
        {
            if (instance is WorkstationQuest.WorkstationQuestData wqd)
                villager = wqd.GetVillager();
        }
        catch { }

        if (villager == null)
        {
            try
            {
                if (instance is DressUpQuest.DressUpQuestData dqd)
                    villager = dqd.GetVillager();
            }
            catch { }
        }

        if (villager == null)
        {
            try
            {
                if (instance is QuestData qd)
                {
                    QuestRunner? runner = qd.QuestRunner;
                    if (runner != null)
                    {
                        try { villager = runner.GetComponent<Villager>(); } catch { }
                    }
                }
            }
            catch { }
        }

        if (villager == null) return ActorInfo.Unresolved;

        string villagerName = "?";
        try { villagerName = villager.GetName() ?? "?"; } catch { }

        string workstationName = "none";
        try
        {
            Workstation? ws = null;
            try { ws = villager.GetWorkstation(); } catch { }
            if (ws == null) { try { ws = villager.GetNonVikingWorkstation(); } catch { } }
            if (ws != null) { try { workstationName = ws.GetName() ?? "none"; } catch { } }
        }
        catch { }

        return new ActorInfo(villagerName, workstationName);
    }

    // Pushed on EVERY outhouse hit (no logging here — must stay cheap). Fixed-size ring buffer:
    // once full, drops the oldest entry before appending the new one.
    private static void PushAsk(string questType, string villagerName, string workstationName)
    {
        try
        {
            float now = Time.realtimeSinceStartup;
            if (_recentAsks.Count >= RecentAsksCapacity)
                _recentAsks.RemoveAt(0);
            _recentAsks.Add(new AskRecord(now, questType, villagerName, workstationName));
        }
        catch { }
    }

    // Called by ComposterDiag's Probe D tripwire when it logs a LOST line. Returns the inner
    // contents of the recentAskers=[...] field (caller supplies the brackets) — most-recent-first,
    // within the last RecentAskWindowSeconds, deduplicated by (villagerName + questType), capped at
    // RecentAskersMaxShown. Distinguishes "gate probes not active" (ProbeGroups isn't 'gates', so
    // the ring buffer can never have entries) from "none in last 10s" (probes active, just no
    // recent hit) — printing the wrong one of these two would misleadingly suggest either that
    // attribution is broken, or that nobody has touched the outhouse recently.
    internal static string DescribeRecentAskers()
    {
        if (!_gatesActive) return "gate probes not active";

        try
        {
            float now = Time.realtimeSinceStartup;
            var seen = new HashSet<string>();
            var parts = new List<string>();

            for (int i = _recentAsks.Count - 1; i >= 0 && parts.Count < RecentAskersMaxShown; i--)
            {
                AskRecord r = _recentAsks[i];
                float age = now - r.TimeSeconds;
                if (age < 0f) age = 0f;
                if (age > RecentAskWindowSeconds) continue;

                string dedupKey = r.VillagerName + "|" + r.QuestType;
                if (!seen.Add(dedupKey)) continue;

                string ageStr = age.ToString("0.0", CultureInfo.InvariantCulture);
                parts.Add($"{r.VillagerName}({r.WorkstationName}, {r.QuestType}) {ageStr}s ago");
            }

            return parts.Count > 0 ? string.Join(", ", parts) : "none in last 10s";
        }
        catch (Exception ex)
        {
            return $"unresolved (error: {ex.Message})";
        }
    }

    // Called from ComposterDiag.NoteWorldLeft() alongside OuthouseGate.ClearCache() — the actor
    // cache is keyed by native pointer (project-wide gotcha: never survives a world session).
    // _recentAsks holds only strings/floats (no native wrappers) but is cleared too so a LOST line
    // right after a world reload can't attribute to a stale pre-reload asker.
    internal static void ClearProbeState()
    {
        _actorCache.Clear();
        _recentAsks.Clear();
    }

    // ── Shared helpers ──────────────────────────────────────────────────────────────────────────
    private static string SafeDeclaringTypeName(MethodBase? mb)
    {
        try { return mb?.DeclaringType?.Name ?? "?"; } catch { return "?"; }
    }

    private static void Increment(Dictionary<string, int> counts, string key)
    {
        counts[key] = counts.TryGetValue(key, out var n) ? n + 1 : 1;
    }

    private static void LogOnce(ref bool flag, string label, Exception ex)
    {
        if (flag) return;
        flag = true;
        Plugin.Logger.LogError($"[OuthouseComposter][probe] {label} postfix error (further {label} errors suppressed): {ex}");
    }

    // ── Rollup (called from ComposterDiag's existing polling loop, at most every 30s) ─────────────
    internal static void LogRollup()
    {
        try
        {
            var gateParts = new List<string>();
            foreach (var kv in _gateCalls)
            {
                int hits = _gateHits.TryGetValue(kv.Key, out var h) ? h : 0;
                gateParts.Add($"{kv.Key}={kv.Value}/{hits}");
            }
            Plugin.Logger.LogInfo($"[OuthouseComposter][probe][rollup] gates: {(gateParts.Count > 0 ? string.Join(", ", gateParts) : "(no calls yet)")}");
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"[OuthouseComposter][probe] LogRollup error: {ex}");
        }
    }
}
