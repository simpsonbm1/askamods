using BepInEx;
using BepInEx.Unity.IL2CPP;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using SSSGame;
using SandSailorStudio.WorldGen;
using Il2CppInterop.Runtime.Injection;
using SandSailorStudio.RNG;

namespace SeedHarvesterMod
{
    [BepInPlugin("com.simpsonbm1.seedharvester", "Seed Harvester", "0.1.0")]
    public class Plugin : BasePlugin
    {
        internal static new ManualLogSource Logger;

        public override void Load()
        {
            Logger = base.Log;
            ClassInjector.RegisterTypeInIl2Cpp<HarvesterRunner>();
            Harmony.CreateAndPatchAll(typeof(Plugin));
            Log.LogInfo("Seed Harvester test loaded.");
        }

        [HarmonyPatch(typeof(MainMenu), "OnActivate")]
        [HarmonyPostfix]
        public static void MainMenuActivated()
        {
            Logger.LogInfo("MainMenu activated! Starting HarvesterRunner...");
            var go = new GameObject("HarvesterRunner");
            go.AddComponent<HarvesterRunner>();
        }
    }

    public class HarvesterRunner : MonoBehaviour
    {
        
        private Il2CppSystem.Collections.IEnumerator _genTask;
        private WorldGenerator _wg;
        private int _frames = 0;

        void Start()
        {
            Plugin.Logger.LogInfo("HarvesterRunner Start...");
        }

        void Update()
        {
            _frames++;
            if (_frames == 120) // wait ~2 seconds
            {
                StartGeneration();
            }

            if (_genTask != null)
            {
                if (!_genTask.MoveNext())
                {
                    _genTask = null;
                    FinishGeneration();
                }
            }
        }

        void StartGeneration()
        {
            Plugin.Logger.LogInfo("Creating WorldGenerator GameObject...");
            var go = new GameObject("TestWorldGenerator");
            _wg = go.AddComponent<WorldGenerator>();

            Plugin.Logger.LogInfo("Setting up WorldGenerator...");
            _wg.Setup(128f, 3, 1, 3);
            
            Plugin.Logger.LogInfo("Attempting to find or create RandomGeneratorManager...");
            var rgm = Object.FindFirstObjectByType<RandomGeneratorManager>();
            if (rgm == null)
            {
                Plugin.Logger.LogInfo("RandomGeneratorManager not found, creating one...");
                rgm = new GameObject("RGM").AddComponent<RandomGeneratorManager>();
            }
            
            Plugin.Logger.LogInfo("Setting seed phrase...");
            rgm.SetSeedPhrase("HarvesterTest");

            Plugin.Logger.LogInfo("Calling GenerateWorldMapAsync...");
            _genTask = _wg.GenerateWorldMapAsync("");
        }

        void FinishGeneration()
        {
            Plugin.Logger.LogInfo("GenerateWorldMapAsync finished!");

            var map = _wg.GetDataMap();
            if (map != null && map._areaInstances != null)
            {
                Plugin.Logger.LogInfo($"Success! Generated {map._areaInstances.Count} area instances.");
            }
            else
            {
                Plugin.Logger.LogInfo("Map or _areaInstances was null.");
            }
        }
    }
}
