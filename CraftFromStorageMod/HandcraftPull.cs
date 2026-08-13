using System;
using System.Collections.Generic;
using SandSailorStudio.Inventory;
using SSSGame;
using SSSGame.UI;
using UnityEngine;

namespace CraftFromStorageMod;

// v1.4.0 — the PERSONAL (bench-free) crafting half of idea 17.
//
// The problem this solves. Pressing B opens SSSGame.UI.BuildMenu, and one of its tabs is the
// personal crafting list where rope and wooden tools are made. That menu is NOT a
// SSSGame.UI.CraftMenu, so every hook the station path uses misses it (in-game 2026-08-13: two
// menus opened five seconds apart produced exactly one CraftMenu.OnActivate). A player standing in
// a settlement full of fibers was told "Cannot craft this item: Missing component".
//
// Why this is a PULL and not a gate widening. The station path widens
// CraftInteraction.CheckOwnedRequirements. The build menu does not use it. The tests that could
// answer here all live on BlueprintInfo - CanBeUsed, _CheckParts, _CheckCost,
// GetAvailablePartsPercent - and every one takes an ItemCollection, the parameter family that
// natively crashes this game on contact. Patching the craft trigger is out too: a postfix on
// Blueprint.Use(GameObject) threw inside the interop trampoline before any user code ran and the
// game never reached a playable state (confirmed 2026-08-13, recorded in
// Patches/PersonalCraftProbePatches.cs).
//
// So nothing here patches a gate or a trigger. It puts the materials in the player's own pack while
// the menu is open, which is exactly where the build menu already looks, and takes back whatever
// went unused when the menu closes. That is the same "reach extension, not a new mechanism" design
// the station path uses.
internal static class HandcraftPull
{
    // The open build menu, captured in BuildMenu.OnActivate and dropped in OnClosed. Never cached
    // across world sessions - project-wide gotcha, cleared by ClearWorldState below.
    // Fully qualified throughout this file: bare ContextMenu is ambiguous with UnityEngine.ContextMenu.
    internal static SSSGame.UI.ContextMenu? ActiveBuildMenu;

    // Ledger key for this feature's pulls. The station path keys its ledger by agent pointer; this
    // path has no agent at the pull site, so it uses the open menu's own pointer, which is unique
    // per open and cannot collide with an agent key.
    private static IntPtr _ledgerKey = IntPtr.Zero;

    // v1.4.2: raised from depth 10 / 400 nodes. The v1.4.1 run hit the node cap on EVERY tick
    // (`400 node(s) walked (depth cap 10, node cap 400)`, six times), so the search was running out
    // of budget before it reached the details panel rather than failing to recognise it. The build
    // menu's hierarchy is far larger than the crafting-table menu these caps were sized for.
    private const int WalkDepthCap = 20;
    private const int WalkNodeCap = 6000;
    private const int MaxNoAgentLogs = 3;

    private static int _nodesWalked;
    private static int _noAgentLogs;
    private static float _accumulator;
    private static int _lastBlueprintId = -1;

    // v1.4.1: every early return in Tick() now says WHY. Without this the five stand-aside paths are
    // indistinguishable in the log, which is what made the v1.4.0 run unreadable: the pull did not
    // happen and nothing recorded which condition stopped it. Rate-limited per reason so a 0.2 s
    // poller cannot flood the log, and re-armed on every menu open so each open reports afresh.
    private const int MaxReasonLogsPerOpen = 3;
    private static readonly Dictionary<string, int> _reasonLogs = new();
    private static int _detailsPanelsSeen;
    private static int _detailsPanelsWithInfo;
    private static string _selectionSourceNode = "";

    // v1.5.1: the preview page that owns the current selection, captured during the same walk that
    // reads the selection. Held only for the duration of one tick's work and re-resolved every tick,
    // so no interop wrapper is cached across world sessions.
    private static BuildPreviewTabPage? _previewPage;
    private static bool _refreshLogged;

    // ---- v1.4.3 one-shot enumeration dump ----
    //
    // Four guesses at which component holds the selected recipe in the build menu have now been
    // wrong (PlayerMenu, CreateItemsTabPage, CraftBlueprint.Activate, ItemDetailsPanel), each
    // costing a play session. This stops guessing: it walks the open menu and reports what is
    // actually there, so the real component can be named from evidence.
    //
    // Component enumeration is not directly available - the plural GetComponentsInChildren<T>(bool)
    // and the non-generic GameObject.GetComponents(Type) are BOTH missing through the interop
    // trampoline (project-wide gotchas). The workaround is the documented one: a base-typed singular
    // GetComponent<T>() DOES return a derived instance, so asking each node for a MonoBehaviour and
    // reading the NATIVE class name off whatever comes back identifies that node's main script.
    private const int MaxDumpLines = 400;
    private const int DumpsPerOpen = 2;
    private static int _dumpsDone;
    private static float _openElapsed;
    private static int _dumpLines;

    private static void DumpMenu(Transform root, int dumpIndex)
    {
        _dumpLines = 0;
        Plugin.Logger.LogInfo($"[CFS] [CFS-HC] ===== MENU DUMP {dumpIndex}/{DumpsPerOpen} BEGIN " +
            $"(at {_openElapsed:F1}s after open; max {MaxDumpLines} lines) =====");
        try { DumpNode(root, 0, ""); }
        catch (Exception ex) { Plugin.Logger.LogError($"[CFS] [CFS-HC] MENU DUMP error: {ex}"); }
        Plugin.Logger.LogInfo($"[CFS] [CFS-HC] ===== MENU DUMP {dumpIndex}/{DumpsPerOpen} END " +
            $"({_dumpLines} line(s) written) =====");
    }

    private static void DumpNode(Transform t, int depth, string path)
    {
        if (depth > WalkDepthCap || _dumpLines >= MaxDumpLines) return;

        string name = "unreadable";
        try { name = t.gameObject.name; } catch { }
        string here = depth == 0 ? name : path + "/" + name;

        bool active = false;
        try { active = t.gameObject.activeInHierarchy; } catch { }

        // The node's main script, by native class name.
        string script = "-";
        try
        {
            var mb = t.gameObject.GetComponent<MonoBehaviour>();
            if (mb != null) script = Plugin.NativeClassName(mb);
        }
        catch { }

        // The two panel types the mod already knows how to read, with whatever item they carry.
        string extra = "";
        try
        {
            var thumb = t.gameObject.GetComponent<ItemThumbnailPanel>();
            if (thumb != null)
            {
                ItemInfo? ti = null;
                try { ti = thumb.ItemInfo; } catch { }
                extra += $" THUMB(item='{SafeInfoName(ti)}')";
            }
        }
        catch { }
        try
        {
            var det = t.gameObject.GetComponent<ItemDetailsPanel>();
            if (det != null)
            {
                ItemInfo? di = null;
                try { di = det.ItemInfo; } catch { }
                extra += $" DETAILS(item='{SafeInfoName(di)}')";
            }
        }
        catch { }

        _dumpLines++;
        Plugin.Logger.LogInfo($"[CFS] [CFS-HC] DUMP d{depth} active={active} script={script} path={here}{extra}");

        int n;
        try { n = t.childCount; } catch { return; }
        for (int i = 0; i < n; i++)
        {
            Transform? c = null;
            try { c = t.GetChild(i); } catch { }
            if (c != null) DumpNode(c, depth + 1, here);
        }
    }

    private static void StandAside(string why)
    {
        int n;
        _reasonLogs.TryGetValue(why, out n);
        if (n >= MaxReasonLogsPerOpen) return;
        _reasonLogs[why] = n + 1;
        Plugin.Logger.LogInfo($"[CFS] [CFS-HC] tick stood aside [{n + 1}/{MaxReasonLogsPerOpen}]: {why}");
    }

    internal static void ClearWorldState()
    {
        ActiveBuildMenu = null;
        _ledgerKey = IntPtr.Zero;
        _noAgentLogs = 0;
        _accumulator = 0f;
        _lastBlueprintId = -1;
    }

    internal static void OnBuildMenuOpened(SSSGame.UI.ContextMenu menu)
    {
        ActiveBuildMenu = menu;
        _lastBlueprintId = -1;
        _accumulator = 0f;
        _reasonLogs.Clear();
        _dumpsDone = 0;
        _openElapsed = 0f;
        _refreshLogged = false;
        try
        {
            _ledgerKey = ((object)menu is Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase b)
                ? b.Pointer : IntPtr.Zero;
        }
        catch { _ledgerKey = IntPtr.Zero; }
        Plugin.Logger.LogInfo("[CFS] [CFS-HC] BuildMenu opened - personal-craft pull armed.");
    }

    // Closing the menu is this feature's sweep-back point. The station path sweeps on craft success;
    // there is no craft-success hook here, so the menu closing is the moment we know the player is
    // done. Anything they actually consumed is already gone from the pack, and the clamp in
    // CraftTransfer.SweepLedgerFor only returns the surplus, so a completed craft is never undone.
    internal static void OnBuildMenuClosed()
    {
        try
        {
            if (_ledgerKey != IntPtr.Zero) CraftTransfer.SweepLedgerFor(_ledgerKey, "[CFS] [CFS-HC]");
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"[CFS] [CFS-HC] sweep-back on menu close failed: {ex}");
        }
        finally
        {
            ActiveBuildMenu = null;
            _ledgerKey = IntPtr.Zero;
            _lastBlueprintId = -1;
        }
    }

    // Driven from CraftUiPoller.Update() rather than from a patch, for the same reason the
    // requirement-count rewrite is: the values we need are written by vanilla code that runs after
    // any postfix we could attach, so polling is the only way to read what is actually on screen.
    internal static void Tick(float deltaTime)
    {
        var menu = ActiveBuildMenu;
        if (menu == null) return;

        // v1.4.2 sweep-back safety net. The v1.4.1 run logged BuildMenu.OnDeactivate exactly once,
        // during startup, and never again after the menu was opened and closed - so the close hook
        // alone cannot be trusted to return borrowed materials. If the menu object is no longer
        // active, treat that as the close and sweep now. Without this, anything pulled would stay
        // in the player's pack permanently.
        bool menuAlive = false;
        try { menuAlive = menu.gameObject.activeInHierarchy; } catch { menuAlive = false; }
        if (!menuAlive)
        {
            Plugin.Logger.LogInfo("[CFS] [CFS-HC] build menu is no longer active - sweeping back on the poller " +
                "rather than the close hook.");
            OnBuildMenuClosed();
            return;
        }

        if (!SafeFlag(Plugin.EnablePlayerPull, true)) { StandAside("EnablePlayerPull is false."); return; }
        if (!SafeFlag(Plugin.EnablePlayerHandcraftPull, true)) { StandAside("EnablePlayerHandcraftPull is false."); return; }
        if (!CraftTransfer.IsHostOrSolo()) { StandAside("not host or solo - write authority refused."); return; }

        float interval = 0.2f;
        try { interval = Plugin.UiPollSeconds?.Value ?? 0.2f; } catch { }
        if (interval <= 0f) interval = 0.2f;

        _accumulator += deltaTime;
        if (_accumulator < interval) return;
        _accumulator = 0f;

        ItemCollection? pack = ResolvePlayerInventory();
        if (pack == null)
        {
            if (_noAgentLogs < MaxNoAgentLogs)
            {
                _noAgentLogs++;
                Plugin.Logger.LogWarning($"[CFS] [CFS-HC] [{_noAgentLogs}/{MaxNoAgentLogs}] could not resolve the " +
                    "player's inventory collection - standing aside, vanilla behaviour unchanged.");
            }
            return;
        }

        Transform? root = null;
        try { root = menu.transform; } catch { }
        if (root == null) { StandAside("the open menu's transform could not be read."); return; }

        // Two dumps per open, at roughly 2 s and 6 s. Two rather than one so a recipe clicked a
        // moment after opening is still captured with the menu in its selected state.
        _openElapsed += interval;
        if (_dumpsDone == 0 && _openElapsed >= 2f) { _dumpsDone = 1; DumpMenu(root, 1); }
        else if (_dumpsDone == 1 && _openElapsed >= 6f) { _dumpsDone = 2; DumpMenu(root, 2); }

        // v1.5.0: rewrite the ingredient rows to show the settlement-wide total, the way the
        // crafting table's rows already do. Runs every tick and before the selection check, so the
        // numbers stay current even on a tick where nothing needs pulling.
        bool uiDiag = false;
        try { uiDiag = Plugin.UiDiagnostics?.Value ?? false; } catch { }
        if (SafeFlag(Plugin.ShowSettlementStockInUI, true)) RewriteMaterialRows(root, 0, uiDiag);

        ItemInfo? selected = FindSelectedItemInfo(root);
        if (selected == null)
        {
            StandAside($"no selected recipe found - {_detailsPanelsSeen} BuildPreviewPage node(s) seen, " +
                $"{_detailsPanelsWithInfo} of them active and holding an item; {_nodesWalked} node(s) walked " +
                $"(depth cap {WalkDepthCap}, node cap {WalkNodeCap}" +
                (_nodesWalked >= WalkNodeCap ? " - NODE CAP HIT, the search ran out of budget" : "") + ").");
            return;
        }

        var parts = ReadBlueprintParts(selected);
        if (parts.Count == 0)
        {
            StandAside($"selection '{SafeInfoName(selected)}' has no readable recipe parts - its native class is " +
                $"{Plugin.NativeClassName(selected)}.");
            return;
        }

        // v1.5.0: several crafts' worth, not one. The v1.4.4 run pulled exactly one craft's worth,
        // which left the player crafting a single item and then having to reselect the recipe before
        // the next one - the crafting table lets you press craft repeatedly, and this menu is
        // supposed to behave the same way (user, 2026-08-13). The count is bounded and configurable
        // rather than unlimited, so a settlement is never emptied into the player's pack; the poller
        // tops the buffer back up within one tick after each craft.
        int crafts = 5;
        try { crafts = Plugin.HandcraftCraftsToStock?.Value ?? 5; } catch { }
        if (crafts < 1) crafts = 1;

        var shortfall = new List<(ItemInfo info, int missing)>();
        foreach (var (info, needQty) in parts)
        {
            int have = 0;
            try { have = pack.GetItemQuantity(info); } catch { }
            int missing = (needQty * crafts) - have;
            if (missing > 0) shortfall.Add((info, missing));
        }
        if (shortfall.Count == 0)
        {
            StandAside($"selection '{SafeInfoName(selected)}' is already fully covered by the player's own pack.");
            return;
        }

        int bpId = -1;
        try { bpId = selected.id; } catch { }
        if (bpId != _lastBlueprintId)
        {
            _lastBlueprintId = bpId;
            Plugin.Logger.LogInfo($"[CFS] [CFS-HC] selection changed to '{SafeInfoName(selected)}' " +
                $"(read from node '{_selectionSourceNode}', native class {Plugin.NativeClassName(selected)}) - " +
                $"{parts.Count} ingredient type(s) in the recipe, {shortfall.Count} of them short.");
        }

        // Pack quantities BEFORE the pull, so the amount actually moved can be measured afterwards.
        // The pull reports no totals of its own, and the displayed settlement number needs the real
        // figure rather than the amount requested - a container can refuse a move.
        var beforeQty = new Dictionary<int, int>();
        foreach (var (info, _) in shortfall)
        {
            int id;
            try { id = info.id; } catch { continue; }
            int q = 0;
            try { q = pack.GetItemQuantity(info); } catch { }
            beforeQty[id] = q;
        }

        try { CraftTransfer.PullShortfallFor(shortfall, pack, _ledgerKey, "[CFS] [CFS-HC]"); }
        catch (Exception ex) { Plugin.Logger.LogError($"[CFS] [CFS-HC] pull failed: {ex}"); }

        // Tell the display cache what left storage, so the ingredient count drops on the same click
        // rather than several crafts later when the snapshot happens to rebuild.
        foreach (var (info, _) in shortfall)
        {
            int id;
            try { id = info.id; } catch { continue; }
            if (!beforeQty.TryGetValue(id, out int before)) continue;
            int after = before;
            try { after = pack.GetItemQuantity(info); } catch { }
            int moved = after - before;
            if (moved > 0) SettlementStock.NoteRemovedForDisplay(id, moved);
        }

        // v1.5.1: ask the game to re-evaluate the craft button. Vanilla decides whether the button
        // is usable at the moment a recipe is selected and never revisits it, so the ingredients
        // arriving a fraction of a second later were invisible to it - the player had to click away
        // and back to force a fresh check (user, 2026-08-13). BuildPreviewTabPage.RefreshConfirmButton()
        // is the game's own re-check, public and zero-parameter, so this is the vanilla path being
        // invoked rather than any state being forged. Called only after a pull actually ran, not on
        // every tick.
        var page = _previewPage;
        if (page != null)
        {
            try
            {
                // Clear FIRST, then refresh. RefreshConfirmButton() re-enables the button but leaves
                // the earlier "not enough materials" line standing, so it rendered behind the button
                // with its text spilling past the edge (user, 2026-08-13). Clearing before the
                // refresh lets the game re-add whatever message is genuinely true now.
                page.ClearMessages();
                page.RefreshConfirmButton();
                if (!_refreshLogged)
                {
                    _refreshLogged = true;
                    Plugin.Logger.LogInfo("[CFS] [CFS-HC] RefreshConfirmButton() called after a pull - " +
                        "asking the game to re-check the craft button.");
                }
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError($"[CFS] [CFS-HC] RefreshConfirmButton failed: {ex}");
            }
        }
    }

    // ---- helpers ----

    private static bool SafeFlag(BepInEx.Configuration.ConfigEntry<bool>? entry, bool fallback)
    {
        try { return entry?.Value ?? fallback; } catch { return fallback; }
    }

    // v1.4.4: the selection lives on the PREVIEW PAGE, not on a details panel. Established by the
    // v1.4.3 enumeration dump, which is the first direct evidence rather than an inference:
    //   DUMP d3 active=True script=BuildPreviewTabPage
    //     path=BuildMenuReworked(Clone)/Panel/SupplyListPanel/BuildPreviewPage THUMB(item='5x Rope')
    //   DUMP d3 active=True script=BuildPreviewTabPage
    //     path=BuildMenuReworked(Clone)/Panel/SupplyListPanel/BuildPreviewPage THUMB(item='Stone Knife')
    // The node carries an ItemThumbnailPanel whose ItemInfo IS the selected recipe, and it tracks
    // the player's clicks.
    //
    // The ItemDetailsPanel this used to search for does exist, but it is the tooltip and it is
    // inactive and empty for the whole time the menu is open:
    //   DUMP d2 active=False script=ItemDetailsPanel
    //     path=BuildMenuReworked(Clone)/Panel/ItemDetailsPanel DETAILS(item='null')
    // That is why v1.4.0 through v1.4.3 found nothing.
    //
    // Two preview pages exist, 'BuildPreviewPage' and 'BuildPreviewPage Sec', and only one is active
    // at a time - hence the active check rather than taking the first match. The ingredient rows sit
    // BELOW this node under MaterialsDiv/MaterialsList and are deliberately not used as the source:
    // their quantities live in display text, whereas the selected recipe's own BlueprintInfo carries
    // them as numbers.
    private static ItemInfo? FindSelectedItemInfo(Transform root)
    {
        _nodesWalked = 0;
        _detailsPanelsSeen = 0;
        _detailsPanelsWithInfo = 0;
        _previewPage = null;
        return WalkForDetails(root, 0);
    }

    // Walks the ingredient rows under the active preview page and hands each to the shared row
    // rewriter. The v1.4.3 dump located them precisely:
    //   DUMP d6 active=True script=ItemThumbnailPanel
    //     path=.../BuildPreviewPage/MaterialsDiv/MaterialsList/ItemThumbnailMaterial_Medium(Clone)
    //     THUMB(item='Fibers')
    // Only ACTIVE rows are touched - the uncloned template sits inactive beside the real rows and
    // rewriting it would write into a hidden prefab instance.
    private static void RewriteMaterialRows(Transform t, int depth, bool diagnostics)
    {
        if (depth > WalkDepthCap) return;

        try
        {
            string name = "";
            try { name = t.gameObject.name ?? ""; } catch { }

            if (name.StartsWith("ItemThumbnailMaterial", StringComparison.Ordinal))
            {
                bool active = false;
                try { active = t.gameObject.activeInHierarchy; } catch { }
                if (active)
                {
                    var panel = t.gameObject.GetComponent<ItemThumbnailPanel>();
                    if (panel != null) CraftUiAvailability.PollApplyBuildMenu(panel, diagnostics);
                }
            }
        }
        catch { }

        int n;
        try { n = t.childCount; } catch { return; }
        for (int i = 0; i < n; i++)
        {
            Transform? c = null;
            try { c = t.GetChild(i); } catch { }
            if (c != null) RewriteMaterialRows(c, depth + 1, diagnostics);
        }
    }

    private static ItemInfo? WalkForDetails(Transform t, int depth)
    {
        if (depth > WalkDepthCap || _nodesWalked >= WalkNodeCap) return null;
        _nodesWalked++;

        try
        {
            // Singular generic only: the PLURAL GetComponentsInChildren<T>(bool) throws
            // MissingMethodException through the interop trampoline (project-wide gotcha).
            string nodeName = "";
            try { nodeName = t.gameObject.name ?? ""; } catch { }

            if (nodeName.StartsWith("BuildPreviewPage", StringComparison.Ordinal))
            {
                _detailsPanelsSeen++;

                bool active = false;
                try { active = t.gameObject.activeInHierarchy; } catch { }
                if (active)
                {
                    var panel = t.gameObject.GetComponent<ItemThumbnailPanel>();
                    if (panel != null)
                    {
                        ItemInfo? info = null;
                        try { info = panel.ItemInfo; } catch { }
                        if (info != null)
                        {
                            _detailsPanelsWithInfo++;
                            _selectionSourceNode = nodeName;
                            try { _previewPage = t.gameObject.GetComponent<BuildPreviewTabPage>(); }
                            catch { _previewPage = null; }
                            return info;
                        }
                    }
                }
            }
        }
        catch { }

        int n;
        try { n = t.childCount; } catch { return null; }
        for (int i = 0; i < n; i++)
        {
            Transform? c = null;
            try { c = t.GetChild(i); } catch { }
            if (c == null) continue;
            var found = WalkForDetails(c, depth + 1);
            if (found != null) return found;
        }
        return null;
    }

    // BlueprintInfo.parts is a public property, so the ingredient list is readable without calling
    // FillManifest (which takes a by-ref primitive) or any of the ItemCollection-taking tests.
    // A managed cast would lie for an interop object under a base declared type, so the type test is
    // a native class-name read followed by a pointer rewrap (project-wide gotcha).
    private static List<(ItemInfo info, int qty)> ReadBlueprintParts(ItemInfo selected)
    {
        var result = new List<(ItemInfo, int)>();
        try
        {
            BlueprintInfo? bpInfo = null;
            try
            {
                if ((object)selected is Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase b)
                {
                    string cls = Plugin.NativeClassName(selected);
                    if (cls.EndsWith("BlueprintInfo", StringComparison.Ordinal))
                        bpInfo = new BlueprintInfo(b.Pointer);
                }
            }
            catch { }
            if (bpInfo == null) return result;

            var parts = bpInfo.parts;
            if (parts == null) return result;

            foreach (var p in parts)
            {
                if (p == null) continue;
                ItemInfo? info = null; int qty = 0;
                try { info = p.itemInfo; qty = p.quantity; } catch { }
                if (info == null || qty <= 0) continue;
                result.Add((info, qty));
            }
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"[CFS] [CFS-HC] ReadBlueprintParts error: {ex}");
        }
        return result;
    }

    // PlayerInteractionAgent is a MonoBehaviour carrying GetInventory() : ItemCollection (Cecil
    // 2026-08-13). It is not guaranteed to sit on the same GameObject as the PlayerCharacter the
    // mod already tracks, so the lookup checks that object, then its ancestors, then its children -
    // the same shape the station-class lookup uses.
    private static ItemCollection? ResolvePlayerInventory()
    {
        PlayerCharacter? player = null;
        try { player = Plugin.LocalPlayer; } catch { }
        if (player == null) return null;

        Transform? t = null;
        try { t = player.transform; } catch { }
        if (t == null) return null;

        var agent = FindAgentOn(t) ?? FindAgentUp(t) ?? FindAgentDown(t, 0);
        if (agent == null) return null;

        try { return agent.GetInventory(); } catch { return null; }
    }

    private static PlayerInteractionAgent? FindAgentOn(Transform t)
    {
        try { return t.gameObject.GetComponent<PlayerInteractionAgent>(); } catch { return null; }
    }

    private static PlayerInteractionAgent? FindAgentUp(Transform t)
    {
        int guard = 0;
        Transform? cur = t;
        while (cur != null && guard++ < WalkDepthCap)
        {
            var found = FindAgentOn(cur);
            if (found != null) return found;
            try { cur = cur.parent; } catch { return null; }
        }
        return null;
    }

    private static PlayerInteractionAgent? FindAgentDown(Transform t, int depth)
    {
        if (depth > WalkDepthCap) return null;
        var here = FindAgentOn(t);
        if (here != null) return here;

        int n;
        try { n = t.childCount; } catch { return null; }
        for (int i = 0; i < n; i++)
        {
            Transform? c = null;
            try { c = t.GetChild(i); } catch { }
            if (c == null) continue;
            var found = FindAgentDown(c, depth + 1);
            if (found != null) return found;
        }
        return null;
    }

    private static string SafeInfoName(ItemInfo? info)
    {
        if (info == null) return "null";
        try { return info.Name ?? "unnamed"; } catch { return "unreadable"; }
    }
}
