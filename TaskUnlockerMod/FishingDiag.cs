using System.Collections.Generic;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using SSSGame;
using SSSGame.AI.FSM;
using UnityEngine;

namespace TaskUnlockerMod
{
    // Read-only fishing diagnostic for the 2026-10-08 Nexus report (iMasonite): grounds marked by
    // this mod produce fishing-hut tasks, but the fisherman never rows out to them. Three layers,
    // so a run answers the question even when one layer is silent:
    //   1. Ground table - every ground near the fishing hut with its mark/discovery state, distance
    //      and whether it is inside the hut's maxFishingGroundRange.
    //   2. Fishing-AI patches - which outlets FSM_Fishing.GetAvailableFishingSpots offers the
    //      fisherman, when he mounts a boat, starts and ends a fishing session. Applied lazily once a
    //      world is loaded (never at plugin load: class-init timing, see villager-ammo dead-end), and
    //      each one logs its first firing so an AOT-inlined method that never fires is visible.
    //   3. Boat polling - no patches at all: each boat's distance from the hut and its nearest
    //      ground, logged whenever the boat has moved, so a trip is recorded even if layer 2 is dead.
    // Nothing here writes game state.
    internal static class FishingDiag
    {
        private const float BoatPollInterval = 5f;
        private const float BoatMoveLogThreshold = 5f;   // metres moved since the last boat line
        private const int TableMaxRows = 30;

        // Grounds the tracker sent a discover+mark request for this world (GetInstanceID), so
        // every log line can say how a ground got its state.
        internal static readonly HashSet<int> ModMarked = new();

        private static bool _patched;
        private static readonly HashSet<string> _fired = new();
        private static readonly Dictionary<long, string> _lastSpots = new();

        private static FishingStation? _station;
        private static float _nextBoatPoll;
        private static readonly Dictionary<int, Vector3> _lastBoatPos = new();
        private static bool _tableAfterFirstTrip;

        internal static bool Enabled => Plugin.DiagnosticsFishing.Value;

        internal static void ResetWorld()
        {
            ModMarked.Clear();
            _lastSpots.Clear();
            _station = null;
            _nextBoatPoll = 0f;
            _lastBoatPos.Clear();
            _tableAfterFirstTrip = false;
            _hutFromAgent = false;
            LastGrounds = null;
            _requestedAt.Clear();
            _lastMarked.Clear();
            _nextWatch = 0f;
        }

        // ── Lazy patching ────────────────────────────────────────────────────────────────────

        internal static void EnsurePatched()
        {
            if (_patched || !Enabled) return;
            _patched = true;
            var h = Plugin.HarmonyInstance;
            if (h == null) return;
            TryPatch(h, "GetAvailableFishingSpots", nameof(GetSpotsPostfix));
            TryPatch(h, "MountBoat", nameof(MountBoatPostfix));
            TryPatch(h, "SetFishingInteraction", nameof(SetFishingInteractionPostfix));
            TryPatch(h, "_EndFishingSession", nameof(EndSessionPostfix));
        }

        private static void TryPatch(Harmony h, string target, string postfix)
        {
            try
            {
                var m = AccessTools.Method(typeof(FSM_Fishing), target);
                if (m == null) { Plugin.Log.LogWarning($"FishDiag: FSM_Fishing.{target} not found - not patched."); return; }
                h.Patch(m, postfix: new HarmonyMethod(typeof(FishingDiag), postfix));
                Plugin.Log.LogInfo($"FishDiag: patched FSM_Fishing.{target} (a 'first fire' line follows if the game ever calls it).");
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning($"FishDiag: patching FSM_Fishing.{target} failed: {e.GetType().Name}: {e.Message}");
            }
        }

        private static void FirstFire(string name)
        {
            if (_fired.Add(name)) Plugin.Log.LogInfo($"FishDiag: first fire of FSM_Fishing.{name}.");
        }

        // ── Patches (postfix only, every body guarded) ───────────────────────────────────────

        private static void GetSpotsPostfix(FSM_Fishing.FishingData actionData, bool filterNoBait)
        {
            try
            {
                FirstFire("GetAvailableFishingSpots");
                if (actionData == null) return;
                var list = actionData.fishingOutlets;
                int n = list?.Count ?? -1;
                var sb = new System.Text.StringBuilder();
                sb.Append($"filterNoBait={filterNoBait} offered={n}");
                for (int i = 0; list != null && i < n; i++)
                {
                    var od = list[i];
                    if (od == null) { sb.Append($"\n    [{i}] null"); continue; }
                    sb.Append($"\n    [{i}] priority={od.priority:F2} reachable={od.reachable} needsBoat={SafeNeedsBoat(od)} {DescribeOutlet(od.outlet)}");
                }
                // Same agent offered the same list again: say nothing (this runs on every re-plan).
                CaptureFishermansHut(actionData);
                long key = PtrOf(actionData);
                string text = sb.ToString();
                if (_lastSpots.TryGetValue(key, out var prev) && prev == text) return;
                _lastSpots[key] = text;
                Plugin.Log.LogInfo($"FishDiag: AI fishing-spot search for {AgentName(actionData)}: {text}");
            }
            catch (System.Exception e) { Plugin.Log.LogWarning($"FishDiag: GetSpots postfix: {e.Message}"); }
        }

        private static void MountBoatPostfix(FSM_Fishing.FishingData actionData, RowingInteraction boat)
        {
            try
            {
                FirstFire("MountBoat");
                Plugin.Log.LogInfo($"FishDiag: {AgentName(actionData)} MOUNTED A BOAT, heading for {DescribeCurrent(actionData)}");
            }
            catch (System.Exception e) { Plugin.Log.LogWarning($"FishDiag: MountBoat postfix: {e.Message}"); }
        }

        private static void SetFishingInteractionPostfix(FSM_Fishing.FishingData actionData, bool __result)
        {
            try
            {
                FirstFire("SetFishingInteraction");
                Plugin.Log.LogInfo($"FishDiag: {AgentName(actionData)} STARTED FISHING (result={__result}) at {DescribeCurrent(actionData)}");
            }
            catch (System.Exception e) { Plugin.Log.LogWarning($"FishDiag: SetFishingInteraction postfix: {e.Message}"); }
        }

        private static void EndSessionPostfix(FSM_Fishing.FishingData actionData)
        {
            try
            {
                FirstFire("_EndFishingSession");
                Plugin.Log.LogInfo($"FishDiag: {AgentName(actionData)} ended a fishing session at {DescribeCurrent(actionData)}");
            }
            catch (System.Exception e) { Plugin.Log.LogWarning($"FishDiag: EndSession postfix: {e.Message}"); }
        }

        // ── Mark-request gates + lost-mark watcher (2026-10-08 hypothesis) ───────────────────
        // NetworkWorldDataManager.MarkFishingGround records a mark in world tile data only when
        // get_CanSendToAnyone and NetworkSession.isMaster are both true (Ghidra decompile); with
        // either false only the live flag changes. These lines show which state each of the mod's
        // requests met, and whether any mark the mod placed is later lost.
        private static readonly Dictionary<int, float> _requestedAt = new();
        private static readonly Dictionary<int, bool> _lastMarked = new();
        private static float _nextWatch;

        internal static void BeforeRequest(SSSGame.Network.NetworkWorldDataManager net, FishingGround g, bool saveLoaded)
        {
            try
            {
                int key = g.GetInstanceID();
                _requestedAt[key] = Time.unscaledTime;
                Plugin.Log.LogInfo($"FishDiag: t={Time.unscaledTime:F1} REQUEST mark id={g._id} uid={g.uid} fish='{g.fish?.name}' " +
                    $"gates: {Gates(net)} saveLoaded={saveLoaded} before: marked={g.IsMarked} discovered={g.Discovered}");
            }
            catch (System.Exception e) { Plugin.Log.LogWarning($"FishDiag: before-request: {e.Message}"); }
        }

        internal static void AfterRequest(FishingGround g)
        {
            try
            {
                Plugin.Log.LogInfo($"FishDiag:   after request: marked={g.IsMarked} discovered={g.Discovered}");
                _lastMarked[g.GetInstanceID()] = g.IsMarked;
            }
            catch (System.Exception e) { Plugin.Log.LogWarning($"FishDiag: after-request: {e.Message}"); }
        }

        private static string Gates(SSSGame.Network.NetworkWorldDataManager? net)
        {
            if (net == null) return "net=null";
            string canSend = "?", master = "?";
            try { canSend = net.CanSendToAnyone.ToString(); } catch { }
            try { var s = net.session; master = s == null ? "session=null" : s.isMaster.ToString(); } catch { }
            return $"canSendToAnyone={canSend} isMaster={master}";
        }

        internal static void WatchMarks(Il2CppSystem.Collections.Generic.List<FishingGround>? grounds)
        {
            if (!Enabled || grounds == null || Time.unscaledTime < _nextWatch) return;
            _nextWatch = Time.unscaledTime + 2f;
            try
            {
                for (int i = 0; i < grounds.Count; i++)
                {
                    var g = grounds[i];
                    if (g == null) continue;
                    int key = g.GetInstanceID();
                    if (!ModMarked.Contains(key)) continue;
                    bool m = g.IsMarked;
                    if (!_lastMarked.TryGetValue(key, out bool prev)) { _lastMarked[key] = m; continue; }
                    if (prev == m) continue;
                    _lastMarked[key] = m;
                    _requestedAt.TryGetValue(key, out float at);
                    Plugin.Log.LogInfo($"FishDiag: t={Time.unscaledTime:F1} {(m ? "MARK SET" : "MARK LOST")} on a mod-marked ground " +
                        $"{(Time.unscaledTime - at):F1}s after the mod's request: {DescribeGround(g)} gates now: {Gates(g.NetworkCommunicator)}");
                }
            }
            catch (System.Exception e) { Plugin.Log.LogWarning($"FishDiag: mark watch: {e.Message}"); }
        }

        // ── Ground table + boat polling (called from the tracker's 1 Hz poll) ────────────────

        internal static void DumpTable(string reason, Il2CppSystem.Collections.Generic.List<FishingGround>? grounds)
        {
            if (!Enabled || grounds == null) return;
            try
            {
                var st = FindStation();
                Vector3 origin = Vector3.zero;
                float range = -1f;
                if (st != null)
                {
                    origin = DockOrHut(st);
                    range = st.maxFishingGroundRange;
                    int boats = st.boats?.Count ?? -1;
                    Plugin.Log.LogInfo($"FishDiag: TABLE ({reason}) fishing hut at {Fmt(st.transform.position)}, dock at {Fmt(origin)} " +
                        $"(distances below are from the dock) maxFishingGroundRange={range:F0} boats={boats}");
                }
                else
                {
                    Plugin.Log.LogInfo($"FishDiag: TABLE ({reason}) no fishing hut found - distances are from the world origin.");
                }

                var rows = new List<(float d, string line)>();
                int marked = 0, discovered = 0;
                for (int i = 0; i < grounds.Count; i++)
                {
                    var g = grounds[i];
                    if (g == null) continue;
                    if (g.IsMarked) marked++;
                    if (g.Discovered) discovered++;
                    float d = Vector3.Distance(origin, g.transform.position);
                    string inRange = range > 0 ? (d <= range ? " IN-RANGE" : " out-of-range") : "";
                    rows.Add((d, $"dist={d:F0}{inRange} {DescribeGround(g)}"));
                }
                rows.Sort((a, b) => a.d.CompareTo(b.d));
                Plugin.Log.LogInfo($"FishDiag: {grounds.Count} grounds, {marked} marked, {discovered} discovered; nearest {System.Math.Min(TableMaxRows, rows.Count)}:");
                for (int i = 0; i < rows.Count && i < TableMaxRows; i++)
                    Plugin.Log.LogInfo($"FishDiag:   {rows[i].line}");
            }
            catch (System.Exception e) { Plugin.Log.LogWarning($"FishDiag: table: {e.Message}"); }
        }

        internal static void PollBoats(Il2CppSystem.Collections.Generic.List<FishingGround>? grounds)
        {
            if (grounds != null) LastGrounds = grounds;
            if (!Enabled || Time.unscaledTime < _nextBoatPoll) return;
            _nextBoatPoll = Time.unscaledTime + BoatPollInterval;
            try
            {
                var st = FindStation();
                var boats = st?.boats;
                if (st == null || boats == null) return;
                Vector3 hut = st.transform.position;
                for (int i = 0; i < boats.Count; i++)
                {
                    var b = boats[i];
                    if (b == null) continue;
                    int key = b.GetInstanceID();
                    Vector3 p = b.transform.position;
                    if (_lastBoatPos.TryGetValue(key, out var last) && Vector3.Distance(last, p) < BoatMoveLogThreshold) continue;
                    bool first = !_lastBoatPos.ContainsKey(key);
                    _lastBoatPos[key] = p;

                    string nearest = "none";
                    if (grounds != null)
                    {
                        float best = float.MaxValue;
                        FishingGround? bg = null;
                        for (int j = 0; j < grounds.Count; j++)
                        {
                            var g = grounds[j];
                            if (g == null) continue;
                            float d = Vector3.Distance(p, g.transform.position);
                            if (d < best) { best = d; bg = g; }
                        }
                        if (bg != null) nearest = $"{best:F0}m from {DescribeGround(bg)}";
                    }
                    bool driven = false;
                    try { driven = b.controllerAgent != null; } catch { }
                    Plugin.Log.LogInfo($"FishDiag: BOAT {key} {(first ? "seen" : "MOVED")} at {Fmt(p)}, {Vector3.Distance(hut, p):F0}m from hut, driven={driven}; nearest ground {nearest}");

                    if (!first && !_tableAfterFirstTrip)
                    {
                        _tableAfterFirstTrip = true;
                        DumpTable("first boat movement", grounds);
                    }
                }
            }
            catch (System.Exception e) { Plugin.Log.LogWarning($"FishDiag: boat poll: {e.Message}"); }
        }

        // A save can hold several fishing huts; FindAnyObjectByType returned a lake hut with no boats
        // on 2026-10-08 while the fisherman worked a sea hut. The fisherman's own workstation is the
        // authoritative hut, so the first AI search re-targets the table and boat tracking to it.
        private static bool _hutFromAgent;
        internal static Il2CppSystem.Collections.Generic.List<FishingGround>? LastGrounds;

        private static void CaptureFishermansHut(FSM_Fishing.FishingData actionData)
        {
            if (_hutFromAgent) return;
            try
            {
                var a = actionData?.agent;
                if (a == null || (object)a is not Il2CppObjectBase ab) return;
                var ws = new Villager(ab.Pointer).GetWorkstation();
                if (ws == null || (object)ws is not Il2CppObjectBase wb) return;
                string cls = NativeClassName(wb.Pointer);
                if (cls != "FishingStation") { Plugin.Log.LogInfo($"FishDiag: fisherman's workstation is a {cls}, not a FishingStation."); return; }
                _hutFromAgent = true;
                _station = new FishingStation(wb.Pointer);
                _lastBoatPos.Clear();
                Plugin.Log.LogInfo($"FishDiag: fisherman {AgentName(actionData)} works at the hut at {Fmt(_station.transform.position)}; table and boat tracking now use it.");
                DumpTable("fisherman's own hut", LastGrounds);
            }
            catch (System.Exception e) { Plugin.Log.LogWarning($"FishDiag: hut capture: {e.Message}"); }
        }

        private static FishingStation? FindStation()
        {
            if (_station != null) return _station;
            try { _station = Object.FindAnyObjectByType<FishingStation>(); } catch { _station = null; }
            return _station;
        }

        private static Vector3 DockOrHut(FishingStation st)
        {
            try { var d = st.dock; if (d != null) return d.transform.position; } catch { }
            return st.transform.position;
        }

        // ── Formatting helpers ───────────────────────────────────────────────────────────────

        internal static string DescribeGround(FishingGround g)
        {
            int key = g.GetInstanceID();
            string how = ModMarked.Contains(key) ? " [mod requested mark]" : "";
            string fish = "?";
            try { fish = g.fish?.name ?? "null"; } catch { }
            string extra = "";
            try { extra = $" depleted={g.IsDepleted} lake={g.IsOnLake} count={g.CurrentCount}"; } catch { }
            return $"ground id={g._id} uid={g.uid} fish='{fish}' marked={g.IsMarked} discovered={g.Discovered} disabled={g.Disabled}{extra} at {Fmt(g.transform.position)}{how}";
        }

        private static string DescribeOutlet(SSSGame.AI.FishingOutlet? o)
        {
            if (o == null) return "outlet=null";
            if ((object)o is not Il2CppObjectBase b) return "outlet=?";
            string cls = NativeClassName(b.Pointer);
            if (cls == "FishingGround") return DescribeGround(new FishingGround(b.Pointer));
            return $"{cls} at {Fmt(o.transform.position)}";
        }

        private static string DescribeCurrent(FSM_Fishing.FishingData actionData)
        {
            try { return actionData == null ? "no action data" : DescribeOutlet(actionData.CurrentOutlet); }
            catch (System.Exception e) { return $"(current outlet unreadable: {e.Message})"; }
        }

        private static string AgentName(FSM_Fishing.FishingData actionData)
        {
            try
            {
                var a = actionData?.agent;
                if (a == null) return "agent?";
                if ((object)a is Il2CppObjectBase b) return new Villager(b.Pointer).name;   // fishing agents are villagers; name is read natively
            }
            catch { }
            return "agent?";
        }

        private static string SafeNeedsBoat(FSM_Fishing.FishingOutletData od)
        {
            try { return od.NeedsBoat.ToString(); } catch { return "?"; }
        }

        private static long PtrOf(object o) => o is Il2CppObjectBase b ? b.Pointer.ToInt64() : 0;

        private static string NativeClassName(System.IntPtr ptr)
        {
            try
            {
                var cls = IL2CPP.il2cpp_object_get_class(ptr);
                return System.Runtime.InteropServices.Marshal.PtrToStringAnsi(IL2CPP.il2cpp_class_get_name(cls)) ?? "?";
            }
            catch { return "?"; }
        }

        // Unity structs must never be interpolated straight into a log call (VerificationException).
        internal static string Fmt(Vector3 v) => "(" + v.x.ToString("F0") + "," + v.y.ToString("F0") + "," + v.z.ToString("F0") + ")";
    }
}
