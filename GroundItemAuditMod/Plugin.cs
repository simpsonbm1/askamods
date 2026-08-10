using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using Il2CppInterop.Runtime.Injection;
using UnityEngine;

namespace GroundItemAuditMod;

// Throwaway diagnostic probe (NOT for Nexus). READ-ONLY - never deletes, modifies, activates or
// deactivates anything. Answers one question in a single in-game run: GroundItemVacuumMod only
// ever sees loose ground items that currently have a spawned GameObject (it tracks them via
// Harmony patches on SSSGame.DynamicItemObject.OnEnable/OnDisable). ASKA also keeps every loose
// world item as a data record (SSSGame.InventoryItemInstance) in per-tile buffers owned by
// SSSGame.WorldDataManager. This probe counts data-layer records versus active (spawned) ones, so
// we learn whether a data-layer sweep would reach further than the current object-layer one.
[BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
public class Plugin : BasePlugin
{
    internal static ManualLogSource Logger = null!;

    internal static ConfigEntry<string> DumpHotkeyRaw = null!;
    internal static ConfigEntry<int> MaxNamesLogged = null!;
    internal static KeyCode HotKey = KeyCode.F8;

    public override void Load()
    {
        Logger = base.Log;

        DumpHotkeyRaw = Config.Bind(
            "GroundItemAudit", "DumpHotkey", "F8",
            "Key that dumps the ground-item data-layer audit to this log. F8 is the only function "
            + "key not already claimed by another probe/mod in this repo (F4, F5, F6, F7, F9, F10, "
            + "F11, F12 are all taken) - do not change this back to F6.");

        MaxNamesLogged = Config.Bind(
            "GroundItemAudit", "MaxNamesLogged", 25,
            "How many distinct item names to print in the per-name breakdown.");

        if (!Enum.TryParse<KeyCode>(DumpHotkeyRaw.Value, true, out HotKey))
        {
            HotKey = KeyCode.F8;
            Logger.LogWarning($"[GroundItemAudit] Unrecognised DumpHotkey '{DumpHotkeyRaw.Value}' - using F8.");
        }

        ClassInjector.RegisterTypeInIl2Cpp<GroundItemAuditRunner>();
        var go = new GameObject("GroundItemAuditMod_Runner");
        UnityEngine.Object.DontDestroyOnLoad(go);
        go.AddComponent<GroundItemAuditRunner>();

        Logger.LogInfo($"[GroundItemAudit] GroundItemAuditMod v{MyPluginInfo.PLUGIN_VERSION} loaded. "
                       + $"Load a world, then press {HotKey}.");
    }
}

// Registered MonoBehaviour + Update() polling - the project's standard input idiom (game Action
// events cannot be subscribed through this interop layer).
public class GroundItemAuditRunner : MonoBehaviour
{
    private void Update()
    {
        try
        {
            if (Input.GetKeyDown(Plugin.HotKey)) ItemDataDump.Run();
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"[GroundItemAudit] runner error: {ex}");
        }
    }
}
