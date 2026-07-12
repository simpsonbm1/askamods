using BepInEx;
using BepInEx.Configuration;
using BepInEx.Unity.IL2CPP;
using BepInEx.Logging;
using HarmonyLib;
using Il2CppInterop.Runtime.Injection;
using SSSGame;
using UnityEngine;

namespace OuthouseComposterMod;

// v0.2.0 — Phase 1, the actual feature: the mod now WRITES game state (host/solo-authority gated).
// v0.1.x was a read-only recon (NEW_MOD_IDEAS_PLAN.md idea 13) that established: the Outhouse has a
// unique ItemContainer (containerType 'Storage_SmallItems_Outhouse', capacity 20, not shared with
// any other structure), and ItemContainer.CanStoreItemType applies some per-item condition beyond
// storage-class membership that rejects fresh food/seeds while accepting Compost (same storageClass
// asset, proven by identical native pointers in the v0.1.2 probe) — we don't reverse-engineer that
// condition, we override it for the outhouse container only (Patches/AcceptancePatches.cs).
//
// The feature: the player throws food/seeds into the Outhouse; independent repeating timers
// (ComposterDiag's converter half) consume a full ratio's worth and produce Compost in the same
// container. Diagnostics (F10 dump, world-session recon) are unchanged from v0.1.x and stay
// read-only; EnableDiagnostics only gates logging verbosity, never the feature itself — the
// converter and acceptance patches always run once the mod is loaded (subject to host/solo gating).
//
// v0.3.0: the converter timers moved from real time (DateTime.UtcNow) onto the IN-GAME clock
// (SSSGame.Weather.WeatherSystem anchor + GetTimeDifferenceFromCurrentGameTimeInSeconds — the same
// opaque-units anchor+measure pattern as TreeRespawnMod's WellRefill.Tick(), see docs/mods/tree-
// respawn.md), so TimeWarpMod-style time acceleration now speeds up composting. Config is
// expressed in in-game hours (FoodGameHours/SeedGameHours) instead of real-time minutes.
//
// v0.4.0: two independent additions. (1) SimultaneousConversion config toggle — default false keeps
// the existing "sequential" fire behavior (pools across slots, one conversion per fire), true adds a
// "simultaneous" mode where every occupied slot holding >= a full ratio converts independently in the
// same fire (no cross-slot pooling). (2) Split InputStackSize into FoodStackSize/SeedStackSize so
// food and seed inputs can be given different forced stack sizes.
//
// v1.0.0: ship polish — SimultaneousConversion description leads with an explicit no-pooling
// warning, and EnableDiagnostics default flips to false now the mod is verified in-game.
[BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
public class Plugin : BasePlugin
{
    internal static ManualLogSource Logger = null!;

    internal static ConfigEntry<bool> EnableDiagnostics = null!;
    internal static ConfigEntry<string> DumpKey = null!;
    internal static ConfigEntry<string> StructureNameMatch = null!;

    // ── [Composter] — the actual feature (v0.2.0) ───────────────────────────────────────────────
    internal static ConfigEntry<bool> AcceptFood = null!;
    internal static ConfigEntry<bool> AcceptSeeds = null!;
    internal static ConfigEntry<int> FoodToCompostRatio = null!;
    internal static ConfigEntry<float> FoodGameHours = null!;
    internal static ConfigEntry<int> SeedsToCompostRatio = null!;
    internal static ConfigEntry<float> SeedGameHours = null!;
    internal static ConfigEntry<string> CompostItemName = null!;
    internal static ConfigEntry<string> FoodCategoryMatch = null!;
    internal static ConfigEntry<int> FoodStackSize = null!;
    internal static ConfigEntry<int> SeedStackSize = null!;
    internal static ConfigEntry<bool> SimultaneousConversion = null!;

    // Captured by Patches/LifecyclePatches.cs (PlayerCharacter.Spawned/Despawned postfixes, gated on
    // HasAuthority). Despawned clearing this also doubles as world-leave state clear. Also the
    // source of the converter's host/solo authority check (LocalPlayer.NetworkObject.Runner).
    internal static PlayerCharacter? LocalPlayer;

    public override void Load()
    {
        Logger = base.Log;

        EnableDiagnostics = Config.Bind(
            section: "Diagnostics",
            key: "EnableDiagnostics",
            defaultValue: false,
            description: "Master switch for OuthouseComposter diagnostic logging (world-session recon, hotkey/auto dump, per-conversion log lines). Shipped default false — flip to true when troubleshooting.");

        DumpKey = Config.Bind(
            section: "Diagnostics",
            key: "DumpKey",
            defaultValue: "F10",
            description: "Hotkey (UnityEngine.KeyCode name) that triggers a full structure/container/compost dump on demand.");

        StructureNameMatch = Config.Bind(
            section: "Diagnostics",
            key: "StructureNameMatch",
            defaultValue: "Outhouse",
            description: "Case-insensitive substring matched against a structure's display name OR Structure.DefaultName to find the outhouse. Also drives the converter's structure discovery (v0.2.0) — the same substring is used to find which structures to run the composter on.");

        AcceptFood = Config.Bind(
            section: "Composter",
            key: "AcceptFood",
            defaultValue: true,
            description: "Whether the outhouse accepts and converts food items (any item whose ItemInfo.category name matches FoodCategoryMatch and whose storageClass is NOT SeedItem).");

        AcceptSeeds = Config.Bind(
            section: "Composter",
            key: "AcceptSeeds",
            defaultValue: true,
            description: "Whether the outhouse accepts and converts seed items (storageClass asset name == 'SeedItem').");

        FoodToCompostRatio = Config.Bind(
            section: "Composter",
            key: "FoodToCompostRatio",
            defaultValue: 1,
            description: "How many food units are consumed per Compost produced. No partial conversions — the food timer skips (and re-arms) if the container's food pool is below this amount when it fires.");

        FoodGameHours = Config.Bind(
            section: "Composter",
            key: "FoodGameHours",
            defaultValue: 5.0f,
            description: "In-game hours between food→Compost conversion attempts, per outhouse. 1 in-game hour = dayLength/24 ≈ 60 real seconds at normal speed; scales with time acceleration (e.g. TimeWarpMod fast-forward). The timer only starts once the container's food pool first becomes non-empty.");

        SeedsToCompostRatio = Config.Bind(
            section: "Composter",
            key: "SeedsToCompostRatio",
            defaultValue: 20,
            description: "How many seed units are consumed per Compost produced. No partial conversions.");

        SeedGameHours = Config.Bind(
            section: "Composter",
            key: "SeedGameHours",
            defaultValue: 10.0f,
            description: "In-game hours between seeds→Compost conversion attempts, per outhouse. Same in-game-hour scale as FoodGameHours.");

        CompostItemName = Config.Bind(
            section: "Composter",
            key: "CompostItemName",
            defaultValue: "Compost",
            description: "Name of the item produced by conversion (resolved from FarmingStation.composts, falling back to a name-scan of the outhouse container's own contents). Also excluded from ever being treated as an input.");

        FoodCategoryMatch = Config.Bind(
            section: "Composter",
            key: "FoodCategoryMatch",
            defaultValue: "Food",
            description: "Comma-separated, case-insensitive substrings matched against ItemInfo.category.Name. An item (that isn't a seed) counts as food if its category name contains any listed substring. The [accept] log lines (when EnableDiagnostics=true) show each item's actual category name — use them to tune this list.");

        FoodStackSize = Config.Bind(
            section: "Composter",
            key: "FoodStackSize",
            defaultValue: 10,
            description: "Forced stack size for accepted FOOD inputs in the outhouse container (ItemContainer.GetStackSize returns 0 for them natively — this is the override).");

        SeedStackSize = Config.Bind(
            section: "Composter",
            key: "SeedStackSize",
            defaultValue: 200,
            description: "Forced stack size for accepted SEED inputs in the outhouse container. Seeds stack to 200 in other storage containers; match that here.");

        SimultaneousConversion = Config.Bind(
            section: "Composter",
            key: "SimultaneousConversion",
            defaultValue: false,
            description: "WARNING: in simultaneous mode there is NO cross-slot pooling — a slot only converts if that single slot holds a full ratio's worth (e.g. two stacks of 10 seeds at ratio 20 will NEVER combine into a conversion; sequential mode is what pools across slots). false = sequential: each timer fire performs ONE conversion per outhouse, consuming the ratio's worth from the first matching slot(s) in container order. true = simultaneous: each timer fire evaluates every occupied food/seed slot independently; each slot holding at least a full ratio's worth converts once (consumes ratio units from that slot, produces 1 Compost), all in the same fire.");

        // Register our custom Mono class into IL2CPP
        ClassInjector.RegisterTypeInIl2Cpp<ComposterDiag>();

        // Spawn our persistent diagnostic GameObject
        var go = new GameObject("OuthouseComposterMod_Diag");
        UnityEngine.Object.DontDestroyOnLoad(go);
        go.AddComponent<ComposterDiag>();

        var harmony = new Harmony(MyPluginInfo.PLUGIN_GUID);
        harmony.PatchAll();

        Logger.LogInfo($"[OuthouseComposter] OuthouseComposterMod v{MyPluginInfo.PLUGIN_VERSION} loaded — Phase 1 composter ACTIVE (host/solo authority gated). AcceptFood={AcceptFood.Value} ({FoodToCompostRatio.Value}:1 / {FoodGameHours.Value}h game-time) AcceptSeeds={AcceptSeeds.Value} ({SeedsToCompostRatio.Value}:1 / {SeedGameHours.Value}h game-time) CompostItemName='{CompostItemName.Value}' SimultaneousConversion={SimultaneousConversion.Value} FoodStack={FoodStackSize.Value} SeedStack={SeedStackSize.Value} DumpKey='{DumpKey.Value}' StructureNameMatch='{StructureNameMatch.Value}' EnableDiagnostics={EnableDiagnostics.Value}.");
    }
}
