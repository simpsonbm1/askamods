using System;
using System.Collections.Generic;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Unity.IL2CPP;
using BepInEx.Logging;
using HarmonyLib;
using Il2CppInterop.Runtime.Injection;
using SandSailorStudio.Inventory;
using SSSGame;
using SSSGame.Combat;
using UnityEngine;

namespace VillagerAmmoMod;

[BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
public class Plugin : BasePlugin
{
    internal static ManualLogSource Logger = null!;

    // --- config ---
    // Tracker re-reads the file periodically so settings can be changed mid-session (SeedScout /
    // GroundItemVacuum pattern).
    internal static ConfigFile? Cfg;
    internal static ConfigEntry<bool> Enabled = null!;
    internal static ConfigEntry<bool> RefundOnlyWhenShooting = null!;
    internal static ConfigEntry<float> RecentShootingWindowSeconds = null!;
    internal static ConfigEntry<bool> EnableDiagnostics = null!;

    // --- live state ---
    // v0.1.2: NO patches on ammo-event methods (see Patches/AmmoPatches.cs header for why). Instead
    // non-player RangedManagers are captured via a safe parameterless Awake postfix into this
    // registry, and AmmoTracker.Update() polls them ~2x/second to detect ammo-count drops.
    internal static readonly HashSet<RangedManager> Registry = new();
    internal static readonly object RegistryLock = new();

    // Last-seen ammo count per tracked manager, used by the poller to detect consumption between
    // polls. Reference-keyed on the exact wrapper instance captured in the Awake postfix - the
    // registry holds exactly one wrapper per native object, so this is safe.
    internal static readonly Dictionary<RangedManager, int> Baselines = new();

    // Last Time.time a tracked manager was seen in Aim/Fire/Reload, read once per poll (not just
    // on a drop). Closes the poll-race leak where a shot's ammo drop is only observed after the
    // manager's aim cycle has already returned to StandBy.
    internal static readonly Dictionary<RangedManager, float> LastShootingSeen = new();

    // Last-known ItemInfo per quiver ItemContainer (keyed by native pointer), for the case where
    // the container is already empty at poll time (the removed item was the last one).
    internal static readonly Dictionary<IntPtr, ItemInfo> InfoCache = new();

    internal static PlayerCharacter? LocalPlayer;

    public override void Load()
    {
        Logger = base.Log;
        Cfg = Config;

        Enabled = Config.Bind(
            "VillagerAmmo", "Enabled", true,
            "Master switch. When true, ammo consumption detected on a villager's (non-player) ranged-manager ammo stack by the mod's polling loop is refunded, so their ammo never runs out. Diagnostics still log when this is off.");

        RefundOnlyWhenShooting = Config.Bind(
            "VillagerAmmo", "RefundOnlyWhenShooting", true,
            "When true, only refund a detected ammo drop whose ranged-manager State (at poll time) is Aim, Fire, or Reload - i.e. it looks like an actual shot. When false, refund EVERY detected villager ammo drop regardless of state; this is a testing lever and can duplicate ammo on plain inventory transfers, so leave it true once verified.");

        RecentShootingWindowSeconds = Config.Bind(
            "VillagerAmmo", "RecentShootingWindowSeconds", 3.0f,
            "A detected ammo drop is still refunded if the villager was seen aiming/firing/reloading within this many seconds, even though the poll caught them back in StandBy - closes the poll-race leak where a shot's ammo drop is only observed after the aim cycle has already returned to StandBy. Drops outside this window are treated as deliberate withdrawals. Only meaningful while RefundOnlyWhenShooting is true.");

        EnableDiagnostics = Config.Bind(
            "VillagerAmmo", "EnableDiagnostics", true,
            "Verbose logging of ammo consumption/refunds seen by the mod's polling loop (consumption detected by polling ammo counts ~2x/second, not by an event patch). Defaults to true until the mod's behavior is verified in-game; set false afterward to keep logs clean.");

        ClassInjector.RegisterTypeInIl2Cpp<AmmoTracker>();

        var go = new GameObject("VillagerAmmoMod_Tracker");
        UnityEngine.Object.DontDestroyOnLoad(go);
        go.AddComponent<AmmoTracker>();

        var harmony = new Harmony(MyPluginInfo.PLUGIN_GUID);
        harmony.PatchAll();

        Logger.LogInfo($"VillagerAmmoMod v{MyPluginInfo.PLUGIN_VERSION} loaded (polling mode). Enabled={Enabled.Value}, RefundOnlyWhenShooting={RefundOnlyWhenShooting.Value}, RecentShootingWindowSeconds={RecentShootingWindowSeconds.Value}, EnableDiagnostics={EnableDiagnostics.Value}");
    }
}
