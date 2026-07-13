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
internal sealed class LedgerEntry
{
    public string WorldId = "?";
    public string PosKey = "?";
    public string StationName = "?";
    public int TaskIndex;
    public string ItemName = "?";
    public int OrigPriority; // rank index (== priority) the task originally occupied
    public int BoostPriority; // rank index (== priority) the task was moved to
    public long UtcTicks;
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
                e.ItemName == entry.ItemName);
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
