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
// The feature: the player throws food/seeds into the Outhouse; independent real-time repeating
// timers (ComposterDiag's converter half) consume a full ratio's worth and produce Compost in the
// same container. Diagnostics (F10 dump, world-session recon) are unchanged from v0.1.x and stay
// read-only; EnableDiagnostics only gates logging verbosity, never the feature itself — the
// converter and acceptance patches always run once the mod is loaded (subject to host/solo gating).
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
    internal static ConfigEntry<float> FoodMinutes = null!;
    internal static ConfigEntry<int> SeedsToCompostRatio = null!;
    internal static ConfigEntry<float> SeedMinutes = null!;
    internal static ConfigEntry<string> CompostItemName = null!;
    internal static ConfigEntry<string> FoodCategoryMatch = null!;
    internal static ConfigEntry<int> InputStackSize = null!;

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
            defaultValue: true,
            description: "Master switch for the OuthouseComposter Phase 0 read-only recon (world-session tracking, hotkey dump, auto-dump). Default true (project rule for pre-verification diagnostics) — flip to false once Phase 0 recon is complete.");

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

        FoodMinutes = Config.Bind(
            section: "Composter",
            key: "FoodMinutes",
            defaultValue: 5.0f,
            description: "Real-time minutes between food→Compost conversion attempts, per outhouse. The timer only starts once the container's food pool first becomes non-empty.");

        SeedsToCompostRatio = Config.Bind(
            section: "Composter",
            key: "SeedsToCompostRatio",
            defaultValue: 20,
            description: "How many seed units are consumed per Compost produced. No partial conversions.");

        SeedMinutes = Config.Bind(
            section: "Composter",
            key: "SeedMinutes",
            defaultValue: 10.0f,
            description: "Real-time minutes between seeds→Compost conversion attempts, per outhouse.");

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

        InputStackSize = Config.Bind(
            section: "Composter",
            key: "InputStackSize",
            defaultValue: 10,
            description: "Forced stack size for accepted food/seed inputs in the outhouse container (ItemContainer.GetStackSize returned 0 for them natively — this is the override).");

        // Register our custom Mono class into IL2CPP
        ClassInjector.RegisterTypeInIl2Cpp<ComposterDiag>();

        // Spawn our persistent diagnostic GameObject
        var go = new GameObject("OuthouseComposterMod_Diag");
        UnityEngine.Object.DontDestroyOnLoad(go);
        go.AddComponent<ComposterDiag>();

        var harmony = new Harmony(MyPluginInfo.PLUGIN_GUID);
        harmony.PatchAll();

        Logger.LogInfo($"[OuthouseComposter] OuthouseComposterMod v{MyPluginInfo.PLUGIN_VERSION} loaded — Phase 1 composter ACTIVE (host/solo authority gated). AcceptFood={AcceptFood.Value} ({FoodToCompostRatio.Value}:1 / {FoodMinutes.Value}min) AcceptSeeds={AcceptSeeds.Value} ({SeedsToCompostRatio.Value}:1 / {SeedMinutes.Value}min) CompostItemName='{CompostItemName.Value}' DumpKey='{DumpKey.Value}' StructureNameMatch='{StructureNameMatch.Value}' EnableDiagnostics={EnableDiagnostics.Value}.");
    }
}
