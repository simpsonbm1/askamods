using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Unity.IL2CPP;
using BepInEx.Logging;
using HarmonyLib;
using Il2CppInterop.Runtime.Injection;
using SSSGame;
using UnityEngine;

namespace MineRefreshMod;

[BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
public class Plugin : BasePlugin
{
    internal static ManualLogSource Logger = null!;
    internal static ConfigEntry<string> TriggerHotkey = null!;
    internal static ConfigEntry<float> SafetyRadius = null!;
    internal static ConfigEntry<bool> TriggerOnlyNearEntrance = null!;
    internal static ConfigEntry<float> MaxEntranceDistance = null!;
    internal static ConfigEntry<bool> RespawnItems = null!;
    internal static ConfigEntry<bool> ForceAllowRefresh = null!;

    internal static readonly System.Collections.Generic.List<Character> ActiveCharacters = new();
    internal static CavesManager? GameCavesManager;
    internal static PlayerCharacter? LocalPlayer;

    // Finds the local player among the tracked characters: native class PlayerCharacter with
    // authority. Called when the hotkey is pressed, because authority is not yet valid in Start.
    // Managed `is PlayerCharacter` lies for wrappers materialized as Character, hence the
    // native class name.
    internal static PlayerCharacter? ResolveLocalPlayer()
    {
        lock (ActiveCharacters)
        {
            for (int i = 0; i < ActiveCharacters.Count; i++)
            {
                var c = ActiveCharacters[i];
                try
                {
                    if (c == null || (object)c is not Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase b) continue;
                    var cls = Il2CppInterop.Runtime.IL2CPP.il2cpp_object_get_class(b.Pointer);
                    var name = System.Runtime.InteropServices.Marshal.PtrToStringAnsi(
                        Il2CppInterop.Runtime.IL2CPP.il2cpp_class_get_name(cls));
                    if (name == "PlayerCharacter" && c.HasAuthority)
                        return new PlayerCharacter(b.Pointer);
                }
                catch { }
            }
        }
        return null;
    }

    // The same native player is held by two wrappers (Character in ActiveCharacters,
    // PlayerCharacter in LocalPlayer), so identity is compared by native pointer.
    internal static bool IsLocalPlayer(Character? c)
    {
        if (c == null || LocalPlayer == null) return false;
        return (object)c is Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase a
            && (object)LocalPlayer is Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase b
            && a.Pointer == b.Pointer;
    }

    public override void Load()
    {
        Logger = base.Log;

        TriggerHotkey = Config.Bind(
            section: "General",
            key: "TriggerHotkey",
            defaultValue: "u",
            description: "The hotkey (lowercase letter or Unity KeyCode name) pressed to trigger the mine refresh. Pressed by the host player while standing near a mine entrance.");

        SafetyRadius = Config.Bind(
            section: "General",
            key: "SafetyRadius",
            defaultValue: 10.0f,
            description: "Safety proximity scan radius (in meters). If any player or worker is within this distance of any hallway/node in the mine, the refresh will be blocked to prevent trapping them.");

        TriggerOnlyNearEntrance = Config.Bind(
            section: "General",
            key: "TriggerOnlyNearEntrance",
            defaultValue: true,
            description: "If true, the player must be standing near a mine entrance to trigger the refresh.");

        MaxEntranceDistance = Config.Bind(
            section: "General",
            key: "MaxEntranceDistance",
            defaultValue: 20.0f,
            description: "Maximum distance (in meters) from a mine entrance the player can be to trigger the refresh. Only applies if TriggerOnlyNearEntrance is true.");

        RespawnItems = Config.Bind(
            section: "General",
            key: "RespawnItems",
            defaultValue: true,
            description: "If true, also run the mine's native item spawners to regenerate loose resources (iron deposits, mushrooms, chests) inside the mine.");

        ForceAllowRefresh = Config.Bind(
            section: "General",
            key: "ForceAllowRefresh",
            defaultValue: false,
            description: "If true, skips the host/server authority check entirely and always allows the refresh. Intended for dedicated-server players or anyone wrongly blocked by the host check. WARNING: in multiplayer, a refresh forced from a non-host client may not sync to other players.");

        // Register our custom Mono class into IL2CPP
        ClassInjector.RegisterTypeInIl2Cpp<MineRefreshTracker>();
        
        // Spawn our persistent manager GameObject
        var go = new GameObject("MineRefreshMod_Tracker");
        UnityEngine.Object.DontDestroyOnLoad(go);
        go.AddComponent<MineRefreshTracker>();

        // Apply Harmony patches one at a time, each guarded.
        // PatchAll() is deliberately NOT used: it sweeps every type in this assembly and makes
        // the CLR materialize every custom attribute on it, so a [HarmonyPatch(typeof(GameType))]
        // attribute forces game-type resolution inside unmanaged attribute-blob parsing. A failure
        // there is a fatal process kill that try/catch cannot intercept. Resolving each target
        // through a lambda keeps the typeof inside this try/catch instead.
        var harmony = new Harmony(MyPluginInfo.PLUGIN_GUID);
        ApplyPatch(harmony, "CavesManager.Start", () => typeof(CavesManager), "Start", typeof(Patches.CavesManagerStartPatch));
        // Character.Start, not Character.Spawned: see CharacterStartPatch for the Aska Plus crash.
        ApplyPatch(harmony, "Character.Start", () => typeof(Character), "Start", typeof(Patches.CharacterStartPatch));
        ApplyPatch(harmony, "Character.Despawned", () => typeof(Character), "Despawned", typeof(Patches.CharacterDespawnedPatch));
        // No PlayerCharacter hooks: the Character hooks above also identify the local player.

        Logger.LogInfo($"MineRefreshMod v{MyPluginInfo.PLUGIN_VERSION} loaded successfully. Hotkey='{TriggerHotkey.Value}', SafetyRadius={SafetyRadius.Value}m, TriggerOnlyNearEntrance={TriggerOnlyNearEntrance.Value}");
    }

    private static void ApplyPatch(Harmony harmony, string label, Func<Type> resolveTarget, string methodName, Type patchClass)
    {
        try
        {
            var targetType = resolveTarget();
            var target = AccessTools.DeclaredMethod(targetType, methodName)
                         ?? AccessTools.Method(targetType, methodName);
            if (target == null)
            {
                Logger.LogWarning($"[MineRefreshMod] Skipped {label}: method not found on this game build. The mod still loads; this hook is inactive.");
                return;
            }

            var postfix = AccessTools.Method(patchClass, "Postfix");
            if (postfix == null)
            {
                Logger.LogError($"[MineRefreshMod] Skipped {label}: {patchClass.Name}.Postfix is missing.");
                return;
            }

            harmony.Patch(target, postfix: new HarmonyMethod(postfix));
            Logger.LogInfo($"[MineRefreshMod] Patched {label}");
        }
        catch (Exception ex)
        {
            Logger.LogError($"[MineRefreshMod] Patch FAILED for {label}: {ex}");
        }
    }
}
