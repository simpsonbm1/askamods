using System;
using System.Collections.Generic;
using SSSGame;
using SSSGame.Combat;
using SandSailorStudio.Storage;
using SandSailorStudio.WorldGen;
using SandSailorStudio.Streaming;
using UnityEngine;
using DenRespawnMod.Patches;

namespace DenRespawnMod;

public class DenTracker : MonoBehaviour
{
    // Distance (m) within which a live tracked den is considered "the same den" as a registry
    // record for the remote-refresh queue (immediate match) and the pending-request scan (a
    // force-loaded tile's den showing up nearby).
    private const float RemoteMatchRadius = 75f;
    private const float RemoteRequestTimeoutSeconds = 30f;

    private int _frame;
    private int _frame300;
    private StorageManager? _storage;
    private string? _lastSessionId;
    private float _nextDiagAt;
    private string _guiMessage = "";
    private float _guiMessageExpiry = 0f;

    // Patches (map click) reach the running tracker through this — set once in Start(), never
    // cleared (the GO is DontDestroyOnLoad so the component itself outlives world sessions;
    // only its per-world collections get cleared, via ClearTransientState). Null-guarded at
    // every call site since a patch can theoretically fire before Start() runs.
    internal static DenTracker? Instance;

    private struct PendingRefresh
    {
        public DenRecord Record;
        public float RequestedAt;
        public string Source;
    }

    private readonly List<PendingRefresh> _pendingRefreshes = new();
    private readonly List<GameObject> _anchors = new();
    private int _anchorCounter;

    private void Start()
    {
        Instance = this;
    }

    private void Update()
    {
        // Map-pin click trigger (v1.1.4). CompassObjectiveMarker.OnSelect only TRACKS the hovered
        // pin (it fires on hover, not click) — the actual click is detected here, every frame,
        // as a Shift+Left-mouse-press while a pin is hovered. TryRevive's own 0.3s dedupe window
        // still applies.
        if (DenMapRevive.HaveHovered && Input.GetMouseButtonDown(0) && DenMapRevive.ModifierHeld())
        {
            try
            {
                if (Plugin.DenDiagnostics.Value)
                {
                    string posStr = DenMapRevive.HoveredPos.ToString();
                    Plugin.Logger.LogInfo($"[DenRespawn] Pin click: reviving '{DenMapRevive.HoveredName}' at {posStr}");
                }

                DenMapRevive.TryRevive(DenMapRevive.HoveredName, DenMapRevive.HoveredPos);
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError($"[DenRespawn] Pin click revive error: {ex}");
            }
        }

        if (_frame++ % 60 == 0)
        {
            PollWorld();
        }

        // Separate, slower counter for registry servicing (defeat-transition scan, pending remote
        // refreshes, day-rule tick) — kept independent of the 60-frame world poll above per design.
        if (_frame300++ % 300 == 0)
        {
            try
            {
                ServiceRegistry();
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError($"[DenRespawn] ServiceRegistry error: {ex}");
            }
        }

        if (Plugin.DenDiagnostics.Value && Time.time >= _nextDiagAt)
        {
            _nextDiagAt = Time.time + Plugin.DiagnosticsIntervalSeconds.Value;
            try
            {
                DiagnosticsDump();
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError($"[DenRespawn] DiagnosticsDump error: {ex}");
            }
        }
    }

    // Throttled (every 60th frame) world-session poll. Detects world leave/switch via
    // StorageManager.ActiveSessionID and clears tracked dens on change (project-wide gotcha:
    // never keep native wrappers of per-world objects across world sessions). Also switches the
    // den registry's save file to match (DenRegistry.Load), mirroring TreeRespawnMod's
    // OnWorldChanged calling LoadPending() right after resolving the new session id.
    private void PollWorld()
    {
        try
        {
            if (_storage == null)
                _storage = UnityEngine.Object.FindAnyObjectByType<StorageManager>();
            if (_storage == null) return;

            string id = _storage.ActiveSessionID;
            if (string.IsNullOrEmpty(id))
            {
                Plugin.NoteWorldLeft();
                _lastSessionId = null;
                return;
            }

            if (_lastSessionId == null)
            {
                // First world since the menu: dens captured during the load arrived BEFORE the
                // session id became readable — keep them (TreeRespawn OnWorldChanged precedent).
                _lastSessionId = id;
                DenRegistry.Load(id);
                return;
            }

            if (id != _lastSessionId)
            {
                Plugin.NoteWorldLeft();
                _lastSessionId = id;
                DenRegistry.Load(id);
            }
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"[DenRespawn] PollWorld error: {ex}");
            _storage = null;
        }
    }

    // Called from Plugin.NoteWorldLeft — drops per-world transient state that must never survive
    // a world switch: the pending remote-refresh queue (its DenRecords belong to the OLD world's
    // registry, already cleared) and the force-load anchor GameObjects (destroyed, not just
    // forgotten, so they don't linger DontDestroyOnLoad forever).
    internal static void ClearTransientState()
    {
        if (Instance == null) return;

        Instance._pendingRefreshes.Clear();

        foreach (var go in Instance._anchors)
            if (go != null) UnityEngine.Object.Destroy(go);
        Instance._anchors.Clear();
    }

    // Shared per-den lever sequence (originally the v1.0.2 hotkey path; the map-revive and timed
    // auto-respawn paths both run the exact same levers + diagnostics through here). den.Revive() is wrapped with
    // Plugin.AllowReviveCall so the Den.Revive prefix gate can tell this call apart from a
    // foreign (game-driven natural respawn) one. Updates the registry (MarkAlive + Save) on
    // success so defeat-transition scanning and the auto-respawn timer see the revive.
    internal bool RunRefresh(Den den, string source)
    {
        try
        {
            if (den == null) return false;

            bool isActive = false;
            try { isActive = den.isActive; } catch { }
            int cooldown = -1;
            try { cooldown = den.ReviveCooldown; } catch { }
            bool blocked = false;
            try { blocked = den.IsBlockedByStructures(); } catch { }
            int hitzones = -1;
            try { hitzones = den._lastActiveHitzones; } catch { }

            bool anyIgnore = false;
            bool anyEmpty = false;
            try
            {
                var affected = den.affectedSpawners;
                if (affected != null)
                {
                    foreach (var spawner in affected)
                    {
                        if (spawner == null) continue;
                        try { if (spawner.ignoreRespawning) anyIgnore = true; } catch { }
                        try { if (spawner.HasNoAliveCreatures()) anyEmpty = true; } catch { }
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError($"[DenRespawn] Error scanning affectedSpawners: {ex}");
            }

            string name = "?";
            string pos = "?";
            bool havePos = false;
            Vector3 posVec = default;
            try { name = den.GetName(); } catch { }
            try { posVec = den.transform.position; pos = posVec.ToString(); havePos = true; } catch { }

            Plugin.Logger.LogInfo($"[DenRespawn] Refreshing den '{name}' pos={pos} isActive={isActive} cooldown={cooldown} blocked={blocked} anyIgnore={anyIgnore} anyEmpty={anyEmpty} hitzones={hitzones} source={source}");

            Plugin.AllowReviveCall = true;
            try { den.Revive(); }
            catch (Exception ex) { Plugin.Logger.LogError($"[DenRespawn] Error calling den.Revive(): {ex}"); }
            finally { Plugin.AllowReviveCall = false; }

            if (Plugin.ClearIgnoreRespawning.Value)
            {
                try { den.IgnorePopulationSpawnersRespawning(false); }
                catch (Exception ex) { Plugin.Logger.LogError($"[DenRespawn] Error calling den.IgnorePopulationSpawnersRespawning(false): {ex}"); }
            }

            if (!isActive)
            {
                try { den.SetDenActive(false, true); }
                catch (Exception ex) { Plugin.Logger.LogError($"[DenRespawn] Error calling den.SetDenActive(false, true): {ex}"); }
            }

            if (Plugin.ForceRespawnPopulations.Value)
            {
                try
                {
                    var affected = den.affectedSpawners;
                    if (affected != null)
                    {
                        foreach (var spawner in affected)
                        {
                            if (spawner == null) continue;
                            try
                            {
                                if (spawner.HasNoAliveCreatures())
                                {
                                    spawner.SetActiveSpawner(true, true);
                                    spawner.RespawnAllPopulations(false);
                                }
                            }
                            catch (Exception ex)
                            {
                                Plugin.Logger.LogError($"[DenRespawn] Error force-respawning a node spawner: {ex}");
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Plugin.Logger.LogError($"[DenRespawn] Error iterating affectedSpawners for force-respawn: {ex}");
                }
            }

            bool nowActive = false;
            try { nowActive = den.isActive; } catch { }
            Plugin.Logger.LogInfo($"[DenRespawn] Post-refresh: isActive={nowActive}");

            if (havePos)
            {
                try { MarkerRefresher.RefreshPinNear(posVec); }
                catch (Exception ex) { Plugin.Logger.LogError($"[DenRespawn] Error calling MarkerRefresher.RefreshPinNear: {ex}"); }
            }

            try
            {
                var affected = den.affectedSpawners;
                if (affected != null)
                {
                    int idx = 0;
                    foreach (var spawner in affected)
                    {
                        LogSpawnerDiag($"post-node[{idx}]", spawner);
                        idx++;
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError($"[DenRespawn] Error logging post-refresh spawner state: {ex}");
            }

            if (havePos)
            {
                try
                {
                    var rec = DenRegistry.Upsert(posVec, name);
                    DenRegistry.MarkAlive(rec);
                    DenRegistry.Save();
                }
                catch (Exception ex)
                {
                    Plugin.Logger.LogError($"[DenRespawn] Error updating registry after refresh: {ex}");
                }
            }

            return true;
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"[DenRespawn] RunRefresh error: {ex}");
            return false;
        }
    }

    // Bundles the three registry-driven services that only need to run a few times a minute
    // (300-frame tick): defeat-transition scan, pending remote-refresh scan, day-rule tick.
    private void ServiceRegistry()
    {
        // Day poll FIRST so ScanDefeatTransitions sees a known CurrentDay from the very first tick
        // (otherwise a defeat observed on tick 1 is stamped day -1 and needs the backfill below).
        bool dayChanged = false;
        try { dayChanged = DayCounter.PollDayChanged(); }
        catch (Exception ex) { Plugin.Logger.LogError($"[DenRespawn] PollDayChanged error: {ex}"); }

        try { ScanDefeatTransitions(); }
        catch (Exception ex) { Plugin.Logger.LogError($"[DenRespawn] ScanDefeatTransitions error: {ex}"); }

        try { ScanPendingRefreshes(); }
        catch (Exception ex) { Plugin.Logger.LogError($"[DenRespawn] ScanPendingRefreshes error: {ex}"); }

        try
        {
            if (dayChanged)
                TickAutoRespawnRules();
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"[DenRespawn] TickAutoRespawnRules error: {ex}");
        }
    }

    // For each live tracked den, defeated := any affectedSpawners element with
    // ignoreRespawning==true — a STRONG signal only (NOT HasNoAliveCreatures, which is transient:
    // an empty-but-not-yet-defeated den reads that way too between waves). On a Defeated
    // false->true transition, stamps the day; on true->false (revived by ANY path — hotkey, map,
    // timer, or even a natural respawn we didn't suppress) marks it alive again.
    private void ScanDefeatTransitions()
    {
        Den[] dens;
        lock (Plugin.TrackedDens)
        {
            Plugin.TrackedDens.RemoveAll(d => d == null);
            dens = Plugin.TrackedDens.ToArray();
        }

        foreach (var den in dens)
        {
            try
            {
                if (den == null) continue;

                Vector3 pos;
                try { pos = den.transform.position; } catch { continue; } // can't key without a position

                string name = "?";
                try { name = den.GetName(); } catch { }

                bool defeated = false;
                try
                {
                    var affected = den.affectedSpawners;
                    if (affected != null)
                    {
                        foreach (var spawner in affected)
                        {
                            if (spawner == null) continue;
                            try { if (spawner.ignoreRespawning) { defeated = true; break; } } catch { }
                        }
                    }
                }
                catch { }

                var rec = DenRegistry.Upsert(pos, name);

                if (defeated && !rec.Defeated)
                {
                    DenRegistry.MarkDefeated(rec, DayCounter.CurrentDay);
                    string posStr = pos.ToString();
                    Plugin.Logger.LogInfo($"[DenRespawn] Den '{rec.TypeName}' at {posStr} DEFEATED (day {DayCounter.CurrentDay})");
                    DenRegistry.Save();
                }
                else if (defeated && rec.Defeated && rec.DefeatedOnDay < 0 && DayCounter.CurrentDay >= 0)
                {
                    // Backfill: den was already defeated when the mod first saw it (pre-existing
                    // defeat, or observed before the day counter resolved) — without a day stamp
                    // the auto-respawn rules would skip it forever. Start its clock now.
                    DenRegistry.MarkDefeated(rec, DayCounter.CurrentDay);
                    Plugin.Logger.LogInfo($"[DenRespawn] Den '{rec.TypeName}' defeat day unknown — clock started at day {DayCounter.CurrentDay}");
                    DenRegistry.Save();
                }
                else if (!defeated && rec.Defeated)
                {
                    DenRegistry.MarkAlive(rec);
                    DenRegistry.Save();
                }
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError($"[DenRespawn] Error scanning a den for defeat transition: {ex}");
            }
        }
    }

    // Live defeat check: any node spawner with ignoreRespawning set — the proven vanilla defeat
    // flag (same signal ScanDefeatTransitions keys on). Errs toward "not defeated" on read
    // failures so a flaky read can only SKIP a timer revive, never fire a spurious one.
    private static bool IsDenDefeated(Den den)
    {
        try
        {
            var affected = den.affectedSpawners;
            if (affected != null)
            {
                foreach (var spawner in affected)
                {
                    if (spawner == null) continue;
                    try { if (spawner.ignoreRespawning) return true; } catch { }
                }
            }
        }
        catch { }
        return false;
    }

    // Stale-registry correction for the timer path: the registry recorded a defeat but the live
    // den reads alive — happens when the player defeats a den then reloads a game save from
    // BEFORE the defeat (the mod's registry saves immediately; the game's save may never have).
    // The auto-rule would otherwise revive an already-alive den (phantom toast, possible
    // double-spawned populations). Map clicks stay unguarded — an explicit Shift+click is user
    // intent. TreeRespawn Issue E is the same desync class; this is the cheap point-of-action
    // guard instead of the parked volatile/committed save-event design.
    private void SkipStaleTimerRevive(DenRecord rec)
    {
        Plugin.Logger.LogInfo($"[DenRespawn] Registry stale — den '{rec.TypeName}' at ({rec.X:F0},{rec.Z:F0}) already alive; skipping timer revive.");
        try
        {
            DenRegistry.MarkAlive(rec);
            DenRegistry.Save();
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"[DenRespawn] Error correcting stale registry record: {ex}");
        }
    }

    // Shared entry point for a remote revive request (map click or auto-respawn timer). Host-gated
    // (Runner.IsServer / IsSharedModeMasterClient). If a live tracked den already sits within RemoteMatchRadius of
    // the record, refreshes it immediately; otherwise force-loads the record's world tile and
    // queues it for the pending scan to pick up once the tile streams the den in.
    internal void EnqueueRemoteRefresh(DenRecord rec, string source)
    {
        try
        {
            if (Plugin.LocalPlayer == null)
            {
                Notify("Player not spawned yet.");
                return;
            }

            var player = Plugin.LocalPlayer;
            if (player.NetworkObject == null || player.NetworkObject.Runner == null ||
                (!player.NetworkObject.Runner.IsServer && !player.NetworkObject.Runner.IsSharedModeMasterClient))
            {
                Notify("Only the host can revive dens!");
                return;
            }

            var recPos = new Vector3(rec.X, rec.Y, rec.Z);

            Den[] dens;
            lock (Plugin.TrackedDens)
            {
                Plugin.TrackedDens.RemoveAll(d => d == null);
                dens = Plugin.TrackedDens.ToArray();
            }

            foreach (var den in dens)
            {
                try
                {
                    if (den == null) continue;
                    if (Vector3.Distance(recPos, den.transform.position) <= RemoteMatchRadius)
                    {
                        if (source == "timer" && !IsDenDefeated(den))
                        {
                            SkipStaleTimerRevive(rec);
                            return;
                        }
                        RunRefresh(den, source);
                        return;
                    }
                }
                catch { }
            }

            var streaming = Plugin.StreamingManager;
            if (streaming == null)
            {
                Notify("World streaming not ready");
                Plugin.Logger.LogWarning("[DenRespawn] EnqueueRemoteRefresh: no WorldStreamingManager captured.");
                return;
            }

            float tileSize = 128f;
            try
            {
                var cfg = WorldConfiguration.GetActive();
                if (cfg != null && cfg.tileSize > 0) tileSize = cfg.tileSize;
            }
            catch { }

            WorldTileId tileId;
            try { tileId = WorldTileId.GetLowest(rec.X, rec.Z, tileSize); }
            catch (Exception ex)
            {
                Plugin.Logger.LogError($"[DenRespawn] EnqueueRemoteRefresh: WorldTileId.GetLowest failed: {ex}");
                Notify("Den revive failed (see log)");
                return;
            }

            var anchor = new GameObject($"DenRespawn_Anchor_{_anchorCounter}");
            anchor.transform.position = recPos;
            UnityEngine.Object.DontDestroyOnLoad(anchor);
            _anchors.Add(anchor);

            int requestId = 771000 + _anchorCounter;
            _anchorCounter++;

            try { streaming.RequestLoadWorldTile(requestId, anchor.transform, tileId); }
            catch (Exception ex)
            {
                Plugin.Logger.LogError($"[DenRespawn] RequestLoadWorldTile failed: {ex}");
            }

            _pendingRefreshes.Add(new PendingRefresh { Record = rec, RequestedAt = Time.time, Source = source });

            string tileStr = tileId.ToString();
            Plugin.Logger.LogInfo($"[DenRespawn] Requested remote tile load for '{rec.TypeName}' at ({rec.X:F0},{rec.Z:F0}) tile={tileStr} reqId={requestId} source={source}");
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"[DenRespawn] EnqueueRemoteRefresh error: {ex}");
        }
    }

    // Pending-request scan (300-frame tick): resolve or time out each queued remote refresh.
    private void ScanPendingRefreshes()
    {
        if (_pendingRefreshes.Count == 0) return;

        Den[] dens;
        lock (Plugin.TrackedDens)
        {
            Plugin.TrackedDens.RemoveAll(d => d == null);
            dens = Plugin.TrackedDens.ToArray();
        }

        for (int i = _pendingRefreshes.Count - 1; i >= 0; i--)
        {
            var entry = _pendingRefreshes[i];
            var recPos = new Vector3(entry.Record.X, entry.Record.Y, entry.Record.Z);

            Den? found = null;
            foreach (var den in dens)
            {
                try
                {
                    if (den == null) continue;
                    if (Vector3.Distance(recPos, den.transform.position) <= RemoteMatchRadius)
                    {
                        found = den;
                        break;
                    }
                }
                catch { }
            }

            if (found != null)
            {
                if (entry.Source == "timer" && !IsDenDefeated(found))
                {
                    SkipStaleTimerRevive(entry.Record);
                }
                else
                {
                    RunRefresh(found, entry.Source);
                    Notify($"Monsters back at {entry.Record.TypeName}!");
                }
                _pendingRefreshes.RemoveAt(i);
            }
            else if (Time.time - entry.RequestedAt > RemoteRequestTimeoutSeconds)
            {
                Plugin.Logger.LogWarning($"[DenRespawn] Remote refresh timed out for '{entry.Record.TypeName}' at ({entry.Record.X:F0},{entry.Record.Z:F0}) (tile never produced a den)");
                Notify("Den revive timed out (see log)");
                _pendingRefreshes.RemoveAt(i);
            }
        }
    }

    // Day-rule tick: runs once per detected in-game day change. Skips silently (no Notify) on a
    // non-host — EnqueueRemoteRefresh's own host gate would otherwise spam "Only the host can
    // revive dens!" toasts on every client, every day.
    private void TickAutoRespawnRules()
    {
        if (Plugin.LocalPlayer == null) return;
        var player = Plugin.LocalPlayer;
        if (player.NetworkObject == null || player.NetworkObject.Runner == null ||
            (!player.NetworkObject.Runner.IsServer && !player.NetworkObject.Runner.IsSharedModeMasterClient))
            return;

        if (Plugin.AutoRules.Count == 0) return;

        var due = new List<DenRecord>();
        lock (DenRegistry.Records)
        {
            foreach (var rec in DenRegistry.Records.Values)
            {
                if (!rec.Defeated || rec.DefeatedOnDay < 0) continue;
                if (!Plugin.AutoRules.TryGetValue(rec.TypeName, out int ruleDays)) continue;
                if (DayCounter.CurrentDay - rec.DefeatedOnDay >= ruleDays)
                    due.Add(rec);
            }
        }

        foreach (var rec in due)
        {
            int ruleDays = Plugin.AutoRules.TryGetValue(rec.TypeName, out var d) ? d : -1;
            Plugin.Logger.LogInfo($"[DenRespawn] Auto-respawn rule '{rec.TypeName}:{ruleDays}' firing for den at ({rec.X:F0},{rec.Z:F0}) (defeated day {rec.DefeatedOnDay}, now day {DayCounter.CurrentDay})");
            EnqueueRemoteRefresh(rec, "timer");
        }
    }

    private void DiagnosticsDump()
    {
        Den[] dens;
        lock (Plugin.TrackedDens)
        {
            Plugin.TrackedDens.RemoveAll(d => d == null);
            dens = Plugin.TrackedDens.ToArray();
        }

        int shown = 0;
        foreach (var den in dens)
        {
            if (shown >= 20)
            {
                Plugin.Logger.LogInfo($"[DenRespawn][diag] ... and {dens.Length - shown} more");
                break;
            }

            try
            {
                string name = "?";
                string pos = "?";
                bool isActive = false;
                bool isDay = false;
                int cooldown = -1;
                bool blocked = false;
                int hitzones = -1;

                try { name = den.GetName(); } catch { }
                try { pos = den.transform.position.ToString(); } catch { }
                try { isActive = den.isActive; } catch { }
                try { isDay = den.isDay; } catch { }
                try { cooldown = den.ReviveCooldown; } catch { }
                try { blocked = den.IsBlockedByStructures(); } catch { }
                try { hitzones = den._lastActiveHitzones; } catch { }

                Plugin.Logger.LogInfo($"[DenRespawn][diag] den='{name}' pos={pos} isActive={isActive} isDay={isDay} reviveCooldown={cooldown} blockedByStructures={blocked} hitzones={hitzones}");

                try
                {
                    LogSpawnerDiag("alpha", den.alphaSpawner);
                }
                catch (Exception ex)
                {
                    Plugin.Logger.LogError($"[DenRespawn][diag] alphaSpawner read error: {ex}");
                }

                try
                {
                    var affected = den.affectedSpawners;
                    if (affected != null)
                    {
                        int idx = 0;
                        foreach (var spawner in affected)
                        {
                            LogSpawnerDiag($"node[{idx}]", spawner);
                            idx++;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Plugin.Logger.LogError($"[DenRespawn][diag] affectedSpawners read error: {ex}");
                }
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError($"[DenRespawn][diag] Error dumping a den: {ex}");
            }

            shown++;
        }

        int regCount, defeatedCount;
        lock (DenRegistry.Records)
        {
            regCount = DenRegistry.Records.Count;
            defeatedCount = 0;
            foreach (var r in DenRegistry.Records.Values)
                if (r.Defeated) defeatedCount++;
        }

        Plugin.Logger.LogInfo($"[DenRespawn][diag] registry={regCount} defeated={defeatedCount} pending={_pendingRefreshes.Count} day={DayCounter.CurrentDay} autoRules={Plugin.AutoRules.Count}");
    }

    private void LogSpawnerDiag(string label, PopulationSpawner? spawner)
    {
        if (spawner == null)
        {
            Plugin.Logger.LogInfo($"[DenRespawn][diag]   {label}: null");
            return;
        }

        bool isActive = false;
        bool blocked = false;
        bool noAlive = false;
        bool ignoreRespawning = false;

        try { isActive = spawner.isActive; } catch { }
        try { blocked = spawner.RespawningBlockedByStructures; } catch { }
        try { noAlive = spawner.HasNoAliveCreatures(); } catch { }
        try { ignoreRespawning = spawner.ignoreRespawning; } catch { }

        Plugin.Logger.LogInfo($"[DenRespawn][diag]   {label}: isActive={isActive} blockedByStructures={blocked} noAlive={noAlive} ignoreRespawning={ignoreRespawning}");
    }

    // Internal wrapper around the private GUI toast — lets patches (map click) surface feedback
    // without exposing ShowMessage itself.
    internal void Notify(string msg) => ShowMessage(msg);

    private void ShowMessage(string message)
    {
        _guiMessage = message;
        _guiMessageExpiry = Time.time + 5.0f;
    }

    private void OnGUI()
    {
        if (Time.time < _guiMessageExpiry && !string.IsNullOrEmpty(_guiMessage))
        {
            var shadowStyle = new GUIStyle(GUI.skin.label);
            shadowStyle.fontSize = 24;
            shadowStyle.fontStyle = FontStyle.Bold;
            shadowStyle.alignment = TextAnchor.MiddleCenter;
            shadowStyle.normal.textColor = Color.black;

            var textStyle = new GUIStyle(shadowStyle);
            textStyle.normal.textColor = Color.yellow;

            GUI.Label(new Rect(0, 81, Screen.width, 100), _guiMessage, shadowStyle);
            GUI.Label(new Rect(0, 80, Screen.width, 100), _guiMessage, textStyle);
        }
    }
}
