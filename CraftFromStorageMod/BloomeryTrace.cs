using System;
using System.Collections.Generic;
using System.Linq;
using SandSailorStudio.Inventory;
using SSSGame;
using SSSGame.AI;
using SSSGame.AI.FSM;
using UnityEngine;

namespace CraftFromStorageMod;

// v0.15.0 (idea-17 next feature groundwork): READ-ONLY diagnostic spike instrumenting the bloomery
// (and kiln) supply pipeline, ahead of extending Transfer/StockTransformStationMaterials (toggle 2)
// to deliver ore/coal into a bloomery. This answers, from ONE play session's log, three questions:
//   (a) what quest-data class a bloomery/kiln fetch carries (FSM_FetchBloomerySupplies/
//       FSM_FetchKilnSupplies.OnStateEnter's GetQuestData(fsmBehaviour) result - same inherited call
//       StationStocker.HandleFetchStateEnter already uses, from FSM_QuestAction);
//   (b) which manifest describes the actual shortfall (Bloomstation.GetKilnRecipeManifest() and its
//       _kilnContainerManifest property, both ItemManifest - CALLED, never patched, per the project's
//       "inventory-family parameters/returns can crash at patch setup" gotcha);
//   (c) what the ore/coal/bloom slot containers are (StorageInteraction -> ItemContainerComponent ->
//       ItemContainer: type name, capacity, accept state).
// All log lines carry "[CFS] [CFS-BLM]". Every interop read is individually try/catch'd - a throw
// here must never break the FSM state transition that called it (same discipline as StationStocker.
// HandleFetchStateEnter and VillagerFetchTrace).
//
// Never patches anything on Bloomstation itself (a Fusion NetworkBehaviour - CopyBackingFieldsToState/
// CopyStateToBackingFields patch-hangs-at-load family, project-wide gotcha) - only READS from it, via
// UnityEngine.Object.FindAnyObjectByType<Bloomstation>() (the SINGULAR form; the plural
// FindObjectsByType<T>() throws MissingMethodException through the trampoline in this game). Finding
// only one bloomery when several exist is an accepted spike limitation, not a bug.
//
// Domain facts (user-supplied 2026-07-31, not yet in-game confirmed by this spike): ore storage is
// the high-value delivery target for a future feature, coal storage second; bloom storage is the
// OUTPUT slot (other stations pull finished blooms from it) and must never become a delivery
// destination; part substitution is likely Iron Ore <-> Metal Scraps, sharing the ore storage.
internal static class BloomeryTrace
{
    // Fire-verification (MarkAlive pattern) - own HashSet, deliberately NOT shared with
    // StationStocker's (this spike tags its own "bloomery"/"kiln" site strings, distinct target set).
    private static readonly HashSet<string> _aliveLogged = new();

    // Rate limiters: string-keyed only (never an interop wrapper - project-wide gotcha), so both are
    // safe to hold across frames and just need clearing on world-leave like every other tracker here.
    private static readonly Dictionary<string, int> _dumpsLoggedPerSite = new();
    private static readonly Dictionary<string, int> _exitsLoggedPerSite = new();

    // v0.16.0 (bloomery delivery feature, toggle 2): per-station cooldown for TryStockBloomery below -
    // string-keyed only (station name, never an interop wrapper - project-wide gotcha), so safe to
    // hold across frames. Stamped only when a real per-item shortfall probe actually runs (see
    // TryStockBloomery's own comment for why), so a fully-supplied bloomery is re-probed at most once
    // per 5s rather than every OnStateEnter.
    private static readonly Dictionary<string, float> _stockCooldown = new();

    // v0.16.0: fire-once-per-site-per-world-session marker for the "stocking OFF" log line, so a
    // villager cycling through FSM_FetchKilnSupplies.OnStateEnter dozens of times (132 enters observed
    // in one spike session) doesn't spam the log while toggle 2 is off (its default).
    private static readonly HashSet<string> _stockOffLogged = new();

    // World-leave: wired into CraftWatcher.ClearWorldState alongside every other tracker in this mod.
    // _aliveLogged is process-lifetime by convention elsewhere in this mod (VillagerFetchTrace) - but
    // this spike's per-world rate limiters are dropped so a stale count from a previous session can
    // never suppress a dump/exit line in the next one.
    internal static void ClearWorldState()
    {
        try
        {
            bool hadState = _dumpsLoggedPerSite.Count > 0 || _exitsLoggedPerSite.Count > 0 ||
                _stockCooldown.Count > 0 || _stockOffLogged.Count > 0;
            _dumpsLoggedPerSite.Clear();
            _exitsLoggedPerSite.Clear();
            _stockCooldown.Clear();
            _stockOffLogged.Clear();
            if (hadState)
                Plugin.Logger.LogInfo("[CFS] [CFS-BLM] BloomeryTrace: world-leave - rate-limit counters cleared.");
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"[CFS] [CFS-BLM] BloomeryTrace.ClearWorldState error: {ex}");
        }
    }

    private static void MarkAlive(string target)
    {
        try
        {
            if (_aliveLogged.Add(target))
                Plugin.Logger.LogInfo($"[CFS] [CFS-BLM] PATCH ALIVE {target}");
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"[CFS] [CFS-BLM] BloomeryTrace.MarkAlive error: {ex}");
        }
    }

    // Called from Patches/BloomeryFetchPatches.cs, both OnStateEnter postfixes (site is "bloomery" or
    // "kiln"). Never writes game state - pure observation.
    internal static void HandleFetchEnter(FSM_QuestAction instance, IFSMBehaviourController fsmBehaviour, string site)
    {
        try
        {
            MarkAlive($"{site} OnStateEnter");

            string villagerName = VillagerFetchTrace.SafeVillagerName(fsmBehaviour);

            // Step 3 idiom (matches StationStocker.HandleFetchStateEnter / VillagerFetchPatches.cs
            // LogQuestDataEnrichment): GetQuestData is inherited from FSM_QuestAction.
            QuestData? qd = null;
            try { qd = instance.GetQuestData(fsmBehaviour); } catch { }

            bool traceOn = VillagerFetchTrace.SafeGetBool(Plugin.EnableBloomeryTrace, false);

            if (qd == null)
            {
                if (traceOn)
                    Plugin.Logger.LogInfo($"[CFS] [CFS-BLM] fetchEnter site={site} villager={villagerName} qd=null");
            }
            else
            {
                // The quest-data class name is itself the answer question (a) needs - this spike
                // deliberately does NOT guess a pointer rewrap for a class it can't name in advance
                // (managed as/is casts lie for interop objects materialized under a base declared
                // type - project-wide gotcha - so an unverified rewrap risks a native crash no
                // try/catch can stop, not just a bad read).
                if (traceOn)
                {
                    string qdClass = Plugin.NativeClassName(qd);
                    Plugin.Logger.LogInfo($"[CFS] [CFS-BLM] fetchEnter site={site} villager={villagerName} qdClass={qdClass}");
                }
            }

            // Resolve the Bloomstation a guess-free way: FindAnyObjectByType<T>() (singular - the
            // plural FindObjectsByType<T>() throws MissingMethodException through the trampoline in
            // this game, project-wide gotcha). NOTE: this only ever finds ONE bloomery even if the
            // settlement has several - accepted spike limitation, not fixed here.
            Bloomstation? b = null;
            try { b = UnityEngine.Object.FindAnyObjectByType<Bloomstation>(); } catch { }
            if (b == null) return;

            _dumpsLoggedPerSite.TryGetValue(site, out int dumps);
            if (dumps >= 3) return;
            _dumpsLoggedPerSite[site] = dumps + 1;

            DumpBloomstation(b, site);
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"[CFS] [CFS-BLM] HandleFetchEnter error (site={site}): {ex}");
        }
    }

    // Called from Patches/BloomeryFetchPatches.cs, both OnStateExit postfixes.
    internal static void HandleFetchExit(FSM_QuestAction instance, IFSMBehaviourController fsmBehaviour, string site)
    {
        try
        {
            MarkAlive($"{site} OnStateExit");

            _exitsLoggedPerSite.TryGetValue(site, out int exits);
            if (exits >= 10) return;
            _exitsLoggedPerSite[site] = exits + 1;

            if (VillagerFetchTrace.SafeGetBool(Plugin.EnableBloomeryTrace, false))
            {
                string villagerName = VillagerFetchTrace.SafeVillagerName(fsmBehaviour);
                Plugin.Logger.LogInfo($"[CFS] [CFS-BLM] fetchExit site={site} villager={villagerName}");
            }
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"[CFS] [CFS-BLM] HandleFetchExit error (site={site}): {ex}");
        }
    }

    // v0.16.0 (bloomery delivery feature, toggle 2): the inversion of the read-only spike above -
    // teleports the kiln recipe's missing inputs (ore/coal, per the spike's own observed manifest)
    // into the bloomery/kiln so the villager's supply walk becomes unnecessary, the same approach
    // StationStocker.HandleFetchStateEnter already uses for ordinary crafting stations. Called from
    // BOTH OnStateEnter postfixes in Patches/BloomeryFetchPatches.cs, AFTER HandleFetchEnter above.
    // Gated behind Plugin.StockTransformStationMaterials (toggle 2, default false) - the delegation
    // spec's own choice, since the bloomery/kiln are the same kind of physical-transform-family
    // delivery the workshop stocker already gates on this toggle (TRANSFORM_STATION_PLAN.md).
    //
    // Reuses CraftTransfer.StockStation unchanged (no ledger, candidate selection, source blacklist,
    // capacity pre-check and logging already built there) - this method's own job is purely: resolve
    // the CORRECT bloomery for this quest (guess-free pointer-rewrap chain, never a
    // FindAnyObjectByType search - several bloomeries can exist in one settlement), compute the
    // shortfall (including part-substitute coverage), and hand it to StockStation.
    //
    // Every interop read is its own try/catch - a throw here must never break the FSM state
    // transition that called it (same discipline as the rest of this file).
    internal static void TryStockBloomery(FSM_QuestAction instance, IFSMBehaviourController fsmBehaviour, string site)
    {
        try
        {
            // ---- Gates, cheapest first ----
            if (!VillagerFetchTrace.SafeGetBool(Plugin.EnableForVillagers, true)) return;
            if (!VillagerFetchTrace.SafeGetBool(Plugin.StockStationOnFetch, true)) return;
            if (!VillagerFetchTrace.SafeGetBool(Plugin.StockTransformStationMaterials, false))
            {
                if (_stockOffLogged.Add(site))
                    Plugin.Logger.LogInfo("[CFS] [CFS-BLM] bloomery stocking OFF (StockTransformStationMaterials=false) - standing aside.");
                return;
            }
            if (!CraftTransfer.IsHostOrSolo()) return;

            string villagerName = VillagerFetchTrace.SafeVillagerName(fsmBehaviour);

            // ---- Resolve the CORRECT bloomery via the quest-data pointer chain (never a
            // FindAnyObjectByType search - this resolves the right one even with several in the
            // settlement). Managed as/is casts lie for interop objects materialized under a base
            // declared type (project-wide gotcha) - the native class name is checked before any
            // rewrap, exactly like HandleFetchEnter above. ----
            QuestData? qd = null;
            try { qd = instance.GetQuestData(fsmBehaviour); } catch { }
            if (qd == null) return;

            string qdClass = Plugin.NativeClassName(qd);
            if (qdClass != "BloomstationSupplyQuestData" && qdClass != "KilnkeeperQuestData") return;

            IntPtr ptr = VillagerFetchTrace.SafePointer(qd);
            if (ptr == IntPtr.Zero) return;

            BloomstationQuest.BloomstationQuestData bqd;
            try { bqd = new BloomstationQuest.BloomstationQuestData(ptr); }
            catch (Exception ex) { Plugin.Logger.LogError($"[CFS] [CFS-BLM] BloomstationQuestData rewrap error: {ex}"); return; }

            BloomstationQuest? quest = null;
            try { quest = bqd.Quest; } catch (Exception ex) { Plugin.Logger.LogError($"[CFS] [CFS-BLM] get_Quest error: {ex}"); }
            if (quest == null) return;

            Bloomstation? station = null;
            try { station = quest.bloomstation; } catch (Exception ex) { Plugin.Logger.LogError($"[CFS] [CFS-BLM] get_bloomstation error: {ex}"); }
            if (station == null) return;

            string stationName;
            try { stationName = station.GetName() ?? Plugin.NativeClassName(station); }
            catch { stationName = Plugin.NativeClassName(station); }

            // ---- Per-station cooldown: read-check here (before the shortfall probe below), stamped
            // only once the actual per-item shortfall probe runs further down - so a fully-supplied
            // bloomery (no shortfall this pass) still gets re-probed at most once per 5s, not every
            // OnStateEnter. ----
            float nowCheck = UnityEngine.Time.realtimeSinceStartup;
            if (_stockCooldown.TryGetValue(stationName, out float lastStock) && nowCheck - lastStock < 5.0f) return;

            // ---- Wanted: the kiln recipe manifest. CALLED, never patched (ItemManifest return =
            // patch-crash family). ----
            ItemManifest? recipeManifest = null;
            try { recipeManifest = station.GetKilnRecipeManifest(); }
            catch (Exception ex) { Plugin.Logger.LogError($"[CFS] [CFS-BLM] GetKilnRecipeManifest error: {ex}"); return; }

            var wanted = CraftTransfer.EnumerateManifest(recipeManifest);
            if (wanted.Count == 0) return;

            // ---- Destination: the bloomery's own inventory, same delivery pattern
            // CraftingStation.GetInventory() uses for the workshop stocker. ----
            ItemCollection? destColl = null;
            try { destColl = station.GetInventory(); }
            catch (Exception ex) { Plugin.Logger.LogError($"[CFS] [CFS-BLM] GetInventory error: {ex}"); return; }
            if (destColl == null) return;

            // ---- Shortfall per wanted item, including substitute coverage. Cooldown is stamped here
            // (not earlier) - this is the actual probe the 5s throttle is protecting. ----
            _stockCooldown[stationName] = UnityEngine.Time.realtimeSinceStartup;

            var shortfall = new List<(ItemInfo info, int missing)>();
            foreach (var (info, qty) in wanted)
            {
                int have = 0;
                try { have = destColl.GetItemQuantity(info); } catch { }

                try
                {
                    if (station.TryGetPartSubstitute(info, out ItemInfo sub) && sub != null)
                        have += destColl.GetItemQuantity(sub);
                }
                catch (Exception ex)
                {
                    Plugin.Logger.LogError($"[CFS] [CFS-BLM] TryGetPartSubstitute error for '{SafeItemName(info)}': {ex}");
                }

                int missing = qty - have;
                if (missing > 0) shortfall.Add((info, missing));
            }

            if (shortfall.Count == 0) return;

            // ---- First pass: the existing no-ledger mover, unchanged. ----
            var (itemsMoved, qtyMoved, stillShort) = CraftTransfer.StockStation(shortfall, destColl, villagerName, stationName);

            // ---- Substitute second pass: only if the first pass left something short. StockStation's
            // tuple carries only an AGGREGATE stillShort, not a per-item breakdown, so a conservative
            // single-entry retry (the first substitutable ORIGINAL entry only, capped at the first
            // pass's aggregate stillShort) avoids over-delivering a substitute for an item that was
            // not actually the one still short. ----
            int subItemsMoved = 0, subQtyMoved = 0;
            if (stillShort > 0)
            {
                (ItemInfo info, int missing)? subEntry = null;
                foreach (var (info, missing) in shortfall)
                {
                    try
                    {
                        if (station.TryGetPartSubstitute(info, out ItemInfo sub) && sub != null)
                        {
                            subEntry = (sub, stillShort);
                            break;
                        }
                    }
                    catch (Exception ex)
                    {
                        Plugin.Logger.LogError($"[CFS] [CFS-BLM] substitute second-pass probe error for '{SafeItemName(info)}': {ex}");
                    }
                }

                if (subEntry != null)
                {
                    var subShortfall = new List<(ItemInfo info, int missing)> { subEntry.Value };
                    var (si, sq, ss) = CraftTransfer.StockStation(subShortfall, destColl, villagerName, stationName);
                    subItemsMoved = si;
                    subQtyMoved = sq;
                    // The second pass's stillShort is the final remaining shortfall (what the
                    // substitute delivery could not cover either) - supersedes the first pass's number
                    // for reporting purposes, since the first pass's own stillShort is exactly what the
                    // second pass just attempted to close.
                    stillShort = ss;
                }
            }

            // ---- One unconditional summary line per stocking attempt. ----
            Plugin.Logger.LogInfo($"[CFS] [CFS-BLM] BLOOMERY STOCKED villager={villagerName} station={stationName} " +
                $"site={site} wanted={wanted.Count} short={shortfall.Count} itemsMoved={itemsMoved} qtyMoved={qtyMoved} " +
                $"stillShort={stillShort} subItemsMoved={subItemsMoved} subQtyMoved={subQtyMoved}");

            // ---- Post-stock dump: shows WHICH slot the delivered items landed in. Own rate-limit key
            // ("<site>-postStock") in the SAME _dumpsLoggedPerSite dictionary HandleFetchEnter uses for
            // its own "<site>" key - distinct keys, so the two quotas never interfere with each other;
            // this one is capped at 6 instead of HandleFetchEnter's 3, per the delegation spec. ----
            if (VillagerFetchTrace.SafeGetBool(Plugin.TransferDiagnostics, true))
            {
                string dumpKey = site + "-postStock";
                _dumpsLoggedPerSite.TryGetValue(dumpKey, out int dumps);
                if (dumps < 6)
                {
                    _dumpsLoggedPerSite[dumpKey] = dumps + 1;
                    DumpBloomstation(station, dumpKey);
                }
            }
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"[CFS] [CFS-BLM] TryStockBloomery error: {ex}");
        }
    }

    // Every read individually try/catch'd - "?" on failure, never lets one bad read abort the rest of
    // the dump. v1.0.0: every trace LogInfo below is gated on EnableBloomeryTrace (default false at
    // ship) - this dump is diagnostic output only, never part of TryStockBloomery's actual delivery.
    private static void DumpBloomstation(Bloomstation b, string site)
    {
        bool traceOn = VillagerFetchTrace.SafeGetBool(Plugin.EnableBloomeryTrace, false);

        string stationName = "?";
        try { stationName = b.GetName() ?? "?"; } catch { }

        bool kilnSupplied = false;
        try { kilnSupplied = b.KilnIsSupplied; } catch { }

        if (traceOn)
            Plugin.Logger.LogInfo($"[CFS] [CFS-BLM] station name={stationName} kilnSupplied={kilnSupplied}");

        DumpSlot(b, "ore", SafeGetOreStorage(b));
        DumpSlot(b, "coal", SafeGetCoalStorage(b));
        DumpSlot(b, "bloom", SafeGetBloomStorage(b));

        // Kiln recipe manifest: CALLED, never patched - ItemManifest returns are the patch-setup
        // native-crash family (project-wide gotcha).
        ItemManifest? recipeManifest = null;
        try { recipeManifest = b.GetKilnRecipeManifest(); } catch (Exception ex) { Plugin.Logger.LogError($"[CFS] [CFS-BLM] GetKilnRecipeManifest error: {ex}"); }
        LogManifest("kilnRecipeManifest", recipeManifest);

        ItemManifest? containerManifest = null;
        try { containerManifest = b._kilnContainerManifest; } catch (Exception ex) { Plugin.Logger.LogError($"[CFS] [CFS-BLM] get__kilnContainerManifest error: {ex}"); }
        LogManifest("kilnContainerManifest", containerManifest);

        // Substitution probe: first item in the kiln recipe manifest only.
        try
        {
            var entries = CraftTransfer.EnumerateManifest(recipeManifest);
            if (entries.Count > 0)
            {
                ItemInfo first = entries[0].info;
                string firstName = SafeItemName(first);
                try
                {
                    bool result = b.TryGetPartSubstitute(first, out ItemInfo sub);
                    if (traceOn)
                    {
                        string subName = result ? SafeItemName(sub) : "none";
                        Plugin.Logger.LogInfo($"[CFS] [CFS-BLM] substitute for '{firstName}' -> '{subName}' (result={result})");
                    }
                }
                catch (Exception ex)
                {
                    Plugin.Logger.LogError($"[CFS] [CFS-BLM] TryGetPartSubstitute error for '{firstName}': {ex}");
                }
            }
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"[CFS] [CFS-BLM] substitution probe error: {ex}");
        }
    }

    private static StorageInteraction? SafeGetOreStorage(Bloomstation b)
    {
        try { return b.oreStorage; } catch { return null; }
    }

    private static StorageInteraction? SafeGetCoalStorage(Bloomstation b)
    {
        try { return b.coalStorage; } catch { return null; }
    }

    private static StorageInteraction? SafeGetBloomStorage(Bloomstation b)
    {
        try { return b.bloomStorage; } catch { return null; }
    }

    // Resolves the slot's container the way SettlementStock.Rebuild/StorageCensus already do -
    // GetComponent<ItemContainerComponent>() on the interaction's own GameObject (a direct
    // StorageInteraction.container-typed property is not confirmed at compile time), then reads
    // containerType.name / capacity / a bounded indexer walk for total+distinct+first item - same
    // shape SettlementStock.Rebuild uses.
    private static void DumpSlot(Bloomstation b, string slotName, StorageInteraction? interaction)
    {
        bool traceOn = VillagerFetchTrace.SafeGetBool(Plugin.EnableBloomeryTrace, false);

        if (interaction == null)
        {
            if (traceOn)
                Plugin.Logger.LogInfo($"[CFS] [CFS-BLM] slot={slotName} containerType=? capacity=? items=? distinct=? first='?'");
            return;
        }

        ItemContainerComponent? icc = null;
        try
        {
            GameObject? go = interaction.gameObject;
            if (go != null) icc = go.GetComponent<ItemContainerComponent>();
        }
        catch { }

        ItemContainer? container = null;
        try { if (icc != null) container = icc.container; } catch { }

        if (container == null)
        {
            if (traceOn)
                Plugin.Logger.LogInfo($"[CFS] [CFS-BLM] slot={slotName} containerType=? capacity=? items=? distinct=? first='?'");
            return;
        }

        string typeName = "?";
        try { typeName = container.containerType?.name ?? "?"; } catch { }

        int capacity = 0;
        try { capacity = container.capacity; } catch { }

        int total = 0;
        int distinct = 0;
        string first = "?";
        try
        {
            int bound = capacity > 0 ? capacity : 64;
            var items = container.GetItems();
            var perItem = new Dictionary<int, int>();
            var infoById = new Dictionary<int, ItemInfo>();
            for (int slot = 0; slot < bound; slot++)
            {
                Item? it = null;
                try { it = items != null ? items[slot] : null; } catch { break; }
                if (it == null) continue;
                ItemInfo? info = null; int cnt = 0;
                try { info = it.info; cnt = it.count; } catch { }
                if (info == null || cnt <= 0) continue;
                int id;
                try { id = info.id; } catch { continue; }
                perItem.TryGetValue(id, out int running);
                perItem[id] = running + cnt;
                infoById[id] = info;
            }

            distinct = perItem.Count;
            foreach (var kv in perItem) total += kv.Value;

            if (perItem.Count > 0)
            {
                var firstKv = perItem.First();
                string firstName = infoById.TryGetValue(firstKv.Key, out var fi) ? SafeItemName(fi) : "?";
                first = $"{firstName}x{firstKv.Value}";
            }
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"[CFS] [CFS-BLM] DumpSlot item walk error (slot={slotName}): {ex}");
        }

        if (traceOn)
            Plugin.Logger.LogInfo($"[CFS] [CFS-BLM] slot={slotName} containerType={typeName} capacity={capacity} " +
                $"items={total} distinct={distinct} first='{first}'");
    }

    private static void LogManifest(string label, ItemManifest? manifest)
    {
        bool traceOn = VillagerFetchTrace.SafeGetBool(Plugin.EnableBloomeryTrace, false);
        try
        {
            if (manifest == null)
            {
                if (traceOn)
                    Plugin.Logger.LogInfo($"[CFS] [CFS-BLM] {label} n=0: (null)");
                return;
            }

            var entries = CraftTransfer.EnumerateManifest(manifest);
            if (traceOn)
            {
                string listing = string.Join(" + ", entries.Take(8).Select(e => $"'{SafeItemName(e.info)}'x{e.qty}"));
                Plugin.Logger.LogInfo($"[CFS] [CFS-BLM] {label} n={entries.Count}: {listing}");
            }
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"[CFS] [CFS-BLM] LogManifest error ({label}): {ex}");
        }
    }

    private static string SafeItemName(ItemInfo? info)
    {
        if (info == null) return "?";
        try { return info.Name ?? "?"; } catch { return "?"; }
    }
}
