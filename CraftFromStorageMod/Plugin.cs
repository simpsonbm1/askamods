using System;
using System.Runtime.InteropServices;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Unity.IL2CPP;
using BepInEx.Logging;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using Il2CppInterop.Runtime.Injection;
using SSSGame;
using UnityEngine;

namespace CraftFromStorageMod;

// idea-17 (NEW_MOD_IDEAS_PLAN.md "17. Craft from settlement storage"). Phase 0 (v0.1.x/v0.2.0) was a
// READ-ONLY diagnostic spike tracing the live player-crafting call flow (CraftInteraction family) so
// Phase 1 could be designed against verified facts instead of Cecil-inferred guesses - those trace
// patches (GatePatches.cs / CraftWatcher.cs) are all still present and still log, unchanged.
// v0.3.0 (Phase 1, this file's EnablePlayerPull config) adds the actual feature on top of the SAME
// patches: when the PLAYER is missing ingredients at a crafting station, missing items are pulled
// just-in-time from non-blacklisted settlement storage into the player's inventory (SettlementStock.
// cs / CraftTransfer.cs), and unconsumed leftovers are swept back afterward. Villager crafting is
// OUT OF SCOPE (Phase 2) and untouched. All log lines carry the "[CFS]" prefix.
// v0.6.0 (Phase 2a) is another READ-ONLY diagnostic spike, this time on the VILLAGER side: it traces
// the CraftingStation FSM chain (FSM_FetchCraftingSupplies/FSM_UseCraftingStation/
// FSM_ReturnCraftingSupplies) and the CrafterFetchQuestData.IsWhitelistedByStorage check to answer,
// from the log alone, whether vanilla's "a villager can only use its OWN station's storage" limit is
// a fetch-REACH restriction or a storage WHITELIST - the two candidate levers a future Phase 2
// feature would need to move. Nothing is moved here. New patches live in
// Patches/VillagerFetchPatches.cs / VillagerFetchTrace.cs; their log lines carry the "[CFS-P2]"
// prefix (distinct from Phase 0/1's "[CFS]") so this spike's output greps cleanly on its own.
// v0.7.0 (Phase 2) extends the SAME storage-pull mechanism (CraftTransfer.cs) to the VILLAGER
// crafting agent via the new EnableForVillagers config, independent of EnablePlayerPull. Villager
// pulls land in the STATION's own inventory rather than the villager's carried inventory (ground
// truth confirmed in-game 2026-07-21: villager craft consumption was observed on the station's
// collections). The transfer ledger (CraftTransfer._ledger) is now keyed per-agent so concurrent
// villager crafts can't cross-attribute. The v0.6.0 read-only fetch-FSM spike above is untouched
// and remains the way to check whether this actually suppresses the villager's vanilla supply
// trip - watch its "[CFS-P2] CYCLE SUMMARY verdict=DIRECT|TOURED" line against this phase's
// "[CFS-V]"-tagged pull/verify/sweep lines.
[BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
public class Plugin : BasePlugin
{
    internal static ManualLogSource Logger = null!;
    internal static ConfigFile? Cfg;

    // --- config: Trace (all default true - unverified diagnostic mod, project rule) ---
    internal static ConfigEntry<bool> TraceCheckOwnedRequirements = null!;
    internal static ConfigEntry<bool> TraceBeginCraftingSequence = null!;
    internal static ConfigEntry<bool> TraceOnCraftingSuccess = null!;
    internal static ConfigEntry<bool> TraceActivateBlueprint = null!;
    internal static ConfigEntry<bool> TraceRecipeListUI = null!;
    internal static ConfigEntry<bool> VerboseGateLogging = null!;

    // --- config: Watch (v0.2.0 craft delta watcher - resolves the "where does consumption
    // actually happen" blocker; see CraftWatcher.cs) ---
    internal static ConfigEntry<bool> EnableCraftWatcher = null!;
    internal static ConfigEntry<float> WatchWindowSeconds = null!;
    internal static ConfigEntry<float> PollIntervalSeconds = null!;

    // --- config: Census ---
    internal static ConfigEntry<string> CensusHotkey = null!;
    internal static ConfigEntry<bool> IncludeEquipmentProbe = null!;
    internal static ConfigEntry<bool> IncludeWorkstationStock = null!;

    // --- config: Transfer (v0.3.0 Phase 1 - the actual storage-pull feature; v0.7.0 Phase 2 extends
    // it to villager crafting via EnableForVillagers, independent of EnablePlayerPull) ---
    internal static ConfigEntry<bool> EnablePlayerPull = null!;
    internal static ConfigEntry<bool> EnableForVillagers = null!;
    internal static ConfigEntry<bool> SweepBackLeftovers = null!;
    internal static ConfigEntry<float> SnapshotTtlSeconds = null!;
    internal static ConfigEntry<string> BlacklistContainerTypes = null!;
    internal static ConfigEntry<string> SourceNodeAllowlist = null!;
    internal static ConfigEntry<bool> TransferDiagnostics = null!;
    internal static ConfigEntry<bool> StockStationOnFetch = null!;
    internal static ConfigEntry<string> SkipBlueprintClasses = null!;
    internal static ConfigEntry<string> SkipStationClasses = null!;
    internal static ConfigEntry<bool> StockTransformStationMaterials = null!;

    // --- config: UI (v0.4.0 idea-17 UI follow-up - settlement-stock requirement-UI feature; folds
    // cached settlement stock into the crafting menu's per-ingredient have/need text. PURELY
    // ADDITIVE - writes no game state. See CraftUiAvailability.cs / Patches/UiAvailabilityPatches.cs) ---
    internal static ConfigEntry<bool> ShowSettlementStockInUI = null!;
    internal static ConfigEntry<bool> UiDiagnostics = null!;
    internal static ConfigEntry<float> UiPollSeconds = null!;

    // --- config: Probe (v0.6.0 idea-17 Phase 2a - READ-ONLY villager-fetch diagnostic spike; traces
    // the CraftingStation FSM chain (Fetch/Use/Return) + the storage whitelist check to answer, from
    // the log alone, whether the vanilla "villager can only use its own station's storage" limit is
    // a fetch-REACH restriction or a storage WHITELIST. See Patches/VillagerFetchPatches.cs /
    // VillagerFetchTrace.cs. Does NOT move either lever - pure observation, no game state written.) ---
    internal static ConfigEntry<bool> EnableVillagerFetchTrace = null!;
    internal static ConfigEntry<bool> TraceStorageWhitelist = null!;
    internal static ConfigEntry<int> MaxWhitelistLogsPerCycle = null!;

    // v0.15.0 idea-17 next-feature groundwork - READ-ONLY bloomery/kiln supply-fetch diagnostic
    // spike (BloomeryTrace.cs / Patches/BloomeryFetchPatches.cs). See that file's header for the
    // three questions it answers ahead of extending toggle 2 to deliver ore/coal into a bloomery.
    internal static ConfigEntry<bool> EnableBloomeryTrace = null!;

    // --- live state ---
    internal static PlayerCharacter? LocalPlayer;

    public override void Load()
    {
        Logger = base.Log;
        Cfg = Config;

        // ==== [1. Features] - user-facing master switches, bound first so they lead the cfg file. ====
        EnablePlayerPull = Config.Bind(
            "1. Features", "EnablePlayerPull", true,
            "v0.3.0 Phase 1 MASTER SWITCH: when the PLAYER is missing ingredients at a crafting station, pull them " +
            "just-in-time from any non-blacklisted settlement storage container into the player's inventory, then " +
            "sweep unconsumed leftovers back afterward. Independent of EnableForVillagers - either toggle can be " +
            "on alone. Set false to fully disable the player half and fall back to vanilla craft-gating behavior.");

        EnableForVillagers = Config.Bind(
            "1. Features", "EnableForVillagers", false,
            "v0.7.0 Phase 2 MASTER SWITCH: when a VILLAGER is missing ingredients at a crafting station, pull them " +
            "just-in-time from any non-blacklisted settlement storage container into the STATION's own inventory " +
            "(NOT the villager's carried inventory - ground truth confirmed in-game 2026-07-21: villager craft " +
            "consumption was observed on the station's collections), then sweep unconsumed leftovers back " +
            "afterward. Independent of EnablePlayerPull - either toggle can be on alone. It is NOT yet known " +
            "whether this actually suppresses the villager's vanilla supply-fetch trip (FSM_FetchCraftingSupplies) " +
            "or whether that trip is scheduled off the station's own supply manifests independently - compare the " +
            "'[CFS-V]' tagged pull/verify/sweep lines this feature emits against the v0.6.0 spike's " +
            "'[CFS-P2] CYCLE SUMMARY verdict=DIRECT|TOURED' line to check. Defaults false so a fresh install " +
            "affects only the player; enable to let villager crafters draw on settlement storage too.");

        StockTransformStationMaterials = Config.Bind(
            "1. Features", "StockTransformStationMaterials", false,
            "v0.11.0 (TRANSFORM_STATION_PLAN.md): when true, the villager station stocker DOES deliver " +
            "materials into a physical-transform station's own material rack, even though the mod " +
            "otherwise stands aside at those stations. Confirmed in-game 2026-07-29 that this keeps a " +
            "carpenter's rack topped up one item at a time so she never walks to a warehouse for a log " +
            "or a long stick. Defaults false because it is the mod acting at a station it is otherwise " +
            "told to leave alone. Has NO effect on the craft-availability check, which always stands " +
            "aside at these stations - reporting a transform recipe already satisfied is what stalled " +
            "villagers entirely.");

        ShowSettlementStockInUI = Config.Bind(
            "1. Features", "ShowSettlementStockInUI", true,
            "v0.4.0 MASTER SWITCH: fold settlement-stock quantities into the crafting menu's per-ingredient " +
            "have/need display (ItemThumbnailPanel.availability) so a row reads e.g. '3/1' - settlement stock " +
            "counted as if owned - instead of the vanilla-only count the gate already looks past. Nets out the " +
            "active station's own inventory so it is never double-counted (that inventory is included in both " +
            "vanilla's own count AND the settlement-wide snapshot). Purely additive - never writes game state, " +
            "only rewrites displayed TMP text; the pull/verify/sweep-back logic in CraftTransfer.cs is untouched. " +
            "Set false to leave the vanilla-only have/need display alone.");

        // ==== [2. Tuning] - values that shape already-enabled behavior. ====
        SweepBackLeftovers = Config.Bind(
            "2. Tuning", "SweepBackLeftovers", true,
            "After a successful craft, return unconsumed pulled items to the settlement containers they came from. " +
            "Setting false leaves pulled leftovers in the player's inventory instead (never swept back).");

        SnapshotTtlSeconds = Config.Bind(
            "2. Tuning", "SnapshotTtlSeconds", 5.0f,
            "How long (seconds) the cached settlement stock snapshot (SettlementStock.cs) is trusted before the " +
            "next read re-walks the settlement's structures/containers. The snapshot is also invalidated " +
            "immediately after any pull or sweep-back, regardless of this TTL.");

        BlacklistContainerTypes = Config.Bind(
            "2. Tuning", "BlacklistContainerTypes",
            "CharacterFlask,CharacterBuilder,ArmorRackHead,ArmorRackChest,ArmorRackLegs,ArmorRackGloves,ArmorRackBoots,ArmorRackShoulders,ArmorRackCape,Storage_Core,Storage_DecorationsTop,Storage_SmallItems_Outhouse,Storage_HotItemsSmall,Storage_MediumItems_L1,Storage_SmallItems_L1,Storage_SmallItems_Kiln",
            "Comma-separated container TYPE asset names (ItemContainer.containerType.name) that are NEVER drained " +
            "as a storage-pull source. The v0.2.0 structural EquipPoint probe (Census IncludeEquipmentProbe) " +
            "tagged 0 of 651 containers in-game, so Phase 1 uses this name-based blacklist instead. v0.9.3: " +
            "hot items (e.g. 'Hot Iron Bloom') are refused by a destination station inventory even when empty, " +
            "so draining them only shuffles blooms between metalworkers (confirmed in-game 2026-07-28). " +
            "v0.14.1: Storage_MediumItems_L1 is the medium materials bin at workshop tables and co-located " +
            "stations (confirmed in-game 2026-07-30); it is excluded so one workshop's staged materials are " +
            "never drained to feed another station, and so those staged items stop inflating availability counts. " +
            "v0.16.0: Storage_SmallItems_L1 is the small items bin at workshop tables (census-confirmed " +
            "2026-07-31; also used by farms). v1.1.0: this list is now overridden per-container by " +
            "SourceNodeAllowlist below, which re-admits the gathering and production OUTPUT bins that " +
            "share a type with the workshop staging bins this list exists to protect.");

        SourceNodeAllowlist = Config.Bind(
            "2. Tuning", "SourceNodeAllowlist",
            "StorageHorns,HideStorage,Fish,Bark,Firewood,Sticks,Thatch,FiberResin," +
            "FruitsStorage,MaterialsStorage,VegetablesStorage,StoneSmall,Ore,CrawlerResources," +
            "FoodStorage,FibersStorage,SeedsStorage,SticksStorage,SmolkrHornContainerArea," +
            "CoalStorageInteraction,coalPileContainer,StorageBloom,Scraps@HuntingStation",
            "v1.1.0: comma-separated container NODE names (the container GameObject's own name) that " +
            "ARE drainable as a storage-pull source even when BlacklistContainerTypes excludes their " +
            "container type. The game reuses one container type for both a station's protected INPUT " +
            "bins and its finished OUTPUT bins - a bloomery's StorageOre, StorageCoal and StorageBloom " +
            "are all Storage_SmallItems_L1 - so type alone cannot tell them apart (census-confirmed " +
            "2026-08-11). The default admits the output bins of hunter huts, fishing houses, " +
            "woodcutters, gatherers, stonecutters, mines, farms, animal pens, charcoal makers and " +
            "bloomeries. It deliberately omits bloomery StorageOre/StorageCoal and fishing Bait, which " +
            "are inputs a neighbouring station must not steal. Matched case-insensitively. Leave empty " +
            "to restore the pre-v1.1.0 behaviour where a blacklisted type is never a source. " +
            "Forestry huts and plain (non-cave) miner huts are not covered yet - no census has shown " +
            "their node names; add them here when one does, no rebuild needed. " +
            "v1.2.0 SYNTAX: an entry is either a bare node name, admitted on any building, or " +
            "'Node@StationClass', admitted only when the owning building's workstation reports that " +
            "native class. 'Scraps@HuntingStation' is the worked example: 'Scraps' is the hunter " +
            "hut's output bin, but the same node name also sits on the Blacksmith, Metalworker, " +
            "Leatherworker and Tailoring benches (census-confirmed 2026-08-11), so the qualifier " +
            "opens the hunter hut's and leaves those four workshop bins protected. Station class " +
            "names are the ones the F12 census prints on its 'station:' lines.");

        SkipBlueprintClasses = Config.Bind(
            "2. Tuning", "SkipBlueprintClasses",
            "ForgingBlueprintInfo,ForgingBlueprint,DyeingBlueprintInfo,PaintingBlueprintInfo,KnowledgeBlueprintInfo",
            "v0.9.2: comma-separated NATIVE class names of blueprint families the villager station-stocker " +
            "NEVER stocks. These craft at auxiliary stations (forge, dye/paint bench, study) rather than from " +
            "a crafting table's own bin, so moving materials into the station inventory does nothing useful. " +
            "Matched EXACTLY (case-insensitive) against the recipe's native class name, so listing a subclass " +
            "never catches its parent. Leave empty to stock every family. Only takes effect when " +
            "StockStationOnFetch=true.");

        SkipStationClasses = Config.Bind(
            "2. Tuning", "SkipStationClasses",
            "AnvilInteraction,CarpenterInteraction,DyeingInteraction",
            "v0.10.0 (TRANSFORM_STATION_PLAN.md): comma-separated NATIVE class names of station " +
            "interaction types the mod never acts on, because they are physical transforms where an " +
            "item is placed on the station rather than drawn from its bin. Matched against the " +
            "interaction's own native class name AND every ancestor class name, so a future subclass " +
            "is caught automatically. Empty disables the test.");

        StockStationOnFetch = Config.Bind(
            "2. Tuning", "StockStationOnFetch", true,
            "v0.9.0 Phase 2c: when a villager's crafting fetch quest STARTS (FSM_FetchCraftingSupplies." +
            "OnStateEnter), move the items she was about to fetch from settlement storage directly into " +
            "the crafting station's inventory, deleting the supply walk. This inverts the v0.8.0 approach: " +
            "instead of suppressing the fetch quest so it never fires, let vanilla choose it as intended " +
            "and intercept it at its start. Only takes effect when EnableForVillagers=true. Set false " +
            "to let villagers make their vanilla supply walks even with villager crafting enabled.");

        UiPollSeconds = Config.Bind(
            "2. Tuning", "UiPollSeconds", 0.2f,
            "v0.5.0: polling interval (seconds) for the settlement-stock requirement-UI feature (CraftUiPoller.cs). " +
            "Replaces the old postfix-time rewrite - in-game evidence showed the ItemThumbnailPanel postfixes run " +
            "BEFORE vanilla writes the real have/need text (count.text is still the prefab placeholder '99' at " +
            "that moment), so a postfix rewrite could never see final values. A MonoBehaviour Update() timer " +
            "re-reads and, if needed, rewrites each visible requirement row on this interval instead, while a " +
            "crafting menu is open.");

        // ==== [3. Diagnostics] - verbose/read-only logging, off by default at ship. ====
        TransferDiagnostics = Config.Bind(
            "3. Diagnostics", "TransferDiagnostics", false,
            "Verbose per-pull/per-sweep-back logging (SettlementStock rebuild summaries, PullShortfall per-item " +
            "lines, HandleCraftingSuccess sweep-back totals). Diagnostic logging only; defaults false at ship - " +
            "enable for troubleshooting.");

        UiDiagnostics = Config.Bind(
            "3. Diagnostics", "UiDiagnostics", false,
            "Verbose logging for the settlement-stock requirement-UI feature: which of _UpdateAvailablility/" +
            "_UpdateAvailabilityStatus actually fires (AOT inlining risk), raw availability.text plus panel " +
            "scoping evidence (parent native class, checkAvailability) for the first ~10 panels seen, and " +
            "rate-limited (first 5) per-item-type rewrite/unparsed-text lines. Diagnostic logging only; " +
            "defaults false at ship - enable for troubleshooting.");

        EnableBloomeryTrace = Config.Bind(
            "3. Diagnostics", "EnableBloomeryTrace", false,
            "v1.0.0: gates ONLY the diagnostic logging in BloomeryTrace.cs (fetchEnter/fetchExit lines, " +
            "the quest-data class dump, the ore/coal/bloom slot dumps, the kiln recipe/container " +
            "manifest dumps, and the part-substitution probe off a FindAnyObjectByType<Bloomstation> " +
            "instance) - it no longer gates the bloomery/kiln delivery feature itself. The four " +
            "FSM_FetchBloomerySupplies/FSM_FetchKilnSupplies patches now attach whenever this flag OR " +
            "(EnableForVillagers AND StockStationOnFetch) is true, so the delivery feature (still also " +
            "gated by StockTransformStationMaterials) works with this left at its default false. Set " +
            "true to also get the read-only diagnostic trace. See BloomeryTrace.cs / " +
            "Patches/BloomeryFetchPatches.cs.");

        EnableVillagerFetchTrace = Config.Bind(
            "3. Diagnostics", "EnableVillagerFetchTrace", false,
            "v0.6.0 Phase 2a MASTER SWITCH: READ-ONLY villager-fetch diagnostic spike. Postfix-logs " +
            "FSM_FetchCraftingSupplies.OnStateEnter/OnStateExit, FSM_UseCraftingStation.OnStateEnter " +
            "(the per-villager CYCLE SUMMARY verdict=DIRECT|TOURED line), and " +
            "FSM_ReturnCraftingSupplies.OnStateEnter. Never writes game state - observation only. " +
            "Diagnostic logging only; defaults false at ship - enable for troubleshooting.");

        TraceStorageWhitelist = Config.Bind(
            "3. Diagnostics", "TraceStorageWhitelist", false,
            "v0.6.0 Phase 2a: postfix-logs CrafterFetchQuestData.IsWhitelistedByStorage(IResourceStorageSite) " +
            "- the OTHER candidate lever (a storage whitelist rather than a fetch-reach restriction). " +
            "Logs the probed site name and the allowed/denied verdict, rate-limited by " +
            "MaxWhitelistLogsPerCycle. Never writes game state - observation only.");

        MaxWhitelistLogsPerCycle = Config.Bind(
            "3. Diagnostics", "MaxWhitelistLogsPerCycle", 40,
            "Caps how many DISTINCT storage sites get a logged line per villager per fetch/use cycle " +
            "under TraceStorageWhitelist, to avoid log spam from a villager re-probing the same sites - " +
            "every call is still COUNTED toward the CYCLE SUMMARY totals regardless of this cap.");

        EnableCraftWatcher = Config.Bind(
            "3. Diagnostics", "EnableCraftWatcher", false,
            "Master switch for the v0.2.0 delta watcher (CraftWatcher.cs). While armed it samples every candidate " +
            "ingredient ItemCollection (agent inventory, station inventory/blueprint inventory, workstation stock, " +
            "crafting-agent inventory) every PollIntervalSeconds and logs only what changed - this is what resolves " +
            "the 'where does crafting actually consume ingredients' blocker that _OnCraftingSuccess snapshots could not.");

        WatchWindowSeconds = Config.Bind(
            "3. Diagnostics", "WatchWindowSeconds", 20f,
            "How long (seconds) after BeginCraftingSequence the CraftWatcher keeps sampling before disarming and " +
            "emitting its [CFS] WATCH SUMMARY line. A single craft should complete well inside the default 20s.");

        PollIntervalSeconds = Config.Bind(
            "3. Diagnostics", "PollIntervalSeconds", 0.1f,
            "CraftWatcher sampling cadence (seconds) while armed. Lower = finer-grained timing at the cost of more " +
            "frequent snapshot work; only changed slots are ever logged, so a lower value does not spam the log.");

        TraceCheckOwnedRequirements = Config.Bind(
            "3. Diagnostics", "TraceCheckOwnedRequirements", false,
            "Postfix-log CraftInteraction.CheckOwnedRequirements(Blueprint, IInteractionAgent). This target is " +
            "public but NON-virtual, so it may have been inlined by the IL2CPP AOT compiler and this patch may " +
            "silently never fire - that is itself a key Phase 0 finding. Logs agent native class, blueprint name, and __result.");

        TraceBeginCraftingSequence = Config.Bind(
            "3. Diagnostics", "TraceBeginCraftingSequence", false,
            "Prefix-log BeginCraftingSequence(InteractionSession) on CraftInteraction, AnvilInteraction, and " +
            "DyeingInteraction (each declares its own override - all three are patched together under this flag). " +
            "Logs which type fired, the session's native class name (reveals player vs villager session), and useAgentInventory.");

        TraceOnCraftingSuccess = Config.Bind(
            "3. Diagnostics", "TraceOnCraftingSuccess", false,
            "Prefix AND Postfix snapshot of the station's ItemInventory around _OnCraftingSuccess(IInteractionAgent) " +
            "on CraftInteraction and DyeingInteraction (both patched together under this flag) - shows where " +
            "ingredient consumption actually happens (PRE vs POST totals + per-item breakdown).");

        TraceActivateBlueprint = Config.Bind(
            "3. Diagnostics", "TraceActivateBlueprint", false,
            "Postfix-log CraftInteraction.ActivateBlueprint(CraftBlueprint) - blueprint name + __result.");

        TraceRecipeListUI = Config.Bind(
            "3. Diagnostics", "TraceRecipeListUI", false,
            "Postfix-log SSSGame.UI.CreateItemsTabPage.Show(bool, TabButton) - AvailableBlueprints/UnavailableBlueprints " +
            "counts at recipe-list-open time (may be null/empty at this point, which is itself informative).");

        VerboseGateLogging = Config.Bind(
            "3. Diagnostics", "VerboseGateLogging", false,
            "v0.2.0: CheckOwnedRequirements / _CheckOwnedBlueprintManifest fire ~96x per recipe-menu-open, so by " +
            "default they only feed a periodic [CFS] GATE rollup summary (see GatePatches.cs GateRollup). Set true " +
            "to ALSO emit the old one-line-per-call logging on top of the rollup - noisy, diagnostic-only.");

        CensusHotkey = Config.Bind(
            "3. Diagnostics", "CensusHotkey", "F12",
            "Hotkey (a Unity KeyCode name, parsed via Enum.TryParse<KeyCode>) that dumps a read-only settlement " +
            "storage census to the log: per-structure container contents and a settlement-wide item total. " +
            "F6-F11 are taken by other mods in this repo - do not change the default.");

        IncludeEquipmentProbe = Config.Bind(
            "3. Diagnostics", "IncludeEquipmentProbe", true,
            "v0.2.0: per-container structural equip-slot probe (walk up for an EquipmentManager, compare its " +
            "equipPoints' ItemContainer by pointer) so armor racks / character slots are tagged and excluded from " +
            "the settlement-wide grand total instead of relying on a container-type name blacklist.");

        IncludeWorkstationStock = Config.Bind(
            "3. Diagnostics", "IncludeWorkstationStock", true,
            "v0.2.0: per-structure Workstation.GetInventory()/GetItemsNeededFromSettlement() line - the fix for " +
            "the v0.1.2 limitation where crafting stations reported a decorative CharacterFlask container instead " +
            "of their real stock, and evidence for the villager-fetch question (idea-17 Phase 1).");

        ClassInjector.RegisterTypeInIl2Cpp<StorageCensus>();
        ClassInjector.RegisterTypeInIl2Cpp<CraftWatcher>();
        ClassInjector.RegisterTypeInIl2Cpp<CraftUiPoller>();
        var go = new GameObject("CraftFromStorageMod_Census");
        UnityEngine.Object.DontDestroyOnLoad(go);
        go.AddComponent<StorageCensus>();
        go.AddComponent<CraftWatcher>();
        go.AddComponent<CraftUiPoller>();

        if (!EnableCraftWatcher.Value)
            Logger.LogInfo("[CFS] EnableCraftWatcher=false - CraftWatcher will not arm on BeginCraftingSequence.");

        var harmony = new Harmony(MyPluginInfo.PLUGIN_GUID);

        // Lifecycle (local-player capture) is always patched - safe param types (PlayerCharacter only),
        // no inventory-family exposure, needed for the census hotkey regardless of which Trace flags are on.
        harmony.PatchAll(typeof(Patches.PlayerSpawnedPatch));
        harmony.PatchAll(typeof(Patches.PlayerDespawnedPatch));

        // Per-patch config gating (NOT harmony.PatchAll() bare): some target methods carry known
        // native-crash risk (inventory-family parameter types), so the user must be able to disable
        // JUST that patch by editing the config file without a rebuild (rebuilds are expensive here -
        // Smart App Control). v0.3.0: CheckOwnedRequirementsPatch now also carries Phase 1's availability check (design
        // point A), so it must apply whenever EITHER the old trace flag OR either master switch
        // wants it - otherwise setting TraceCheckOwnedRequirements=false while leaving
        // EnablePlayerPull/EnableForVillagers=true would silently disable the "master switch" it
        // claims to be. v0.7.0: EnableForVillagers joins EnablePlayerPull in this OR - without it,
        // the villager availability branch in CraftTransfer.TryReportAvailable would be unreachable
        // even with EnableForVillagers=true, because the underlying Harmony patch itself would
        // never be installed.
        if (TraceCheckOwnedRequirements.Value || EnablePlayerPull.Value || EnableForVillagers.Value)
            harmony.PatchAll(typeof(Patches.CheckOwnedRequirementsPatch));
        else
            Logger.LogInfo("[CFS] TraceCheckOwnedRequirements=false, EnablePlayerPull=false and EnableForVillagers=false - CheckOwnedRequirements patch NOT applied.");

        // v0.3.0: these three BeginCraftingSequence patches now also carry Phase 1's actual pull
        // (design point C) - same "trace flag OR master switch" reasoning as CheckOwnedRequirements
        // above. v0.7.0: EnableForVillagers joins the OR (same reasoning as above).
        if (TraceBeginCraftingSequence.Value || EnablePlayerPull.Value || EnableForVillagers.Value)
        {
            harmony.PatchAll(typeof(Patches.BeginCraftingSequenceCraftPatch));
            harmony.PatchAll(typeof(Patches.BeginCraftingSequenceAnvilPatch));
            harmony.PatchAll(typeof(Patches.BeginCraftingSequenceDyeingPatch));
        }
        else
        {
            Logger.LogInfo("[CFS] TraceBeginCraftingSequence=false, EnablePlayerPull=false and EnableForVillagers=false - BeginCraftingSequence patches NOT applied.");
        }

        // v0.3.0: the two OnCraftingSuccess patches now also carry Phase 1's sweep-back (design
        // point D) - same "trace flag OR master switch" reasoning. OnCraftingFinishedPatch rides
        // along under the same flag as before (Phase 1 doesn't use it; it's a harmless zero-param
        // diagnostic patch, not worth its own flag). v0.7.0: EnableForVillagers joins the OR (same
        // reasoning as above).
        if (TraceOnCraftingSuccess.Value || EnablePlayerPull.Value || EnableForVillagers.Value)
        {
            harmony.PatchAll(typeof(Patches.OnCraftingSuccessCraftPatch));
            harmony.PatchAll(typeof(Patches.OnCraftingSuccessDyeingPatch));
            // v0.2.0: CraftingAgent._OnCraftingFinished() is zero-parameter (no inventory-family
            // crash exposure), gated under this same flag rather than a new one.
            harmony.PatchAll(typeof(Patches.OnCraftingFinishedPatch));
        }
        else
        {
            Logger.LogInfo("[CFS] TraceOnCraftingSuccess=false, EnablePlayerPull=false and EnableForVillagers=false - _OnCraftingSuccess/_OnCraftingFinished patches NOT applied.");
        }

        if (TraceActivateBlueprint.Value)
            harmony.PatchAll(typeof(Patches.ActivateBlueprintPatch));
        else
            Logger.LogInfo("[CFS] TraceActivateBlueprint=false - ActivateBlueprint patch NOT applied.");

        if (TraceRecipeListUI.Value)
            harmony.PatchAll(typeof(Patches.RecipeListUIPatch));
        else
            Logger.LogInfo("[CFS] TraceRecipeListUI=false - CreateItemsTabPage.Show patch NOT applied.");

        // v0.4.0: settlement-stock requirement-UI feature. All four patches exist solely to serve this
        // one config flag (unlike the Trace/EnablePlayerPull dual-purpose patches above), so gate
        // PatchAll on it directly. The postfixes ALSO re-check ShowSettlementStockInUI.Value themselves
        // (CraftUiAvailability.OnUpdateAvailability) so a live config edit can still silence the
        // feature's output without a restart, even though re-enabling it live cannot re-patch.
        if (ShowSettlementStockInUI.Value)
        {
            harmony.PatchAll(typeof(Patches.CraftMenuActivatePatch));
            harmony.PatchAll(typeof(Patches.CraftMenuClosedPatch));
            harmony.PatchAll(typeof(Patches.UpdateAvailablilityPatch));
            harmony.PatchAll(typeof(Patches.UpdateAvailabilityStatusPatch));
        }
        else
        {
            Logger.LogInfo("[CFS] ShowSettlementStockInUI=false - craft-menu availability-UI patches NOT applied.");
        }

        // v0.6.0 Phase 2a: villager-fetch diagnostic spike (Patches/VillagerFetchPatches.cs).
        // READ-ONLY - see that file's header comment. Two independent flags: EnableVillagerFetchTrace
        // gates the four FSM state-transition patches, TraceStorageWhitelist gates the storage-
        // whitelist probe patch (kept separate so either half can be silenced without a rebuild).
        if (EnableVillagerFetchTrace.Value)
        {
            harmony.PatchAll(typeof(Patches.FetchCraftingSuppliesEnterPatch));
            harmony.PatchAll(typeof(Patches.FetchCraftingSuppliesExitPatch));
            harmony.PatchAll(typeof(Patches.UseCraftingStationEnterPatch));
            harmony.PatchAll(typeof(Patches.ReturnCraftingSuppliesEnterPatch));
        }
        else
        {
            Logger.LogInfo("[CFS] EnableVillagerFetchTrace=false - villager fetch FSM trace patches NOT applied.");
        }

        if (TraceStorageWhitelist.Value)
            harmony.PatchAll(typeof(Patches.IsWhitelistedByStoragePatch));
        else
            Logger.LogInfo("[CFS] TraceStorageWhitelist=false - IsWhitelistedByStorage patch NOT applied.");

        // v0.8.0 Phase 2b (RETIRED lever, removed from the config surface entirely): the
        // CrafterFetchQuest/CrafterSpecificFetchQuest GetPriority suppression patches are no longer
        // attached from here - confirmed in-game 2026-07-27 to stall villager crafting entirely. See
        // FetchQuestSuppression.cs for the full evidence and why the patches stay in the tree unattached.

        // v0.15.0: bloomery/kiln supply-fetch diagnostic spike (Patches/BloomeryFetchPatches.cs).
        // v1.0.0: these four patches also carry the bloomery/kiln delivery feature (BloomeryTrace.
        // TryStockBloomery, called from both OnStateEnter postfixes) - EnableBloomeryTrace alone can no
        // longer gate patch application, or the delivery feature would be unreachable whenever a user
        // leaves the (now-default-false) diagnostic flag off. Attach whenever EITHER the diagnostic
        // flag OR the delivery feature's own master switches want it - same "trace flag OR master
        // switch" reasoning as the Trace/EnablePlayerPull dual-purpose patches above. Four patches
        // attached individually (not PatchAll on the whole file) so a per-patch try/catch and its own
        // load-time log line exist for each, matching StationStockPatches.cs / VillagerFetchPatches.cs's style.
        if (EnableBloomeryTrace.Value || (EnableForVillagers.Value && StockStationOnFetch.Value))
        {
            try
            {
                harmony.PatchAll(typeof(Patches.FetchBloomerySuppliesEnterPatch));
                Logger.LogInfo("[CFS] [CFS-BLM] attached FetchBloomerySuppliesEnterPatch (FSM_FetchBloomerySupplies.OnStateEnter).");
            }
            catch (Exception ex) { Logger.LogError($"[CFS] [CFS-BLM] failed to attach FetchBloomerySuppliesEnterPatch: {ex}"); }

            try
            {
                harmony.PatchAll(typeof(Patches.FetchBloomerySuppliesExitPatch));
                Logger.LogInfo("[CFS] [CFS-BLM] attached FetchBloomerySuppliesExitPatch (FSM_FetchBloomerySupplies.OnStateExit).");
            }
            catch (Exception ex) { Logger.LogError($"[CFS] [CFS-BLM] failed to attach FetchBloomerySuppliesExitPatch: {ex}"); }

            try
            {
                harmony.PatchAll(typeof(Patches.FetchKilnSuppliesEnterPatch));
                Logger.LogInfo("[CFS] [CFS-BLM] attached FetchKilnSuppliesEnterPatch (FSM_FetchKilnSupplies.OnStateEnter).");
            }
            catch (Exception ex) { Logger.LogError($"[CFS] [CFS-BLM] failed to attach FetchKilnSuppliesEnterPatch: {ex}"); }

            try
            {
                harmony.PatchAll(typeof(Patches.FetchKilnSuppliesExitPatch));
                Logger.LogInfo("[CFS] [CFS-BLM] attached FetchKilnSuppliesExitPatch (FSM_FetchKilnSupplies.OnStateExit).");
            }
            catch (Exception ex) { Logger.LogError($"[CFS] [CFS-BLM] failed to attach FetchKilnSuppliesExitPatch: {ex}"); }
        }
        else
        {
            Logger.LogInfo("[CFS] EnableBloomeryTrace=false, EnableForVillagers=false or StockStationOnFetch=false - bloomery/kiln fetch FSM patches NOT applied.");
        }

        // v0.9.0 Phase 2c: stock the crafting station directly from settlement storage the moment a
        // villager's fetch quest STARTS (FSM_FetchCraftingSupplies.OnStateEnter), so the walk becomes
        // unnecessary through vanilla's own scheduling rather than by suppressing it. See
        // StationStocker.cs / Patches/StationStockPatches.cs.
        if (EnableForVillagers.Value && StockStationOnFetch.Value)
        {
            harmony.PatchAll(typeof(Patches.FetchCraftingSuppliesStockPatch));
        }
        else
        {
            string offFlag = !EnableForVillagers.Value ? "EnableForVillagers=false" : "StockStationOnFetch=false";
            Logger.LogInfo($"[CFS] {offFlag} - FetchCraftingSuppliesStockPatch (station stocker) NOT applied.");
        }

        Logger.LogInfo($"[CFS] CraftFromStorageMod v{MyPluginInfo.PLUGIN_VERSION} loaded. " +
            $"EnablePlayerPull={EnablePlayerPull.Value} EnableForVillagers={EnableForVillagers.Value} " +
            $"StockTransformStationMaterials={StockTransformStationMaterials.Value} " +
            $"ShowSettlementStockInUI={ShowSettlementStockInUI.Value} StockStationOnFetch={StockStationOnFetch.Value}.");
    }

    // Managed casts LIE for interop objects materialized under a base declared type (project-wide
    // gotcha) - always read the NATIVE class name via IL2CPP.il2cpp_object_get_class, never as/is.
    // Shared by GatePatches.cs and StorageCensus.cs. Falls back to the managed wrapper's own type
    // name for non-Il2Cpp objects.
    internal static string NativeClassName(object? obj)
    {
        if (obj == null) return "null";
        try
        {
            if (obj is Il2CppObjectBase b)
            {
                IntPtr cls = IL2CPP.il2cpp_object_get_class(b.Pointer);
                return Marshal.PtrToStringAnsi(IL2CPP.il2cpp_class_get_name(cls)) ?? "?";
            }
        }
        catch { }
        return obj.GetType().Name;
    }
}
