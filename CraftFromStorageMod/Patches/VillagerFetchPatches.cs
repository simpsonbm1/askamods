using System;
using HarmonyLib;
using SSSGame;
using SSSGame.AI;
using SSSGame.AI.FSM;

namespace CraftFromStorageMod.Patches;

// Phase 2a (idea-17) villager-fetch spike (v0.6.0). READ-ONLY: every patch below only reads state
// and logs via Plugin.Logger - none writes inventory, transfers items, flips a __result, or calls a
// mutating method. Every patch body is wrapped in try/catch so a throw here can never break
// villager AI. All log lines carry the "[CFS-P2]" prefix (distinct from Phase 0/1's "[CFS]") so
// this spike's output greps cleanly on its own.
//
// Question this spike answers, from the log alone: does a villager crafter walk STRAIGHT to its
// workstation (verdict=DIRECT), or does it detour through FSM_FetchCraftingSupplies first
// (verdict=TOURED) - i.e. is the vanilla "a villager can only use its OWN station's storage" limit
// a fetch-REACH restriction (search depth/range) or a storage WHITELIST? See CYCLE SUMMARY
// (UseCraftingStationEnterPatch below) for the one line that answers this directly. This spike does
// NOT move either lever - pure observation.
//
// Confirmed signatures (Cecil 2026-07-20/21, _explore/cecil_craft_from_storage7_out.txt +
// cecil_craft_from_storage8_out.txt) - all in namespace SSSGame.AI.FSM (no "Il2Cpp" prefix, same as
// SSSGame.CraftInteraction elsewhere in this mod), all virtual (low AOT-inlining risk, still
// fire-verified below via PATCH ALIVE):
//   FSM_FetchCraftingSupplies : FSM_QuestAction
//     Void OnStateEnter(IFSMBehaviourController fsmBehaviour)   - virtual, public
//     Void OnStateExit(IFSMBehaviourController fsmBehaviour)    - virtual, public
//     P: maxSearchDepth Int32, worldSearchRange Single, storageSearchRange Single, searchWorld Bool,
//        searchMarkers Bool, searchStorages Bool, searchWorldForCompletedObjects Bool,
//        ignoreHiddenFromResourceGatherers Bool
//   FSM_UseCraftingStation : FSM_QuestAction
//     Void OnStateEnter(IFSMBehaviourController fsmBehaviour)   - virtual, public
//   FSM_ReturnCraftingSupplies : FSM_QuestAction
//     Void OnStateEnter(IFSMBehaviourController fsmBehaviour)   - virtual, public
//   SSSGame.CrafterFetchQuest/CrafterFetchQuestData : WorkstationQuestData : QuestData (SSSGame.AI)
//     Boolean IsWhitelistedByStorage(IResourceStorageSite rss)  - virtual, public
//     P: _noPartsFound Bool, _noStorageSpace Bool, _noCarryCapacity Bool (all read as C# PROPERTIES
//        despite the underscore prefix - confirmed by Cecil, not raw fields)
//     M: Villager GetVillager() - inherited from WorkstationQuestData
//
// Patch application (not an in-body re-check) is gated in Plugin.cs Load(): targets 1-4 under
// EnableVillagerFetchTrace, target 5 under TraceStorageWhitelist - matching this mod's established
// per-flag harmony.PatchAll() convention (GatePatches.cs), so a disabled patch is never even
// applied rather than applied-but-silent.

// ---- 1. FSM_FetchCraftingSupplies.OnStateEnter - the money shot: proves whether a supply run
//          started, and dumps every fetch-REACH lever candidate off the live instance. ----
[HarmonyPatch(typeof(FSM_FetchCraftingSupplies), nameof(FSM_FetchCraftingSupplies.OnStateEnter))]
internal static class FetchCraftingSuppliesEnterPatch
{
    private const string TargetName = "FSM_FetchCraftingSupplies.OnStateEnter";

    static void Postfix(FSM_FetchCraftingSupplies __instance, IFSMBehaviourController fsmBehaviour)
    {
        try
        {
            VillagerFetchTrace.MarkAlive(TargetName);

            string villagerName = VillagerFetchTrace.SafeVillagerName(fsmBehaviour);
            string instancePtr = VillagerFetchTrace.SafeHex(__instance);

            int maxSearchDepth = 0;
            float storageSearchRange = 0f, worldSearchRange = 0f;
            bool searchWorld = false, searchMarkers = false, searchStorages = false,
                 searchWorldForCompletedObjects = false, ignoreHiddenFromResourceGatherers = false;
            try { maxSearchDepth = __instance.maxSearchDepth; } catch { }
            try { storageSearchRange = __instance.storageSearchRange; } catch { }
            try { worldSearchRange = __instance.worldSearchRange; } catch { }
            try { searchWorld = __instance.searchWorld; } catch { }
            try { searchMarkers = __instance.searchMarkers; } catch { }
            try { searchStorages = __instance.searchStorages; } catch { }
            try { searchWorldForCompletedObjects = __instance.searchWorldForCompletedObjects; } catch { }
            try { ignoreHiddenFromResourceGatherers = __instance.ignoreHiddenFromResourceGatherers; } catch { }

            Plugin.Logger.LogInfo($"[CFS-P2] FETCH ENTER villager={villagerName} instance=0x{instancePtr} " +
                $"maxSearchDepth={maxSearchDepth} storageSearchRange={storageSearchRange} worldSearchRange={worldSearchRange} " +
                $"searchWorld={searchWorld} searchMarkers={searchMarkers} searchStorages={searchStorages} " +
                $"searchWorldForCompletedObjects={searchWorldForCompletedObjects} " +
                $"ignoreHiddenFromResourceGatherers={ignoreHiddenFromResourceGatherers}");

            VillagerFetchTrace.RecordFetchEnter(villagerName);
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"[CFS-P2] FetchCraftingSuppliesEnterPatch: {ex}");
        }
    }
}

// ---- 2. FSM_FetchCraftingSupplies.OnStateExit - minimal transition marker + fire-verify. Not part
//          of the "money shot" dataset (see targets 1/3/5); its jobs are to (a) prove this method
//          isn't AOT-inlined and (b) mark the enter/exit pairing for full traceability, since the
//          whole point of this spike is that the log alone must tell the story. ----
[HarmonyPatch(typeof(FSM_FetchCraftingSupplies), nameof(FSM_FetchCraftingSupplies.OnStateExit))]
internal static class FetchCraftingSuppliesExitPatch
{
    private const string TargetName = "FSM_FetchCraftingSupplies.OnStateExit";

    static void Postfix(FSM_FetchCraftingSupplies __instance, IFSMBehaviourController fsmBehaviour)
    {
        try
        {
            VillagerFetchTrace.MarkAlive(TargetName);

            string villagerName = VillagerFetchTrace.SafeVillagerName(fsmBehaviour);
            string instancePtr = VillagerFetchTrace.SafeHex(__instance);
            Plugin.Logger.LogInfo($"[CFS-P2] FETCH EXIT villager={villagerName} instance=0x{instancePtr}");
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"[CFS-P2] FetchCraftingSuppliesExitPatch: {ex}");
        }
    }
}

// ---- 3. FSM_UseCraftingStation.OnStateEnter - the line that answers the tester's question
//          directly: CYCLE SUMMARY verdict=DIRECT|TOURED. Also fires the best-effort quest-data
//          enrichment (complaint flags) as a SEPARATE log line, so a failure there can never
//          suppress or corrupt the primary summary line above it. ----
[HarmonyPatch(typeof(FSM_UseCraftingStation), nameof(FSM_UseCraftingStation.OnStateEnter))]
internal static class UseCraftingStationEnterPatch
{
    private const string TargetName = "FSM_UseCraftingStation.OnStateEnter";

    static void Postfix(FSM_UseCraftingStation __instance, IFSMBehaviourController fsmBehaviour)
    {
        string villagerName = "?";
        try
        {
            VillagerFetchTrace.MarkAlive(TargetName);

            villagerName = VillagerFetchTrace.SafeVillagerName(fsmBehaviour);
            string summaryLine = VillagerFetchTrace.FormatAndResetCycle(villagerName);
            Plugin.Logger.LogInfo(summaryLine);
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"[CFS-P2] UseCraftingStationEnterPatch: {ex}");
            return;
        }

        // Villager enrichment: best-effort, independent try/catch - the CYCLE SUMMARY line above has
        // already been logged by this point, so a failure here can never suppress it.
        try
        {
            LogQuestDataEnrichment(__instance, fsmBehaviour, villagerName);
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"[CFS-P2] UseCraftingStationEnterPatch enrichment: {ex}");
        }
    }

    // FSM_QuestAction.GetQuestData(fsmBehaviour) returns a BASE-typed QuestData - managed as/is
    // casts lie for interop objects materialized under a base declared type (project-wide gotcha),
    // so a plain "as CrafterFetchQuestData" would return null even if the native object really is
    // one. Per the documented workaround (CLAUDE.md): identify the REAL native class first
    // (Plugin.NativeClassName), and only rewrap via new T(pointer) when it actually matches - a
    // blind rewrap of a wrongly-typed pointer risks a native crash that no try/catch can stop, not
    // just a bad read, so this check is a deliberate belt-and-suspenders step beyond a bare rewrap.
    private static void LogQuestDataEnrichment(FSM_UseCraftingStation instance, IFSMBehaviourController fsmBehaviour, string villagerName)
    {
        QuestData? qd = null;
        try { qd = instance.GetQuestData(fsmBehaviour); } catch { }
        if (qd == null)
        {
            Plugin.Logger.LogInfo($"[CFS-P2] CYCLE SUMMARY DETAIL villager={villagerName} questdata=unresolved");
            return;
        }

        string nativeClass = Plugin.NativeClassName(qd);
        if (nativeClass != "CrafterFetchQuestData")
        {
            Plugin.Logger.LogInfo($"[CFS-P2] CYCLE SUMMARY DETAIL villager={villagerName} questdata=unresolved nativeClass={nativeClass}");
            return;
        }

        IntPtr ptr = VillagerFetchTrace.SafePointer(qd);
        if (ptr == IntPtr.Zero)
        {
            Plugin.Logger.LogInfo($"[CFS-P2] CYCLE SUMMARY DETAIL villager={villagerName} questdata=unresolved nativeClass={nativeClass} nullPointer=true");
            return;
        }

        var cfqd = new CrafterFetchQuest.CrafterFetchQuestData(ptr);

        bool noPartsFound = false, noStorageSpace = false, noCarryCapacity = false;
        try { noPartsFound = cfqd._noPartsFound; } catch { }
        try { noStorageSpace = cfqd._noStorageSpace; } catch { }
        try { noCarryCapacity = cfqd._noCarryCapacity; } catch { }

        string questVillager = "?";
        try { questVillager = VillagerFetchTrace.SafeVillagerNameFromVillager(cfqd.GetVillager()); } catch { }

        Plugin.Logger.LogInfo($"[CFS-P2] CYCLE SUMMARY DETAIL villager={villagerName} questdata=resolved " +
            $"noPartsFound={noPartsFound} noStorageSpace={noStorageSpace} noCarryCapacity={noCarryCapacity} " +
            $"questVillager={questVillager}");
    }
}

// ---- 4. FSM_ReturnCraftingSupplies.OnStateEnter - minimal transition marker + fire-verify, same
//          reasoning as target 2. A villager reaching THIS state at all is itself strong
//          corroborating evidence for a TOURED cycle. ----
[HarmonyPatch(typeof(FSM_ReturnCraftingSupplies), nameof(FSM_ReturnCraftingSupplies.OnStateEnter))]
internal static class ReturnCraftingSuppliesEnterPatch
{
    private const string TargetName = "FSM_ReturnCraftingSupplies.OnStateEnter";

    static void Postfix(FSM_ReturnCraftingSupplies __instance, IFSMBehaviourController fsmBehaviour)
    {
        try
        {
            VillagerFetchTrace.MarkAlive(TargetName);

            string villagerName = VillagerFetchTrace.SafeVillagerName(fsmBehaviour);
            string instancePtr = VillagerFetchTrace.SafeHex(__instance);
            Plugin.Logger.LogInfo($"[CFS-P2] RETURN ENTER villager={villagerName} instance=0x{instancePtr}");
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"[CFS-P2] ReturnCraftingSuppliesEnterPatch: {ex}");
        }
    }
}

// ---- 5. CrafterFetchQuestData.IsWhitelistedByStorage - the other candidate lever: a storage
//          WHITELIST rather than a fetch-reach restriction. Villager identity comes straight off
//          __instance.GetVillager() (inherited from WorkstationQuestData) - this method has no
//          IFSMBehaviourController parameter of its own, so it can't use SafeVillagerName; reading
//          .gameObject.name off the SAME Villager component keys into the SAME cycle-state bucket
//          as targets 1-4 whenever it's the same GameObject (the expected case). ----
[HarmonyPatch(typeof(CrafterFetchQuest.CrafterFetchQuestData), nameof(CrafterFetchQuest.CrafterFetchQuestData.IsWhitelistedByStorage))]
internal static class IsWhitelistedByStoragePatch
{
    private const string TargetName = "CrafterFetchQuestData.IsWhitelistedByStorage";

    static void Postfix(CrafterFetchQuest.CrafterFetchQuestData __instance, IResourceStorageSite rss, bool __result)
    {
        try
        {
            VillagerFetchTrace.MarkAlive(TargetName);

            Villager? villager = null;
            try { villager = __instance?.GetVillager(); } catch { }
            string villagerName = VillagerFetchTrace.SafeVillagerNameFromVillager(villager);

            IntPtr sitePtr = VillagerFetchTrace.SafePointer(rss);
            int maxLogs = VillagerFetchTrace.SafeGetInt(Plugin.MaxWhitelistLogsPerCycle, 40);
            bool shouldLog = VillagerFetchTrace.RecordStorageProbe(villagerName, sitePtr, __result, maxLogs);
            if (!shouldLog) return;

            string siteName = "?";
            try
            {
                var t = rss?.GetTransform();
                var go = t != null ? t.gameObject : null;
                if (go != null) siteName = go.name;
            }
            catch { }

            Plugin.Logger.LogInfo($"[CFS-P2] STORAGE PROBE villager={villagerName} site={siteName} allowed={__result}");
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"[CFS-P2] IsWhitelistedByStoragePatch: {ex}");
        }
    }
}
