using System;
using HarmonyLib;
using SSSGame;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace MineRefreshMod.Patches;

[HarmonyPatch(typeof(CavesManager), nameof(CavesManager.Start))]
internal static class CavesManagerStartPatch
{
    static void Postfix(CavesManager __instance)
    {
        try
        {
            if (__instance == null) return;
            if (__instance.gameObject == null) return;

            var scene = __instance.gameObject.scene;
            bool isValidScene = scene.IsValid() && !string.IsNullOrEmpty(scene.name);

            Plugin.Logger.LogInfo($"[MineRefreshMod] CavesManager.Start postfix: Name='{__instance.gameObject.name}', Scene='{(isValidScene ? scene.name : "<invalid>")}', Active={__instance.gameObject.activeInHierarchy}");

            if (isValidScene)
            {
                Plugin.GameCavesManager = __instance;
                Plugin.Logger.LogInfo("[MineRefreshMod] Active CavesManager instance captured successfully!");
            }
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"[MineRefreshMod] CavesManagerStartPatch: {ex}");
        }
    }
}

[HarmonyPatch(typeof(Character), nameof(Character.Spawned))]
internal static class CharacterSpawnedPatch
{
    static void Postfix(Character __instance)
    {
        try
        {
            if (__instance == null) return;
            lock (Plugin.ActiveCharacters)
            {
                if (!Plugin.ActiveCharacters.Contains(__instance))
                {
                    Plugin.ActiveCharacters.Add(__instance);
                    
                    // Diagnostics in logs only
                    string charName = "Unknown";
                    try { charName = __instance.GetName(); } catch { }
                    Plugin.Logger.LogInfo($"[MineRefreshMod] Character spawned and tracked: '{charName}' (Total={Plugin.ActiveCharacters.Count})");
                }
            }
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"[MineRefreshMod] CharacterSpawnedPatch: {ex}");
        }
    }
}

[HarmonyPatch(typeof(Character), nameof(Character.Despawned))]
internal static class CharacterDespawnedPatch
{
    static void Postfix(Character __instance)
    {
        try
        {
            if (__instance == null) return;
            lock (Plugin.ActiveCharacters)
            {
                if (Plugin.ActiveCharacters.Remove(__instance))
                {
                    string charName = "Unknown";
                    try { charName = __instance.GetName(); } catch { }
                    Plugin.Logger.LogInfo($"[MineRefreshMod] Character despawned and removed: '{charName}' (Total={Plugin.ActiveCharacters.Count})");
                }
            }
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"[MineRefreshMod] CharacterDespawnedPatch: {ex}");
        }
    }
}
