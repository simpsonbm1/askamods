using System;
using SSSGame;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace MineRefreshMod.Patches;

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

// Characters are tracked from Character.Start, never Character.Spawned. Aska Plus patches
// Character.Spawned, and a second Harmony patch on that method makes the next load of
// PlayerCharacter kill the process with "Internal CLR error (0x80131506)" (reproduced
// 2026-10-17 with Aska Plus 0.5.2 + OuthouseComposterMod).
internal static class CharacterStartPatch
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
                    Plugin.Logger.LogInfo($"[MineRefreshMod] Character started and tracked: '{charName}' (Total={Plugin.ActiveCharacters.Count})");
                }
            }
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"[MineRefreshMod] CharacterStartPatch: {ex}");
        }
    }
}

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

            if (Plugin.IsLocalPlayer(__instance))
            {
                Plugin.LocalPlayer = null;
                Plugin.Logger.LogInfo("[MineRefreshMod] Local player character cleared.");
            }
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"[MineRefreshMod] CharacterDespawnedPatch: {ex}");
        }
    }
}
