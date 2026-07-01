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
        public const string PLUGIN_VERSION = "1.3.22";

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
            PlacementDiagnostics = Config.Bind("General", "PlacementDiagnostics", false, "Log which placement guards fire during a drag (Begin/ChangeGridSize/snap/dynamic) and the grid size they see. Confirmed fixed in-game 2026-07-01; flip true only to re-diagnose.");

            Harmony.CreateAndPatchAll(typeof(Plugin));
            Log.LogInfo($"Plugin {PLUGIN_GUID} is loaded!");
        }

        [HarmonyPatch(typeof(DynamicDimensionsPlacementTool), nameof(DynamicDimensionsPlacementTool.Begin))]
        [HarmonyPrefix]
        public static void DynamicDimensionsPlacementTool_Begin_Prefix(DynamicDimensionsPlacementTool __instance)
        {
            // Soft UX cap on how far the marker follows the player during the drag. This is NOT the
            // crash fix -- that's the tile-count clamp on ChangeGridSize below. The real cap is the
            // BitSet256 network-state struct capacity (confirmed via Cpp2IL decompile + WER crash-offset
            // mapping; see DRAG_CRASH_PLAN.md), which is independent of how far the marker can travel.
            __instance.maxRange = MaxDragRange.Value;

            // Always raise the height-difference limit so the native physics don't block placement.
            __instance.maxHeightDifference = 10000f;

            _lastLoggedGridTiles = -1; // reset diag throttle for a fresh placement
            if (PlacementDiagnostics.Value) ModLogger.LogInfo($"[Diag] Begin fired; maxRange set to {__instance.maxRange}");
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
            __instance.maxHeightDifference = 10000f;
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

        [HarmonyPatch(typeof(DynamicDimensionTemplate), nameof(DynamicDimensionTemplate.CreatePreview))]
        [HarmonyPrefix]
        public static void DynamicDimensionTemplate_CreatePreview_Prefix(DynamicDimensionTemplate __instance)
        {
            // Capped at 256 to match the REAL Fusion network-state capacity for this structure (BitSet256
            // on NetworkDynamicDimensionBuildingState_256, confirmed via Cpp2IL decompile + WER crash-offset
            // mapping). The old 512 matched a DIFFERENT variant (BitSet512) used by other building types.
            if (PlacementDiagnostics.Value) ModLogger.LogInfo($"[Diag] CreatePreview vanilla maxNumberOfTiles={__instance.maxNumberOfTiles}");
            __instance.maxNumberOfTiles = 256;
        }

        [HarmonyPatch(typeof(DynamicDimensionTemplate), nameof(DynamicDimensionTemplate.Create))]
        [HarmonyPrefix]
        public static void DynamicDimensionTemplate_Create_Prefix(DynamicDimensionTemplate __instance)
        {
            if (PlacementDiagnostics.Value) ModLogger.LogInfo($"[Diag] Create vanilla maxNumberOfTiles={__instance.maxNumberOfTiles}");
            __instance.maxNumberOfTiles = 256;
        }

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
