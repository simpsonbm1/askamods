using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using Il2CppInterop.Runtime.Injection;
using UnityEngine;

namespace TerraformAuditMod;

// Throwaway diagnostic probe (NOT for Nexus). Answers one question in a single in-game run: does
// ASKA's per-cell "heightmap modified" flag get set by BOTH terrain-leveling routes - the vanilla
// terraforming field, and TerrainLevelerMod's bulldozer (which writes through
// HeightmapTool.RunOnArea, the raw heightmap writer)?
//
// A planned TreeRespawnMod feature will suppress gatherable respawn on terraformed ground by
// reading that flag, so it must be known which write paths actually set it. Cecil cannot answer
// this - the IL2CPP interop assembly carries signatures only, no method bodies - so it takes a
// live read of TerraformingMap state at the player's feet.
[BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
public class Plugin : BasePlugin
{
    internal static ManualLogSource Logger = null!;

    internal static ConfigEntry<string> DumpHotkeyRaw = null!;
    internal static KeyCode HotKey = KeyCode.F4;

    public override void Load()
    {
        Logger = base.Log;

        DumpHotkeyRaw = Config.Bind(
            "TerraformAudit", "DumpHotkey", "F4",
            "Key that dumps the terraforming-map state at the player's feet to this log. F4 was "
            + "chosen because every other function key is already claimed by another mod in this "
            + "repo.");

        if (!Enum.TryParse<KeyCode>(DumpHotkeyRaw.Value, true, out HotKey))
        {
            HotKey = KeyCode.F4;
            Logger.LogWarning($"[TerraformAudit] Unrecognised DumpHotkey '{DumpHotkeyRaw.Value}' - using F4.");
        }

        ClassInjector.RegisterTypeInIl2Cpp<TerraformAuditRunner>();
        var go = new GameObject("TerraformAuditMod_Runner");
        UnityEngine.Object.DontDestroyOnLoad(go);
        go.AddComponent<TerraformAuditRunner>();

        new Harmony(MyPluginInfo.PLUGIN_GUID).PatchAll();

        Logger.LogInfo($"[TerraformAudit] TerraformAuditMod v{MyPluginInfo.PLUGIN_VERSION} loaded. "
                       + $"Load a world, stand on the ground you want to test, then press {HotKey}.");
    }
}

// Registered MonoBehaviour + Update() polling - the project's standard input idiom (game Action
// events cannot be subscribed through this interop layer).
public class TerraformAuditRunner : MonoBehaviour
{
    private void Update()
    {
        try
        {
            if (Input.GetKeyDown(Plugin.HotKey)) TerraformDump.Run();
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"[TerraformAudit] runner error: {ex}");
        }
    }
}
