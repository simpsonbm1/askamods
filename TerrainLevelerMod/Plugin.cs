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
        public const string PLUGIN_VERSION = "1.3.15";

        public static ConfigEntry<float> MaxDragRange;
        public static ConfigEntry<bool> OneHitClear;
        public static ConfigEntry<int> MaxPassesPerTile;
        public static ConfigEntry<bool> UseDebugComplete;
        public static ConfigEntry<bool> ClearObstructions;
        public static ConfigEntry<int> BombShots;
        public static ConfigEntry<bool> BruteForceFallback;
        public static ConfigEntry<string> BruteForceNameFilter;
        public static ConfigEntry<float> ClearVerticalRange;
        public static ConfigEntry<bool> ClearDiagnostics;
        public static ConfigEntry<float> MaxHeightDifference;
        public static ManualLogSource ModLogger;

        // Grids whose obstructions have already been cleared, so we only sweep once per grid
        // (keyed by Unity instance id). Cleared-per-grid; the flatten loop still runs every hit.
        private static readonly System.Collections.Generic.HashSet<int> _clearedGrids = new System.Collections.Generic.HashSet<int>();

        public override void Load()
        {
            ModLogger = Log;
            MaxDragRange = Config.Bind("General", "MaxDragRange", 20f, "Maximum drag distance for the terraforming tool (vanilla is 10)");
            OneHitClear = Config.Bind("General", "OneHitClear", true, "If true, a single hit fully flattens every tile in the grid (loops each tile to completion instead of one increment per hit)");
            MaxPassesPerTile = Config.Bind("General", "MaxPassesPerTile", 256, "Safety cap on how many level increments to apply per tile before giving up (prevents an infinite loop if a tile can never reach the target). Raise if tall/deep terrain isn't fully flattening in one hit.");
            UseDebugComplete = Config.Bind("General", "UseDebugComplete", false, "Experimental: also call the game's 'complete field' RPC. In testing this only finalises the field and does NOT deform terrain, so leave false unless investigating.");

            ClearObstructions = Config.Bind("Obstructions", "ClearObstructions", true, "If true, the first hit on a grid detonates a bomb-style area blast over it to destroy trees/rocks/props before flattening.");
            BombShots = Config.Bind("Obstructions", "BombShots", 2, "How many blasts to detonate over the grid. 2 mirrors 'two bombs': the first knocks trees to stumps, the second clears the stumps and rocks.");
            BruteForceFallback = Config.Bind("Obstructions", "BruteForceFallback", true, "If true, objects that aren't cleanly harvestable (e.g. baked Fusion stone clumps) are disabled outright (SetActive(false)) when their name matches BruteForceNameFilter. Single-player cheat-grade: no loot, and networked objects may reappear on chunk reload.");
            BruteForceNameFilter = Config.Bind("Obstructions", "BruteForceNameFilter", "Harvest_,Stone,Rock,Boulder,Ore,Cliff,Deposit,Bush,Log,Trunk", "Comma-separated name tokens (case-insensitive). A non-harvestable object is only brute-force disabled if its name (or a parent's) contains one of these. Keeps buildings/terrain/NPCs safe.");
            ClearVerticalRange = Config.Bind("Obstructions", "ClearVerticalRange", 30f, "Half-height (m) of the box searched for obstructions above/below the grid. Raise if tall trees/rocks near the grid aren't detected.");
            ClearDiagnostics = Config.Bind("Obstructions", "ClearDiagnostics", false, "Log details of the flatten + obstacle-clearing blast ([Flatten]/[Bomb] lines). Off by default; enable to debug.");
            MaxHeightDifference = Config.Bind("General", "MaxHeightDifference", 15f, "Maximum height difference before the grid turns red and is unplaceable. Native is ~2m, 15m is a safe limit before mesh NaN crashes.");

            Harmony.CreateAndPatchAll(typeof(Plugin));
            Log.LogInfo($"Plugin {PLUGIN_GUID} is loaded!");
        }

        [HarmonyPatch(typeof(DynamicDimensionsPlacementTool), nameof(DynamicDimensionsPlacementTool.Begin))]
        [HarmonyPrefix]
        public static void DynamicDimensionsPlacementTool_Begin_Prefix(DynamicDimensionsPlacementTool __instance)
        {
            // A 22.5m drag radius yields exactly 506 tiles natively (under the 512 crash limit).
            // Any larger and the native Fusion NetworkArray violently overflows.
            __instance.maxRange = System.Math.Min(MaxDragRange.Value, 22.5f);
            
            // Always raise the height-difference limit so the native physics don't block placement.
            // We handle the Y-Clamp safely below.
            __instance.maxHeightDifference = 10000f;
        }

        [HarmonyPatch(typeof(DynamicDimensionsPlacementTool), nameof(DynamicDimensionsPlacementTool._ValidateFootprint))]
        [HarmonyPrefix]
        public static bool DynamicDimensionsPlacementTool_ValidateFootprint_Prefix(ref bool __result)
        {
            // ALWAYS bypass native footprint validation — this is CRASH-PREVENTION, not a cheat, so it
            // must never be gated behind a config toggle. Native _ValidateFootprint uses a fixed-size
            // OverlapBoxNonAlloc buffer that overflows and hard-crashes the game on a large grid placed
            // over dense obstacles. Returning true also lets us place over trees/rocks, which the bomb pass then clears.
            __result = true;
            return false;
        }

        [HarmonyPatch(typeof(TerraformingGrid), nameof(TerraformingGrid.CheckLevelDifference))]
        [HarmonyPrefix]
        public static bool TerraformingGrid_CheckLevelDifference_Prefix(ref bool __result)
        {
            // Always report the level difference as acceptable to avoid native rejection.
            __result = true;
            return false;
        }

        [HarmonyPatch(typeof(DynamicDimensionsPlacementTool), nameof(DynamicDimensionsPlacementTool._OnBuildRayChanged))]
        [HarmonyPrefix]
        public static void DynamicDimensionsPlacementTool_OnBuildRayChanged_Prefix(DynamicDimensionsPlacementTool __instance)
        {
            __instance.maxHeightDifference = 10000f;
        }

        [HarmonyPatch(typeof(DynamicDimensionsPlacementTool), nameof(DynamicDimensionsPlacementTool._OnBuildingDimensionsChanged))]
        [HarmonyPrefix]
        public static void DynamicDimensionsPlacementTool_OnBuildingDimensionsChanged_Prefix(DynamicDimensionsPlacementTool __instance)
        {
            if (__instance.firstMarker != null && __instance._marker != null)
            {
                var start = __instance.firstMarker.transform.position;
                var end = __instance._marker.transform.position;
                
                // We only care about horizontal (X/Z) bounds to calculate the grid array size
                var start2D = new UnityEngine.Vector2(start.x, start.z);
                var end2D = new UnityEngine.Vector2(end.x, end.z);
                
                // Cap the actual physical marker tether at 22.5m so it never physically exceeds 512 tiles
                float maxDist = System.Math.Min(MaxDragRange.Value, 22.5f);
                float currentDist = UnityEngine.Vector2.Distance(start2D, end2D);
                
                if (currentDist > maxDist)
                {
                    // Clamp it!
                    UnityEngine.Vector2 direction = (end2D - start2D).normalized;
                    UnityEngine.Vector2 clamped2D = start2D + (direction * maxDist);
                    
                    // We need to find the REAL ground height at the clamped position
                    // Fire a ray straight down from the sky at that X/Z coordinate
                    UnityEngine.Vector3 rayStart = new UnityEngine.Vector3(clamped2D.x, 1000f, clamped2D.y);
                    if (Physics.Raycast(rayStart, UnityEngine.Vector3.down, out UnityEngine.RaycastHit hit, 2000f, __instance.hitMask))
                    {
                        end = hit.point;
                    }
                    else
                    {
                        // Fallback
                        end = new UnityEngine.Vector3(clamped2D.x, end.y, clamped2D.y);
                    }
                }
                
                // ANTI-CRASH Y-CLAMP:
                // If the user crests a hill, the raycast drops into the valley, creating a massive vertical height difference (e.g. 50m).
                // The native TerraformingGrid mesh generator will violently crash with NaN normals if it tries to render a grid spanning a near-vertical cliff.
                // We strictly clamp the maximum vertical stretch to guarantee a safe slope gradient.
                // 15m is the absolute highest safe limit before the engine starts dividing by zero on slopes.
                float maxYDiff = System.Math.Min(MaxHeightDifference.Value, 15f);
                if (System.Math.Abs(end.y - start.y) > maxYDiff)
                {
                    end.y = start.y + (System.Math.Sign(end.y - start.y) * maxYDiff);
                }
                
                // Apply the final heavily-sanitized coordinate
                __instance._marker.transform.position = end;
            }
        }

        [HarmonyPatch(typeof(DynamicDimensionTemplate), nameof(DynamicDimensionTemplate.CreatePreview))]
        [HarmonyPrefix]
        public static void DynamicDimensionTemplate_CreatePreview_Prefix(DynamicDimensionTemplate __instance)
        {
            // Capped at 512 to match the native Fusion NetworkArray limit
            __instance.maxNumberOfTiles = 512;
        }

        [HarmonyPatch(typeof(DynamicDimensionTemplate), nameof(DynamicDimensionTemplate.Create))]
        [HarmonyPrefix]
        public static void DynamicDimensionTemplate_Create_Prefix(DynamicDimensionTemplate __instance)
        {
            __instance.maxNumberOfTiles = 512;
        }

        [HarmonyPatch(typeof(TerraformingGrid), nameof(TerraformingGrid.OnDynamicBuildingDimensionsChanged))]
        [HarmonyPrefix]
        public static bool TerraformingGrid_OnDynamicBuildingDimensionsChanged_Prefix(TerraformingGrid __instance)
        {
            // If the grid exceeds 512 tiles, freeze the visual update by aborting this method.
            // This mathematically guarantees we never overflow the native 512 arrays.
            if (__instance.GetCellsCount() > 512)
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
            if ((gridSize.x * gridSize.z) > 512)
            {
                ModLogger.LogWarning("Grid exceeds maximum networked capacity (512 tiles). Placement blocked.");
                return false;
            }
            return true;
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

        // Diagnostic: sample the reference level and a few cell heights so we can see whether the
        // flatten actually converged to a single plane (vs. leaving cells at varied heights = lumpy).
        private static void LogFlattenState(TerraformingGrid grid, int cellsCount, string phase)
        {
            try
            {
                float refLvl = 0f;
                try { refLvl = grid.ReferenceLevel; } catch { }
                int mid = cellsCount / 2;
                int last = cellsCount - 1;
                string s0 = SampleCell(grid, 0);
                string sMid = SampleCell(grid, mid);
                string sLast = SampleCell(grid, last);
                ModLogger.LogInfo($"[Flatten:{phase}] cells={cellsCount} ref={refLvl:F2} | cell0={s0} cellMid={sMid} cellLast={sLast}");
            }
            catch (System.Exception e) { ModLogger.LogError($"[Flatten:{phase}] diag failed: {e}"); }
        }

        private static string SampleCell(TerraformingGrid grid, int i)
        {
            if (i < 0) return "n/a";
            float h = float.NaN; bool lvl = false;
            try { h = grid.GetCellAverageHeight(i); } catch { }
            try { lvl = grid.IsCellLeveled(i); } catch { }
            return $"h={h:F2},lvl={lvl}";
        }

        private static float SafeCellHeight(TerraformingGrid grid, int i)
        {
            try { return grid.GetCellAverageHeight(i); } catch { return float.NaN; }
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

        // ---- Obstruction clearing (legacy physics sweep, retained for reference) --------------

        // Sweep the grid's world-space box for trees/rocks/props and remove them.
        // Harvestable resources go through the game's own TERRAFORMING destruction level (clean,
        // networked); anything left over is disabled outright if brute-force fallback is enabled.
        private static void ClearObstructionsInGrid(TerraformingGrid grid)
        {
            int cells = grid.GetCellsCount();
            if (cells <= 0) return;

            // Build a horizontal AABB from the cell world positions.
            float minX = float.MaxValue, minZ = float.MaxValue, maxX = float.MinValue, maxZ = float.MinValue;
            float sumY = 0f; int yCount = 0;
            for (int i = 0; i < cells; i++)
            {
                UnityEngine.Vector3 p;
                try { p = grid.GetCellPosition(i); } catch { continue; }
                if (p.x < minX) minX = p.x;
                if (p.x > maxX) maxX = p.x;
                if (p.z < minZ) minZ = p.z;
                if (p.z > maxZ) maxZ = p.z;
                sumY += p.y; yCount++;
            }
            if (yCount == 0) return;

            const float pad = 1.0f; // half a tile of horizontal margin
            var center = new UnityEngine.Vector3((minX + maxX) * 0.5f, sumY / yCount, (minZ + maxZ) * 0.5f);
            var half = new UnityEngine.Vector3((maxX - minX) * 0.5f + pad, ClearVerticalRange.Value, (maxZ - minZ) * 0.5f + pad);

            bool diag = ClearDiagnostics.Value;
            if (diag) ModLogger.LogInfo($"[Clear] Sweeping grid {grid.GetInstanceID()} center={center} half={half} cells={cells}");

            Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<UnityEngine.Collider> hits;
            try { hits = UnityEngine.Physics.OverlapBox(center, half, UnityEngine.Quaternion.identity, ~0, UnityEngine.QueryTriggerInteraction.Collide); }
            catch (System.Exception e) { ModLogger.LogError($"[Clear] OverlapBox failed: {e}"); return; }
            if (hits == null) return;

            string[] tokens = ParseFilterTokens(BruteForceNameFilter.Value);
            var processedNodes = new System.Collections.Generic.HashSet<int>();
            int harvested = 0, bruteForced = 0, skipped = 0;

            for (int h = 0; h < hits.Length; h++)
            {
                var col = hits[h];
                if (col == null) continue;
                var go = col.gameObject;
                if (go == null) continue;

                // 1) Harvestable resource (trees, bushes, most veg): clean removal via the game's
                //    own terraforming destruction path.
                HarvestInteraction hi = null;
                try { hi = col.GetComponentInParent<HarvestInteraction>(); } catch { }
                if (hi != null)
                {
                    if (!processedNodes.Add(hi.GetInstanceID())) continue; // multiple colliders, one node
                    if (TryHarvestDestroy(hi, diag)) { harvested++; continue; }
                    // Clean removal declined (e.g. not TERRAFORMING-removable) → fall through to brute force.
                }

                // 2) Protect characters (player/villagers/creatures) and structures (buildings, the
                //    terraforming grid itself). Never disable these.
                try { if (col.GetComponentInParent<Character>() != null) { skipped++; continue; } } catch { }
                try { if (col.GetComponentInParent<Structure>() != null) { skipped++; continue; } } catch { }

                // 3) Brute-force fallback: disable the nearest ancestor whose name matches the filter.
                if (!BruteForceFallback.Value) { skipped++; continue; }
                var target = FindNamedTarget(col.transform, tokens, 4);
                if (target == null) { skipped++; continue; }
                try
                {
                    if (diag) ModLogger.LogInfo($"[Clear] Brute-force disabling '{target.name}'");
                    target.gameObject.SetActive(false);
                    bruteForced++;
                }
                catch (System.Exception e) { ModLogger.LogError($"[Clear] disable '{target.name}' failed: {e}"); }
            }

            ModLogger.LogInfo($"[Clear] Grid {grid.GetInstanceID()}: {harvested} harvested, {bruteForced} brute-forced, {skipped} skipped ({hits.Length} colliders).");
        }

        // Removes a harvestable node via the game's TERRAFORMING destruction level. Returns true if gone.
        private static bool TryHarvestDestroy(HarvestInteraction hi, bool diag)
        {
            WorldItemInstance wi = null;
            try { wi = hi._worldInstance; } catch { }
            if (wi == null) return false;
            try
            {
                bool removed = false;
                var level = InstanceDestructionLevel.TERRAFORMING;
                wi.Destroy(ref removed, ref level);
                bool gone = removed || wi.Destroyed;
                if (diag) ModLogger.LogInfo($"[Clear] Harvest-destroy '{hi.name}' removed={removed} destroyed={wi.Destroyed}");
                return gone;
            }
            catch (System.Exception e)
            {
                ModLogger.LogError($"[Clear] Harvest-destroy '{hi.name}' failed: {e}");
                return false;
            }
        }

        // Walk up (self + up to maxDepth ancestors) and return the HIGHEST transform whose name
        // contains one of the filter tokens (case-insensitive). Returning the topmost match means we
        // disable the whole resource object (e.g. the tree root) rather than just a collider child
        // like 'Collision_Trunk', which left the visible mesh standing. Null if nothing matches.
        private static UnityEngine.Transform FindNamedTarget(UnityEngine.Transform start, string[] tokens, int maxDepth)
        {
            UnityEngine.Transform bestMatch = null;
            var t = start;
            for (int d = 0; d <= maxDepth && t != null; d++)
            {
                string name = t.name;
                if (!string.IsNullOrEmpty(name))
                {
                    string lower = name.ToLowerInvariant();
                    for (int i = 0; i < tokens.Length; i++)
                    {
                        if (lower.Contains(tokens[i])) { bestMatch = t; break; }
                    }
                }
                t = t.parent;
            }
            return bestMatch;
        }

        private static string[] ParseFilterTokens(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return System.Array.Empty<string>();
            var parts = raw.Split(',');
            var list = new System.Collections.Generic.List<string>(parts.Length);
            foreach (var p in parts)
            {
                var tok = p.Trim().ToLowerInvariant();
                if (tok.Length > 0) list.Add(tok);
            }
            return list.ToArray();
        }
    }
}
