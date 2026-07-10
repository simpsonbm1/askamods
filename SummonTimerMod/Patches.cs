using System;
using System.Collections.Generic;
using HarmonyLib;
using SandSailorStudio.Types;
using SSSGame;
using UnityEngine;

namespace SummonTimerMod;

[HarmonyPatch(typeof(VillagerOutlet))]
public static class VillagerOutletPatches
{
    // Original (unscaled) cooldown threshold values, keyed by the VillagerSpawnCooldownsConfig
    // asset name. That ScriptableObject is shared across VillagerOutlet instances and world
    // reloads, so scaling must be idempotent against a remembered baseline rather than compounding
    // on every Spawned(). Plain managed floats only — never cache the outlet/timeline wrappers
    // themselves (project rule: stale interop wrappers across world reloads can crash the process
    // natively).
    private static readonly Dictionary<string, float[]> _originalCooldowns = new();

    [HarmonyPatch(nameof(VillagerOutlet.Spawned))]
    [HarmonyPostfix]
    public static void Spawned_Postfix(VillagerOutlet __instance)
    {
        try
        {
            ApplyScaling(__instance);
        }
        catch (Exception ex)
        {
            Plugin.Log.LogError($"[SummonTimer] Spawned postfix failed: {ex}");
        }
    }

    [HarmonyPatch(nameof(VillagerOutlet.OnStorageMenuConfirmationPressed))]
    [HarmonyPostfix]
    public static void OnStorageMenuConfirmationPressed_Postfix(VillagerOutlet __instance)
    {
        try
        {
            if (!Plugin.Diagnostics.Value) return;

            Plugin.Log.LogInfo(
                $"[SummonTimer] confirm: gametimeToSpawnVillager={__instance.gametimeToSpawnVillager}, " +
                $"_NetworkedVillagerTimerEnd={__instance._NetworkedVillagerTimerEnd}, " +
                $"_SpawnPending={__instance._SpawnPending.ToString()}, Time.time={Time.time}");
        }
        catch (Exception ex)
        {
            Plugin.Log.LogError($"[SummonTimer] OnStorageMenuConfirmationPressed postfix failed: {ex}");
        }
    }

    [HarmonyPatch(nameof(VillagerOutlet.SpawnVillager))]
    [HarmonyPostfix]
    public static void SpawnVillager_Postfix(VillagerOutlet __instance)
    {
        try
        {
            if (!Plugin.Diagnostics.Value) return;

            Plugin.Log.LogInfo($"[SummonTimer] SpawnVillager fired at Time.time={Time.time}");
        }
        catch (Exception ex)
        {
            Plugin.Log.LogError($"[SummonTimer] SpawnVillager postfix failed: {ex}");
        }
    }

    // Scales the escalating villager-summon cooldown thresholds (shared ScriptableObject config)
    // and this outlet instance's own gametimeToSpawnVillager by TimerMultiplier. Idempotent: a
    // per-asset-name baseline of the ORIGINAL threshold values is captured on first sight and
    // every subsequent Spawned() re-derives from that baseline instead of compounding.
    private static void ApplyScaling(VillagerOutlet outlet)
    {
        if (!Plugin.Enabled.Value) return;

        float mult = Math.Max(0f, Plugin.TimerMultiplier.Value);
        int entriesScaled = 0;

        try
        {
            if (outlet._spawnTimeline != null)
            {
                var list = outlet._spawnTimeline.cooldowns.thresholds;
                if (list != null)
                {
                    string key = outlet._spawnTimeline.name;
                    if (string.IsNullOrEmpty(key)) key = "default";

                    if (!_originalCooldowns.TryGetValue(key, out var original) || original.Length != list.Count)
                    {
                        original = new float[list.Count];
                        for (int i = 0; i < list.Count; i++) original[i] = list[i].value;
                        _originalCooldowns[key] = original;
                    }

                    for (int i = 0; i < list.Count; i++)
                    {
                        var entry = list[i];
                        float newVal = original[i] * mult;

                        if (Plugin.Diagnostics.Value)
                        {
                            Plugin.Log.LogInfo($"[SummonTimer] count>={entry.threshold}: {entry.value} -> {newVal}");
                        }

                        entry.value = newVal;
                        list[i] = entry;
                        entriesScaled++;
                    }
                }
            }

            float oldGametime = outlet.gametimeToSpawnVillager;
            outlet.gametimeToSpawnVillager *= mult;

            if (Plugin.Diagnostics.Value)
            {
                Plugin.Log.LogInfo($"[SummonTimer] gametimeToSpawnVillager: {oldGametime} -> {outlet.gametimeToSpawnVillager}");
            }

            // Fire-verification marker — always logs, regardless of Diagnostics, whenever scaling
            // actually runs (i.e. Enabled is true).
            Plugin.Log.LogInfo($"[SummonTimer] VillagerOutlet.Spawned — scaling applied (x{entriesScaled} entries)");
        }
        catch (Exception ex)
        {
            Plugin.Log.LogError($"[SummonTimer] scaling failed: {ex}");
        }
    }
}
