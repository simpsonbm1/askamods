using System;
using System.Collections.Generic;
using System.IO;

namespace SupplyChainMod;

// Crash-safe file ledger for ActuationSpike (v0.2.0 Phase 1a). Write-ahead log of in-flight
// priority boosts: Append() BEFORE the mutation, Remove() after a clean revert. If the process
// dies between Append and revert, the entry survives on disk and ActuationSpike.OnWorldReady
// restores it on next load of the SAME world. Plain-text, pipe-separated, no JSON libs.
//
// v0.2.3: OrigPriority/BoostPriority are RANK INDICES (list position, == priority — test run 1
// confirmed priority is a rank continuously renumbered from list order), not arbitrary values.
// Schema is unchanged from v0.2.0-0.2.2 since the two are numerically identical.
//
// v0.5.0 — Phase 2b (QuotaSpike) reuses this same ledger for quota boosts, distinguished by Kind:
// "rank" (ActuationSpike/SupplyController, the original meaning above) vs. "quota" (QuotaSpike) —
// for Kind=="quota", OrigPriority/BoostPriority carry the ORIGINAL and BOOSTED QUOTA values instead
// (field names kept as-is for schema/backward-compat stability). SupplyOwner is quota-specific
// bookkeeping (the ResourceStorageTaskData's supply owner name), "-" for rank entries. Both new
// fields are backward-compatible: a pre-v0.5.0 8-field line has no Kind/SupplyOwner columns and
// parses as Kind="rank" SupplyOwner="-" (see LoadAll).
//
// v0.7.0 — Phase 2d fire-verify spike (EvictionSpike) adds a third Kind: "tier". Schema unchanged —
// for Kind=="tier", OrigPriority/BoostPriority carry the ORIGINAL and BOOSTED TIER VALUES (High=0/
// Med=1/Low=2/None=3, NOT rank indices) of a warehouse ResourceStorageTaskData row, written via
// Rpc_ChangeTaskPriority/SetPriority. SupplyOwner is populated the same way "quota" entries populate
// it. The ground-drop eviction half of EvictionSpike writes no ledger entries at all (nothing to
// restore — a dropped item just lives in the world; see EvictionSpike.cs).
internal sealed class LedgerEntry
{
    public string WorldId = "?";
    public string PosKey = "?";
    public string StationName = "?";
    public int TaskIndex;
    public string ItemName = "?";
    public int OrigPriority; // rank index (Kind="rank") or original quota (Kind="quota")
    public int BoostPriority; // rank index (Kind="rank") or boosted quota (Kind="quota")
    public long UtcTicks;
    public string Kind = "rank"; // "rank" | "quota"
    public string SupplyOwner = "-";
}

internal static class SpikeLedger
{
    private static string PathOnDisk => Path.Combine(BepInEx.Paths.ConfigPath, "SupplyChainMod_ledger.dat");

    private static string Sanitize(string? s) => (s ?? "?").Replace('|', '/');

    internal static void Append(LedgerEntry entry)
    {
        try
        {
            var all = LoadAll();
            all.Add(entry);
            WriteAll(all);
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"[SupplyChain] [ledger] Append error: {ex}");
        }
    }

    internal static void Remove(LedgerEntry entry)
    {
        try
        {
            var all = LoadAll();
            all.RemoveAll(e =>
                e.WorldId == entry.WorldId &&
                e.PosKey == entry.PosKey &&
                e.TaskIndex == entry.TaskIndex &&
                e.ItemName == entry.ItemName &&
                e.Kind == entry.Kind);
            WriteAll(all);
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"[SupplyChain] [ledger] Remove error: {ex}");
        }
    }

    internal static List<LedgerEntry> LoadFor(string worldId)
    {
        var result = new List<LedgerEntry>();
        try
        {
            foreach (var e in LoadAll())
            {
                if (e.WorldId == worldId) result.Add(e);
            }
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"[SupplyChain] [ledger] LoadFor error: {ex}");
        }
        return result;
    }

    private static List<LedgerEntry> LoadAll()
    {
        var result = new List<LedgerEntry>();
        var path = PathOnDisk;
        try
        {
            if (!File.Exists(path)) return result;
            var lines = File.ReadAllLines(path);
            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var parts = line.Split('|');
                    if (parts.Length < 8)
                    {
                        Plugin.Logger.LogWarning($"[SupplyChain] [ledger] Skipping malformed line (fields={parts.Length}): {line}");
                        continue;
                    }
                    result.Add(new LedgerEntry
                    {
                        WorldId = parts[0],
                        PosKey = parts[1],
                        StationName = parts[2],
                        TaskIndex = int.Parse(parts[3]),
                        ItemName = parts[4],
                        OrigPriority = int.Parse(parts[5]),
                        BoostPriority = int.Parse(parts[6]),
                        UtcTicks = long.Parse(parts[7]),
                        // v0.5.0 — legacy 8-field lines (v0.2.x-v0.4.x, pre-QuotaSpike) have no
                        // Kind/SupplyOwner columns and parse as Kind="rank" SupplyOwner="-".
                        Kind = parts.Length >= 9 ? parts[8] : "rank",
                        SupplyOwner = parts.Length >= 10 ? parts[9] : "-",
                    });
                }
                catch (Exception ex)
                {
                    Plugin.Logger.LogWarning($"[SupplyChain] [ledger] Skipping malformed line ({ex.Message}): {line}");
                }
            }
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"[SupplyChain] [ledger] LoadAll error: {ex}");
        }
        return result;
    }

    private static void WriteAll(List<LedgerEntry> entries)
    {
        var path = PathOnDisk;
        var tmp = path + ".tmp";
        try
        {
            var lines = new List<string>(entries.Count);
            foreach (var e in entries)
            {
                lines.Add(string.Join("|", new[]
                {
                    Sanitize(e.WorldId),
                    Sanitize(e.PosKey),
                    Sanitize(e.StationName),
                    e.TaskIndex.ToString(),
                    Sanitize(e.ItemName),
                    e.OrigPriority.ToString(),
                    e.BoostPriority.ToString(),
                    e.UtcTicks.ToString(),
                    Sanitize(e.Kind),
                    Sanitize(e.SupplyOwner),
                }));
            }
            File.WriteAllLines(tmp, lines);
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"[SupplyChain] [ledger] WriteAll error: {ex}");
        }
    }
}
