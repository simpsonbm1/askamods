using System.Collections.Generic;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using SSSGame;
using UnityEngine;

namespace TaskUnlockerMod
{
    // Polling MonoBehaviour (never subscribe to game Action events — see project gotchas).
    //
    // Cooking: recipes are blueprint-discoverables — Rpc_AddDiscoverable(id) unlocks them
    // (confirmed in-game 2026-07-05).
    //
    // Fishing: tasks are NOT discovery-gated. They are created by the game itself when a
    // FishingGround is MARKED (buoy): mark → PopulationManager marked-count →
    // FishingStation._OnFishingGroundMarkChanged → _UpdateFishStatus → task. So we invoke the
    // vanilla request path — NetworkWorldDataManager.RequestDiscoverFishingGround +
    // RequestMarkFishinGround (game's own typo) — and let native code build the tasks.
    public class TaskUnlockTracker : MonoBehaviour
    {
        private const int MaxMarkAttempts = 3;
        private const float FishingPassInterval = 5f;

        private bool _cookingDone;
        private float _nextFishingPass;
        private bool _loggedGroundTotal;
        private readonly Dictionary<int, int> _markAttempts = new();

        private float _pollTimer;
        private const float PollInterval = 1f;   // 1 Hz — world-gate detection doesn't need per-frame polling

        void Update()
        {
            _pollTimer += Time.deltaTime;
            if (_pollTimer < PollInterval) return;
            _pollTimer = 0f;

            // BlueprintConditionsDatabase existing == a world is loaded; use it as the world gate.
            var blueprintDb = UnityEngine.Object.FindAnyObjectByType<BlueprintConditionsDatabase>();
            if (blueprintDb == null)
            {
                _cookingDone = false;
                _loggedGroundTotal = false;
                _markAttempts.Clear();
                return;
            }

            if (Plugin.UnlockCookingRecipes.Value && !_cookingDone)
                UnlockCooking(blueprintDb);

            if (Plugin.UnlockFish.Value && Time.time >= _nextFishingPass)
            {
                _nextFishingPass = Time.time + FishingPassInterval;
                MarkFishingGrounds();
            }
        }

        private void UnlockCooking(BlueprintConditionsDatabase blueprintDb)
        {
            var netDb = blueprintDb.NetworkLogic;
            if (netDb == null) return;
            if (!netDb.HasStateAuthority) return; // host runs the unlock; clients receive state

            var itemDb = Plugin.ItemDb;
            var items = itemDb?.CompleteItemInfoList?.itemInfoList;
            if (items == null) return;

            int unlocked = 0;
            for (int i = 0; i < items.Count; i++)
            {
                var info = items[i];
                if (info == null) continue;

                // Managed casts lie for list elements materialized under the base type —
                // identify by native class name, then rewrap (project gotcha).
                if ((object)info is not Il2CppObjectBase obj) continue;
                if (NativeClassName(obj) != "CrockpotRecipeInfo") continue;

                var recipe = new CrockpotRecipeInfo(obj.Pointer);
                if (!recipe.requiresDiscovery) continue;

                netDb.Rpc_AddDiscoverable(info.id);
                unlocked++;
                if (Plugin.DiagnosticsLogItemUnlocks.Value)
                    Plugin.Log.LogInfo($"Cooking: Rpc_AddDiscoverable({info.id}) for recipe '{info.name}'");
            }

            Plugin.Log.LogInfo($"TaskUnlocker: cooking pass done, {unlocked} crockpot recipes sent for discovery.");
            _cookingDone = true;
        }

        private void MarkFishingGrounds()
        {
            var popMgr = UnityEngine.Object.FindAnyObjectByType<PopulationManager>();
            var grounds = popMgr?._fishingGrounds;
            if (grounds == null) return;

            if (!_loggedGroundTotal && grounds.Count > 0)
            {
                Plugin.Log.LogInfo($"TaskUnlocker: PopulationManager has {grounds.Count} registered fishing grounds.");
                _loggedGroundTotal = true;
            }

            int requested = 0;
            for (int i = 0; i < grounds.Count; i++)
            {
                var fg = grounds[i];
                if (fg == null) continue;
                if (fg.IsMarked)
                {
                    _markAttempts.Remove(fg._id);
                    continue;
                }

                int id = fg._id;
                _markAttempts.TryGetValue(id, out int attempts);
                if (attempts >= MaxMarkAttempts)
                {
                    if (attempts == MaxMarkAttempts)
                    {
                        Plugin.Log.LogWarning($"TaskUnlocker: ground id={id} fish='{fg.fish?.name}' still unmarked after " +
                            $"{MaxMarkAttempts} requests (Discovered={fg.Discovered}, Disabled={fg.Disabled}) — " +
                            "marking may be gated (see FishingGround.UnlockMarking).");
                        _markAttempts[id] = attempts + 1; // warn once
                    }
                    continue;
                }

                var net = fg.NetworkCommunicator;
                if (net == null) continue;

                if (!fg.Discovered)
                    net.RequestDiscoverFishingGround(id);
                net.RequestMarkFishinGround(id, true); // game's own typo
                _markAttempts[id] = attempts + 1;
                requested++;

                if (Plugin.DiagnosticsLogItemUnlocks.Value)
                    Plugin.Log.LogInfo($"Fishing: requested discover+mark for ground id={id} fish='{fg.fish?.name}' " +
                        $"(was Discovered={fg.Discovered}, Disabled={fg.Disabled}, attempt {attempts + 1})");
            }

            if (requested > 0)
                Plugin.Log.LogInfo($"TaskUnlocker: fishing pass requested marking of {requested} ground(s).");
        }

        private static string NativeClassName(Il2CppObjectBase o)
        {
            try
            {
                var cls = IL2CPP.il2cpp_object_get_class(o.Pointer);
                return System.Runtime.InteropServices.Marshal.PtrToStringAnsi(IL2CPP.il2cpp_class_get_name(cls)) ?? "?";
            }
            catch { return "?"; }
        }
    }
}
