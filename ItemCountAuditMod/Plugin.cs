using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using Il2CppInterop.Runtime.Injection;
using UnityEngine;

namespace ItemCountAuditMod;

// Diagnostic probe (NOT for Nexus; handed one-off to a reporter over Discord, user ruling
// 2026-09-10). F3 is READ-ONLY. Ctrl+F3 is the one write: it evicts stacks whose count is <= 0
// through the game's own ItemContainer.RemoveItem. Chases the Nexus report on DynamicVillagerNeedsMod (mugsy33, 2026-09-09): the game floods
// its log with `Someone added negative quantities to item manifest, ignored!`. Cpp2IL (2026-09-09)
// shows that line comes from SandSailorStudio.Inventory.ItemManifest.AddItem when quantity < 0, and
// ItemManifest.FillFromContainer feeds AddItem every Item.count in a container. So one Item whose
// count has gone below zero re-fires the error every time a manifest is built from its container.
// Item.Remove(int) is a bare `count -= quantity` with no floor, so a double-consume can do that.
//
// This probe answers: WHICH item, in WHICH container, has count <= 0, and WHEN did that first appear.
//   * Harmony prefix on UnityEngine.Debug.LogError(object): when the message is the manifest error,
//     count it and run the scan once per ErrorScanCooldownSeconds window.
//   * Hotkey (F3 by default) runs the same scan on demand.
[BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
public class Plugin : BasePlugin
{
    internal static ManualLogSource Logger = null!;

    internal static ConfigEntry<string> ScanHotkeyRaw = null!;
    internal static ConfigEntry<float> ErrorScanCooldownSeconds = null!;
    internal static ConfigEntry<bool> LogEveryItem = null!;
    internal static KeyCode HotKey = KeyCode.F3;

    public override void Load()
    {
        Logger = base.Log;

        ScanHotkeyRaw = Config.Bind(
            "ItemCountAudit", "ScanHotkey", "F3",
            "Press in-world to list every storage stack whose count is 0 or below, with the item name and "
            + "where it is relative to you. Ctrl + this key removes those stacks through the game's own "
            + "container removal. F3 is unclaimed by every other mod/probe in this repo.");

        ErrorScanCooldownSeconds = Config.Bind(
            "ItemCountAudit", "ErrorScanCooldownSeconds", 10f,
            "After the game logs the 'negative quantities to item manifest' error, run the audit at most "
            + "once per this many seconds. The error repeats many times per second once an item is bad.");

        LogEveryItem = Config.Bind(
            "ItemCountAudit", "LogEveryItem", false,
            "true = print every item with its count on each scan (very long). false = print only items "
            + "whose count is <= 0, plus per-owner totals.");

        if (!Enum.TryParse<KeyCode>(ScanHotkeyRaw.Value, true, out HotKey))
        {
            HotKey = KeyCode.F3;
            Logger.LogWarning($"[ItemCountAudit] Unrecognised ScanHotkey '{ScanHotkeyRaw.Value}' - using F3.");
        }

        var harmony = new Harmony(MyPluginInfo.PLUGIN_GUID);
        harmony.PatchAll(typeof(Plugin).Assembly);

        ClassInjector.RegisterTypeInIl2Cpp<ItemCountAuditRunner>();
        var go = new GameObject("ItemCountAuditMod_Runner");
        UnityEngine.Object.DontDestroyOnLoad(go);
        go.AddComponent<ItemCountAuditRunner>();

        Logger.LogInfo($"[ItemCountAudit] ItemCountAuditMod v{MyPluginInfo.PLUGIN_VERSION} loaded. "
                       + $"Load a world, then press {HotKey}; the audit also runs by itself when the game "
                       + "logs the negative-manifest error.");
    }
}

// Registered MonoBehaviour + Update() polling - the project's standard input idiom (game Action
// events cannot be subscribed through this interop layer). Also drains the "scan requested" flag
// set by the LogError prefix, so the scan runs on the main thread's Update rather than inside the
// game's own logging call.
public class ItemCountAuditRunner : MonoBehaviour
{
    private void Update()
    {
        try
        {
            if (Input.GetKeyDown(Plugin.HotKey))
            {
                bool ctrl = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
                if (ctrl) Experiment.Repair();
                else
                {
                    Plugin.Logger.LogInfo($"[ItemCountAudit] {Plugin.HotKey} pressed - manual audit.");
                    ItemCountScan.Run("hotkey");
                }
            }
            if (LogErrorPatch.ScanRequested)
            {
                LogErrorPatch.ScanRequested = false;
                ItemCountScan.Run($"manifest-error #{LogErrorPatch.ErrorCount}");
            }
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"[ItemCountAudit] runner error: {ex}");
        }
    }
}

// Fires on every Debug.LogError(object) the game makes. UnityEngine.Debug is not an inventory-family
// type, so this is outside the Harmony-crash gotcha (CLAUDE.md). It only READS the message.
[HarmonyPatch(typeof(Debug), nameof(Debug.LogError), typeof(Il2CppSystem.Object))]
internal static class LogErrorPatch
{
    internal static volatile bool ScanRequested;
    internal static int ErrorCount;
    private static float _lastScanRealtime = -1000f;
    private static bool _fireVerified;

    static void Prefix(Il2CppSystem.Object message)
    {
        try
        {
            if (!_fireVerified)
            {
                _fireVerified = true;
                Plugin.Logger.LogInfo("[ItemCountAudit] Debug.LogError prefix fired (fire-verified).");
            }
            string text = "";
            try { text = message?.ToString() ?? ""; } catch { }
            if (!text.Contains("negative quantities to item manifest")) return;

            ErrorCount++;
            float now = Time.realtimeSinceStartup;
            if (now - _lastScanRealtime < Plugin.ErrorScanCooldownSeconds.Value) return;
            _lastScanRealtime = now;
            Plugin.Logger.LogInfo($"[ItemCountAudit] manifest error seen (total {ErrorCount}) - audit requested.");
            ScanRequested = true;
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"[ItemCountAudit] LogError prefix: {ex}");
        }
    }
}
