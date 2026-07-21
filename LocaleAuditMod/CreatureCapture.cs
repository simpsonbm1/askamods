using System;
using System.Collections.Generic;
using HarmonyLib;
using SSSGame;

namespace LocaleAuditMod;

// Creature identity has no global registry (Cecil 2026-07-21: CreatureDataSheet appears only inside
// PopulationManager._densMap and a deeds condition, neither a usable enumeration). So creatures are
// recorded as they spawn instead.
//
// Capture point is Creature.Spawned() - virtual and zero-argument, which is the shape the project's
// own rules call for: lifecycle capture into a static list is the documented pattern for
// NetworkBehaviours, and taking no parameters keeps it clear of the inventory-family parameter crash
// (VillagerAmmoMod) entirely.
//
// IMPORTANT: this stores only MANAGED STRINGS, never the Creature or CreatureDataSheet wrapper.
// Caching interop wrappers of per-world objects across world sessions is the documented native-crash
// family (reads through a stale wrapper AV in native code with no catchable exception), and copying
// the three strings out at capture time sidesteps that class of bug completely.
[HarmonyPatch(typeof(Creature), nameof(Creature.Spawned))]
public static class CreatureSpawnedPatch
{
    [HarmonyPostfix]
    public static void Postfix(Creature __instance)
    {
        if (!Plugin.CaptureCreatures.Value) return;
        try { CreatureCapture.Record(__instance); }
        catch { /* a probe must never destabilise the run it is measuring */ }
    }
}

internal static class CreatureCapture
{
    internal sealed class CreatureRow
    {
        public string Asset = "?";   // INVARIANT - CreatureDataSheet asset name
        public string Loc = "?";     // translated - what VFB's whitelist matches today
        public string Faction = "?"; // INVARIANT - enum
    }

    private static readonly Dictionary<string, CreatureRow> _rows = new();

    // Fire-verification: an empty CREATURES section must be attributable. Without this counter,
    // "the patch never ran" (inlined / wrong target) and "ran fine, no creatures spawned yet" and
    // "ran, but every datasheet read failed" all look identical in the log.
    private static int _fires;
    private static int _noSheet;
    private static bool _firstFireLogged;

    public static int Fires { get { lock (_rows) return _fires; } }
    public static int NoSheet { get { lock (_rows) return _noSheet; } }

    public static void Record(Creature? c)
    {
        if (c == null) return;

        lock (_rows) { _fires++; }
        if (!_firstFireLogged)
        {
            _firstFireLogged = true;
            Plugin.Logger.LogInfo("[LocaleAudit] Creature.Spawned postfix FIRED (fire-verification).");
        }

        string asset = "?", loc = "?", faction = "?";
        try
        {
            var sheet = c.dataSheet;
            if (sheet != null)
            {
                try { asset = sheet.name ?? "?"; } catch { }
                try { loc = sheet.localizedName ?? "?"; } catch { }
            }
        }
        catch { }
        try { faction = c.Faction.ToString(); } catch { }

        if (asset == "?" && loc == "?") { lock (_rows) { _noSheet++; } return; }

        lock (_rows)
        {
            if (_rows.ContainsKey(asset)) return;
            _rows[asset] = new CreatureRow { Asset = asset, Loc = loc, Faction = faction };
        }
    }

    public static List<CreatureRow> Snapshot()
    {
        lock (_rows) return new List<CreatureRow>(_rows.Values);
    }
}
