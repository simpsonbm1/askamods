using System;
using System.Collections.Generic;
using System.Text;
using SSSGame;
using SandSailorStudio.Inventory;
using UnityEngine;

namespace GroundItemVacuumMod;

public class VacuumTracker : MonoBehaviour
{
    private KeyCode _key = KeyCode.N;
    private string _keyRaw = "";
    private string _guiMessage = "";
    private float _guiExpiry = 0f;
    private float _autoTimer = 0f;
    private float _cfgReloadTimer = 0f;

    private sealed class Candidate
    {
        public WorldItemObject Item = null!;
        public string Name = "?";
        public string CatChain = "";
        public Vector3 Pos;
    }

    private sealed class CorpseCandidate
    {
        public Monster Creature = null!;
        public string Name = "?";
        public Vector3 Pos;
    }

    private void Start()
    {
        ApplyHotkey();
    }

    // Re-binds _key from config. No-op unless the raw config string changed, so it's safe to
    // call on every config reload without log spam.
    private void ApplyHotkey()
    {
        string raw = Plugin.VacuumHotkey.Value ?? "";
        if (raw == _keyRaw) return;
        _keyRaw = raw;
        if (Enum.TryParse<KeyCode>(raw, true, out var parsed))
            _key = parsed;
        else
            Plugin.Logger.LogWarning($"[Vacuum] Could not parse hotkey '{raw}'. Keeping '{_key}'.");
        Plugin.Logger.LogInfo($"[Vacuum] Hotkey bound to {_key}.");
    }

    private void Update()
    {
        // Live config pickup: BepInEx does NOT re-read an edited cfg on its own — reload it
        // periodically so DryRun/filters/radius/hotkey can be changed mid-session without a
        // relaunch (SeedScout pattern). Every other setting is already read fresh at sweep time,
        // so the hotkey is the only value that needs explicit re-application. Slowed to 30s (perf-
        // diagnostic round) — hotkey rebinding now picks up within 30s instead of 5s.
        _cfgReloadTimer += Time.deltaTime;
        if (_cfgReloadTimer >= 30f)
        {
            _cfgReloadTimer = 0f;
            try { Plugin.Cfg?.Reload(); } catch { }
            ApplyHotkey();
        }

        if (!IsTextInputFocused() && Input.GetKeyDown(_key))
        {
            try { Sweep(auto: false); }
            catch (Exception ex)
            {
                Plugin.Logger.LogError($"[Vacuum] Sweep error: {ex}");
                ShowMessage("Vacuum error - see BepInEx log.");
            }
        }

        float autoMin = Plugin.AutoVacuumMinutes.Value;
        if (autoMin > 0f)
        {
            _autoTimer += Time.deltaTime;
            if (_autoTimer >= autoMin * 60f)
            {
                _autoTimer = 0f;
                try { Sweep(auto: true); }
                catch (Exception ex) { Plugin.Logger.LogError($"[Vacuum] Auto-sweep error: {ex}"); }
            }
        }
    }

    private void Sweep(bool auto)
    {
        bool entireWorld = Plugin.VacuumEntireWorld.Value;
        bool dryRun = Plugin.DryRun.Value;
        bool trace = Plugin.TraceEachItem.Value;

        Vector3 playerPos = Vector3.zero;
        bool havePlayer = Plugin.LocalPlayer != null && Plugin.LocalPlayer.gameObject != null;
        if (havePlayer) playerPos = Plugin.LocalPlayer!.transform.position;

        if (!entireWorld && !havePlayer)
        {
            if (!auto) ShowMessage("Vacuum: player not ready yet.");
            return;
        }

        // Snapshot our OWN tracked set (managed references, maintained by OnEnable/OnDisable).
        // We never touch the game's linked list. Copy under lock, then release before touching
        // native getters.
        DynamicItemObject[] snap;
        lock (Plugin.TrackedItems)
        {
            snap = new DynamicItemObject[Plugin.TrackedItems.Count];
            Plugin.TrackedItems.CopyTo(snap);
        }

        if (!Plugin.TrackingConfirmed && snap.Length == 0)
        {
            if (!auto) ShowMessage("Vacuum: ground-item tracking not ready yet.");
            return;
        }

        // Corpse pass is entirely gated on IncludeCorpses - snapshot stays empty when the feature
        // is off, so every corpse-related count downstream is naturally zero.
        bool includeCorpses = Plugin.IncludeCorpses.Value;
        Monster[] corpseSnap = Array.Empty<Monster>();
        if (includeCorpses)
        {
            lock (Plugin.TrackedCorpses)
            {
                corpseSnap = new Monster[Plugin.TrackedCorpses.Count];
                Plugin.TrackedCorpses.CopyTo(corpseSnap);
            }
        }

        Plugin.Logger.LogInfo($"[Vacuum] Sweep start ({(auto ? "auto" : "manual")}, {(dryRun ? "DRY-RUN" : "LIVE")}): tracked={snap.Length}, trace={trace}, corpses={corpseSnap.Length}.");

        // Build candidates (read-only). Per-step trace so a native crash pinpoints the exact item.
        var all = new List<Candidate>(snap.Length);
        for (int i = 0; i < snap.Length; i++)
        {
            var node = snap[i];
            if (node == null) continue;
            try { AddCandidate(node, i, trace, all); }
            catch (Exception ex) { Plugin.Logger.LogError($"[Vacuum] item #{i} resolve error: {ex}"); }
        }

        // Filter.
        var excludeCats = ParseCsv(Plugin.ExcludeCategories.Value);
        var excludeItems = ParseCsv(Plugin.ExcludeItems.Value);
        var onlyItems = ParseCsv(Plugin.OnlyItems.Value);
        float radiusSqr = Plugin.Radius.Value * Plugin.Radius.Value;

        var targets = new List<Candidate>();
        var byName = new Dictionary<string, int>();
        int inRangeTotal = 0;

        foreach (var c in all)
        {
            if (!entireWorld)
            {
                float dSqr = (c.Pos - playerPos).sqrMagnitude;
                if (dSqr > radiusSqr) continue;
            }
            inRangeTotal++;

            if (onlyItems.Count > 0 && !ContainsAny(c.Name, onlyItems)) continue;
            if (ContainsAny(c.Name, excludeItems)) continue;
            if (ContainsAny(c.CatChain, excludeCats)) continue;

            targets.Add(c);
            byName[c.Name] = byName.TryGetValue(c.Name, out var n) ? n + 1 : 1;
        }

        // Corpse pass. Kept in a SEPARATE list from item targets so counts stay distinct. Corpses
        // are exempt from OnlyItems/ExcludeItems/ExcludeCategories (those are debris filters) -
        // only the radius (or VacuumEntireWorld) and ExcludeCorpseNames apply. Only dead Monsters
        // are ever candidates (live monsters are skipped by the IsDead check above); Den and Pet
        // are never tracked in the first place (see Plugin.TrackedCorpses).
        var excludeCorpseNames = ParseCsv(Plugin.ExcludeCorpseNames.Value);
        var corpseTargets = new List<CorpseCandidate>();
        var corpseByName = new Dictionary<string, int>();
        int corpsesInRange = 0;

        if (includeCorpses)
        {
            for (int i = 0; i < corpseSnap.Length; i++)
            {
                var m = corpseSnap[i];
                try
                {
                    // Unity-destroyed since Spawned - prune and skip. This replaces an
                    // OnDisable-style hook; Monster only gives us Spawned/Despawned.
                    if (m == null)
                    {
                        lock (Plugin.TrackedCorpses) { Plugin.TrackedCorpses.Remove(m); }
                        continue;
                    }

                    // Live monsters are never candidates and never touched beyond this read.
                    if (!m.IsDead) continue;

                    string name = m.GetTargetName() ?? "?";
                    Vector3 pos = m.transform.position;

                    if (!entireWorld)
                    {
                        float dSqr = (pos - playerPos).sqrMagnitude;
                        if (dSqr > radiusSqr) continue;
                    }
                    corpsesInRange++;

                    if (ContainsAny(name, excludeCorpseNames))
                    {
                        if (Plugin.Diagnostics.Value)
                            Plugin.Logger.LogInfo($"[Vacuum]   corpse SKIPPED (ExcludeCorpseNames): {name}");
                        continue;
                    }

                    corpseTargets.Add(new CorpseCandidate { Creature = m, Name = name, Pos = pos });
                    corpseByName[name] = corpseByName.TryGetValue(name, out var cn) ? cn + 1 : 1;
                }
                catch (Exception ex)
                {
                    Plugin.Logger.LogError($"[Vacuum] corpse #{i} resolve error: {ex}");
                }
            }
        }

        // Report.
        if (Plugin.Diagnostics.Value)
        {
            Plugin.Logger.LogInfo($"[Vacuum] Sweep result: {all.Count} tracked items, {inRangeTotal} in range, {targets.Count} match filters.");
            foreach (var kv in byName)
                Plugin.Logger.LogInfo($"[Vacuum]   would remove x{kv.Value}: {kv.Key}");
            var catTally = new Dictionary<string, int>();
            foreach (var c in all)
                catTally[c.CatChain] = catTally.TryGetValue(c.CatChain, out var m) ? m + 1 : 1;
            foreach (var kv in catTally)
                Plugin.Logger.LogInfo($"[Vacuum]   category x{kv.Value}: {(string.IsNullOrEmpty(kv.Key) ? "<none>" : kv.Key)}");

            if (includeCorpses)
            {
                Plugin.Logger.LogInfo($"[Vacuum] Corpse sweep result: {corpseSnap.Length} tracked, {corpsesInRange} dead in range, {corpseTargets.Count} match filters.");
                foreach (var kv in corpseByName)
                    Plugin.Logger.LogInfo($"[Vacuum]   corpse x{kv.Value}: {kv.Key}");
            }
        }

        if (dryRun)
        {
            string scanMsg = includeCorpses
                ? $"SCAN: {targets.Count} item(s) + {corpseTargets.Count} corpse(s) would be vacuumed (of {inRangeTotal} items / {corpsesInRange} corpses in range). See log; set DryRun=false to remove."
                : $"SCAN: {targets.Count} item(s) would be vacuumed (of {inRangeTotal} in range). See log; set DryRun=false to remove.";
            ShowMessage(scanMsg);
            return;
        }

        if (Plugin.HostOnly.Value && !IsHost())
        {
            if (!auto) ShowMessage("Only the host can vacuum ground items.");
            return;
        }

        int removed = 0;
        foreach (var c in targets)
        {
            try
            {
                if (c.Item != null)
                {
                    c.Item.RemoveObjectFromWorld();
                    removed++;
                }
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError($"[Vacuum] Failed to remove '{c.Name}': {ex}");
            }
        }

        int corpseRemoved = 0;
        if (includeCorpses)
        {
            foreach (var c in corpseTargets)
            {
                try
                {
                    if (c.Creature != null)
                    {
                        c.Creature.DespawnImmediatelyIfStateAuthority();
                        corpseRemoved++;
                    }
                }
                catch (Exception ex)
                {
                    Plugin.Logger.LogError($"[Vacuum] Failed to despawn corpse '{c.Name}': {ex}");
                }
            }
        }

        if (includeCorpses)
        {
            Plugin.Logger.LogInfo($"[Vacuum] Removed {removed}/{targets.Count} ground items, {corpseRemoved}/{corpseTargets.Count} corpses.");
            if (!auto || removed > 0 || corpseRemoved > 0)
                ShowMessage($"Vacuumed {removed} ground item(s) + {corpseRemoved} corpse(s).");
        }
        else
        {
            Plugin.Logger.LogInfo($"[Vacuum] Removed {removed}/{targets.Count} ground items.");
            if (!auto || removed > 0)
                ShowMessage($"Vacuumed {removed} ground item(s).");
        }
    }

    private void AddCandidate(DynamicItemObject node, int idx, bool trace, List<Candidate> outList)
    {
        if (trace) Plugin.Logger.LogInfo($"[Vacuum] trace #{idx}: node ok");

        var itemObj = node._itemObject;
        if (itemObj == null) { if (trace) Plugin.Logger.LogInfo($"[Vacuum] trace #{idx}: _itemObject null - skip"); return; }
        if (trace) Plugin.Logger.LogInfo($"[Vacuum] trace #{idx}: _itemObject ok");

        string name = "?";
        string catChain = "";
        var item = itemObj.ItemInstance;
        if (trace) Plugin.Logger.LogInfo($"[Vacuum] trace #{idx}: ItemInstance {(item == null ? "null" : "ok")}");
        var info = item?.info;
        if (info != null)
        {
            name = info.Name ?? "?";
            if (trace) Plugin.Logger.LogInfo($"[Vacuum] trace #{idx}: name={name}");
            catChain = BuildCategoryChain(info.category);
            if (trace) Plugin.Logger.LogInfo($"[Vacuum] trace #{idx}: category='{catChain}'");
        }

        Vector3 pos = node.transform.position;
        if (trace) Plugin.Logger.LogInfo($"[Vacuum] trace #{idx}: pos ok ({pos.x:F0},{pos.z:F0})");

        outList.Add(new Candidate { Item = itemObj, Name = name, CatChain = catChain, Pos = pos });
    }

    private static string BuildCategoryChain(ItemCategoryInfo? cat)
    {
        if (cat == null) return "";
        var sb = new StringBuilder();
        int depth = 0;
        var c = cat;
        while (c != null && depth++ < 8)
        {
            string n = "";
            try { n = c.Name ?? ""; } catch { }
            if (!string.IsNullOrEmpty(n))
            {
                if (sb.Length > 0) sb.Append('/');
                sb.Append(n);
            }
            try { c = c.parent; } catch { break; }
        }
        return sb.ToString();
    }

    private bool IsHost()
    {
        try
        {
            var p = Plugin.LocalPlayer;
            if (p == null || p.NetworkObject == null || p.NetworkObject.Runner == null) return false;
            var runner = p.NetworkObject.Runner;
            return runner.IsServer || runner.IsSharedModeMasterClient;
        }
        catch { return false; }
    }

    private static List<string> ParseCsv(string raw)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(raw)) return result;
        foreach (var part in raw.Split(','))
        {
            var t = part.Trim();
            if (t.Length > 0) result.Add(t.ToLowerInvariant());
        }
        return result;
    }

    private static bool ContainsAny(string haystack, List<string> needles)
    {
        if (needles.Count == 0 || string.IsNullOrEmpty(haystack)) return false;
        string h = haystack.ToLowerInvariant();
        foreach (var n in needles)
            if (h.Contains(n)) return true;
        return false;
    }

    private void ShowMessage(string message)
    {
        _guiMessage = message;
        _guiExpiry = Time.time + 5.0f;
    }

    // Typing guard: keystrokes in the game's text fields (e.g. structure rename) also reach
    // Input.GetKeyDown, so letter-bound hotkeys fire while the player types. Skip hotkey handling
    // whenever the UI's selected object is a text input. (confirmed leak 2026-07-10)
    private static bool IsTextInputFocused()
    {
        try
        {
            var es = UnityEngine.EventSystems.EventSystem.current;
            var go = es != null ? es.currentSelectedGameObject : null;
            if (go == null) return false;
            return go.GetComponent<TMPro.TMP_InputField>() != null
                || go.GetComponent<UnityEngine.UI.InputField>() != null;
        }
        catch { return false; }
    }

    private void OnGUI()
    {
        if (Time.time < _guiExpiry && !string.IsNullOrEmpty(_guiMessage))
        {
            var shadow = new GUIStyle(GUI.skin.label)
            {
                fontSize = 24,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter
            };
            shadow.normal.textColor = Color.black;
            var text = new GUIStyle(shadow);
            text.normal.textColor = Color.cyan;

            GUI.Label(new Rect(0, 81, Screen.width, 100), _guiMessage, shadow);
            GUI.Label(new Rect(0, 80, Screen.width, 100), _guiMessage, text);
        }
    }
}
