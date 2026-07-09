using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using BepInEx;
using UnityEngine;

namespace DenRespawnMod;

// A single tracked den's persistent state. Keyed by world position (project-wide gotcha: key
// dictionaries by world position, never UniqueId — UniqueId-style indices restart per spatial
// chunk and aren't globally unique).
internal class DenRecord
{
    public float X, Y, Z;
    public string TypeName = "?";
    public bool Defeated;
    public int DefeatedOnDay = -1;
}

// Position-keyed, per-world-persisted den registry. Mirrors TreeRespawnMod's per-world save
// convention (TreeRespawnMod/Plugin.cs "Per-world save selection" region): one save file per
// StorageManager.ActiveSessionID, loaded when a world becomes active and flushed on world leave.
// A single shared file would let one world's defeat/auto-respawn state bleed into another
// (worldgen reuses the same coordinate space across saves) — same rationale TreeRespawn documents
// for its own per-world file.
internal static class DenRegistry
{
    internal static readonly Dictionary<string, DenRecord> Records = new();

    private static string? _saveFilePath;

    private static string Key(float x, float z) =>
        $"{Mathf.RoundToInt(x)},{Mathf.RoundToInt(z)}";

    // Create-or-get a record for this position. Refreshes TypeName if it was previously unknown.
    internal static DenRecord Upsert(Vector3 pos, string typeName)
    {
        lock (Records)
        {
            string key = Key(pos.x, pos.z);
            if (Records.TryGetValue(key, out var rec))
            {
                if (rec.TypeName == "?" && !string.IsNullOrEmpty(typeName) && typeName != "?")
                    rec.TypeName = typeName;
                return rec;
            }

            rec = new DenRecord
            {
                X = pos.x,
                Y = pos.y,
                Z = pos.z,
                TypeName = string.IsNullOrEmpty(typeName) ? "?" : typeName
            };
            Records[key] = rec;
            return rec;
        }
    }

    // Linear scan is fine here — registries stay well under 100 records for a single world.
    internal static DenRecord? FindNearest(Vector3 pos, float maxDist)
    {
        lock (Records)
        {
            DenRecord? best = null;
            float bestDistSq = maxDist * maxDist;
            foreach (var rec in Records.Values)
            {
                float dx = rec.X - pos.x;
                float dz = rec.Z - pos.z;
                float distSq = dx * dx + dz * dz;
                if (distSq <= bestDistSq)
                {
                    bestDistSq = distSq;
                    best = rec;
                }
            }
            return best;
        }
    }

    // DefeatedOnDay is only (re)stamped when it was unknown (-1) or the record is transitioning
    // FROM alive — a re-defeat after a revive gets a fresh day, not the stale day from a prior cycle.
    internal static void MarkDefeated(DenRecord rec, int day)
    {
        lock (Records)
        {
            bool wasDefeated = rec.Defeated;
            rec.Defeated = true;
            if (rec.DefeatedOnDay < 0 || !wasDefeated)
                rec.DefeatedOnDay = day;
        }
    }

    internal static void MarkAlive(DenRecord rec)
    {
        lock (Records)
        {
            rec.Defeated = false;
        }
    }

    internal static void Save()
    {
        lock (Records)
        {
            if (_saveFilePath == null) return; // no world loaded yet — nothing to persist
            try
            {
                var lines = new List<string>(Records.Count);
                foreach (var rec in Records.Values)
                {
                    lines.Add(string.Join("|",
                        rec.X.ToString("R", CultureInfo.InvariantCulture),
                        rec.Y.ToString("R", CultureInfo.InvariantCulture),
                        rec.Z.ToString("R", CultureInfo.InvariantCulture),
                        rec.TypeName,
                        rec.Defeated ? "1" : "0",
                        rec.DefeatedOnDay.ToString(CultureInfo.InvariantCulture)));
                }
                File.WriteAllLines(_saveFilePath, lines);
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError($"[DenRespawn] DenRegistry.Save failed: {ex}");
            }
        }
    }

    // Switches the active save file to this session and loads whatever it contains (empty if the
    // file doesn't exist yet — a fresh world/first visit).
    internal static void Load(string sessionId)
    {
        lock (Records)
        {
            Records.Clear();
            _saveFilePath = Path.Combine(Paths.ConfigPath, $"DenRespawn_{SanitizeId(sessionId)}.save");

            if (!File.Exists(_saveFilePath))
            {
                Plugin.Logger.LogInfo($"[DenRespawn] No existing den registry for this world ({Path.GetFileName(_saveFilePath)}) — starting fresh.");
                return;
            }

            try
            {
                int loaded = 0;
                foreach (var line in File.ReadAllLines(_saveFilePath))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    var parts = line.Split('|');
                    if (parts.Length < 6) continue;

                    if (!float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float x)) continue;
                    if (!float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float y)) continue;
                    if (!float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float z)) continue;
                    string typeName = parts[3];
                    bool defeated = parts[4] == "1";
                    if (!int.TryParse(parts[5], NumberStyles.Integer, CultureInfo.InvariantCulture, out int defeatedOnDay))
                        defeatedOnDay = -1;

                    var rec = new DenRecord { X = x, Y = y, Z = z, TypeName = typeName, Defeated = defeated, DefeatedOnDay = defeatedOnDay };
                    Records[Key(x, z)] = rec;
                    loaded++;
                }
                Plugin.Logger.LogInfo($"[DenRespawn] Loaded {loaded} den record(s) from {Path.GetFileName(_saveFilePath)}.");
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError($"[DenRespawn] DenRegistry.Load failed: {ex}");
            }
        }
    }

    // World-leave flush: called from Plugin.NoteWorldLeft alongside every other per-world drop.
    internal static void Clear()
    {
        lock (Records)
        {
            Records.Clear();
            _saveFilePath = null;
        }
    }

    // Turn an arbitrary session id into a safe, collision-free file-name fragment. Mirrors
    // TreeRespawnMod.Plugin.SanitizeId/StableHash exactly (same rationale: session ids may contain
    // characters unsafe for file names, and a naive strip could collide two different ids).
    private static string SanitizeId(string id)
    {
        var sb = new System.Text.StringBuilder(id.Length + 9);
        foreach (char c in id)
            sb.Append(char.IsLetterOrDigit(c) ? c : '_');
        sb.Append('-');
        sb.Append(StableHash(id).ToString("x8"));
        return sb.ToString();
    }

    // FNV-1a 32-bit — deterministic across processes (unlike String.GetHashCode, which is randomized).
    private static uint StableHash(string s)
    {
        uint hash = 2166136261u;
        foreach (char c in s) { hash ^= c; hash *= 16777619u; }
        return hash;
    }
}
