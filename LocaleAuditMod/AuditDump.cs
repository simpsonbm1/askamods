using System;
using System.Collections.Generic;
using System.Text;
using SandSailorStudio.Inventory;
using SandSailorStudio.Localization;
using SSSGame;
using UnityEngine;

namespace LocaleAuditMod;

// The whole point of this mod: for every game entity our other mods currently identify by its
// TRANSLATED display string, print the LOCALE-INVARIANT identity sitting next to it.
//
// Invariant identity per entity (all Cecil-confirmed 2026-07-21 against the interop assembly):
//   ItemInfo / ItemCategoryInfo : derive AssetNode : ScriptableObject -> lowercase .name is the
//        asset name; .id is a stable int. Uppercase .Name resolves through LocalizationManager.
//   CreatureDataSheet           : derives ParameterList : AssetNode : ScriptableObject -> .name.
//   Structure                   : .Template (StructureTemplate : BlueprintInfo : AssetNode) -> .name,
//        plus .TemplateID; .DefaultName is translated, .gameObject.name is not.
//
// Reading lowercase .name off a ScriptableObject through interop is already proven in shipping code
// (OuthouseComposterMod/OuthouseGate.cs:257 reads info.storageClass.name), so this is an applied
// pattern rather than a new technique.
internal static class AuditDump
{
    // Every English token currently hardcoded or config-defaulted in the affected mods. The dump
    // resolves each one to its invariant replacement so the fix needs no manual cross-referencing.
    private static readonly (string Mod, string Setting, string Kind, string Token)[] ProbeTargets =
    {
        ("FishFilletMod",         "HarvestToolCategories", "category", "Knives"),
        ("OuthouseComposterMod",  "FoodCategoryMatch",     "category", "Food"),
        ("OuthouseComposterMod",  "CompostItemName",       "item",     "Compost"),
        ("JotunBloodYieldMod",    "(hardcoded)",           "item",     "Blood"),
        ("JotunBloodYieldMod",    "(hardcoded)",           "item",     "Jotun"),
        ("TreeRespawnMod",        "(mushroom filter)",     "item",     "Mushroom"),
        ("VillagerFightBackMod",  "FightBackAgainst",      "creature", "Wisp"),
    };

    public static void Run()
    {
        var log = Plugin.Logger;
        log.LogInfo("========== LOCALE AUDIT v" + MyPluginInfo.PLUGIN_VERSION + " BEGIN ==========");
        log.LogInfo("[lang] active language = " + ActiveLanguage());

        var items = CollectItems();
        // Source AND size, always together: a token that matches nothing is only evidence of the
        // locale bug if the table it was searched against is known-complete.
        log.LogInfo("[items] source=" + ItemSource + "  unique rows=" + items.Count);

        DumpTargets(items);
        DumpCategories(items);
        if (Plugin.DumpAllItems.Value) DumpItems(items);
        DumpStructures();
        DumpCreatures();

        log.LogInfo("========== LOCALE AUDIT END ==========");
    }

    // ---------------------------------------------------------------- items

    internal sealed class ItemRow
    {
        public int Id;
        public string Asset = "?";      // INVARIANT
        public string Loc = "?";        // translated
        public string CatAsset = "?";   // INVARIANT
        public int CatId = -1;
        public string CatLoc = "?";     // translated
        public string Storage = "?";    // INVARIANT (already used by the composter's seed gate)
    }

    // Which registry the item rows came from. Reported in the log because v0.2.0 silently produced
    // an 8-row table and the shortfall was only caught because the in-game hitch was too small:
    // ItemInfo.s_itemInfoList is NOT the item database, it is a small transient working list
    // (v0.2.0 run returned creature-butchering yields, with duplicates). The real registry is
    // ItemInfoDatabase.CompleteItemInfoList.itemInfoList. Never trust an item table whose source
    // and size are not printed next to it.
    internal static string ItemSource = "none";

    private static Il2CppSystem.Collections.Generic.List<ItemInfo>? ResolveItemRegistry()
    {
        // 1. The database component directly (MonoBehaviour -> FindAnyObjectByType; the SINGULAR
        //    form is the one that survives this interop layer).
        try
        {
            var db = UnityEngine.Object.FindAnyObjectByType<ItemInfoDatabase>();
            if (db != null)
            {
                var cil = db.CompleteItemInfoList;
                var l = cil != null ? cil.itemInfoList : null;
                if (l != null && l.Count > 0) { ItemSource = "ItemInfoDatabase.CompleteItemInfoList"; return l; }
            }
        }
        catch (Exception e) { Plugin.Logger.LogWarning("[items] ItemInfoDatabase probe failed: " + e.Message); }

        // 2. Same database reached through its manager.
        try
        {
            var mgr = UnityEngine.Object.FindAnyObjectByType<ItemDatabaseManager>();
            var db = mgr != null ? mgr.Database : null;
            if (db != null)
            {
                var cil = db.CompleteItemInfoList;
                var l = cil != null ? cil.itemInfoList : null;
                if (l != null && l.Count > 0) { ItemSource = "ItemDatabaseManager.Database"; return l; }
            }
        }
        catch (Exception e) { Plugin.Logger.LogWarning("[items] ItemDatabaseManager probe failed: " + e.Message); }

        // 3. Last resort - the transient list v0.2.0 used. Kept ONLY so a total failure still
        //    prints something, and it is labelled so the rows are never mistaken for the database.
        try
        {
            var l = ItemInfo.s_itemInfoList;
            if (l != null && l.Count > 0) { ItemSource = "ItemInfo.s_itemInfoList (TRANSIENT - NOT the database)"; return l; }
        }
        catch { }

        return null;
    }

    private static List<ItemRow> CollectItems()
    {
        var rows = new List<ItemRow>();
        var seenIds = new HashSet<int>(); // the transient list repeats entries; the database may too
        try
        {
            var list = ResolveItemRegistry();
            if (list == null)
            {
                Plugin.Logger.LogWarning("[items] no item registry resolved - item table unavailable.");
                return rows;
            }

            int n = list.Count; // interop List: Count + indexer, never foreach
            for (int i = 0; i < n; i++)
            {
                ItemInfo? info = null;
                try { info = list[i]; } catch { }
                if (info == null) continue;

                var row = new ItemRow();
                try { row.Id = info.id; } catch { }
                if (!seenIds.Add(row.Id)) continue;
                try { row.Asset = Safe(info.name); } catch { }
                try { row.Loc = Safe(info.Name); } catch { }
                try
                {
                    var cat = info.category;
                    if (cat != null)
                    {
                        try { row.CatAsset = Safe(cat.name); } catch { }
                        try { row.CatId = cat.id; } catch { }
                        try { row.CatLoc = Safe(cat.Name); } catch { }
                    }
                }
                catch { }
                try { var sc = info.storageClass; if (sc != null) row.Storage = Safe(sc.name); } catch { }
                rows.Add(row);
            }
        }
        catch (Exception e) { Plugin.Logger.LogWarning("[items] collect failed: " + e.Message); }
        return rows;
    }

    private static void DumpItems(List<ItemRow> rows)
    {
        Plugin.Logger.LogInfo("--- SECTION: ALL ITEMS (n=" + rows.Count + ") ---");
        foreach (var r in rows)
        {
            string line = "[item] id=" + r.Id
                        + " asset='" + r.Asset + "'"
                        + " loc='" + r.Loc + "'"
                        + " cat.asset='" + r.CatAsset + "'"
                        + " cat.id=" + r.CatId
                        + " cat.loc='" + r.CatLoc + "'"
                        + " storage='" + r.Storage + "'";
            Plugin.Logger.LogInfo(line);
        }
    }

    // Deduped category table - far shorter than the item list and the thing most gates key on.
    private static void DumpCategories(List<ItemRow> rows)
    {
        var seen = new Dictionary<string, ItemRow>();
        foreach (var r in rows)
        {
            string key = r.CatId + "|" + r.CatAsset;
            if (!seen.ContainsKey(key)) seen[key] = r;
        }
        Plugin.Logger.LogInfo("--- SECTION: CATEGORIES (deduped, n=" + seen.Count + ") ---");
        foreach (var kv in seen)
        {
            var r = kv.Value;
            Plugin.Logger.LogInfo("[cat] id=" + r.CatId + " asset='" + r.CatAsset + "' loc='" + r.CatLoc + "'");
        }
    }

    // ---------------------------------------------------------------- targets

    // For each English token the affected mods match on today, show what it currently matches by
    // TRANSLATED name and what the INVARIANT replacement is. In a non-English run the "matched"
    // count is expected to be 0 for exactly the tokens that are broken - which is itself the
    // confirmation that the diagnosis is right.
    private static void DumpTargets(List<ItemRow> rows)
    {
        Plugin.Logger.LogInfo("--- SECTION: TARGETS (English token -> invariant replacement) ---");
        foreach (var t in ProbeTargets)
        {
            if (t.Kind == "creature")
            {
                Plugin.Logger.LogInfo("[target] " + t.Mod + " " + t.Setting + "=\"" + t.Token
                                      + "\" (creature) -> see CREATURES section for sheet.asset values");
                continue;
            }

            int hits = 0;
            var sb = new StringBuilder();
            foreach (var r in rows)
            {
                string hay = t.Kind == "category" ? r.CatLoc : r.Loc;
                if (string.IsNullOrEmpty(hay) || hay == "?") continue;
                if (hay.IndexOf(t.Token, StringComparison.OrdinalIgnoreCase) < 0) continue;

                hits++;
                if (hits <= Plugin.MaxTargetExamples.Value)
                {
                    if (t.Kind == "category")
                        sb.Append("\n    -> cat.loc='").Append(r.CatLoc).Append("' cat.asset='")
                          .Append(r.CatAsset).Append("' cat.id=").Append(r.CatId);
                    else
                        sb.Append("\n    -> loc='").Append(r.Loc).Append("' asset='").Append(r.Asset)
                          .Append("' id=").Append(r.Id).Append(" storage='").Append(r.Storage).Append('\'');
                }
            }

            string head = "[target] " + t.Mod + " " + t.Setting + "=\"" + t.Token + "\" ("
                          + t.Kind + ") matched=" + hits;
            // A zero here has TWO possible causes and they must not be conflated: the token really
            // is untranslatable in this language, OR the registry above was incomplete. v0.2.0
            // reported zero for "Knives"/"Compost" in an ENGLISH run purely because its table held
            // 8 rows. Name the source so the reader resolves the ambiguity instead of guessing.
            if (hits == 0) head += "  <<< ZERO - interpret against source=" + ItemSource;
            Plugin.Logger.LogInfo(head + sb.ToString());
        }
    }

    // ---------------------------------------------------------------- structures

    private static void DumpStructures()
    {
        try
        {
            SettlementManager? sm = null;
            try { sm = UnityEngine.Object.FindAnyObjectByType<SettlementManager>(); } catch { }
            if (sm == null)
            {
                Plugin.Logger.LogInfo("--- SECTION: STRUCTURES - no SettlementManager (not in a world?) ---");
                return;
            }

            // SettlementManager.settlements stays null even in a loaded world (project gotcha) -
            // resolve through the three accessors instead.
            Settlement? st = null;
            try { st = sm.GetPlayerSettlement(); } catch { }
            if (st == null) { try { st = sm.GetCurrentSettlement(); } catch { } }
            if (st == null) { try { st = sm.worldSettlement; } catch { } }
            if (st == null)
            {
                Plugin.Logger.LogInfo("--- SECTION: STRUCTURES - no settlement resolved ---");
                return;
            }

            var list = st.GetStructures();
            if (list == null) { try { list = st._structures; } catch { } }
            if (list == null)
            {
                Plugin.Logger.LogInfo("--- SECTION: STRUCTURES - structure list unavailable ---");
                return;
            }

            int n = list.Count;
            Plugin.Logger.LogInfo("--- SECTION: STRUCTURES (n=" + n + ") ---");
            var seen = new HashSet<string>();
            for (int i = 0; i < n; i++)
            {
                Structure? s = null;
                try { s = list[i]; } catch { }
                if (s == null) continue;

                string tmplAsset = "?", tmplLoc = "?", def = "?", custom = "?", go = "?";
                int tmplId = -1;
                try { tmplId = s.TemplateID; } catch { }
                try
                {
                    var tmpl = s.Template;
                    if (tmpl != null)
                    {
                        try { tmplAsset = Safe(tmpl.name); } catch { }
                        try { tmplLoc = Safe(tmpl.Name); } catch { }
                    }
                }
                catch { }
                try { def = Safe(s.DefaultName); } catch { }
                try { custom = Safe(s.StructureName); } catch { }
                try { if (s.gameObject != null) go = Safe(s.gameObject.name); } catch { }

                // One line per TEMPLATE, not per placed building - 40 huts would otherwise bury it.
                string key = tmplId + "|" + tmplAsset;
                if (!seen.Add(key)) continue;

                Plugin.Logger.LogInfo("[struct] tmplId=" + tmplId + " tmpl.asset='" + tmplAsset
                                      + "' tmpl.loc='" + tmplLoc + "' default='" + def
                                      + "' custom='" + custom + "' go='" + go + "'");
            }
        }
        catch (Exception e) { Plugin.Logger.LogWarning("[struct] dump failed: " + e.Message); }
    }

    // ---------------------------------------------------------------- creatures

    private static void DumpCreatures()
    {
        var rows = CreatureCapture.Snapshot();
        int fires = CreatureCapture.Fires;
        Plugin.Logger.LogInfo("--- SECTION: CREATURES (unique n=" + rows.Count + ", Spawned fires="
                              + fires + ", sheet-read failures=" + CreatureCapture.NoSheet + ") ---");
        if (rows.Count == 0)
        {
            // Fire count disambiguates the three reasons this can be empty.
            if (!Plugin.CaptureCreatures.Value)
                Plugin.Logger.LogInfo("[creature] CaptureCreatures=false - patch was never wired.");
            else if (fires == 0)
                Plugin.Logger.LogInfo("[creature] PATCH NEVER FIRED - Creature.Spawned was not hit. "
                                      + "Either no creature spawned since load, or the patch target is "
                                      + "wrong/inlined. Walk past a wisp and re-press; if it still reads "
                                      + "0 the target needs changing, not more walking.");
            else
                Plugin.Logger.LogInfo("[creature] patch fired " + fires + "x but no datasheet was "
                                      + "readable - dataSheet is null or its members threw.");
            return;
        }
        foreach (var r in rows)
            Plugin.Logger.LogInfo("[creature] sheet.asset='" + r.Asset + "' sheet.loc='" + r.Loc
                                  + "' faction=" + r.Faction);
    }

    // ---------------------------------------------------------------- util

    private static string ActiveLanguage()
    {
        try
        {
            var lm = LocalizationManager.Instance;
            if (lm == null) return "?(no LocalizationManager)";
            return lm.language.ToString();
        }
        catch (Exception e) { return "?(" + e.Message + ")"; }
    }

    private static string Safe(string? s) => string.IsNullOrEmpty(s) ? "?" : s!;
}
