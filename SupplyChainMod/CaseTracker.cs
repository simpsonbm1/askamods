using System;
using System.Collections.Generic;
using UnityEngine;

namespace SupplyChainMod;

// CaseTracker (v0.15.0, the "case layer", DEMAND_MODEL_PLAN.md follow-up, user-approved
// 2026-07-16) — turns BudgetPlane.Evaluate's per-poll findings (fed as structured
// BudgetPlane.Finding records, never parsed log strings) into persistent CASES with a
// CANDIDATE(silent) -> OPEN -> RESOLVED lifecycle. A real problem in this settlement flickers
// poll-to-poll (a real HOG logged 14 times over 25 min; a transient HOG appeared for exactly one
// poll) — the case layer's job is to read that flicker as ONE tracked thing with a beginning and
// (eventually) an end, instead of N unrelated log lines.
//
// Case KEYS (the merge rules — DEMAND_MODEL_PLAN.md, user-approved):
//   hog|<containerKey>|<hogItem>   — the victim item is carried as Payload, never part of the key.
//   tier|<item>                    — a BLOCKAGE(cause=priority-shadowed) and/or a SHADOWED finding
//                                     on the SAME item collapse into ONE case: the same routing
//                                     failure seen from the producer side (blockage) and the
//                                     warehouse side (shadowed). This merge is the whole point.
//   route|<item>                   — BLOCKAGE with cause=destination-full or no-route.
//   labor|<item>                   — STARVED.
//   off|<posKey>|<item>            — OFF-CONFLICT.
// SURPLUS is never a case (it's the hog-eligibility pool, not a problem — see BudgetPlane header).
//
// Lifecycle (config-driven, Plugin.CaseOpenPolls/CaseOpenWindow/CaseClosePolls): every poll, every
// tracked case (plus any brand-new key seen this poll) gets one presence bool appended to a
// rolling window bounded at CaseOpenWindow. CANDIDATE -> OPEN once present in >= CaseOpenPolls of
// that window (logs OPEN, assigns a chain tag). OPEN -> RESOLVED after CaseClosePolls CONSECUTIVE
// absent polls (logs RESOLVED, case removed — a later resighting starts a brand-new case id, never
// reopens the old one). A CANDIDATE that racks up the same consecutive-absence count is dropped
// silently (it never became visible, so nothing to announce).
//
// Chain tag: when a case opens, DemandGraph.AreChainRelated(item, otherOpenCase.item) is checked
// against every other currently-OPEN case. A related case adopts an existing chainmate's id (first
// match wins — greedy); otherwise a fresh id is minted for BOTH. Shown as `chain=#k` on the OPEN
// line; omitted when the case has no chainmates.
//
// Per-world state: ALL fields below are plain strings/ints/bools/floats (+ a nullable int and a
// small string-keyed tuple dict) — never an interop wrapper (project-wide static-state rule; these
// survive across polls). Reset wholesale on world-leave via NoteWorldLeft, same pattern as every
// other plane in this mod (WarehouseWatch/BudgetPlane/DemandGraph.NoteWorldLeft).
internal static class CaseTracker
{
    private enum CaseState { Candidate, Open }

    private sealed class Case
    {
        public int Id;
        public string Family = "?";     // HOG | TIER | ROUTE | LABOR | OFF
        public string Item = "?";
        public string Warehouse = "?";  // most-recent contributing finding's display place
        public string Payload = "";     // most-recent contributing finding's extra context (hog victim item)
        public CaseState State = CaseState.Candidate;
        public readonly List<bool> Presence = new(); // bounded to CaseOpenWindow, oldest first
        public int ConsecutiveAbsent;
        public int PollsSeenTotal;
        public int DistressCount;
        public int MinStored = int.MaxValue;
        public int MaxStored = int.MinValue;
        public string LastLine = "";
        public float OpenedAtTime = -1f;
        public int? ChainId;
        // classTag ("HOG"/"BLOCKAGE"/"SHADOWED"/...) -> most-recent (place, stored) — every finding
        // class that has EVER contributed to this case. Drives the TIER merge-note (>= 2 classes).
        public readonly Dictionary<string, (string Place, int Stored)> ClassSources = new();
    }

    private static Dictionary<string, Case> _cases = new();
    private static int _nextCaseId = 1;
    private static int _nextChainId = 1;
    private static string _lastSummaryFingerprint = "";

    internal static void NoteWorldLeft()
    {
        try
        {
            _cases = new Dictionary<string, Case>();
            _nextCaseId = 1;
            _nextChainId = 1;
            _lastSummaryFingerprint = "";
        }
        catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] [case] NoteWorldLeft error: {ex}"); }
    }

    // Fed once per BudgetPlane.Evaluate() with that poll's structured findings. `fullTable` is the
    // same flag BudgetPlane received (F9 / first poll) — hooks the F9 full-case dump into the
    // identical path BudgetPlane's own full-table dump already services.
    internal static void Ingest(List<BudgetPlane.Finding> findings, bool fullTable)
    {
        if (!Plugin.EnableCaseLayer.Value) return;
        try
        {
            var thisPoll = new Dictionary<string, List<BudgetPlane.Finding>>();
            foreach (var f in findings)
            {
                string key = BuildCaseKey(f);
                if (key.Length == 0) continue; // SURPLUS / unrecognized — never a case
                if (!thisPoll.TryGetValue(key, out var list)) { list = new List<BudgetPlane.Finding>(); thisPoll[key] = list; }
                list.Add(f);
            }

            int window = Math.Max(1, Plugin.CaseOpenWindow.Value);
            int openPolls = Math.Max(1, Plugin.CaseOpenPolls.Value);
            int closePolls = Math.Max(1, Plugin.CaseClosePolls.Value);

            var allKeys = new HashSet<string>(_cases.Keys);
            foreach (var k in thisPoll.Keys) allKeys.Add(k);

            var toRemove = new List<string>();
            foreach (var key in allKeys)
            {
                bool present = thisPoll.TryGetValue(key, out var contributors);

                if (!_cases.TryGetValue(key, out var c))
                {
                    c = new Case { Id = _nextCaseId++, Family = FamilyOf(key), Item = contributors![0].Item };
                    _cases[key] = c;
                }

                c.Presence.Add(present);
                if (c.Presence.Count > window) c.Presence.RemoveAt(0);

                if (present)
                {
                    c.ConsecutiveAbsent = 0;
                    c.PollsSeenTotal++;
                    bool anyDistress = false;
                    foreach (var f in contributors!)
                    {
                        c.Warehouse = f.Warehouse;
                        c.Payload = f.Payload;
                        c.LastLine = f.Line;
                        if (f.Stored < c.MinStored) c.MinStored = f.Stored;
                        if (f.Stored > c.MaxStored) c.MaxStored = f.Stored;
                        if (f.Distress) anyDistress = true;
                        c.ClassSources[f.Class.ToUpperInvariant()] = (f.Warehouse, f.Stored);
                    }
                    if (anyDistress) c.DistressCount++;
                }
                else
                {
                    c.ConsecutiveAbsent++;
                }

                if (c.State == CaseState.Candidate)
                {
                    int presentCount = 0;
                    foreach (var p in c.Presence) if (p) presentCount++;

                    if (presentCount >= openPolls)
                    {
                        c.State = CaseState.Open;
                        c.OpenedAtTime = Time.time;
                        AssignChain(c);
                        LogOpen(c, presentCount, window);
                    }
                    else if (c.ConsecutiveAbsent >= closePolls)
                    {
                        toRemove.Add(key); // never opened — silent prune, nothing to announce
                    }
                }
                else if (c.ConsecutiveAbsent >= closePolls)
                {
                    LogResolved(c);
                    toRemove.Add(key);
                }
            }

            foreach (var key in toRemove) _cases.Remove(key);

            LogSummaryIfChanged();

            if (fullTable) DumpFullCases();
        }
        catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] [case] Ingest error: {ex}"); }
    }

    private static string BuildCaseKey(BudgetPlane.Finding f)
    {
        switch (f.Class)
        {
            case "hog": return $"hog|{f.ContainerKey}|{f.Item}";
            case "blockage": return f.Cause == "priority-shadowed" ? $"tier|{f.Item}" : $"route|{f.Item}";
            case "shadowed": return $"tier|{f.Item}";
            case "starved": return $"labor|{f.Item}";
            case "off": return $"off|{f.PosKey}|{f.Item}";
            default: return "";
        }
    }

    private static string FamilyOf(string key)
    {
        int idx = key.IndexOf('|');
        string prefix = idx >= 0 ? key.Substring(0, idx) : key;
        return prefix switch
        {
            "hog" => "HOG",
            "tier" => "TIER",
            "route" => "ROUTE",
            "labor" => "LABOR",
            "off" => "OFF",
            _ => prefix.ToUpperInvariant(),
        };
    }

    // Greedy chain assignment: the FIRST currently-OPEN, BOM-related case wins. If it already
    // carries a chain id, adopt it; otherwise mint a fresh one for both. First match only — this is
    // a "these are related" tag, not an exact connected-components solve.
    private static void AssignChain(Case c)
    {
        try
        {
            foreach (var kv in _cases)
            {
                var other = kv.Value;
                if (ReferenceEquals(other, c) || other.State != CaseState.Open) continue;

                bool related;
                try { related = DemandGraph.AreChainRelated(c.Item, other.Item); }
                catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] [case] AreChainRelated error: {ex}"); related = false; }
                if (!related) continue;

                if (other.ChainId.HasValue) { c.ChainId = other.ChainId; return; }
                int newId = _nextChainId++;
                c.ChainId = newId;
                other.ChainId = newId;
                return;
            }
        }
        catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] [case] AssignChain error: {ex}"); }
    }

    private static void LogOpen(Case c, int presentCount, int window)
    {
        try
        {
            string mergeNote = "";
            if (c.Family == "TIER" && c.ClassSources.Count >= 2)
            {
                var keys = new List<string>(c.ClassSources.Keys);
                keys.Sort(StringComparer.Ordinal);
                var parts = new List<string>();
                foreach (var k in keys) parts.Add($"{k}@{c.ClassSources[k].Place} {c.ClassSources[k].Stored}");
                mergeNote = $" (merge: {string.Join(" + ", parts)})";
            }
            string chainNote = c.ChainId.HasValue ? $" chain=#{c.ChainId.Value}" : "";
            string tail = ExtractTail(c.LastLine);

            Plugin.Logger.LogInfo(
                $"[SupplyChain] [case] OPEN #{c.Id} {c.Family} '{c.Item}' @ '{c.Warehouse}'{mergeNote}{chainNote} " +
                $"seen {presentCount}/{window} polls -> {tail}");
        }
        catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] [case] LogOpen error: {ex}"); }
    }

    private static void LogResolved(Case c)
    {
        try
        {
            float minutes = c.OpenedAtTime >= 0f ? Math.Max(0f, (Time.time - c.OpenedAtTime) / 60f) : 0f;
            int min = c.MinStored == int.MaxValue ? 0 : c.MinStored;
            int max = c.MaxStored == int.MinValue ? 0 : c.MaxStored;

            Plugin.Logger.LogInfo(
                $"[SupplyChain] [case] RESOLVED #{c.Id} {c.Family} '{c.Item}' after {minutes:F0}m — " +
                $"seen {c.PollsSeenTotal} polls, distress {c.DistressCount}/{c.PollsSeenTotal}, stored {min}→{max}");
        }
        catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] [case] LogResolved error: {ex}"); }
    }

    private static void LogSummaryIfChanged()
    {
        try
        {
            int hog = 0, tier = 0, route = 0, labor = 0, off = 0, candidates = 0;
            foreach (var kv in _cases)
            {
                var c = kv.Value;
                if (c.State == CaseState.Candidate) { candidates++; continue; }
                switch (c.Family)
                {
                    case "HOG": hog++; break;
                    case "TIER": tier++; break;
                    case "ROUTE": route++; break;
                    case "LABOR": labor++; break;
                    case "OFF": off++; break;
                }
            }
            int open = hog + tier + route + labor + off;
            string fp = $"{open}|{hog}|{tier}|{route}|{labor}|{off}|{candidates}";
            if (fp == _lastSummaryFingerprint) return;
            _lastSummaryFingerprint = fp;

            Plugin.Logger.LogInfo(
                $"[SupplyChain] [case] summary: open={open} (hog={hog} tier={tier} route={route} labor={labor} off={off}) candidates={candidates}");
        }
        catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] [case] LogSummaryIfChanged error: {ex}"); }
    }

    // F9 full dump — hooked into the same fullTable path BudgetPlane.Evaluate already services.
    private static void DumpFullCases()
    {
        try
        {
            Plugin.Logger.LogInfo("[SupplyChain] [case] === full dump: OPEN cases ===");
            int shown = 0;
            foreach (var kv in _cases)
            {
                var c = kv.Value;
                if (c.State != CaseState.Open) continue;

                float ageMin = c.OpenedAtTime >= 0f ? Math.Max(0f, (Time.time - c.OpenedAtTime) / 60f) : 0f;
                string payloadStr = string.IsNullOrEmpty(c.Payload) ? "" : $" payload='{c.Payload}'";
                string chainStr = c.ChainId.HasValue ? $" chain=#{c.ChainId.Value}" : "";
                int min = c.MinStored == int.MaxValue ? 0 : c.MinStored;
                int max = c.MaxStored == int.MinValue ? 0 : c.MaxStored;

                Plugin.Logger.LogInfo(
                    $"[SupplyChain] [case] #{c.Id} {c.Family} '{c.Item}' age={ageMin:F0}m{payloadStr}{chainStr} " +
                    $"stored={min}→{max} distress={c.DistressCount}/{c.PollsSeenTotal}");
                shown++;
            }
            Plugin.Logger.LogInfo($"[SupplyChain] [case] === end full dump: {shown} open case(s) ===");
        }
        catch (Exception ex) { Plugin.Logger.LogError($"[SupplyChain] [case] DumpFullCases error: {ex}"); }
    }

    // Strips everything up to and including the first "->" from a proposal line (the finding's own
    // "@ '...': detail -> ACTION" text), leaving just the action tail the OPEN line re-shows.
    private static string ExtractTail(string line)
    {
        if (string.IsNullOrEmpty(line)) return line ?? "";
        int idx = line.IndexOf("->", StringComparison.Ordinal);
        return idx >= 0 ? line.Substring(idx + 2).Trim() : line;
    }
}
