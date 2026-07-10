using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;

namespace SummonTimerMod;

[BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
public class Plugin : BasePlugin
{
    internal static new ManualLogSource Log = null!;

    internal static ConfigEntry<bool> Enabled = null!;
    internal static ConfigEntry<float> TimerMultiplier = null!;
    internal static ConfigEntry<bool> Diagnostics = null!;

    public override void Load()
    {
        Log = base.Log;

        Enabled = Config.Bind(
            section: "SummonTimer",
            key: "Enabled",
            defaultValue: true,
            description: "If true, scales the Eye of Odin villager-summon wait timer by TimerMultiplier.");

        TimerMultiplier = Config.Bind(
            section: "SummonTimer",
            key: "TimerMultiplier",
            defaultValue: 0.0f,
            description: "Multiplier on the villager-summon wait timer. 0 = no wait (instant), 0.5 = half the vanilla wait, 1 = vanilla.");

        Diagnostics = Config.Bind(
            section: "SummonTimer",
            key: "Diagnostics",
            defaultValue: true,
            description: "If true, logs verbose diagnostics about VillagerOutlet timer state.");

        var harmony = new Harmony(MyPluginInfo.PLUGIN_GUID);
        harmony.PatchAll();

        Log.LogInfo($"Plugin {MyPluginInfo.PLUGIN_GUID} is loaded!");
    }
}
