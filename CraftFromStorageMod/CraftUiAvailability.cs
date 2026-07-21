using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using BepInEx.Configuration;
using SandSailorStudio.Inventory;
using SSSGame;
using SSSGame.UI;
using TMPro;

namespace CraftFromStorageMod;

// v0.4.0 (idea-17 UI follow-up): the settlement-stock requirement-UI feature. The mod already flips
// the craft AVAILABILITY GATE (CraftTransfer.TryReportAvailable) when settlement storage can cover a
// shortfall, but the per-ingredient "have/need" text in the crafting menu (ItemThumbnailPanel.
// availability) still showed vanilla-only values - a recipe reads "craftable" while its rows say
// otherwise (real observed case, in-game 2026-07-20: Iron Knife Blade and Stick both read "0/1"
// despite being pulled from remote storage). This class computes the COMBINED have/need text and
// rewrites it in place. PURELY ADDITIVE - never writes game state, only rewrites displayed TMP text.
//
// v0.5.0 (timing fix, in-game evidence 2026-07-20): the ItemThumbnailPanel postfixes DO fire (AOT
// inlining ruled out), but at the moment they run, count.text is still the literal prefab
// placeholder "99" - the real "0/2"/"6/10" string is written LATER by other vanilla code, so a
// postfix-time rewrite could never see final values. The write moved to POLLING instead
// (CraftUiPoller.cs, a registered MonoBehaviour Update() timer - the DayTracker/RegenTracker/
// CraftWatcher idiom used throughout this project); OnUpdateAvailability() below (still called from
// the same two postfixes, Patches/UiAvailabilityPatches.cs) is now DIAGNOSTICS ONLY - it proves the
// panels exist and fire, but does not write. PollApply() below is the poller's entry point and the
// ONLY writer left. Field resolution was also TIGHTENED: a one-shot hierarchy dump had shown a false
// positive matching a details-panel "Durability 100/100" label (the old have/need test was an
// unanchored substring match) - selection now requires the WHOLE trimmed/detagged label text to be
// nothing but the have/need pair (LooksLikeStrictHaveNeed), while the byte-exact splice against the
// original (possibly rich-text-tagged) string is unchanged.
internal static class CraftUiAvailability
{
    // ---- THE DOUBLE-COUNT TRAP ----
    // Vanilla's displayed "have" already includes the active station's own inventory (that's why Rope
    // read 20/1 while sitting in station storage). SettlementStock's snapshot walks ALL settlement
    // containers, which INCLUDES that same station - naively adding the two would double-count it.
    // Net it out: displayedHave = vanillaHave + max(0, settlementStockForItem - activeStationQtyForItem)
    //
    // Unanchored - used only to LOCATE the have/need pair within a string for the byte-exact splice
    // (TryParseHaveNumber below). NOT used to decide whether a label IS a requirement row - that
    // selection now requires LooksLikeStrictHaveNeed (see below), since this pattern alone is what
    // matched "Durability 100/100" on the details panel (confirmed in-game 2026-07-20).
    private static readonly Regex HaveNeedPattern = new(@"(?<have>\d+)\s*/\s*(?<need>\d+)", RegexOptions.Compiled);

    // ---- STRICT have/need test (v0.5.0) ----
    // Strips rich-text tags and surrounding whitespace, then requires the ENTIRE remaining string to
    // be nothing but "<digits>/<digits>". A bare durability string like "Durability 100/100" fails
    // this (it has a "Durability " prefix) while "0/2" and "<color=red>6/10</color>" both pass.
    private static readonly Regex RichTextTagPattern = new(@"<[^>]*>", RegexOptions.Compiled);
    private static readonly Regex StrictHaveNeedPattern = new(@"^\d+\s*/\s*\d+$", RegexOptions.Compiled);

    private const int MaxScopingLogs = 25;
    private const int MaxPerItemLogs = 5;
    private const int MaxHierarchyDumps = 3;

    private static readonly HashSet<string> _firedMethods = new();
    private static int _scopingLogCount;
    private static readonly Dictionary<int, int> _rewriteLogCounts = new();
    private static readonly Dictionary<int, int> _unparsedLogCounts = new();

    // Entry point - called from both ItemThumbnailPanel patches (Patches/UiAvailabilityPatches.cs).
    // v0.5.0: DIAGNOSTICS ONLY - see the class header. Proves the postfixes still fire (the top
    // AOT-inlining risk) and records panel-scoping evidence; the write happens in PollApply() below,
    // called from CraftUiPoller's Update() timer instead.
    internal static void OnUpdateAvailability(ItemThumbnailPanel instance, string methodName)
    {
        try
        {
            LogFiredOnce(methodName);

            if (!SafeGetBool(Plugin.ShowSettlementStockInUI, true)) return;
            if (CraftUiState.ActiveCraftMenu == null) return; // diagnostics only while a crafting menu is open

            if (SafeGetBool(Plugin.UiDiagnostics, true)) LogScopingEvidence(instance);
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"[CFS] CraftUiAvailability.OnUpdateAvailability[{methodName}] error: {ex}");
        }
    }

    // v0.5.0 entry point - called from CraftUiPoller.Update() once per poll tick, once per
    // ItemThumbnailPanel found under the active craft menu that has a non-null ItemInfo. This is the
    // ONLY method that writes displayed TMP text.
    internal static void PollApply(ItemThumbnailPanel instance, CraftMenu menu, bool diagnostics)
    {
        try
        {
            ItemInfo? info = SafeGetItemInfo(instance);
            if (info == null) return; // pooled/blank slot - nothing to describe

            TextMeshProUGUI? tmp = ResolveQuantityLabel(instance, diagnostics);
            string? raw = SafeGetText(tmp);
            if (tmp == null || string.IsNullOrEmpty(raw)) return;

            int labelId;
            try { labelId = tmp.GetInstanceID(); } catch { return; }

            // IDEMPOTENCY GUARD - critical. The poller re-reads text it may itself have written;
            // without this a row compounds forever ("0/2" -> "20/2" -> next tick reads have=20 ->
            // "40/2" -> ...). If the label's current text is byte-identical to what we last wrote
            // for it, we already handled this row - skip. If it differs, vanilla has rewritten the
            // row since (fresh true quantities) - ApplyCombinedDisplay recomputes from THIS current
            // text as the new baseline.
            if (_lastWrittenText.TryGetValue(labelId, out var lastWritten) && raw == lastWritten)
                return;

            ApplyCombinedDisplay(tmp, raw!, info, menu, diagnostics, labelId);
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"[CFS] CraftUiAvailability.PollApply error: {ex}");
        }
    }

    private static void ApplyCombinedDisplay(TextMeshProUGUI tmp, string raw, ItemInfo info, CraftMenu menu, bool diagnostics, int labelId)
    {
        if (!TryParseHaveNumber(raw, out int haveIndex, out int haveLength, out int vanillaHave))
        {
            if (diagnostics) LogUnparsedRateLimited(info, raw);
            return; // never write a malformed string into the UI
        }

        // READ-ONLY cached lookup - never forces a settlement rebuild from this UI path (point 6:
        // the walk covers 651 containers and _UpdateAvailablility can fire on every container item
        // add/remove for every visible panel). No snapshot yet at all -> skip the row rather than guess.
        if (!SettlementStock.TryGetCachedQuantity(info, out int settlementQty)) return;

        int stationQty = 0;
        string stationSource;
        try
        {
            var stationInv = GetStationInventoryCollection(menu, out stationSource);
            if (stationInv != null) stationQty = stationInv.GetItemQuantity(info);
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"[CFS] CraftUiAvailability station-qty lookup error: {ex}");
            stationSource = "none";
        }

        // Fail closed: with no station collection resolved we cannot tell how much of the settlement
        // total vanilla is ALREADY showing, so adding the whole total would silently inflate the row.
        // Leave the vanilla text alone instead and say so once.
        if (stationSource == "none")
        {
            WarnNoStationCollectionOnce();
            return;
        }

        int extra = settlementQty - stationQty;
        if (extra <= 0) return; // already covered by vanilla's own station-inventory count

        int displayedHave = vanillaHave + extra;
        string newText = raw.Substring(0, haveIndex) + displayedHave.ToString() + raw.Substring(haveIndex + haveLength);

        try { tmp.text = newText; }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"[CFS] CraftUiAvailability: writing rewritten text failed: {ex}");
            return;
        }

        _lastWrittenText[labelId] = newText;

        if (diagnostics) LogRewriteRateLimited(info, vanillaHave, settlementQty, stationQty, stationSource, extra, displayedHave);
    }

    // Confirmed fact (CraftWatcher.cs BuildSlots): InventoryComponent's item collection is reached via
    // GetItemCollection(), falling back to the .items field - same accessor pattern already proven
    // in-game for CraftInteraction.inventory/blueprintInventory.
    // Resolves the station-side collection whose quantity must be SUBTRACTED from the settlement
    // total (SettlementStock walks the station's own containers too, so without this the station's
    // stock is counted twice - vanilla already includes it in the displayed "have").
    //
    // Two sources, in order. CraftMenu.GetCraftStationInventory() is the menu's own notion, but it
    // is NOT confirmed to be the same object vanilla counts. CraftInteraction.ItemInventory IS
    // confirmed in-game (2026-07-20): it is pointer-identical to CraftInteraction.inventory and to
    // Workstation.GetInventory(), the composite over ALL of the structure's containers, and it is
    // the collection consumption actually drains. If the first source yields nothing, fall back to
    // it rather than leaving stationQty at 0 - a silent 0 would double-count the station and
    // inflate every row (the failure would look like a broken feature, not a wrong subtrahend).
    // `source` is logged so one run reveals which path is live and whether the two agree.
    private static ItemCollection? GetStationInventoryCollection(CraftMenu menu, out string source)
    {
        source = "none";
        ItemCollection? coll = null;
        try
        {
            InventoryComponent? invComp = menu.GetCraftStationInventory();
            if (invComp != null)
            {
                try { coll = invComp.GetItemCollection(); } catch { }
                if (coll == null) { try { coll = invComp.items; } catch { } }
                if (coll != null) source = "CraftMenu.GetCraftStationInventory";
            }
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"[CFS] CraftUiAvailability: GetCraftStationInventory failed: {ex}");
        }

        if (coll == null)
        {
            try
            {
                var interaction = menu.GetCraftInteraction();
                if (interaction != null)
                {
                    coll = interaction.ItemInventory;
                    if (coll != null) source = "CraftInteraction.ItemInventory (fallback)";
                }
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError($"[CFS] CraftUiAvailability: CraftInteraction.ItemInventory fallback failed: {ex}");
            }
        }

        return coll;
    }

    // Finds the FIRST "<digits> / <digits>" occurrence anywhere in the text (unanchored - correctly
    // skips unrelated digit runs in a prefix, e.g. a tier number) and returns just the "have" digits'
    // span so the caller can splice in a replacement while preserving everything else (separator,
    // need number, rich-text markup, prefix/suffix) byte-for-byte. The text format is NOT confirmed
    // ("0/1" is only what one player reported seeing) - a non-match is the expected, handled case.
    private static bool TryParseHaveNumber(string text, out int haveIndex, out int haveLength, out int haveValue)
    {
        haveIndex = 0; haveLength = 0; haveValue = 0;
        Match m;
        try { m = HaveNeedPattern.Match(text); }
        catch { return false; }
        if (!m.Success) return false;
        var g = m.Groups["have"];
        haveIndex = g.Index;
        haveLength = g.Length;
        return int.TryParse(g.Value, out haveValue);
    }

    // ---- diagnostics (all rate-limited so a long session can't flood the log) ----

    private static void LogFiredOnce(string methodName)
    {
        try
        {
            if (_firedMethods.Add(methodName))
                Plugin.Logger.LogInfo($"[CFS] UI availability patch FIRED: {methodName}() postfix is executing (not AOT-inlined away).");
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"[CFS] CraftUiAvailability.LogFiredOnce error: {ex}");
        }
    }

    // Scoping evidence for the next iteration: parent's native class name + checkAvailability +
    // raw availability.text, BEFORE any rewrite. ItemThumbnailPanel is reused for inventory, chests,
    // and every other item panel in the game - if this log shows ordinary inventory panels reaching
    // here, that's the signal to tighten the gate in OnUpdateAvailability.
    private static void LogScopingEvidence(ItemThumbnailPanel instance)
    {
        if (_scopingLogCount >= MaxScopingLogs) return;
        try
        {
            string availText = "null";
            try
            {
                var tmp = instance.availability;
                availText = tmp != null ? $"\"{tmp.text ?? ""}\"" : "null";
            }
            catch { }
            string countText = "null";
            try
            {
                var tmp = instance.count;
                countText = tmp != null ? $"\"{tmp.text ?? ""}\"" : "null";
            }
            catch { }
            ItemInfo? info = SafeGetItemInfo(instance);

            // v0.4.2: SKIP blank/pooled panels entirely. In v0.4.1 all ten samples were empty slots
            // (no item, both labels null) and the budget was exhausted before a single real
            // requirement row was ever recorded - so the log could not answer the one question it
            // existed to answer. Only panels that actually describe an item are worth a slot here.
            if (info == null && availText == "null" && countText == "null") return;

            _scopingLogCount++;
            string parentClass = "null";
            try { parentClass = Plugin.NativeClassName(instance.parent); } catch { }
            bool checkAvail = false;
            try { checkAvail = instance.checkAvailability; } catch { }
            int qty = -1;
            try { qty = instance.Quantity; } catch { }
            Plugin.Logger.LogInfo($"[CFS] UI SCOPE [{_scopingLogCount}/{MaxScopingLogs}]: item='{SafeName(info)}' " +
                $"qty={qty} parent={parentClass} checkAvailability={checkAvail} " +
                $"availability.text={availText} count.text={countText}");
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"[CFS] CraftUiAvailability.LogScopingEvidence error: {ex}");
        }
    }

    private static bool _warnedNoStationCollection;

    private static void WarnNoStationCollectionOnce()
    {
        if (_warnedNoStationCollection) return;
        _warnedNoStationCollection = true;
        Plugin.Logger.LogWarning("[CFS] UI availability: neither CraftMenu.GetCraftStationInventory() nor " +
            "CraftInteraction.ItemInventory resolved a station collection - cannot net out the station's own " +
            "stock, so requirement rows are being LEFT AT VANILLA VALUES rather than risk inflating them. " +
            "If this appears, the station-side accessor needs re-research.");
    }

    private static void LogRewriteRateLimited(ItemInfo info, int vanillaHave, int settlementQty, int stationQty, string stationSource, int extra, int displayedHave)
    {
        try
        {
            int id;
            try { id = info.id; } catch { return; }
            _rewriteLogCounts.TryGetValue(id, out int count);
            if (count >= MaxPerItemLogs) return;
            _rewriteLogCounts[id] = count + 1;
            Plugin.Logger.LogInfo($"[CFS] UI availability rewrite: '{SafeName(info)}' vanillaHave={vanillaHave} " +
                $"settlementStock={settlementQty} stationQty={stationQty} (via {stationSource}) " +
                $"extra=+{extra} -> displayed {displayedHave}.");
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"[CFS] CraftUiAvailability.LogRewriteRateLimited error: {ex}");
        }
    }

    private static void LogUnparsedRateLimited(ItemInfo info, string raw)
    {
        try
        {
            int id;
            try { id = info.id; } catch { id = -1; }
            _unparsedLogCounts.TryGetValue(id, out int count);
            if (count >= MaxPerItemLogs) return;
            _unparsedLogCounts[id] = count + 1;
            Plugin.Logger.LogWarning($"[CFS] UI availability: '{SafeName(info)}' text did not match the " +
                $"<have>/<need> shape - left unchanged. raw=\"{raw}\"");
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"[CFS] CraftUiAvailability.LogUnparsedRateLimited error: {ex}");
        }
    }

    // ---- small safe-accessors (local copies - CraftTransfer.cs's own helpers are private and that
    // file is intentionally not touched by this feature) ----

    private static bool LooksLikeStrictHaveNeed(string text)
    {
        try
        {
            string stripped = RichTextTagPattern.Replace(text ?? "", "").Trim();
            return StrictHaveNeedPattern.IsMatch(stripped);
        }
        catch { return false; }
    }

    // ---- Structural resolution of the have/need label ----
    // v0.5.0: resolution order tightened per in-game evidence. `availability` was confirmed ALWAYS
    // null (v0.4.1/v0.4.2 - dead end, no longer tried at all). `count` holds the prefab placeholder
    // "99" until vanilla writes the real text later, which the STRICT test naturally rejects (no
    // "/" in "99"), falling through to the subtree walk without any special-case code.
    private static TextMeshProUGUI? ResolveQuantityLabel(ItemThumbnailPanel instance, bool diagnostics)
    {
        TextMeshProUGUI? countTmp = SafeGetCountLabel(instance);
        string? countRaw = SafeGetText(countTmp);
        if (!string.IsNullOrEmpty(countRaw) && LooksLikeStrictHaveNeed(countRaw!))
        {
            if (diagnostics) LogFieldChoiceOnce("count");
            return countTmp;
        }

        TextMeshProUGUI? walked = ResolveHaveNeedLabel(instance, diagnostics);
        if (walked != null && diagnostics) LogFieldChoiceOnce("subtree-walk");
        return walked;
    }

    // Cached per panel instance: the subtree walk is bounded (depth 6) but the poller ticks
    // frequently, so it must not re-walk every tick. Cleared on world-leave with everything else -
    // never hold interop wrappers of per-world objects across sessions.
    private static readonly Dictionary<int, TextMeshProUGUI> _resolvedLabels = new();
    private static int _hierarchyDumpCount;

    // v0.5.0: per-label record of the EXACT string this mod last wrote (keyed by the TMP label's own
    // GetInstanceID(), not the panel's) - the idempotency guard PollApply() checks before recomputing.
    private static readonly Dictionary<int, string> _lastWrittenText = new();

    internal static void ClearWorldState()
    {
        _resolvedLabels.Clear();
        _hierarchyDumpCount = 0;
        _scopingLogCount = 0;
        _loggedFieldChoice.Clear();
        _lastWrittenText.Clear();
    }

    // v0.5.0: called from Patches/UiAvailabilityPatches.cs CraftMenuClosedPatch. The per-label
    // last-written record must not survive a menu close - panels are pooled and reused, so reopening
    // should start from a fresh baseline rather than risk comparing a new row's text against a
    // previous row's last-written value.
    internal static void ClearOnMenuClose()
    {
        _lastWrittenText.Clear();
    }

    private static TextMeshProUGUI? ResolveHaveNeedLabel(ItemThumbnailPanel instance, bool diagnostics)
    {
        int id;
        try { id = instance.GetInstanceID(); } catch { return null; }

        if (_resolvedLabels.TryGetValue(id, out var cached))
        {
            // Panels are pooled and reused, so a cached label can drift onto a different row.
            // Revalidate cheaply (STRICT test - see LooksLikeStrictHaveNeed); drop it if it no
            // longer reads as a have/need pair.
            try { if (cached != null && LooksLikeStrictHaveNeed(cached.text ?? "")) return cached; }
            catch { }
            _resolvedLabels.Remove(id);
        }

        UnityEngine.Transform? root;
        try { root = instance.transform; } catch { return null; }
        if (root == null) return null;

        TextMeshProUGUI? found = null;
        // v0.5.0: log the first few dumps (not just one) - a details-panel dump (whose subtree is
        // the WHOLE crafting page) could otherwise consume the only slot before a material-row dump
        // ever gets recorded.
        bool dump = diagnostics && _hierarchyDumpCount < MaxHierarchyDumps;
        var lines = dump ? new List<string>() : null;
        WalkForText(root, 0, "", ref found, lines);

        if (dump)
        {
            _hierarchyDumpCount++;
            Plugin.Logger.LogInfo($"[CFS] UI hierarchy dump for panel '{SafeName(SafeGetItemInfo(instance))}': " +
                (lines!.Count > 0 ? string.Join(" | ", lines) : "NO TMP text components found in subtree"));
        }

        if (found != null) _resolvedLabels[id] = found;
        return found;
    }

    // Manual per-node GetComponent walk: the PLURAL GetComponentsInChildren<T>(bool) throws
    // MissingMethodException through the interop trampoline (project-wide gotcha), so the hierarchy
    // must be walked by hand with the singular generic. Descends into children ONLY (never t.parent),
    // so a panel can never reach a label outside its own subtree (e.g. another row's quantity).
    // v0.5.1 - NESTED-PANEL BOUNDARY. The details/preview panel's subtree CONTAINS the material rows:
    // the in-game hierarchy dump (2026-07-20) recorded
    // `Page/MaterialsDiv/MaterialsList/ItemThumbnailMaterial_Medium/Fitter/Quantity` at depth 5,
    // inside the old depth-6 cap. So the details panel - whose ItemInfo is the item BEING CRAFTED -
    // would resolve to a MATERIAL row's label and rewrite that row using the crafted item's
    // settlement stock: wrong item, wrong number, and two panels writing the same label each tick.
    // Two independent guards: stop descending at any node that owns its own ItemThumbnailPanel (that
    // is another panel's territory), and cap the depth at 3 (a material row's own label sits at
    // depth 2 - `Fitter/Quantity` - so 3 leaves margin without reaching a nested panel).
    private const int MaxWalkDepth = 3;

    private static void WalkForText(UnityEngine.Transform t, int depth, string path,
        ref TextMeshProUGUI? found, List<string>? lines)
    {
        if (depth > MaxWalkDepth) return;

        if (depth > 0)
        {
            try { if (t.gameObject.GetComponent<ItemThumbnailPanel>() != null) return; }
            catch { }
        }

        string name;
        try { name = t.name ?? "?"; } catch { name = "?"; }
        string here = depth == 0 ? name : path + "/" + name;

        try
        {
            var tmp = t.gameObject.GetComponent<TextMeshProUGUI>();
            if (tmp != null)
            {
                string txt;
                try { txt = tmp.text ?? ""; } catch { txt = ""; }
                lines?.Add($"{here}=\"{txt}\"");
                if (found == null && LooksLikeStrictHaveNeed(txt)) found = tmp;
            }
        }
        catch { }

        int n;
        try { n = t.childCount; } catch { return; }
        for (int i = 0; i < n; i++)
        {
            UnityEngine.Transform? c = null;
            try { c = t.GetChild(i); } catch { }
            if (c != null) WalkForText(c, depth + 1, here, ref found, lines);
        }
    }

    // Logged once per field so the log states plainly which label actually carries the numbers -
    // `availability` was confirmed always null (v0.4.1) and is no longer tried at all in v0.5.0.
    private static readonly HashSet<string> _loggedFieldChoice = new();
    private static void LogFieldChoiceOnce(string field)
    {
        try
        {
            if (_loggedFieldChoice.Add(field))
                Plugin.Logger.LogInfo($"[CFS] UI availability: requirement rows are being read from the " +
                    $"'{field}' label.");
        }
        catch { }
    }

    private static TextMeshProUGUI? SafeGetCountLabel(ItemThumbnailPanel instance)
    {
        try { return instance.count; } catch { return null; }
    }

    private static string? SafeGetText(TextMeshProUGUI? tmp)
    {
        if (tmp == null) return null;
        try { return tmp.text; } catch { return null; }
    }

    private static ItemInfo? SafeGetItemInfo(ItemThumbnailPanel instance)
    {
        try { return instance.ItemInfo; } catch { return null; }
    }

    private static string SafeName(ItemInfo? info)
    {
        if (info == null) return "?";
        try { return info.Name ?? "?"; } catch { return "?"; }
    }

    private static bool SafeGetBool(ConfigEntry<bool>? entry, bool fallback)
    {
        try { return entry?.Value ?? fallback; } catch { return fallback; }
    }
}
