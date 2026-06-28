using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using Il2CppInterop.Runtime.Injection;
using Il2CppInterop.Runtime;
using UnityEngine;
using SandSailorStudio.WorldGen;
using SandSailorStudio.Game;
using SandSailorStudio.RNG;
using SSSGame;
using System.IO;
using System.Collections.Generic;
using BepInEx.Unity.IL2CPP.Utils.Collections;

namespace SeedHarvesterMod
{
    [BepInPlugin("com.askamods.seedharvester", "SeedHarvester", "0.5.0")]
    public class Plugin : BepInEx.Unity.IL2CPP.BasePlugin
    {
        public static ManualLogSource? Logger;
        public static string LogPath = "";
        
        public static bool CallNative = false;

        public override void Load()
        {
            Logger = Log;
            LogPath = Path.Combine(Paths.ConfigPath, "SeedHarvester_Results.txt");
            Log.LogInfo($"Plugin com.askamods.seedharvester is loaded!");
            Harmony.CreateAndPatchAll(typeof(GameStateLoadPatch));
        }
    }

    [HarmonyPatch(typeof(SSSGame.GameState_Load), nameof(SSSGame.GameState_Load._LoadingRoutine))]
    public static class GameStateLoadPatch
    {
        [HarmonyPrefix]
        public static bool Prefix(SSSGame.GameState_Load __instance, Il2CppSystem.Action onEnd, ref Il2CppSystem.Collections.IEnumerator __result)
        {
            if (Plugin.CallNative) return true; // Let original run if called natively
            
            Plugin.Logger?.LogInfo("SeedHarvester: Hijacking GameState_Load._LoadingRoutine for FAST HARVEST!");
            
            // Replace the original enumerator with our custom fast harvest wrapper
            __result = FastHarvestRoutine(__instance, onEnd).WrapToIl2Cpp();
            return false;
        }

        private static System.Collections.IEnumerator FastHarvestRoutine(SSSGame.GameState_Load __instance, Il2CppSystem.Action onEnd)
        {
            var wg = Object.FindAnyObjectByType<WorldGenerator>();
            var bm = Object.FindAnyObjectByType<BiomesManager>();
            var cm = Object.FindAnyObjectByType<CavesManager>();
            var rgm = Object.FindAnyObjectByType<RandomGeneratorManager>();
            
            int attempts = 0;
            int goodSeeds = 0;
            string bestSeed = "unknown";
            float bestSeedScore = -9999f;
            
            while (attempts < 200 && goodSeeds < 10)
            {
                attempts++;
                
                // 1. Reset World (cleans up any instances created by Biomes/Caves managers on previous loops)
                if (wg != null)
                {
                    wg.ResetWorld();
                }
                
                // 2. Set Random Seed
                string seed = new System.Random().Next(100000, 999999).ToString();
                if (rgm != null)
                {
                    rgm.SetSeedPhrase(seed);
                }
                
                Plugin.Logger?.LogInfo($"[SeedHarvester] Evaluating Seed: {seed} (Attempt {attempts})");
                
                // 3. Run WorldGenerator
                if (wg != null)
                {
                    var wgRoutine = wg.GenerateWorldMapAsync("");
                    while (true)
                    {
                        bool hasNext = false;
                        try { hasNext = wgRoutine.MoveNext(); } catch { break; }
                        if (!hasNext) break;
                        yield return wgRoutine.Current;
                    }
                    while (!wg.WorldDataReady()) yield return null;
                }
                
                // 4. Run BiomesManager
                if (bm != null)
                {
                    // Pass null to WorldBuildResult
                    var bmRoutine = bm.UpdateDataAsync(null);
                    while (true)
                    {
                        bool hasNext = false;
                        try { hasNext = bmRoutine.MoveNext(); } catch { break; }
                        if (!hasNext) break;
                        yield return bmRoutine.Current;
                    }
                }
                
                // 5. Run CavesManager
                if (cm != null)
                {
                    var cmRoutine = cm.UpdateDataAsync(null);
                    while (true)
                    {
                        bool hasNext = false;
                        try { hasNext = cmRoutine.MoveNext(); } catch { break; }
                        if (!hasNext) break;
                        yield return cmRoutine.Current;
                    }
                }
                
                // 6. Score Map
                float score = ScoreCurrentMap(wg);
                
                if (score > bestSeedScore)
                {
                    bestSeedScore = score;
                    bestSeed = seed;
                }
                
                if (score > 250f)
                {
                    goodSeeds++;
                    string msg = $"WINNER! Seed: {seed} | Score: {score:0} | Good Seeds: {goodSeeds}/10 | Attempt: {attempts}";
                    Plugin.Logger?.LogInfo(msg);
                    File.AppendAllText(Plugin.LogPath, msg + "\n");
                }
                else
                {
                    Plugin.Logger?.LogInfo($"[SeedHarvester] Seed {seed} rejected (Score: {score:0})");
                }
            }
            
            // We have our best seed!
            Plugin.Logger?.LogInfo($"[SeedHarvester] Fast Harvest complete! Setting best seed '{bestSeed}' (Score {bestSeedScore:0}) and handing off to native loading...");
            
            if (rgm != null) rgm.SetSeedPhrase(bestSeed);
            if (wg != null) wg.ResetWorld();
            
            // NOW run the native _LoadingRoutine
            Plugin.CallNative = true;
            var nativeRoutine = __instance._LoadingRoutine(onEnd);
            Plugin.CallNative = false;
            
            while (true)
            {
                bool hasNext = false;
                try { hasNext = nativeRoutine.MoveNext(); } catch (System.Exception e) { 
                    Plugin.Logger?.LogError($"SeedHarvester: Native coroutine error: {e}");
                    break; 
                }
                if (!hasNext) break;
                yield return nativeRoutine.Current;
            }
        }

        private static float ScoreCurrentMap(WorldGenerator wg)
        {
            if (wg == null) return -9999f;
            
            var map = wg.GetDataMap();
            if (map == null) return -9999f;
            
            var caves = new List<Vector2>();
            var seas = new List<Vector2>();
            
            var dict = map._areaInstances;
            if (dict == null) 
            {
                Plugin.Logger?.LogInfo("ScoreCurrentMap: _areaInstances is null");
                return -9999f;
            }
            
            int total = 0, unknown = 0;
            var en = dict.Values.GetEnumerator();
            while (en.MoveNext())
            {
                var a = en.Current;
                if (a == null) continue;
                total++;
                
                if (a.TryCast<CaveAreaInstance>() != null)
                {
                    caves.Add(new Vector2(a.position.x, a.position.y));
                }
                else if (a.TryCast<SeaAreaInstance>() != null)
                {
                    seas.Add(new Vector2(a.position.x, a.position.y));
                }
                else
                {
                    unknown++;
                }
            }
            
            Plugin.Logger?.LogInfo($"ScoreCurrentMap: total={total} caves={caves.Count} seas={seas.Count} other={unknown}");
            
            if (caves.Count == 0) return -9999f;
            
            float bestScore = -9999f;
            foreach (var c in caves)
            {
                float waterDist = float.MaxValue;
                if (seas.Count > 0)
                {
                    foreach (var s in seas)
                    {
                        float d = (c - s).magnitude;
                        if (d < waterDist) waterDist = d;
                    }
                }
                
                if (waterDist == float.MaxValue) continue;
                
                // Base score
                float score = 100f;
                // Reward being close to water (sea)
                float waterScore = 1000f - (waterDist * 2.5f);
                score += waterScore;
                
                if (score > bestScore) bestScore = score;
            }
            
            return bestScore;
        }
    }
}
