using BepInEx;
using BepInEx.Configuration;
using BepInEx.Unity.IL2CPP;
using BepInEx.Logging;
using HarmonyLib;
using SSSGame;
using UnityEngine;

namespace TerrainLevelerMod
{
    [BepInPlugin(PLUGIN_GUID, PLUGIN_NAME, PLUGIN_VERSION)]
    public class Plugin : BasePlugin
    {
        public const string PLUGIN_GUID = "com.askamods.terrainleveler";
        public const string PLUGIN_NAME = "TerrainLevelerMod";
        public const string PLUGIN_VERSION = "1.4.8";

        // Stable identity of the injected "Bulldozer Field" template. Saves reference placed
        // structures by this id, so it must NEVER change once shipped.
        public const int BulldozerTemplateId = 919191001;
        public const string BulldozerAssetName = "TerrainLevelField_Bulldozer";

        // Display strings for the bulldozer square, injected into the game's localization store
        // (the raw localizedName/localizedDescription fields are ignored by the UI — it always
        // resolves through LocalizationManager, key format confirmed in-game 2026-07-01:
        // 'item.Blueprints_TerrainLevelField_Bulldozer_name').
        private const string BulldozerDisplayName = "Bulldozer Field";
        private const string BulldozerDisplayDesc = "Marks an area to be instantly flattened and cleared of trees and rocks in a single strike.";
        private const string BulldozerDisplayLore = "Why swing a hoe a hundred times when the ground can simply be told to obey?";

        public static ConfigEntry<float> MaxDragRange;
        public static ConfigEntry<bool> OneHitClear;
        public static ConfigEntry<bool> ClearObstructions;
        public static ConfigEntry<int> BombShots;
        public static ConfigEntry<float> ClearVerticalRange;
        public static ConfigEntry<bool> ClearDiagnostics;
        public static ConfigEntry<float> MaxHeightDifference;
        public static ConfigEntry<bool> PlacementDiagnostics;
        public static ManualLogSource ModLogger;

        // Throttle for the ChangeGridSize diag log so a multi-second drag doesn't flood the log.
        private static long _lastLoggedGridTiles = -1;

        // Grids whose obstructions have already been cleared, so we only sweep once per grid
        // (keyed by Unity instance id). Cleared-per-grid; the flatten loop still runs every hit.
        private static readonly System.Collections.Generic.HashSet<int> _clearedGrids = new System.Collections.Generic.HashSet<int>();

        public override void Load()
        {
            ModLogger = Log;
            MaxDragRange = Config.Bind("General", "MaxDragRange", 20f, "Maximum drag distance for the terraforming tool (vanilla is 10)");
            OneHitClear = Config.Bind("General", "OneHitClear", true, "If true, a single hit fully flattens every tile in the grid (loops each tile to completion instead of one increment per hit)");

            ClearObstructions = Config.Bind("Obstructions", "ClearObstructions", true, "If true, the first hit on a grid detonates a bomb-style area blast over it to destroy trees/rocks/props before flattening.");
            BombShots = Config.Bind("Obstructions", "BombShots", 2, "How many blasts to detonate over the grid. 2 mirrors 'two bombs': the first knocks trees to stumps, the second clears the stumps and rocks.");
            ClearVerticalRange = Config.Bind("Obstructions", "ClearVerticalRange", 30f, "Half-height (m) of the box searched for obstructions above/below the grid. Raise if tall trees/rocks near the grid aren't detected.");
            ClearDiagnostics = Config.Bind("Obstructions", "ClearDiagnostics", false, "Log details of the flatten + obstacle-clearing blast ([Flatten]/[Bomb] lines). Off by default; enable to debug.");
            MaxHeightDifference = Config.Bind("General", "MaxHeightDifference", 15f, "Maximum height difference before the grid turns red and is unplaceable. Native is ~2m, 15m is a safe limit before mesh NaN crashes.");
            // Feature confirmed in-game 2026-07-01 (v1.4.7) — diagnostics ship disabled per the rule.
            PlacementDiagnostics = Config.Bind("General", "PlacementDiagnostics", false, "Log placement guards (Begin/ChangeGridSize/snap/dynamic), template identities at Use, and bulldozer menu-entry injection/grant steps. Enable to debug placement or the bulldozer menu entry.");

            Harmony.CreateAndPatchAll(typeof(Plugin));
            Log.LogInfo($"Plugin {PLUGIN_GUID} is loaded!");
        }

        // ---- Per-template behavior gating (BULLDOZER_UI_PLAN.md Phase 2) ----------------------
        // True while the current drag is placing OUR bulldozer field — set in Begin from the
        // preview's template id, cleared in End. Placement-time patches that can't reach a template
        // (the preview has no placed Structure yet) key off this flag.
        private static bool _bulldozerDrag;

        // The placement tool instance persists across drags, so after a bulldozer drag mutates
        // maxRange/maxHeightDifference the next VANILLA drag must get the native values back.
        // Captured on the first Begin, before anything is mutated.
        private static float _vanillaMaxRange = -1f;
        private static float _vanillaMaxHeightDifference = -1f;

        // Identify a drag's template from Begin's preview param. The param is declared as the base
        // StructurePreviewData, and managed `as` casts LIE for interop objects materialized under a
        // base declared type — check the native class name, then wrap via the IntPtr ctor.
        private static bool IsBulldozerPreview(StructurePreviewData previewData)
        {
            try
            {
                if (previewData == null) return false;
                if (GetNativeClassName(previewData) != "DynamicDimensionStructurePreviewData") return false;
                var dd = new DynamicDimensionTemplate.DynamicDimensionStructurePreviewData(previewData.Pointer);
                var tpl = dd.DynamicTemplate;
                return tpl != null && tpl.id == BulldozerTemplateId;
            }
            catch (System.Exception e)
            {
                ModLogger.LogError($"[Diag] IsBulldozerPreview failed: {e}");
                return false;
            }
        }

        // Template-id test for a live grid: the owning Structure (in the grid's parents) carries
        // TemplateID once placed; during a drag preview there is no structure yet, so fall back to
        // the drag flag.
        private static bool IsBulldozerGrid(TerraformingGrid grid)
        {
            try
            {
                var st = grid != null ? grid.GetComponentInParent<Structure>() : null;
                if (st != null) return st.TemplateID == BulldozerTemplateId;
            }
            catch { }
            return _bulldozerDrag;
        }

        [HarmonyPatch(typeof(DynamicDimensionsPlacementTool), nameof(DynamicDimensionsPlacementTool.Begin))]
        [HarmonyPrefix]
        public static void DynamicDimensionsPlacementTool_Begin_Prefix(DynamicDimensionsPlacementTool __instance, StructurePreviewData previewData)
        {
            if (_vanillaMaxRange < 0f)
            {
                try { _vanillaMaxRange = __instance.maxRange; _vanillaMaxHeightDifference = __instance.maxHeightDifference; } catch { }
            }

            _bulldozerDrag = IsBulldozerPreview(previewData);
            if (_bulldozerDrag)
            {
                // Soft UX cap on how far the marker follows the player during the drag. This is NOT the
                // crash fix -- that's the tile-count clamp on ChangeGridSize below. The real cap is the
                // BitSet256 network-state struct capacity (confirmed via Cpp2IL decompile + WER crash-offset
                // mapping; see DRAG_CRASH_PLAN.md), which is independent of how far the marker can travel.
                __instance.maxRange = MaxDragRange.Value;

                // Raise the height-difference limit so the native physics don't block placement.
                __instance.maxHeightDifference = 10000f;
            }
            else if (_vanillaMaxRange > 0f)
            {
                __instance.maxRange = _vanillaMaxRange;
                __instance.maxHeightDifference = _vanillaMaxHeightDifference;
            }

            _lastLoggedGridTiles = -1; // reset diag throttle for a fresh placement
            if (PlacementDiagnostics.Value) ModLogger.LogInfo($"[Diag] Begin fired; bulldozer={_bulldozerDrag} maxRange={__instance.maxRange} maxHeightDiff={__instance.maxHeightDifference}");
        }

        [HarmonyPatch(typeof(DynamicDimensionsPlacementTool), nameof(DynamicDimensionsPlacementTool.End))]
        [HarmonyPostfix]
        public static void DynamicDimensionsPlacementTool_End_Postfix()
        {
            // Belt-and-suspenders: Begin re-evaluates the flag per drag anyway, but clear it so
            // grid-side fallbacks (IsBulldozerGrid) don't see a stale true between drags.
            _bulldozerDrag = false;
        }

        [HarmonyPatch(typeof(DynamicDimensionsPlacementTool), nameof(DynamicDimensionsPlacementTool._ValidateFootprint))]
        [HarmonyPrefix]
        public static bool DynamicDimensionsPlacementTool_ValidateFootprint_Prefix(ref bool __result)
        {
            // Vanilla drags keep native footprint validation (red-when-obstructed). Bulldozer drags
            // bypass it: native _ValidateFootprint uses a fixed-size OverlapBoxNonAlloc buffer that
            // overflows and hard-crashes on a large grid placed over dense obstacles — and returning
            // true is also what lets the bulldozer place over trees/rocks for the bomb pass to clear.
            // Vanilla grids cap at 25 tiles, far below the overflow point, so they're safe natively.
            if (!_bulldozerDrag) return true;
            __result = true;
            return false;
        }

        [HarmonyPatch(typeof(TerraformingGrid), nameof(TerraformingGrid.CheckLevelDifference))]
        [HarmonyPrefix]
        public static bool TerraformingGrid_CheckLevelDifference_Prefix(TerraformingGrid __instance, ref bool __result)
        {
            // Only bulldozer grids skip the native level-difference rejection; vanilla fields keep
            // their native slope limits.
            if (!IsBulldozerGrid(__instance)) return true;
            __result = true;
            return false;
        }

        // Raises the native height-difference limit every frame so vanilla physics don't block placement
        // on undulating terrain. The old firstMarker/_marker raycast-skip and tether logic that used to
        // live here (and in a since-deleted _OnBuildingDimensionsChanged patch) was a dead end: firstMarker
        // sits ~580m from the live marker throughout the whole drag (confirmed via diag logs), so every
        // clamp built on it measured a stale, unrelated distance and never bounded grid growth -- and the
        // raycast-skip approach hid the preview entirely (skipped the original too eagerly, every frame).
        // The real crash cause is a fixed-size Fusion network struct (BitSet256/512) that the drag
        // overflows past 256 tiles; the actual fix is the tile-count clamp below, at the source
        // (ChangeGridSize) -- see DRAG_CRASH_PLAN.md for the full evidence chain.
        [HarmonyPatch(typeof(DynamicDimensionsPlacementTool), nameof(DynamicDimensionsPlacementTool._OnBuildRayChanged))]
        [HarmonyPrefix]
        public static void DynamicDimensionsPlacementTool_OnBuildRayChanged_Prefix(DynamicDimensionsPlacementTool __instance)
        {
            if (_bulldozerDrag) __instance.maxHeightDifference = 10000f;
        }

        // Guards ChangeGridSize against recursive re-entry from our own clamped re-call below.
        private static bool _inChangeGridSize;

        // THE ACTUAL FIX. The drag preview's per-tile validity is stored in a FIXED-SIZE Fusion network
        // struct: BitSet256 on NetworkDynamicDimensionBuildingState_256 (the variant this structure uses),
        // BitSet512 on the _512 variant used by other building types. Past that many tiles,
        // UpdateGridValidityOnNetwork writes validity bits past the struct and corrupts the network state,
        // which hard-crashes the game (confirmed by mapping WER crash offsets to these exact methods via
        // Cpp2IL -- see DRAG_CRASH_PLAN.md). ChangeGridSize(Vector3Int) is the single non-virtual,
        // by-value entry point every drag frame funnels through before that struct is touched, so we clamp
        // the tile count here, at the source, instead of guessing at marker/distance math further up the
        // call chain (every attempt at that was a dead end -- see comment above).
        [HarmonyPatch(typeof(SSSGame.Network.NetworkDynamicDimensionBuildingState), nameof(SSSGame.Network.NetworkDynamicDimensionBuildingState.ChangeGridSize))]
        [HarmonyPrefix]
        public static bool NetworkDynamicDimensionBuildingState_ChangeGridSize_Prefix(SSSGame.Network.NetworkDynamicDimensionBuildingState __instance, Vector3Int gridSize)
        {
            if (_inChangeGridSize) return true;

            int cap = 256;
            try { cap = __instance.NetworkedGridSize; } catch { }
            if (cap <= 0) cap = 256;

            long tiles = (long)gridSize.x * gridSize.z;
            bool diag = PlacementDiagnostics.Value;

            if (tiles <= cap)
            {
                if (diag && tiles != _lastLoggedGridTiles)
                {
                    _lastLoggedGridTiles = tiles;
                    ModLogger.LogInfo($"[Diag:ChangeGridSize] size={gridSize.x}x{gridSize.z} tiles={tiles} cap={cap} OK");
                }
                return true;
            }

            // Over cap: shrink the dominant axis one tile at a time until it fits, preserving the drag's
            // aspect/direction rather than snapping to a fixed shape.
            var c = gridSize;
            while ((long)c.x * c.z > cap)
            {
                if (c.x >= c.z) c.x--; else c.z--;
                if (c.x < 1) c.x = 1;
                if (c.z < 1) c.z = 1;
                if ((long)c.x * c.z <= 1) break; // safety valve; should never trigger
            }

            long clampedTiles = (long)c.x * c.z;
            if (diag && clampedTiles != _lastLoggedGridTiles)
            {
                _lastLoggedGridTiles = clampedTiles;
                ModLogger.LogWarning($"[Diag:ChangeGridSize] size={gridSize.x}x{gridSize.z} tiles={tiles} EXCEEDS cap={cap}; clamped to {c.x}x{c.z} ({clampedTiles} tiles)");
            }

            _inChangeGridSize = true;
            try { __instance.ChangeGridSize(c); }
            finally { _inChangeGridSize = false; }
            return false; // skip the oversized original call
        }

        // Safety net for the clamp above: if the backing grid somehow still exceeds this variant's fixed
        // BitSet capacity when the game is about to write per-tile validity bits into it, skip the write
        // instead of corrupting past the struct. DO NOT patch CopyBackingFieldsToState/
        // CopyStateToBackingFields on these types -- that hangs the game at load (learned the hard way,
        // same rule as SpellsManager).
        [HarmonyPatch(typeof(SSSGame.Network.NetworkDynamicDimensionBuildingState_256), nameof(SSSGame.Network.NetworkDynamicDimensionBuildingState_256.UpdateGridValidityOnNetwork))]
        [HarmonyPrefix]
        public static bool NetworkDynamicDimensionBuildingState_256_UpdateGridValidityOnNetwork_Prefix(SSSGame.Network.NetworkDynamicDimensionBuildingState_256 __instance)
        {
            return SafeToUpdateGridValidity(__instance, 256);
        }

        [HarmonyPatch(typeof(SSSGame.Network.NetworkDynamicDimensionBuildingState_512), nameof(SSSGame.Network.NetworkDynamicDimensionBuildingState_512.UpdateGridValidityOnNetwork))]
        [HarmonyPrefix]
        public static bool NetworkDynamicDimensionBuildingState_512_UpdateGridValidityOnNetwork_Prefix(SSSGame.Network.NetworkDynamicDimensionBuildingState_512 __instance)
        {
            return SafeToUpdateGridValidity(__instance, 512);
        }

        private static bool SafeToUpdateGridValidity(SSSGame.Network.NetworkDynamicDimensionBuildingState state, int cap)
        {
            try
            {
                var building = state._building;
                if (building == null) return true;
                var size = building.GridSize;
                long tiles = (long)size.x * size.z;
                if (tiles > cap)
                {
                    if (PlacementDiagnostics.Value)
                        ModLogger.LogWarning($"[Diag:UpdateGridValidity] blocked OOB write: {size.x}x{size.z}={tiles} tiles > cap={cap}");
                    return false;
                }
            }
            catch { }
            return true;
        }

        // NOTE: the old CreatePreview/Create prefixes that forced maxNumberOfTiles=256 are GONE on
        // purpose — they mutated the SHARED vanilla asset for the whole session, which is why the
        // vanilla square could also drag past its native 25-tile cap. The 256 cap now lives on the
        // clone only, set once at injection (see ItemInfoDatabase_Initialize_Prefix).

        [HarmonyPatch(typeof(TerraformingGrid), nameof(TerraformingGrid.OnDynamicBuildingDimensionsChanged))]
        [HarmonyPrefix]
        public static bool TerraformingGrid_OnDynamicBuildingDimensionsChanged_Prefix(TerraformingGrid __instance)
        {
            // Belt-and-suspenders backstop at the placed-structure layer. The real fix + cap is at the
            // BitSet256 network-state layer via ChangeGridSize (above); this just freezes the visual
            // update if a grid somehow still exceeds 256 tiles by the time it reaches this layer.
            int cells = __instance.GetCellsCount();
            if (PlacementDiagnostics.Value && cells >= 150 && cells % 25 == 0)
                ModLogger.LogInfo($"[Diag] OnDynamicBuildingDimensionsChanged: cells={cells} frozen={cells > 256}");
            if (cells > 256)
            {
                return false;
            }
            return true;
        }

        [HarmonyPatch(typeof(DynamicDimensionsPlacementTool), nameof(DynamicDimensionsPlacementTool._OnSnap))]
        [HarmonyPrefix]
        public static bool DynamicDimensionsPlacementTool_OnSnap_Prefix(DynamicDimensionsPlacementTool __instance)
        {
            if (__instance._dynamicPlacement == null) return true;
            var gridSize = __instance._dynamicPlacement.gridSize;
            if (PlacementDiagnostics.Value)
                ModLogger.LogInfo($"[Diag] OnSnap: gridSize={gridSize.x}x{gridSize.z} product={gridSize.x * gridSize.z} blocked={(gridSize.x * gridSize.z) > 256}");
            if ((gridSize.x * gridSize.z) > 256)
            {
                ModLogger.LogWarning("Grid exceeds maximum networked capacity (256 tiles). Placement blocked.");
                return false;
            }
            return true;
        }

        // ---- Bulldozer build-menu entry (see BULLDOZER_UI_PLAN.md) ----------------------------
        //
        // The "Terrain Level Field" build-menu square is a DynamicDimensionTemplate ScriptableObject,
        // which is an ItemInfo (NodeTemplate -> StructureTemplate -> BlueprintInfo -> ItemInfo).
        // ItemInfoDatabase.Initialize() builds its id->info and category->infos maps from the plain
        // list in CompleteItemInfoList, so a clone appended in a PREFIX gets registered everywhere by
        // the game's own code. The clone keeps the vanilla category (-> TERRAFORMING tab), icon and
        // result structure; only its asset name, id, databaseIndex and display name differ.

        // Session references, re-resolved on every Initialize (world reloads recreate the database).
        private static DynamicDimensionTemplate _bulldozerTemplate;
        private static DynamicDimensionTemplate _vanillaFieldTemplate;
        private static SandSailorStudio.Inventory.ItemInfoDatabase _itemDb;
        private static bool _dbAudited;

        [HarmonyPatch(typeof(SandSailorStudio.Inventory.ItemInfoDatabase), nameof(SandSailorStudio.Inventory.ItemInfoDatabase.Initialize))]
        [HarmonyPrefix]
        public static void ItemInfoDatabase_Initialize_Prefix(SandSailorStudio.Inventory.ItemInfoDatabase __instance)
        {
            bool diag = PlacementDiagnostics.Value;
            _itemDb = __instance;
            _dbAudited = false; // re-audit after every database (re)build
            try
            {
                var infoList = __instance.CompleteItemInfoList;
                var items = infoList != null ? infoList.itemInfoList : null;
                if (items == null) { ModLogger.LogWarning("[Bulldozer] CompleteItemInfoList empty; cannot inject."); return; }

                // Identify by ASSET NAME, not managed casts: interop materializes list entries as
                // plain ItemInfo wrappers, so `info as DynamicDimensionTemplate` is ALWAYS null here
                // (confirmed in-game 2026-07-01 — the v1.4.0 scan found nothing while the template
                // demonstrably existed). Once matched by name, build the typed wrapper from the
                // native pointer — the underlying il2cpp object really is a DynamicDimensionTemplate.
                const string vanillaAssetName = "Item_Structures_TerrainLevelField";
                DynamicDimensionTemplate original = null;
                bool idCollision = false, alreadyInjected = false;
                int count = items.Count;
                for (int i = 0; i < count; i++)
                {
                    var info = items[i];
                    if (info == null) continue;
                    if (info.id == BulldozerTemplateId)
                    {
                        if (info.name == BulldozerAssetName) { alreadyInjected = true; _bulldozerTemplate = new DynamicDimensionTemplate(info.Pointer); }
                        else idCollision = true;
                        continue;
                    }
                    string assetName = info.name;
                    if (assetName != vanillaAssetName) continue;
                    original = new DynamicDimensionTemplate(info.Pointer);
                    if (diag) ModLogger.LogInfo($"[Bulldozer] found vanilla template: name='{assetName}' id={original.id} dbIndex={original.databaseIndex} maxTiles={original.maxNumberOfTiles} (list count={count})");
                }

                if (idCollision) { ModLogger.LogError($"[Bulldozer] id {BulldozerTemplateId} already used by a vanilla item; injection aborted."); return; }
                if (alreadyInjected)
                {
                    _vanillaFieldTemplate = original;
                    try { _bulldozerTemplate.maxNumberOfTiles = 256; } catch { }
                    InjectLocalizedStrings(SandSailorStudio.Localization.LocalizationManager.Instance, "re-Initialize");
                    if (diag) ModLogger.LogInfo("[Bulldozer] clone already present in list; skipping re-inject.");
                    return;
                }
                if (original == null) { ModLogger.LogWarning($"[Bulldozer] '{vanillaAssetName}' not found in item list (count={count}); cannot inject."); return; }

                var cloneObj = UnityEngine.Object.Instantiate(original);
                if (cloneObj == null) { ModLogger.LogError("[Bulldozer] Instantiate returned null; injection aborted."); return; }
                var clone = new DynamicDimensionTemplate(cloneObj.Pointer);

                clone.name = BulldozerAssetName;
                clone.id = BulldozerTemplateId;
                clone.databaseIndex = (ushort)count; // its position after append
                // Raw (non-localized) display strings; if these render blank in-game, fall back to
                // leaving Localized=true (clone then displays the vanilla name).
                clone.Localized = false;
                clone.localizedName = "Bulldozer Field";
                clone.localizedDescription = "Marks an area to be instantly flattened and cleared of obstacles in a single strike.";
                clone.showUnlockNotification = false;
                // 256 = the real Fusion BitSet256 network capacity for this structure (see the
                // ChangeGridSize clamp). Set on the CLONE only; the vanilla asset keeps its native 25.
                clone.maxNumberOfTiles = 256;
                // icon/previewImage/category/result structure: inherited from vanilla on purpose.

                items.Add(clone);
                _bulldozerTemplate = clone;
                _vanillaFieldTemplate = original;
                ModLogger.LogInfo($"[Bulldozer] injected '{BulldozerAssetName}' id={BulldozerTemplateId} dbIndex={count} (cloned from '{original.name}' id={original.id}).");
                InjectLocalizedStrings(SandSailorStudio.Localization.LocalizationManager.Instance, "template injection");
            }
            catch (System.Exception e) { ModLogger.LogError($"[Bulldozer] injection failed: {e}"); }
        }

        // ---- Localized display strings for the bulldozer square ------------------------------
        // The build menu resolves item.<key>_name/_desc/_lore through LocalizationManager's
        // per-language Dictionary<string,string>; our injected template has no entries there, so
        // the UI showed raw keys (confirmed in-game 2026-07-01). Inject our strings into
        // _localizedStrings + _localizedFallbackStrings (and _allKeys, so KeyExists agrees)
        // whenever both the manager and the clone exist. Idempotent; re-runs after SetLanguage
        // (language switches rebuild the dictionaries).
        private static void InjectLocalizedStrings(SandSailorStudio.Localization.LocalizationManager lm, string source)
        {
            try
            {
                if (lm == null || _bulldozerTemplate == null) return;
                int added = 0;
                // Tokens: "name" is confirmed; the desc token spelling is unverified, so cover both
                // "desc" and "description" — unused extra dictionary entries are harmless.
                added += SetLocEntry(lm, _bulldozerTemplate.GetKey("name"), BulldozerDisplayName) ? 1 : 0;
                added += SetLocEntry(lm, _bulldozerTemplate.GetKey("desc"), BulldozerDisplayDesc) ? 1 : 0;
                added += SetLocEntry(lm, _bulldozerTemplate.GetKey("description"), BulldozerDisplayDesc) ? 1 : 0;
                added += SetLocEntry(lm, _bulldozerTemplate.GetKey("lore"), BulldozerDisplayLore) ? 1 : 0;
                if (added > 0) ModLogger.LogInfo($"[Bulldozer] injected {added} localized strings via {source}.");
            }
            catch (System.Exception e) { ModLogger.LogError($"[Bulldozer] loc injection ({source}) failed: {e}"); }
        }

        private static bool SetLocEntry(SandSailorStudio.Localization.LocalizationManager lm, string key, string value)
        {
            if (string.IsNullOrEmpty(key)) return false;
            bool changed = false;
            try
            {
                var main = lm._localizedStrings;
                if (main != null && !main.ContainsKey(key)) { main[key] = value; changed = true; }
            }
            catch { }
            try
            {
                var fb = lm._localizedFallbackStrings;
                if (fb != null && !fb.ContainsKey(key)) fb[key] = value;
            }
            catch { }
            try
            {
                // _allKeys is a STATIC member on the manager type.
                var all = SandSailorStudio.Localization.LocalizationManager._allKeys;
                if (all != null) all.Add(key);
            }
            catch { }
            return changed;
        }

        [HarmonyPatch(typeof(SandSailorStudio.Localization.LocalizationManager), nameof(SandSailorStudio.Localization.LocalizationManager.SetLanguage))]
        [HarmonyPostfix]
        public static void LocalizationManager_SetLanguage_Postfix(SandSailorStudio.Localization.LocalizationManager __instance)
        {
            InjectLocalizedStrings(__instance, "SetLanguage");
        }

        [HarmonyPatch(typeof(SandSailorStudio.Localization.LocalizationManager), "Awake")]
        [HarmonyPostfix]
        public static void LocalizationManager_Awake_Postfix(SandSailorStudio.Localization.LocalizationManager __instance)
        {
            // The clone usually doesn't exist yet this early — then this is a no-op and the
            // template-injection call (or SetLanguage) picks it up later.
            InjectLocalizedStrings(__instance, "Awake");
        }

        // Safety net in case a lookup path bypasses the injected dictionaries (index/cache based):
        // resolve our keys directly on the main string API. Cheap early-out for every other key.
        [HarmonyPatch(typeof(SandSailorStudio.Localization.LocalizationManager), nameof(SandSailorStudio.Localization.LocalizationManager.Loc), new[] { typeof(string), typeof(bool) })]
        [HarmonyPostfix]
        public static void LocalizationManager_Loc_Postfix(string key, ref string __result)
        {
            try
            {
                if (key == null || !key.Contains(BulldozerAssetName)) return;
                if (!string.IsNullOrEmpty(__result) && !string.Equals(__result, key, System.StringComparison.OrdinalIgnoreCase)) return; // already resolved to a real string
                if (key.EndsWith("_name")) __result = BulldozerDisplayName;
                else if (key.EndsWith("_desc") || key.EndsWith("_description")) __result = BulldozerDisplayDesc;
                else if (key.EndsWith("_lore")) __result = BulldozerDisplayLore;
            }
            catch { }
        }

        // The build-menu squares are ITEMS: BuildItemsTabPage.ShowPage lists
        // ItemCollection.GetAllItems(categoryFilter) from an InventoryComponent's collection
        // (call-graph verified). Rather than guess which component owns the buildables catalog, we
        // self-locate it: while the menu page is opening (window below), any collection the menu
        // queries that contains the VANILLA field's item (i.e. terraforming is unlocked there) gets
        // our clone's item added once (idempotent via GetFirstItem). Grant rides the same unlock
        // gate as vanilla for free.
        private static float _menuGrantWindowUntil = -1f;

        // ShowPage is private with a single caller and gets INLINED by the IL2CPP AOT compiler — a
        // postfix on it never fires (confirmed in-game 2026-07-01, v1.4.0). Init and Show are
        // virtual (vtable-dispatched, can't be inlined away). Init is what (re)builds the squares
        // (ShowPage's body is inlined into it), and it runs once per tab per menu open — so the
        // grant must land BEFORE Init's original body: a PREFIX via the page's own _pi. The v1.4.1
        // window-grant fired mid-Init-storm, after the TERRAFORMING tab had already built its
        // buttons, which is why the item was granted yet no 4th square appeared.
        // TabButtonCategory selects the hoe squares via an EXPLICIT additionalInfos list — that's the
        // real gate (BULLDOZER_UI_PLAN.md, confirmed v1.4.5); everything DB-side was necessary but not
        // sufficient. Append the clone to a tab whose additionalInfos holds the vanilla field.
        // Returns true if it changed the list.
        private static bool TryAppendCloneToTab(SandSailorStudio.UI.TabButton button, string source)
        {
            if (button == null || _bulldozerTemplate == null || _vanillaFieldTemplate == null) return false;
            try
            {
                // Managed casts lie for base-declared interop objects — check the native class name.
                if (GetNativeClassName(button) != "TabButtonCategory") return false;
                var tab = new SSSGame.UI.TabButtonCategory(button.Pointer);
                var extra = tab.additionalInfos;
                if (extra == null) return false;
                bool hasVanilla = false, hasClone = false;
                int n = extra.Count;
                for (int i = 0; i < n; i++)
                {
                    var info = extra[i];
                    if (info == null) continue;
                    if (info.id == BulldozerTemplateId) hasClone = true;
                    else if (info.id == _vanillaFieldTemplate.id) hasVanilla = true;
                }
                if (!hasVanilla || hasClone) return false;
                extra.Add(_bulldozerTemplate);
                ModLogger.LogInfo($"[Bulldozer] appended clone to the hoe tab's additionalInfos ({source}).");
                return true;
            }
            catch (System.Exception e) { ModLogger.LogError($"[Bulldozer] tab append ({source}) failed: {e}"); return false; }
        }

        [HarmonyPatch(typeof(SSSGame.UI.BuildItemsTabPage), nameof(SSSGame.UI.BuildItemsTabPage.Init))]
        [HarmonyPrefix]
        public static void BuildItemsTabPage_Init_Prefix(SSSGame.UI.BuildItemsTabPage __instance, SandSailorStudio.UI.TabButton button)
        {
            _menuGrantWindowUntil = UnityEngine.Time.realtimeSinceStartup + 5f; // fallback path stays armed
            try
            {
                var pi = __instance._pi;
                var col = pi != null ? pi.GetItemCollection() : null;
                if (col != null) TryGrantBulldozer(col, "Init prefix (page._pi)");
                else if (PlacementDiagnostics.Value) ModLogger.LogInfo("[Bulldozer] Init prefix: page._pi/collection null; relying on window fallback.");

                // First-open fix: land the tab-filter append BEFORE Init's original body builds the
                // squares, so the 4th square is there on the very first menu open (the Show postfix
                // used to do this after the build, needing a close/reopen to show up).
                TryAppendCloneToTab(button, "Init prefix");
            }
            catch (System.Exception e) { ModLogger.LogError($"[Bulldozer] Init-prefix grant failed: {e}"); }
        }

        // One-shot page rebuild guard (we re-call Init after a late grant; don't recurse).
        private static bool _reinitInProgress;

        // Show fires when the page becomes visible — by then ShowPage's inlined body has resolved
        // _pi, so this is the reliable place to reach the menu's OWN collection (the inlined
        // GetItemCollection/GetAllItems calls inside ShowPage are invisible to Harmony, so the
        // window-grant may have been feeding a different collection all along). If we grant here,
        // the squares were already built without our item, so re-run Init once to rebuild them.
        [HarmonyPatch(typeof(SSSGame.UI.BuildItemsTabPage), nameof(SSSGame.UI.BuildItemsTabPage.Show))]
        [HarmonyPostfix]
        public static void BuildItemsTabPage_Show_Postfix(SSSGame.UI.BuildItemsTabPage __instance, bool value, SandSailorStudio.UI.TabButton button)
        {
            if (!value || _reinitInProgress) return;
            _menuGrantWindowUntil = UnityEngine.Time.realtimeSinceStartup + 5f;
            bool diag = PlacementDiagnostics.Value;
            try
            {
                bool changedSomething = false;

                // 0) Loc-string self-repair right before the tooltip could be shown (idempotent,
                // ContainsKey-cheap when already injected).
                InjectLocalizedStrings(SandSailorStudio.Localization.LocalizationManager.Instance, "menu Show");

                // 1) Item possession in the page's own collection.
                var pi = __instance._pi;
                var col = pi != null ? pi.GetItemCollection() : null;
                if (col != null && _bulldozerTemplate != null && _vanillaFieldTemplate != null
                    && col.GetFirstItem(_vanillaFieldTemplate) != null
                    && col.GetFirstItem(_bulldozerTemplate) == null)
                {
                    int added = col.AddItems(_bulldozerTemplate, 1);
                    ModLogger.LogInfo($"[Bulldozer] granted blueprint item via Show (page._pi) (added={added}).");
                    changedSomething = true;
                }

                // 2) Database-side audit + self-repair (once per database build). The menu's
                // template enumeration calls are inlined and unobservable, so verify the maps the
                // menu could be reading and fix them directly if Initialize skipped our late-added
                // clone: _itemsMap (id -> info) and _categoryMap (category -> infos).
                if (!_dbAudited && _itemDb != null && _bulldozerTemplate != null && _vanillaFieldTemplate != null)
                {
                    _dbAudited = true;
                    try
                    {
                        var byId = _itemDb.GetItemInfoFromID(BulldozerTemplateId);
                        ModLogger.LogInfo($"[Bulldozer] DB audit: GetItemInfoFromID({BulldozerTemplateId}) -> {(byId != null ? byId.name : "NULL")}");

                        // Localization prep (runbook Step 1): the exact key strings the UI derives
                        // for our clone vs vanilla, so the loc-table injection targets the right keys.
                        try { ModLogger.LogInfo($"[Bulldozer] loc keys: clone name-key='{_bulldozerTemplate.GetKey("name")}' vanilla name-key='{_vanillaFieldTemplate.GetKey("name")}'"); }
                        catch (System.Exception e) { ModLogger.LogWarning($"[Bulldozer] GetKey probe failed: {e.Message}"); }
                        var itemsMap = _itemDb._itemsMap;
                        if (byId == null && itemsMap != null && !itemsMap.ContainsKey(BulldozerTemplateId))
                        {
                            itemsMap.Add(BulldozerTemplateId, _bulldozerTemplate);
                            ModLogger.LogWarning("[Bulldozer] DB audit: clone was missing from _itemsMap; added.");
                            changedSomething = true;
                        }

                        var cat = _vanillaFieldTemplate.category;
                        var catMap = _itemDb._categoryMap;
                        if (cat != null && catMap != null && catMap.ContainsKey(cat))
                        {
                            var catList = catMap[cat];
                            bool hasClone = false, hasVanilla = false;
                            int n = catList != null ? catList.Count : 0;
                            for (int i = 0; i < n; i++)
                            {
                                var info = catList[i];
                                if (info == null) continue;
                                if (info.id == BulldozerTemplateId) hasClone = true;
                                else if (info.id == _vanillaFieldTemplate.id) hasVanilla = true;
                            }
                            ModLogger.LogInfo($"[Bulldozer] DB audit: categoryMap['{cat.name}'] count={n} hasVanilla={hasVanilla} hasClone={hasClone}");
                            if (!hasClone && catList != null)
                            {
                                catList.Add(_bulldozerTemplate);
                                ModLogger.LogWarning("[Bulldozer] DB audit: clone was missing from _categoryMap; added.");
                                changedSomething = true;
                            }
                        }
                        else ModLogger.LogWarning($"[Bulldozer] DB audit: category '{(cat != null ? cat.name : "null")}' not in _categoryMap.");
                    }
                    catch (System.Exception e) { ModLogger.LogError($"[Bulldozer] DB audit failed: {e}"); }
                }

                // 3) The tab button itself — self-repair path; the primary append now happens in the
                // Init prefix so the square is present on the first open.
                if (TryAppendCloneToTab(button, "Show postfix (self-repair)")) changedSomething = true;

                // 4) If anything changed, the squares were built from stale data — rebuild once.
                if (changedSomething)
                {
                    ModLogger.LogInfo("[Bulldozer] rebuilding build-menu page after repair.");
                    _reinitInProgress = true;
                    try { __instance.Init(button); }
                    finally { _reinitInProgress = false; }
                }
            }
            catch (System.Exception e) { ModLogger.LogError($"[Bulldozer] Show grant/rebuild failed: {e}"); }
        }

        // Reads the native il2cpp class name of a wrapped object — managed-side casts (`as`) check
        // the WRAPPER type and lie for objects materialized under a base declared type, so this is
        // the only reliable runtime type test before constructing a derived wrapper via IntPtr.
        private static string GetNativeClassName(Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase obj)
        {
            try
            {
                var cls = Il2CppInterop.Runtime.IL2CPP.il2cpp_object_get_class(obj.Pointer);
                var namePtr = Il2CppInterop.Runtime.IL2CPP.il2cpp_class_get_name(cls);
                return System.Runtime.InteropServices.Marshal.PtrToStringAnsi(namePtr);
            }
            catch { return "<unknown>"; }
        }

        // Adds one clone-blueprint item if this collection is the buildables catalog (holds the
        // vanilla field's item = unlocked) and doesn't have ours yet. Idempotent.
        private static void TryGrantBulldozer(SandSailorStudio.Inventory.ItemCollection col, string source)
        {
            if (col == null || _bulldozerTemplate == null || _vanillaFieldTemplate == null) return;
            if (col.GetFirstItem(_vanillaFieldTemplate) == null) return;
            if (col.GetFirstItem(_bulldozerTemplate) != null) return;
            int added = col.AddItems(_bulldozerTemplate, 1);
            ModLogger.LogInfo($"[Bulldozer] granted blueprint item via {source} (added={added}).");
        }

        [HarmonyPatch(typeof(SandSailorStudio.Inventory.InventoryComponent), nameof(SandSailorStudio.Inventory.InventoryComponent.GetItemCollection))]
        [HarmonyPostfix]
        public static void InventoryComponent_GetItemCollection_Postfix(SandSailorStudio.Inventory.InventoryComponent __instance, SandSailorStudio.Inventory.ItemCollection __result)
        {
            try
            {
                if (UnityEngine.Time.realtimeSinceStartup > _menuGrantWindowUntil) return;
                TryGrantBulldozer(__result, $"window ('{__instance.gameObject.name}')");
            }
            catch (System.Exception e) { ModLogger.LogError($"[Bulldozer] window grant failed: {e}"); }
        }

        // Guards against our own re-entry: the flatten loop below calls grid.TryLevelTile, and the
        // native Use may re-fire while we work. We only want to run the full sweep+flatten once per hit.
        private static bool _handlingUse = false;

        // A leveling hit routes through TerraformingFieldInteraction.Use (the TryLevelTile /
        // MarkCellInteracted methods are inlined by the IL2CPP AOT compiler, so Harmony patches on
        // them never fire — confirmed in-game 2026-07-01 via call-path probes). We hook Use's postfix,
        // grab its grid, then clear obstructions and flatten every tile to completion.
        [HarmonyPatch(typeof(TerraformingFieldInteraction), nameof(TerraformingFieldInteraction.Use))]
        [HarmonyPostfix]
        public static void TerraformingFieldInteraction_Use_Postfix(TerraformingFieldInteraction __instance, SSSGame.IInteractionAgent __0, bool __result)
        {
            if (!__result || _handlingUse) return;                 // Use failed, or re-entry
            if (!OneHitClear.Value && !ClearObstructions.Value) return;

            TerraformingGrid grid = null;
            try { grid = __instance.terraformingGrid; } catch { }
            if (grid == null) return;

            // Per-template gating: only OUR bulldozer field gets the flatten+bomb treatment; the
            // vanilla field keeps its native incremental hoe leveling.
            int templateId = -1;
            string templateName = "null";
            try
            {
                var st = __instance.GetComponentInParent<Structure>();
                var tpl = st != null ? st.Template : null;
                if (st != null) templateId = st.TemplateID;
                if (tpl != null) templateName = tpl.name;
            }
            catch (System.Exception e) { ModLogger.LogError($"[Diag:Use] template probe failed: {e}"); }
            if (PlacementDiagnostics.Value)
                ModLogger.LogInfo($"[Diag:Use] structure templateId={templateId} template='{templateName}' bulldozer={templateId == BulldozerTemplateId}");
            if (templateId != BulldozerTemplateId) return;

            _handlingUse = true;
            try
            {
                // Clear obstructions once per grid, before flattening (while trees/rocks still sit on
                // the original ground). __0 is the interacting agent (the local player).
                if (ClearObstructions.Value)
                {
                    int gid = grid.GetInstanceID();
                    if (_clearedGrids.Add(gid))
                    {
                        try { DetonateAoEOverGrid(__instance, __0, grid); }
                        catch (System.Exception e) { ModLogger.LogError($"DetonateAoEOverGrid failed: {e}"); }
                    }
                }

                if (OneHitClear.Value)
                {
                    FlattenViaHeightmapTool(__instance, grid);

                    // We deform the terrain directly via HeightmapTool and bypass the field's own
                    // gradual completion, so the grid marker/UI would otherwise linger. The game's
                    // complete-field RPC finalises the field (dismissing the marker) without itself
                    // deforming terrain — exactly what we want as a tidy-up.
                    try
                    {
                        var st = grid._state;
                        if (st != null) st.Rpc_DebugCompleteField();
                    }
                    catch (System.Exception e) { ModLogger.LogError($"[Flatten] complete-field (dismiss UI) failed: {e}"); }
                }
            }
            finally
            {
                _handlingUse = false;
            }
        }

        // TRUE FLATTEN via the game's raw terrain engine. The TerraformingGrid/TryLevelTile path is a
        // slow "field quest" layer that corrupts the mesh when driven directly. HeightmapTool is the
        // actual heightmap writer (its LEVEL op sets the terrain to a reference height in one pass and
        // can clear vegetation), so we drive that over the grid's footprint instead.
        private static void FlattenViaHeightmapTool(TerraformingFieldInteraction interaction, TerraformingGrid grid)
        {
            bool diag = ClearDiagnostics.Value;

            HeightmapTool tool = null;
            try { tool = interaction.heightmapTool; } catch { }
            if (tool == null)
            {
                ModLogger.LogWarning("[Flatten] heightmapTool is null; cannot flatten this grid.");
                return;
            }

            int cellsCount = grid.GetCellsCount();
            if (cellsCount <= 0) return;

            // Footprint bounds + reference height from the grid's cells.
            float minX = float.MaxValue, minZ = float.MaxValue, maxX = float.MinValue, maxZ = float.MinValue;
            for (int i = 0; i < cellsCount; i++)
            {
                UnityEngine.Vector3 p;
                try { p = grid.GetCellPosition(i); } catch { continue; }
                if (p.x < minX) minX = p.x;
                if (p.x > maxX) maxX = p.x;
                if (p.z < minZ) minZ = p.z;
                if (p.z > maxZ) maxZ = p.z;
            }
            if (minX > maxX) return;

            float target = 0f;
            try { target = grid.ReferenceLevel; } catch { }

            // A tile of padding so the whole plot is covered, then center it.
            float width = (maxX - minX) + 2f;
            float depth = (maxZ - minZ) + 2f;
            var center = new UnityEngine.Vector3((minX + maxX) * 0.5f, target, (minZ + maxZ) * 0.5f);

            if (diag) ModLogger.LogInfo($"[Flatten] HeightmapTool LEVEL center={center} width={width:F1} depth={depth:F1} target={target:F2}");

            try
            {
                // Force the tool into a permissive, networked configuration and set the flat target.
                TrySet(() => tool.clearVegetation = ClearObstructions.Value);
                TrySet(() => tool.setVegetationMask = ClearObstructions.Value);
                TrySet(() => tool.propagateOnNetwork = true);
                TrySet(() => tool.updateNavigation = true);
                TrySet(() => tool.onlyValidate = false);
                TrySet(() => tool.validateHeight = false);
                TrySet(() => tool.checkTerrainSlope = false);
                TrySet(() => tool.checkItemInstances = false);
                TrySet(() => tool.SetReferenceHeight(target, true));

                var result = tool.RunOnArea(TerraformingToolOperation.LEVEL, width, depth, 0f, center, 0f);
                if (diag) ModLogger.LogInfo($"[Flatten] RunOnArea result={result}");
            }
            catch (System.Exception e)
            {
                ModLogger.LogError($"[Flatten] RunOnArea failed: {e}");
            }
            finally
            {
                try { tool.ResetHeightReference(); } catch { }
            }
        }

        // Runs an assignment/call, swallowing interop errors so one missing field can't abort the flatten.
        private static void TrySet(System.Action action)
        {
            try { action(); } catch { }
        }

        // ---- Obstruction clearing (bomb-style area blast) -------------------------------------

        // Build a configured AOESpell over the grid footprint and cast it through the player's
        // SpellsManager. CastSpellOnPos is the game's proper entry point — it spawns the spell, builds
        // a valid SpellcastData (with the SpellsManager set) and runs the detonation, which removes
        // resource instances (clearItemInstances) and damages trees/rocks (dealDamageToHarvestables).
        // Calling OnDetonated directly instead NRE'd because that setup was skipped.
        // Cast BombShots times so trees knocked to stumps on the first blast are cleared by the next.
        private static void DetonateAoEOverGrid(TerraformingFieldInteraction interaction, SSSGame.IInteractionAgent agent, TerraformingGrid grid)
        {
            bool diag = ClearDiagnostics.Value;
            int cellsCount = grid.GetCellsCount();
            if (cellsCount <= 0) return;

            var sm = ResolveSpellsManager(agent, interaction);
            if (sm == null)
            {
                ModLogger.LogWarning("[Bomb] Could not resolve player's SpellsManager; skipping obstacle clear.");
                return;
            }

            float minX = float.MaxValue, minZ = float.MaxValue, maxX = float.MinValue, maxZ = float.MinValue;
            for (int i = 0; i < cellsCount; i++)
            {
                UnityEngine.Vector3 p;
                try { p = grid.GetCellPosition(i); } catch { continue; }
                if (p.x < minX) minX = p.x;
                if (p.x > maxX) maxX = p.x;
                if (p.z < minZ) minZ = p.z;
                if (p.z > maxZ) maxZ = p.z;
            }
            if (minX > maxX) return;

            float yCenter = 0f;
            try { yCenter = grid.ReferenceLevel; } catch { }
            var center = new UnityEngine.Vector3((minX + maxX) * 0.5f, yCenter, (minZ + maxZ) * 0.5f);
            float vRange = ClearVerticalRange.Value;
            var boxSize = new UnityEngine.Vector3((maxX - minX) + 2f, vRange * 2f, (maxZ - minZ) + 2f);

            int shots = BombShots.Value;
            if (shots < 1) shots = 1;

            if (diag) ModLogger.LogInfo($"[Bomb] center={center} box={boxSize} shots={shots} sm={sm.name}");

            UnityEngine.GameObject go = null;
            try
            {
                go = new UnityEngine.GameObject("TerrainLevelerBomb");
                go.transform.position = center;
                var aoe = go.AddComponent<SSSGame.Combat.AOESpell>();

                TrySet(() => aoe.shape = SSSGame.Combat.AoEShape.Box);
                TrySet(() => aoe.boxSize = boxSize);
                TrySet(() => aoe.radius = System.Math.Max(boxSize.x, boxSize.z) * 0.5f);
                TrySet(() => aoe.clearItemInstances = true);
                TrySet(() => aoe.dealDamageToHarvestables = true);
                TrySet(() => aoe.baseDamage = 999999f);
                TrySet(() => aoe.damageMultiplier = 1f);
                TrySet(() => aoe.friendlyFire = false);
                // Must be FALSE: skipAllCollisionChecks skips CollisionCheck(), which is the overlap that
                // gathers damage targets (harvestables). With it true, only clearItemInstances (a separate
                // data-layer query) ran — clearing static rocks but never damaging trees.
                TrySet(() => aoe.skipAllCollisionChecks = false);
                TrySet(() => aoe.detonationDelay = 0f);
                TrySet(() => aoe.preDetonationTime = 0f);
                TrySet(() => aoe.flags = SSSGame.Combat.DamageFlags.Unblockable | SSSGame.Combat.DamageFlags.Undodgeable
                                        | SSSGame.Combat.DamageFlags.IgnoreHarvestTool | SSSGame.Combat.DamageFlags.NoTool
                                        | SSSGame.Combat.DamageFlags.HeavyAttack);
                // Damage mask: all layers EXCEPT those occupied by living things (player/villagers/
                // creatures) in the blast box, so the bomb only destroys resources and never harms them.
                int dmgMask = BuildLivingExclusionMask(center, boxSize * 0.5f, diag);
                TrySet(() => { var lm = aoe.damageMask; lm.value = dmgMask; aoe.damageMask = lm; });

                // Read the config back so we can see which fields actually took (silent TrySet may have
                // swallowed a bad assignment — a damageMask of 0 would explain harvestables surviving
                // while clearItemInstances still worked via the data layer).
                if (diag)
                {
                    int mask = -12345; bool dmgH = false, clr = false; float bd = -1f; bool skip = false;
                    try { mask = aoe.damageMask.value; } catch { }
                    try { dmgH = aoe.dealDamageToHarvestables; } catch { }
                    try { clr = aoe.clearItemInstances; } catch { }
                    try { bd = aoe.baseDamage; } catch { }
                    try { skip = aoe.skipAllCollisionChecks; } catch { }
                    ModLogger.LogInfo($"[Bomb] configured: clearItems={clr} dmgHarvest={dmgH} baseDmg={bd} mask={mask} skipChecks={skip}");
                }

                for (int shot = 0; shot < shots; shot++)
                {
                    try
                    {
                        var pos = center;
                        sm.CastSpellOnPos(aoe, ref pos);
                        if (diag) ModLogger.LogInfo($"[Bomb] shot {shot} CastSpellOnPos ok");
                    }
                    catch (System.Exception e)
                    {
                        ModLogger.LogError($"[Bomb] shot {shot} CastSpellOnPos failed: {e}");
                    }
                }
            }
            catch (System.Exception e)
            {
                ModLogger.LogError($"[Bomb] setup failed: {e}");
            }
            finally
            {
                // The spell is spawned as its own instance by CastSpellOnPos; drop our template after a
                // grace period so we don't kill an in-flight spell.
                if (go != null) { try { UnityEngine.Object.Destroy(go, 10f); } catch { } }
            }
        }

        // Resolve the local player's SpellsManager. Primary route is the interacting agent (the player
        // who pressed E); the tool's _playerAgent is only populated during active tool use, so it's a
        // fallback. Logs which link is null so a failure is diagnosable.
        // All layers minus those occupied by living entities (anything with a Character in its parents:
        // the player, villagers, creatures) inside the blast box. Removing their layers from the damage
        // mask means the AOE's target overlap never gathers them, so they take no damage — while trees
        // and rocks (on resource layers) are still hit.
        private static int BuildLivingExclusionMask(UnityEngine.Vector3 center, UnityEngine.Vector3 halfExtents, bool diag)
        {
            int mask = ~0;
            try
            {
                var hits = UnityEngine.Physics.OverlapBox(center, halfExtents, UnityEngine.Quaternion.identity, ~0, UnityEngine.QueryTriggerInteraction.Collide);
                if (hits != null)
                {
                    int excluded = 0;
                    for (int i = 0; i < hits.Length; i++)
                    {
                        var col = hits[i];
                        if (col == null) continue;
                        bool living = false;
                        try { living = col.GetComponentInParent<Character>() != null; } catch { }
                        if (!living) continue;
                        int layer = 0;
                        try { layer = col.gameObject.layer; } catch { continue; }
                        int bit = 1 << layer;
                        if ((mask & bit) != 0) { mask &= ~bit; excluded |= bit; }
                    }
                    if (diag) ModLogger.LogInfo($"[Bomb] excluded living-entity layers bitmask={excluded} -> damageMask={mask}");
                }
            }
            catch (System.Exception e) { ModLogger.LogError($"[Bomb] BuildLivingExclusionMask failed: {e}"); }
            return mask;
        }

        private static SSSGame.Combat.SpellsManager _cachedSpellsManager;

        // SpellsManager is a standalone Fusion network object (not under the player hierarchy), so we
        // can't reach it by GetComponent searches. Instead we capture the local player's instance from
        // its Fusion state-sync methods run in the network simulation path and patching them hangs the
        // game at load (learned the hard way). Instead we register every SpellsManager from its Awake
        // (a plain Unity lifecycle, safe to patch) and pick the local player's — IsPlayer + HasAuthority
        // — at cast time, when those network flags are valid.
        private static readonly System.Collections.Generic.List<SSSGame.Combat.SpellsManager> _knownSpellsManagers
            = new System.Collections.Generic.List<SSSGame.Combat.SpellsManager>();

        [HarmonyPatch(typeof(SSSGame.Combat.SpellsManager), "Awake")]
        [HarmonyPostfix]
        public static void SpellsManager_Awake_Postfix(SSSGame.Combat.SpellsManager __instance)
        {
            try { if (__instance != null) _knownSpellsManagers.Add(__instance); } catch { }
        }

        // Scan the registered managers for the local player's (valid network flags evaluated now).
        private static SSSGame.Combat.SpellsManager FindPlayerSpellsManager(bool diag)
        {
            SSSGame.Combat.SpellsManager fallback = null;
            for (int i = _knownSpellsManagers.Count - 1; i >= 0; i--)
            {
                var sm = _knownSpellsManagers[i];
                bool alive = false;
                try { alive = sm != null && sm.gameObject != null; } catch { }
                if (!alive) { _knownSpellsManagers.RemoveAt(i); continue; }
                try
                {
                    if (sm.IsPlayer && sm.HasAuthority) return sm;
                    if (fallback == null && sm.HasAuthority) fallback = sm;
                }
                catch { }
            }
            if (diag && fallback != null) ModLogger.LogInfo("[Bomb] no IsPlayer+authority SpellsManager; using authority fallback");
            return fallback;
        }

        private static SSSGame.Combat.SpellsManager ResolveSpellsManager(SSSGame.IInteractionAgent agent, TerraformingFieldInteraction interaction)
        {
            bool diag = ClearDiagnostics.Value;

            // Fast path: reuse a previously resolved manager (persists for the session).
            if (_cachedSpellsManager != null)
            {
                try { if (_cachedSpellsManager.gameObject != null) return _cachedSpellsManager; }
                catch { _cachedSpellsManager = null; } // stale (world reload) — fall through and re-resolve
            }

            // Primary: the registry populated from SpellsManager.Awake.
            var fromRegistry = FindPlayerSpellsManager(diag);
            if (fromRegistry != null)
            {
                _cachedSpellsManager = fromRegistry;
                if (diag) ModLogger.LogInfo($"[Bomb] resolved SpellsManager '{fromRegistry.name}' from registry");
                return fromRegistry;
            }

            PlayerCharacter pc = null;

            // Route 1: the interacting agent.
            try
            {
                var pia = agent != null ? agent.TryCast<PlayerInteractionAgent>() : null;
                if (pia != null) pc = pia.GetCharacter();
                else if (diag) ModLogger.LogInfo($"[Bomb] agent is not a PlayerInteractionAgent (agent null? {agent == null})");
            }
            catch (System.Exception e) { ModLogger.LogError($"[Bomb] agent route failed: {e}"); }

            // Route 2: the tool's player agent.
            if (pc == null)
            {
                try
                {
                    HeightmapTool tool = interaction.heightmapTool;
                    PlayerInteractionAgent pia2 = tool != null ? tool._playerAgent : null;
                    if (pia2 != null) pc = pia2.GetCharacter();
                    else if (diag) ModLogger.LogInfo($"[Bomb] tool._playerAgent null (tool null? {tool == null})");
                }
                catch (System.Exception e) { ModLogger.LogError($"[Bomb] tool route failed: {e}"); }
            }

            if (pc == null) { if (diag) ModLogger.LogInfo("[Bomb] could not resolve PlayerCharacter"); return null; }

            // SpellsManager lives somewhere under the player's root and its GameObject may be inactive,
            // so search WITH includeInactive. Cache the result — it persists for the session, so once
            // found we never have to search (and can't flake) again.
            SSSGame.Combat.SpellsManager sm = null;
            try { sm = pc.GetComponentInChildren<SSSGame.Combat.SpellsManager>(true); } catch { }
            if (sm == null) { try { sm = pc.GetComponentInParent<SSSGame.Combat.SpellsManager>(); } catch { } }
            if (sm == null)
            {
                try
                {
                    var root = pc.transform != null ? pc.transform.root : null;
                    if (root != null) sm = root.GetComponentInChildren<SSSGame.Combat.SpellsManager>(true);
                }
                catch { }
            }
            if (sm != null) { _cachedSpellsManager = sm; if (diag) ModLogger.LogInfo($"[Bomb] resolved & cached SpellsManager '{sm.name}'"); }
            else if (diag) ModLogger.LogInfo("[Bomb] PlayerCharacter found but no SpellsManager under it");
            return sm;
        }
    }
}
