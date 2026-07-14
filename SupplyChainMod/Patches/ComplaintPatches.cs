using System;
using System.Collections.Generic;
using System.Text;
using HarmonyLib;
using SSSGame;
using SSSGame.AI;
using SandSailorStudio.Inventory;

namespace SupplyChainMod.Patches;

// ComplaintWatcher (Phase 0, read-only) — postfixes SSSGame.VillagerSocial.AddComplaint/
// RemoveComplaint to measure complaint add/remove cadence and payload shape. Gated behind
// Plugin.PatchVillagerComplaints (checked in Plugin.Load BEFORE these are patched) as a safety
// valve: neither Complaint (AddComplaint's param) nor Int32 (RemoveComplaint's param) are
// inventory-family types (Item/ItemCollection/ItemEventContext — the confirmed fatal-crash family
// from VillagerAmmoMod's dead-end), so this SHOULD be safe, but it's new ground for this repo.
//
// AddComplaint is NOT marked [virtual] in the interop metadata — AOT-inlining risk (project-wide
// gotcha: the IL2CPP AOT compiler inlines small/single-caller methods, silently killing the
// patch). "[SupplyChain] ComplaintWatcher armed" logs once at patch setup (Plugin.Load).
//
// v0.1.1 — first in-game run (2,938 fires, no exceptions) showed 60% of captures were
// GenericMessageComplaint with no discriminator, and the console scrolled heavily with
// "feels unsafe" complaints from a defenseless test save. Added: GenericMessageComplaint.message
// discrimination + a config-driven + statics-driven ignore filter (suppresses ADD *and* the
// matching REMOVE, via a small id->suppressed map), FailedObjectiveComplaint detail, and REMOVE
// dedupe aggregation (REMOVE id=0 was firing in microsecond bursts).
//
// v0.1.2 — run 2 (v0.1.1) confirmed the filter/dedupe work but the console STILL scrolled: the
// game re-ADDs the same complaint every ~50-100ms for as long as its condition holds (3,580
// [complaint] lines in minutes, dominated by villager_jobless/villager_homeless re-adds + REMOVE
// id=0 churn). Complaints are a LEVEL signal, not an edge — replaced per-event ADD/REMOVE logging
// with TRANSITION-based logging: an active-complaints map (keyed "villager|id") now tracks
// first-seen time + re-add count per held complaint; only the first ADD and the eventual CLEAR log
// a line, everything in between is silently counted. The REMOVE-dedupe machinery from v0.1.1 is
// superseded by this (an unmatched REMOVE — the id=0 churn — is now just a silent counter, never a
// per-line log, so there's nothing left to dedupe-and-flush).
internal static class ComplaintLog
{
    // ── Ignore filter (v0.1.1) ──────────────────────────────────────────────────────────────────
    // Built lazily on first use (not in Plugin.Load) so the config value is definitely bound and
    // the two Complaint statics are definitely resolvable. Process-global — NOT cleared on
    // world-leave (config text and the two static string constants don't change per world).
    private static bool _ignoreSetBuilt;
    private static readonly List<string> _ignoreSubstrings = new();
    private static readonly HashSet<string> _ignoreExactValues = new();

    // v0.3.0 — militia-alarm lockout signal for SupplyController. Complaint.c_militia_alarm is a
    // STRING message-key static (Cecil-confirmed — there is NO bool alarm member), cached the same
    // lazy way as the ignore-filter statics above.
    private static string? _militiaAlarmKey;

    // Per-world: which complaint ids were suppressed at ADD, so the matching REMOVE(id) can also be
    // suppressed. Cleared on world-leave (ClearState, called from SupplyChainTracker.NoteWorldLeft)
    // — ids are per-world-session, never globally unique (project-wide gotcha).
    private static readonly HashSet<int> _suppressedIds = new();
    private static int _suppressedSinceReport;

    // ── Transition tracking (v0.1.2) ────────────────────────────────────────────────────────────
    // Keyed "villagerName|id" — the exact identity RemoveComplaint(id) can look up (it only gets
    // the id, via the same VillagerSocial instance). Holds the DISCRIMINATOR (native type name, or
    // the GenericMessageComplaint.message / FailedObjectiveComplaint.descriptionKey when those
    // apply) so a same-id-different-content re-ADD (rare, but the id is a per-villager slot, not a
    // global identity) is still treated as a fresh complaint rather than folded into the old hold.
    private sealed class ActiveEntry
    {
        public string Discriminator = "?";
        public DateTime FirstSeen;
        public int ReAdds;
        // v0.3.0 — cached demand-item extraction (ItemComplaint/ItemManifestComplaint only) and the
        // complaint's important flag, so re-adds can feed SupplyController.NoteDemand without any
        // TryCast work (re-adds fire at ~20/s per held complaint — this must stay cheap).
        public List<string>? DemandItems;
        public bool Important;
    }

    private static readonly Dictionary<string, ActiveEntry> _active = new();
    private static int _statsNewAdds;
    private static int _statsReAdds;
    private static int _statsClears;
    private static int _statsUnmatchedRemoves;

    private static void BuildIgnoreSetIfNeeded()
    {
        if (_ignoreSetBuilt) return;
        _ignoreSetBuilt = true;

        try
        {
            string raw = Plugin.IgnoredComplaintMessages.Value ?? "";
            foreach (var part in raw.Split(','))
            {
                var t = part.Trim();
                if (t.Length > 0) _ignoreSubstrings.Add(t.ToLowerInvariant());
            }
        }
        catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] BuildIgnoreSetIfNeeded config parse error: {ex}"); }

        try { var v = Complaint.c_combat_feelUnsafeToWork; if (!string.IsNullOrEmpty(v)) _ignoreExactValues.Add(v); }
        catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] Complaint.c_combat_feelUnsafeToWork read error: {ex}"); }

        try { var v = Complaint.c_combat_feelUnsafeAtHome; if (!string.IsNullOrEmpty(v)) _ignoreExactValues.Add(v); }
        catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] Complaint.c_combat_feelUnsafeAtHome read error: {ex}"); }

        try { _militiaAlarmKey = Complaint.c_militia_alarm; }
        catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] Complaint.c_militia_alarm read error: {ex}"); }

        Plugin.Logger.LogInfo($"[SupplyChain] [complaint] ignore filter built: {_ignoreSubstrings.Count} substring(s), {_ignoreExactValues.Count} exact static value(s).");
    }

    private static bool IsIgnoredMessage(string? message)
    {
        if (string.IsNullOrEmpty(message)) return false;
        if (_ignoreExactValues.Contains(message)) return true;
        string lower = message!.ToLowerInvariant();
        foreach (var sub in _ignoreSubstrings)
            if (lower.Contains(sub)) return true;
        return false;
    }

    // Called from SupplyChainTracker on world-leave — ids are per-world-session (never globally
    // unique), so a stale suppressed-id set or active-complaints map from a prior world must not
    // bleed into the next one.
    internal static void ClearState()
    {
        _suppressedIds.Clear();
        _suppressedSinceReport = 0;
        _active.Clear();
        _statsNewAdds = 0;
        _statsReAdds = 0;
        _statsClears = 0;
        _statsUnmatchedRemoves = 0;
    }

    // Called from SupplyChainTracker once per WidgetPollSeconds interval — a single stats line
    // (active count + new-adds/re-adds/clears/unmatched-removes/suppressed since the last report),
    // only emitted when at least one counter is nonzero.
    internal static void PeriodicFlush()
    {
        if (_statsNewAdds == 0 && _statsReAdds == 0 && _statsClears == 0 && _statsUnmatchedRemoves == 0 && _suppressedSinceReport == 0)
            return;

        Plugin.Logger.LogInfo(
            $"[SupplyChain] [complaint] stats: active={_active.Count} newAdds={_statsNewAdds} reAdds={_statsReAdds} " +
            $"clears={_statsClears} unmatchedRemoves={_statsUnmatchedRemoves} suppressed={_suppressedSinceReport} (since last report)");

        _statsNewAdds = 0;
        _statsReAdds = 0;
        _statsClears = 0;
        _statsUnmatchedRemoves = 0;
        _suppressedSinceReport = 0;
    }

    private static string ResolveVillagerName(VillagerSocial? social)
    {
        try
        {
            var villager = social?._villager;
            if (villager != null) return villager.GetName() ?? "?";
        }
        catch { }
        return "?";
    }

    // Discriminator per the coordinator's spec: native type name; for GenericMessageComplaint the
    // message key; for FailedObjectiveComplaint the descriptionKey. `gm`/`gmMessage` are passed in
    // so LogAdd doesn't TryCast<GenericMessageComplaint> twice (once for the ignore filter, once
    // here).
    private static string ComputeDiscriminator(Complaint complaint, string nativeType, GenericMessageComplaint? gm, string? gmMessage)
    {
        if (gm != null) return !string.IsNullOrEmpty(gmMessage) ? gmMessage! : nativeType;

        try
        {
            var fo = complaint.TryCast<FailedObjectiveComplaint>();
            if (fo != null)
            {
                try
                {
                    var obj = fo._objective;
                    if (obj != null)
                    {
                        var desc = obj.GetDescriptionKey();
                        if (!string.IsNullOrEmpty(desc)) return desc!;
                    }
                }
                catch { }
            }
        }
        catch { }

        return nativeType;
    }

    internal static void LogAdd(VillagerSocial? social, Complaint? complaint)
    {
        try
        {
            BuildIgnoreSetIfNeeded();

            int id = -1;
            try { if (complaint != null) id = complaint.Id; } catch { }

            // GenericMessageComplaint discrimination + ignore filter — checked BEFORE any logging or
            // active-map tracking so a suppressed complaint produces zero log lines and never enters
            // the transition map (its matching REMOVE is suppressed via _suppressedIds instead).
            GenericMessageComplaint? gm = null;
            try { gm = complaint?.TryCast<GenericMessageComplaint>(); } catch { }
            string? gmMessage = null;
            if (gm != null) { try { gmMessage = gm.message; } catch { } }

            // v0.3.0 — militia-alarm lockout signal, checked BEFORE the ignore-filter early return
            // so it fires even if this exact message also happens to be filtered from logging.
            if (!string.IsNullOrEmpty(gmMessage) && gmMessage == _militiaAlarmKey)
            {
                try { SupplyController.NoteAlarm(); }
                catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] SupplyController.NoteAlarm error: {ex}"); }
            }

            if (gm != null && IsIgnoredMessage(gmMessage))
            {
                if (id >= 0) _suppressedIds.Add(id);
                _suppressedSinceReport++;
                return;
            }

            string villagerName = ResolveVillagerName(social);

            string nativeType = "?";
            try { nativeType = complaint != null ? complaint.GetIl2CppType().FullName : "null"; } catch { }

            string discriminator = complaint != null ? ComputeDiscriminator(complaint, nativeType, gm, gmMessage) : nativeType;

            // ── Transition check (v0.1.2): the game re-ADDs the same held complaint every
            // ~50-100ms — only a genuinely NEW identity (or a same-id-slot complaint whose
            // discriminator changed) logs a line; a re-add of the SAME identity just increments a
            // silent counter. ────────────────────────────────────────────────────────────────────
            string key = $"{villagerName}|{id}";
            if (_active.TryGetValue(key, out var entry))
            {
                if (entry.Discriminator == discriminator)
                {
                    entry.ReAdds++;
                    _statsReAdds++;

                    // v0.3.0 — re-adds are the freshness signal for SupplyController; use ONLY the
                    // cached list (no TryCasts on this hot path, which fires ~20/s per held complaint).
                    if (entry.DemandItems != null && entry.DemandItems.Count > 0)
                    {
                        try { SupplyController.NoteDemand(entry.DemandItems, entry.Important); }
                        catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] SupplyController.NoteDemand (readd) error: {ex}"); }
                    }

                    return; // silent — level signal, not an edge
                }
                // same id slot, different content — treat as a fresh complaint occupying the slot.
            }
            else
            {
                entry = new ActiveEntry();
            }

            entry.Discriminator = discriminator;
            entry.FirstSeen = DateTime.UtcNow;
            entry.ReAdds = 0;
            _active[key] = entry;
            _statsNewAdds++;

            bool important = false, notification = false, forcedMenuNotification = false;
            try { if (complaint != null) important = complaint.important; } catch { }
            try { if (complaint != null) notification = complaint.notification; } catch { }
            try { if (complaint != null) forcedMenuNotification = complaint.forcedMenuNotification; } catch { }

            string ts = DateTime.UtcNow.ToString("HH:mm:ss.fff");
            string messagePart = gm != null ? $" message='{gmMessage}'" : "";
            Plugin.Logger.LogInfo(
                $"[SupplyChain] [complaint] ADD (new) villager='{villagerName}' type='{nativeType}' id={id}{messagePart} " +
                $"important={important} notification={notification} forcedMenuNotification={forcedMenuNotification} t={ts}");

            if (complaint != null) LogPayload(complaint);

            // v0.3.0 — demand-item extraction: ItemComplaint/ItemManifestComplaint only in this
            // phase (Category/Complex/Loadouts/Structure/FailedObjective yield no demand items).
            // Cached on the entry so the hot re-add path above never needs a TryCast.
            List<string> demandItems = complaint != null ? ExtractDemandItems(complaint) : new List<string>();
            entry.DemandItems = demandItems.Count > 0 ? demandItems : null;
            entry.Important = important;
            if (demandItems.Count > 0)
            {
                try { SupplyController.NoteDemand(demandItems, important); }
                catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] SupplyController.NoteDemand error: {ex}"); }
            }
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"[SupplyChain] LogAdd error: {ex}");
        }
    }

    // v0.3.0 — demand-item extraction for SupplyController, factored out so the ItemComplaint /
    // ItemManifestComplaint enumeration recipe (already used by LogPayload below) isn't duplicated.
    // Category/Complex/Loadouts/Structure/FailedObjective complaints yield NO demand items in this
    // phase — their "what item" signal isn't a single ItemInfo/manifest the controller can target.
    private static List<string> ExtractDemandItems(Complaint complaint)
    {
        var result = new List<string>();

        try
        {
            var itemComplaint = complaint.TryCast<ItemComplaint>();
            if (itemComplaint != null)
            {
                string name = Common.SafeItemName(itemComplaint.itemInfo);
                if (name != "?") result.Add(name);
            }
        }
        catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] ExtractDemandItems ItemComplaint error: {ex}"); }

        try
        {
            var manifestComplaint = complaint.TryCast<ItemManifestComplaint>();
            if (manifestComplaint != null)
            {
                var manifest = manifestComplaint.itemManifest;
                if (manifest != null)
                {
                    var items = manifest.GetItems();
                    int n = items != null ? items.Count : 0;
                    for (int i = 0; i < n; i++)
                    {
                        try
                        {
                            var iq = items![i];
                            string name = Common.SafeItemName(iq.itemInfo);
                            if (name != "?") result.Add(name);
                        }
                        catch { }
                    }
                }
            }
        }
        catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] ExtractDemandItems ItemManifestComplaint error: {ex}"); }

        return result;
    }

    internal static void LogRemove(VillagerSocial? social, int id)
    {
        try
        {
            if (_suppressedIds.Remove(id)) return; // matching ADD was suppressed — suppress this too

            string villagerName = ResolveVillagerName(social);
            string key = $"{villagerName}|{id}";

            if (_active.TryGetValue(key, out var entry))
            {
                _active.Remove(key);
                double afterSeconds = (DateTime.UtcNow - entry.FirstSeen).TotalSeconds;
                string ts = DateTime.UtcNow.ToString("HH:mm:ss.fff");
                Plugin.Logger.LogInfo(
                    $"[SupplyChain] [complaint] CLEARED villager='{villagerName}' id={id} discriminator='{entry.Discriminator}' " +
                    $"after={afterSeconds:F1}s readds={entry.ReAdds} t={ts}");
                _statsClears++;
                return;
            }

            // Unmatched removal — the id=0 churn the coordinator flagged. Per spec: count it, don't
            // log a per-line for it (that's exactly the volume this version collapses).
            _statsUnmatchedRemoves++;
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"[SupplyChain] LogRemove error: {ex}");
        }
    }

    // TryCast<T>() works here — Complaint and its subclasses are all Il2CppSystem.Object-derived
    // (confirmed via Cecil sweep), not UnityEngine.Object-derived, so the TryCast compile-time
    // limitation (project-wide gotcha) doesn't apply.
    private static void LogPayload(Complaint complaint)
    {
        try
        {
            var itemComplaint = complaint.TryCast<ItemComplaint>();
            if (itemComplaint != null)
            {
                Plugin.Logger.LogInfo($"[SupplyChain] [complaint]   ItemComplaint item='{Common.SafeItemName(itemComplaint.itemInfo)}'");
            }
        }
        catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] LogPayload ItemComplaint error: {ex}"); }

        try
        {
            var manifestComplaint = complaint.TryCast<ItemManifestComplaint>();
            if (manifestComplaint != null)
            {
                int maxCount = -1;
                try { maxCount = manifestComplaint.maxItemCount; } catch { }
                Plugin.Logger.LogInfo($"[SupplyChain] [complaint]   ItemManifestComplaint maxItemCount={maxCount}");
                try
                {
                    var manifest = manifestComplaint.itemManifest;
                    if (manifest != null)
                    {
                        var items = manifest.GetItems();
                        int n = items != null ? items.Count : 0;
                        var sb = new StringBuilder();
                        for (int i = 0; i < n; i++)
                        {
                            try
                            {
                                var iq = items![i];
                                if (sb.Length > 0) sb.Append(", ");
                                sb.Append(Common.SafeItemName(iq.itemInfo)).Append(" x").Append(iq.quantity);
                            }
                            catch { }
                        }
                        Plugin.Logger.LogInfo($"[SupplyChain] [complaint]     manifest ({n}): {sb}");
                    }
                }
                catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] LogPayload manifest error: {ex}"); }
            }
        }
        catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] LogPayload ItemManifestComplaint error: {ex}"); }

        try
        {
            var categoryComplaint = complaint.TryCast<ItemCategoryComplaint>();
            if (categoryComplaint != null)
            {
                string catName = "?";
                try { catName = categoryComplaint.itemCategory?.Name ?? "?"; } catch { }
                Plugin.Logger.LogInfo($"[SupplyChain] [complaint]   ItemCategoryComplaint category='{catName}'");
            }
        }
        catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] LogPayload ItemCategoryComplaint error: {ex}"); }

        try
        {
            var complexComplaint = complaint.TryCast<ComplexCategoryComplaint>();
            if (complexComplaint != null)
            {
                string catName = "?";
                try { catName = complexComplaint.itemCategory?.Name ?? "?"; } catch { }
                Plugin.Logger.LogInfo(
                    $"[SupplyChain] [complaint]   ComplexCategoryComplaint itemA='{Common.SafeItemName(complexComplaint.itemA)}' " +
                    $"itemB='{Common.SafeItemName(complexComplaint.itemB)}' category='{catName}'");
            }
        }
        catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] LogPayload ComplexCategoryComplaint error: {ex}"); }

        try
        {
            var loadoutsComplaint = complaint.TryCast<LoadoutsComplaint>();
            if (loadoutsComplaint != null)
            {
                // missingItems is List<IItemFilter> (not ItemInfo) — IItemFilter has no name, so log
                // each entry's native class only.
                var missing = loadoutsComplaint.missingItems;
                int n = missing != null ? missing.Count : 0;
                var sb = new StringBuilder();
                for (int i = 0; i < n; i++)
                {
                    try
                    {
                        var filter = missing![i];
                        if (sb.Length > 0) sb.Append(", ");
                        sb.Append(Common.NativeClassName(filter));
                    }
                    catch { }
                }
                Plugin.Logger.LogInfo($"[SupplyChain] [complaint]   LoadoutsComplaint missingItems ({n} filters): {sb}");
            }
        }
        catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] LogPayload LoadoutsComplaint error: {ex}"); }

        try
        {
            var structureComplaint = complaint.TryCast<StructureComplaint>();
            if (structureComplaint != null)
            {
                string structName = "?";
                try { structName = Common.SafeName(structureComplaint.Structure); } catch { }
                Plugin.Logger.LogInfo($"[SupplyChain] [complaint]   StructureComplaint structure='{structName}'");
            }
        }
        catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] LogPayload StructureComplaint error: {ex}"); }

        // v0.1.1 — FailedObjectiveComplaint._objective : SSSGame.Objective (Il2CppSystem.Object-
        // derived, confirmed via Cecil sweep — GetIl2CppType() compiles directly). Best-effort
        // description via GetDescriptionKey()/GetLocalizedDescription(); the native type name alone
        // is fine if those throw.
        try
        {
            var failedObjective = complaint.TryCast<FailedObjectiveComplaint>();
            if (failedObjective != null)
            {
                Objective? objective = null;
                try { objective = failedObjective._objective; } catch { }

                string objType = "?";
                try { objType = objective != null ? objective.GetIl2CppType().FullName : "null"; } catch { }

                string objDesc = "?";
                try { if (objective != null) objDesc = objective.GetDescriptionKey() ?? "?"; } catch { }

                Plugin.Logger.LogInfo($"[SupplyChain] [complaint]   FailedObjectiveComplaint objective type='{objType}' descriptionKey='{objDesc}'");
            }
        }
        catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] LogPayload FailedObjectiveComplaint error: {ex}"); }
    }
}

[HarmonyPatch(typeof(VillagerSocial), nameof(VillagerSocial.AddComplaint))]
internal static class AddComplaintPatch
{
    static void Postfix(VillagerSocial __instance, Complaint complaint)
    {
        ComplaintLog.LogAdd(__instance, complaint);
    }
}

[HarmonyPatch(typeof(VillagerSocial), nameof(VillagerSocial.RemoveComplaint))]
internal static class RemoveComplaintPatch
{
    static void Postfix(VillagerSocial __instance, int id)
    {
        ComplaintLog.LogRemove(__instance, id);
    }
}
