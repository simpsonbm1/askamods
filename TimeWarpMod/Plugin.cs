using BepInEx;
using BepInEx.Configuration;
using BepInEx.Unity.IL2CPP;
using BepInEx.Logging;
using Il2CppInterop.Runtime.Injection;
using UnityEngine;

namespace TimeWarpMod;

[BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
public class Plugin : BasePlugin
{
    internal static ManualLogSource Logger = null!;

    internal static ConfigEntry<string> FastForwardHotkey = null!;
    internal static ConfigEntry<string> SkipDayHotkey = null!;
    internal static ConfigEntry<bool> TimeDiagnostics = null!;
    internal static ConfigEntry<float> DiagnosticsIntervalSeconds = null!;

    public override void Load()
    {
        Logger = base.Log;

        FastForwardHotkey = Config.Bind(
            section: "General",
            key: "FastForwardHotkey",
            defaultValue: "k",
            description: "Toggle the game's built-in fast-forward (WeatherSystem.Rpc_ToggleFastForward). Dev/test tool — will distort normal gameplay.");

        SkipDayHotkey = Config.Bind(
            section: "General",
            key: "SkipDayHotkey",
            defaultValue: "l",
            description: "Jump the calendar forward one day (WeatherSystem.Rpc_SetDayOfYear(DayOfYear+1)). Dev/test tool.");

        TimeDiagnostics = Config.Bind(
            section: "Diagnostics",
            key: "TimeDiagnostics",
            defaultValue: true,
            description: "If true, periodically logs the game's time/weather state for debugging.");

        DiagnosticsIntervalSeconds = Config.Bind(
            section: "Diagnostics",
            key: "DiagnosticsIntervalSeconds",
            defaultValue: 10.0f,
            description: "How often (in seconds) to dump time diagnostics when TimeDiagnostics is enabled.");

        // Register our custom Mono class into IL2CPP
        ClassInjector.RegisterTypeInIl2Cpp<TimeWarpTracker>();

        // Spawn our persistent manager GameObject
        var go = new GameObject("TimeWarpMod_Tracker");
        UnityEngine.Object.DontDestroyOnLoad(go);
        go.AddComponent<TimeWarpTracker>();

        Logger.LogInfo($"TimeWarpMod v{MyPluginInfo.PLUGIN_VERSION} loaded. FastForward='{FastForwardHotkey.Value}', SkipDay='{SkipDayHotkey.Value}' — TEST TOOL, distorts game time.");
    }
}
