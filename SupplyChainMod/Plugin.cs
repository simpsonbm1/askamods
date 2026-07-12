using BepInEx;
using BepInEx.Configuration;
using BepInEx.Unity.IL2CPP;
using BepInEx.Logging;
using HarmonyLib;
using Il2CppInterop.Runtime.Injection;
using UnityEngine;

namespace SupplyChainMod;

// SupplyChainMod v0.1.x — Phase 0 of the demand-driven supply-chain autopilot
// (NEW_MOD_IDEAS_PLAN.md idea 12). READ-ONLY flow diagnostic: observes complaints, workstation
// task datas, and station inventory composition, and logs them. Writes NOTHING — no priority
// changes, no task creation, no inventory writes. Later phases (not this mod, not yet) would act
// on what Phase 0 measures.
//
// v0.1.1 — first in-game run follow-up: GenericMessageComplaint discrimination + an ignore filter
// for "feels unsafe" complaint spam (IgnoredComplaintMessages), FailedObjectiveComplaint detail,
// REMOVE-log dedupe aggregation, a SettlementIssueTrackerWidget.Instance fallback
// (FindAnyObjectByType), and unambiguous BomDump input/output instrumentation.
//
// v0.1.2 — run 2 showed complaints are a LEVEL signal (the game re-ADDs a held complaint every
// ~50-100ms): ComplaintWatcher now logs TRANSITIONS only (ADD (new) / CLEARED), collapsing the
// v0.1.1 REMOVE-dedupe into a periodic stats line (active/newAdds/reAdds/clears/unmatchedRemoves/
// suppressed). Also: run 2 confirmed CraftingStation.KnowsBlueprintForItem returns false+null for
// every valid item on every station, so BomDump now additionally probes the crafting TABLES
// (CraftInteraction._localBlueprints) directly — a second, more promising BOM path.
//
// v0.1.3 — run 3: transition logging + table BOM dump both confirmed working (434 lines vs. 3,580;
// every table recipe manifest printed cleanly), but KnowsBlueprintForItem STILL returns false+null
// at both station and table level even when the SAME table's _localBlueprints demonstrably held
// the item's blueprint. Two small diagnostics added: WidgetWatcher now logs the six complaint
// maps' raw sizes (once on connect, thereafter on change) to tell "genuinely empty this save" from
// "our enumeration sees nothing silently"; BomDump now also logs a pointer-identity comparison
// between a task item's ItemInfo and the name-matched blueprint's produced ItemInfo, to tell
// "different native instance" from "the method's semantics are just something else".
[BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
public class Plugin : BasePlugin
{
    internal static ManualLogSource Logger = null!;

    // ── [General] ────────────────────────────────────────────────────────────────────────────
    internal static ConfigEntry<float> TickSeconds = null!;
    internal static ConfigEntry<string> DumpHotkey = null!;

    // ── [Diagnostics] ────────────────────────────────────────────────────────────────────────
    internal static ConfigEntry<bool> EnableDiagnostics = null!;
    internal static ConfigEntry<bool> PatchVillagerComplaints = null!;
    internal static ConfigEntry<float> WidgetPollSeconds = null!;
    internal static ConfigEntry<int> SweepStationsPerTick = null!;
    internal static ConfigEntry<string> IgnoredComplaintMessages = null!;

    public override void Load()
    {
        Logger = base.Log;

        TickSeconds = Config.Bind(
            section: "General",
            key: "TickSeconds",
            defaultValue: 5.0f,
            description: "Master tick interval (seconds) for the SupplyChain tracker — drives the rolling composition-sweep pacing and the ~60s post-load auto TaskDump.");

        DumpHotkey = Config.Bind(
            section: "General",
            key: "DumpHotkey",
            defaultValue: "F9",
            description: "Hotkey (UnityEngine.KeyCode name) that triggers a full on-demand dump: task datas (TaskDump), the crafting BOM probe (BomDump), and an immediate full composition sweep (CompositionSweep). Read-only.");

        EnableDiagnostics = Config.Bind(
            section: "Diagnostics",
            key: "EnableDiagnostics",
            defaultValue: true,
            description: "Master switch for SupplyChain diagnostic logging. Shipped default TRUE (Phase 0 diagnostic mod, per project convention) — flip to false once you're done measuring.");

        PatchVillagerComplaints = Config.Bind(
            section: "Diagnostics",
            key: "PatchVillagerComplaints",
            defaultValue: true,
            description: "Whether the VillagerSocial.AddComplaint/RemoveComplaint Harmony patches (ComplaintWatcher) are applied at load. Safety valve: if this patch pair ever crashes the game at plugin load, flip this to false — the rest of the mod (WidgetWatcher, TaskDump, CompositionSweep, BomDump) keeps working.");

        WidgetPollSeconds = Config.Bind(
            section: "Diagnostics",
            key: "WidgetPollSeconds",
            defaultValue: 10.0f,
            description: "How often (seconds) WidgetWatcher polls SettlementIssueTrackerWidget's complaint maps for deltas (entries appearing/disappearing).");

        SweepStationsPerTick = Config.Bind(
            section: "Diagnostics",
            key: "SweepStationsPerTick",
            defaultValue: 1,
            description: "How many stations CompositionSweep's rolling pass processes per master tick (TickSeconds). Higher = faster full coverage but more per-tick cost. A full pass also runs immediately on DumpHotkey.");

        IgnoredComplaintMessages = Config.Bind(
            section: "Diagnostics",
            key: "IgnoredComplaintMessages",
            defaultValue: "feelUnsafe",
            description: "Comma-separated, case-insensitive substrings. A GenericMessageComplaint whose message contains any of these (or exactly matches the game's own 'feels unsafe at work/home' complaint text) is NOT logged — neither its ADD nor its matching REMOVE. v0.1.1 default suppresses the 'feels unsafe' combat complaints that scrolled the console on a defenseless test save. Empty = suppress nothing.");

        ClassInjector.RegisterTypeInIl2Cpp<SupplyChainTracker>();

        var go = new GameObject("SupplyChainMod_Tracker");
        UnityEngine.Object.DontDestroyOnLoad(go);
        go.AddComponent<SupplyChainTracker>();

        var harmony = new Harmony(MyPluginInfo.PLUGIN_GUID);

        // WidgetWatcher's push-method postfixes (safe param types: Workstation, HarvestMarkerComplaint)
        // are always patched — they're fire-verification only, no inventory-family params.
        Harmony.CreateAndPatchAll(typeof(Patches.AddStorageFullComplaintPatch));
        Harmony.CreateAndPatchAll(typeof(Patches.RemoveStorageFullComplaintPatch));
        Harmony.CreateAndPatchAll(typeof(Patches.AddMarkerComplaintPatch));
        Harmony.CreateAndPatchAll(typeof(Patches.AddMineExhaustedComplainPatch));

        // ComplaintWatcher is gated: AddComplaint(Complaint)/RemoveComplaint(Int32) are believed safe
        // (Complaint is Il2CppSystem.Object-derived, not inventory-family), but AddComplaint is
        // non-virtual (AOT-inlining risk) and this is new, unverified ground — PatchVillagerComplaints
        // is the safety valve if patch setup ever proves fatal at load.
        if (PatchVillagerComplaints.Value)
        {
            Harmony.CreateAndPatchAll(typeof(Patches.AddComplaintPatch));
            Harmony.CreateAndPatchAll(typeof(Patches.RemoveComplaintPatch));
            Logger.LogInfo("[SupplyChain] ComplaintWatcher armed.");
        }
        else
        {
            Logger.LogInfo("[SupplyChain] ComplaintWatcher DISABLED via PatchVillagerComplaints=false.");
        }

        Logger.LogInfo($"[SupplyChain] SupplyChainMod v{MyPluginInfo.PLUGIN_VERSION} loaded — Phase 0 read-only flow diagnostic. TickSeconds={TickSeconds.Value} DumpHotkey='{DumpHotkey.Value}' EnableDiagnostics={EnableDiagnostics.Value} PatchVillagerComplaints={PatchVillagerComplaints.Value} WidgetPollSeconds={WidgetPollSeconds.Value} SweepStationsPerTick={SweepStationsPerTick.Value} IgnoredComplaintMessages='{IgnoredComplaintMessages.Value}'.");
    }
}
