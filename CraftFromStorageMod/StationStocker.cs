using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using SandSailorStudio.Inventory;
using SSSGame;
using SSSGame.AI;
using SSSGame.AI.FSM;

namespace CraftFromStorageMod;

// v0.9.0 (idea-17 Phase 2c) - the inverted approach to villager crafting from settlement storage.
// v0.8.0 tried to make the AI scheduler skip FSM_FetchCraftingSupplies entirely (FetchQuestSuppression.
// cs) so the villager would craft immediately instead of walking off first. Confirmed in-game
// 2026-07-27 that this stalled crafting entirely: 704 of 708 cycles logged verdict=DIRECT
// fetchEnters=0 (the walk WAS suppressed), but all 708 cycles logged modPulls=0 and no crafting-
// success marker ever appeared in a 5m49s run. Root cause: the mod's existing just-in-time pull hangs
// off BeginCraftingSequence (Point C, CraftTransfer.HandleBeginCraftingSequence), which the AI
// scheduler never reaches for a villager standing at an EMPTY station - suppressing the walk removed
// the only thing that would ever have stocked it.
//
// v0.9.0 instead lets the fetch quest be chosen as vanilla intends, then intercepts it the MOMENT it
// starts (FSM_FetchCraftingSupplies.OnStateEnter) and teleports the materials she was about to fetch
// from settlement storage directly into the crafting station's inventory - so the walk becomes
// unnecessary through vanilla's own scheduling path rather than by fighting it. The v0.8.0 lever is
// retired behind Plugin.SuppressFetchQuestPriority (default false), not deleted - see Plugin.cs Load()
// and FetchQuestSuppression.cs for that history.
//
// All log lines here carry the "[CFS-SS]" tag (on top of the mod-wide "[CFS]" tag), distinct from both
// the v0.6.0 read-only "[CFS-P2]" spike and the v0.7.x/v0.8.0 "[CFS-V]"/"[CFS-FQ]" tags, so this
// feature's output greps cleanly on its own.
internal static class StationStocker
{
    // Own HashSet, own tag - deliberately NOT VillagerFetchTrace.MarkAlive (hardcodes "[CFS-P2]",
    // reserved for Patches/VillagerFetchPatches.cs's READ-ONLY spike) and NOT FetchQuestSuppression.
    // MarkAlive (hardcodes "[CFS-FQ]", reserved for the v0.8.0 retired lever).
    private static readonly HashSet<string> _aliveLogged = new();

    internal static void MarkAlive(string target)
    {
        try
        {
            if (_aliveLogged.Add(target))
                Plugin.Logger.LogInfo($"[CFS] [CFS-SS] PATCH ALIVE {target}");
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"[CFS] [CFS-SS] StationStocker.MarkAlive error: {ex}");
        }
    }

    // Called from Patches/StationStockPatches.cs FetchCraftingSuppliesStockPatch.Postfix. Wrapped in
    // try/catch by the caller as well as here (belt-and-suspenders) - a throw in villager-AI-adjacent
    // code must never break the FSM state transition that called it.
    internal static void HandleFetchStateEnter(FSM_FetchCraftingSupplies instance, IFSMBehaviourController fsmBehaviour)
    {
        try
        {
            MarkAlive("FSM_FetchCraftingSupplies.OnStateEnter (stocker)");

            // Gate: both master switches must be on, and this client must hold write authority over
            // settlement-shared state (same host gate CraftTransfer/FetchQuestSuppression already use).
            if (!SafeGetBool(Plugin.EnableForVillagers, true)) return;
            if (!SafeGetBool(Plugin.StockStationOnFetch, true)) return;
            if (!CraftTransfer.IsHostOrSolo()) return;

            // Step 3: GetQuestData is inherited from FSM_QuestAction (Patches/VillagerFetchPatches.cs
            // line 165 already calls it this exact way).
            QuestData? qd = null;
            try { qd = instance.GetQuestData(fsmBehaviour); } catch { }
            if (qd == null) return;

            // Step 4: identify the REAL native class before rewrapping - managed as/is casts lie for
            // interop objects materialized under a base declared type (project-wide gotcha). Mirrors
            // LogQuestDataEnrichment in Patches/VillagerFetchPatches.cs.
            string nativeClass = Plugin.NativeClassName(qd);
            if (nativeClass != "CrafterFetchQuestData") return;

            IntPtr ptr = VillagerFetchTrace.SafePointer(qd);
            if (ptr == IntPtr.Zero) return;

            CrafterFetchQuest.CrafterFetchQuestData cfqd;
            try { cfqd = new CrafterFetchQuest.CrafterFetchQuestData(ptr); }
            catch (Exception ex) { Plugin.Logger.LogError($"[CFS] [CFS-SS] CrafterFetchQuestData rewrap error: {ex}"); return; }

            // Step 5.
            CrafterFetchQuest? quest = null;
            try { quest = cfqd.Quest; } catch (Exception ex) { Plugin.Logger.LogError($"[CFS] [CFS-SS] get_Quest error: {ex}"); }
            if (quest == null) return;

            // Step 6.
            CraftingStation? station = null;
            try { station = quest.craftingStation; } catch (Exception ex) { Plugin.Logger.LogError($"[CFS] [CFS-SS] get_craftingStation error: {ex}"); }
            if (station == null) return;

            // Step 7: CALL GetNeededSuppliesManifest(), never patch it - ItemManifest return type is
            // the inventory-family patch-crash risk this project avoids (FetchQuestSuppression.cs line
            // 161 already calls it the same way).
            ItemManifest? manifest = null;
            try { manifest = quest.GetNeededSuppliesManifest(); }
            catch (Exception ex) { Plugin.Logger.LogError($"[CFS] [CFS-SS] GetNeededSuppliesManifest error: {ex}"); return; }

            // Step 8.
            var pairs = CraftTransfer.EnumerateManifest(manifest);
            if (pairs.Count == 0) return;

            // Step 9.
            ItemCollection? stationInv = null;
            try { stationInv = station.GetInventory(); } catch (Exception ex) { Plugin.Logger.LogError($"[CFS] [CFS-SS] GetInventory error: {ex}"); }
            if (stationInv == null) return;

            // Step 10: shortfall against the STATION inventory only (not the villager's own inventory -
            // she hasn't picked anything up yet, this fires the moment the fetch quest STARTS).
            var shortfall = new List<(ItemInfo info, int missing)>();
            foreach (var (info, qty) in pairs)
            {
                int have = 0;
                try { have = stationInv.GetItemQuantity(info); } catch { }
                int missing = qty - have;
                if (missing > 0) shortfall.Add((info, missing));
            }
            if (shortfall.Count == 0) return;

            string villagerName = VillagerFetchTrace.SafeVillagerName(fsmBehaviour);
            string stationName = ResolveStationName(station);
            string stationObjName = ResolveStationObjectName(station); // v0.9.1

            // Step 11.
            var (itemsMoved, qtyMoved, stillShort) = CraftTransfer.StockStation(shortfall, stationInv, villagerName, stationName);

            // Step 12: one unconditional summary line - this is the run's success metric, not behind
            // TransferDiagnostics.
            Plugin.Logger.LogInfo($"[CFS] [CFS-SS] STOCKED villager={villagerName} station={stationName} " +
                $"stationObj={stationObjName} wanted={pairs.Count} short={shortfall.Count} itemsMoved={itemsMoved} " +
                $"qtyMoved={qtyMoved} stillShort={stillShort}");
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"[CFS] [CFS-SS] StationStocker.HandleFetchStateEnter error: {ex}");
        }
    }

    // Same shape FetchQuestSuppression.ResolveStationName already uses.
    private static string ResolveStationName(CraftingStation station)
    {
        try
        {
            string? n = station.GetName();
            if (!string.IsNullOrEmpty(n)) return n!;
        }
        catch { }
        return Plugin.NativeClassName(station);
    }

    // v0.9.1: the station's own GameObject name, independent of ResolveStationName's building-level
    // display name (station.GetName(), which returns the containing building's name - e.g. "Workshop
    // House 4" - for every station inside it). Added so a specific workstation (a carpenter's table,
    // say) can be told apart from the building it sits in. CraftingStation derives from MonoBehaviour
    // through its base chain, so .gameObject is available directly; other code in this project reads
    // GameObject.name off a component the same way (Patches/VillagerFetchPatches.cs,
    // IsWhitelistedByStoragePatch's site-name block).
    private static string ResolveStationObjectName(CraftingStation station)
    {
        try { return station.gameObject.name; }
        catch { return "?"; }
    }

    // Same defensive config-read helper shape FetchQuestSuppression.SafeGetBool uses.
    private static bool SafeGetBool(ConfigEntry<bool>? entry, bool fallback)
    {
        try { return entry?.Value ?? fallback; } catch { return fallback; }
    }
}
