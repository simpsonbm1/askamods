using System.Collections.Generic;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using SSSGame;
using UnityEngine;

namespace TaskUnlockerMod
{
    // Polling MonoBehaviour (never subscribe to game Action events — see project gotchas).
    //
    // Cooking + item journal: recipes AND ordinary items are blueprint-discoverables — every
    // ItemInfo family implementing IDiscoverableItem (CrockpotRecipeInfo, ResourceInfo,
    // WearableItemInfo, PlantableItemInfo, WeaponizedItemInfo, plus their derived types) is gated
    // by BlueprintConditionsDatabase, and Rpc_AddDiscoverable(id) unlocks it natively (recipes
    // confirmed in-game 2026-07-05). Item discovery is what reveals item-gated tasks at other
    // buildings (tavern, harbor loading, ...): picking an item up calls into the same database
    // (BlueprintConditionsDatabase.OnCharacterItemAdded), which is exactly what we pre-fire.
    //
    // Fishing: tasks are NOT discovery-gated. They are created by the game itself when a
    // FishingGround is MARKED (buoy): mark → PopulationManager marked-count →
    // FishingStation._OnFishingGroundMarkChanged → _UpdateFishStatus → task. So we invoke the
    // vanilla request path — NetworkWorldDataManager.RequestDiscoverFishingGround +
    // RequestMarkFishinGround (game's own typo) — and let native code build the tasks.
    //
    // Steady-state cost (the v1.3.0 perf pass): world identity comes from the persistent
    // StorageManager's ActiveSessionID (TreeRespawn pattern) instead of a per-second
    // FindAnyObjectByType scene search; per-world managers are found once and dropped on
    // world-leave (stale-wrapper gotcha); the fishing pass reduces to a single ground-count read
    // once every ground is marked or given up on; already-discovered items are never re-sent.
    public class TaskUnlockTracker : MonoBehaviour
    {
        private const int MaxMarkAttempts = 3;
        private const float FishingPassInterval = 5f;
        private const float PollInterval = 1f;   // 1 Hz — nothing here needs per-frame polling
        // Spread the one-time unlock burst across passes instead of flooding Fusion in one frame.
        private const int MaxUnlocksPerPass = 64;
        // NetworkBlueprintConditionsDatabase._discoverablesMaxSize (Cpp2IL codegen constant,
        // 2026-07-14). The Fusion network array holds 1 status bit per registered discoverable;
        // never risk pushing registrations past it (BitSet-overflow family of crashes).
        private const int DiscoverablesNetworkMax = 768;

        private float _pollTimer;

        // Persistent across worlds (the manager itself survives world unloads).
        private SandSailorStudio.Storage.StorageManager? _storage;

        // ── Per-world state — all of it dropped by ResetWorldState() on world leave/switch ──
        private string _worldId = "";
        private BlueprintConditionsDatabase? _blueprintDb;
        private PopulationManager? _popMgr;

        private bool _scanDone;
        private readonly Queue<(int id, bool discover)> _unlockQueue = new();
        private bool _capacityWarned;
        private int _zeroSendPasses;

        private float _nextFishingPass;
        private bool _loggedGroundTotal;
        private int _idleGroundCount = -1;   // grounds.Count when the pass last confirmed nothing left to do
        private readonly Dictionary<int, int> _markAttempts = new();
        private readonly HashSet<int> _handledGrounds = new();   // marked once, or given up on — never touched again

        void Update()
        {
            _pollTimer += Time.unscaledDeltaTime;
            if (_pollTimer < PollInterval) return;
            _pollTimer = 0f;

            // World gate: empty ActiveSessionID == main menu / no world. Same-world reloads keep
            // the same id, but pass through an empty read at the menu, which is the reset signal
            // (per-world wrapper caching is only safe with this reset — universal gotcha).
            string sessionId = ReadSessionId();
            if (string.IsNullOrEmpty(sessionId))
            {
                if (_worldId.Length > 0) ResetWorldState();
                return;
            }
            if (sessionId != _worldId)
            {
                ResetWorldState();
                _worldId = sessionId;
            }

            if (_blueprintDb == null)
            {
                _blueprintDb = UnityEngine.Object.FindAnyObjectByType<BlueprintConditionsDatabase>();
                if (_blueprintDb == null) return;   // world still loading — retry next poll
            }

            if (!_scanDone)
                ScanDiscoverables();
            else if (_unlockQueue.Count > 0)
                DrainUnlockQueue();

            if (Plugin.UnlockFish.Value && Time.unscaledTime >= _nextFishingPass)
            {
                _nextFishingPass = Time.unscaledTime + FishingPassInterval;
                MarkFishingGrounds();
            }
        }

        private string ReadSessionId()
        {
            try
            {
                if (_storage == null)
                    _storage = UnityEngine.Object.FindAnyObjectByType<SandSailorStudio.Storage.StorageManager>();
                if (_storage == null) return "";
                return _storage.ActiveSessionID ?? "";
            }
            catch
            {
                _storage = null;   // re-find next poll
                return "";
            }
        }

        private void ResetWorldState()
        {
            _worldId = "";
            _blueprintDb = null;
            _popMgr = null;
            _scanDone = false;
            _unlockQueue.Clear();
            _capacityWarned = false;
            _zeroSendPasses = 0;
            _nextFishingPass = 0f;
            _loggedGroundTotal = false;
            _idleGroundCount = -1;
            _markAttempts.Clear();
            _handledGrounds.Clear();
        }

        // ── Discoverables (cooking recipes + item journal entries) ──────────────────────────

        // Native class names whose instances carry the IDiscoverableItem members
        // (requiresDiscovery / IsDiscovered / Discover). Derived types (ConsumableInfo,
        // AmmoItemInfo, FishingItemInfo, ...) are matched by walking the native parent chain.
        private static readonly string[] DiscoverableBases =
            { "CrockpotRecipeInfo", "ResourceInfo", "WearableItemInfo", "PlantableItemInfo", "WeaponizedItemInfo" };

        private static bool CategoryEnabled(string family) => family switch
        {
            "CrockpotRecipeInfo" => Plugin.UnlockCookingRecipes.Value,
            "ResourceInfo" => Plugin.UnlockResourceJournalEntries.Value,
            "PlantableItemInfo" => Plugin.UnlockSeedJournalEntries.Value,
            "WearableItemInfo" => Plugin.UnlockWearableJournalEntries.Value,
            "WeaponizedItemInfo" => Plugin.UnlockWeaponJournalEntries.Value,
            _ => false,
        };

        private void ScanDiscoverables()
        {
            var netDb = _blueprintDb!.NetworkLogic;
            if (netDb == null) return;              // retry next poll
            if (!netDb.HasStateAuthority) return;   // host runs the unlock; clients receive state

            var items = Plugin.ItemDb?.CompleteItemInfoList?.itemInfoList;
            if (items == null) return;              // retry next poll

            // Save-data load gate: the database is findable (and its network logic spawned) while
            // the world is still loading, BEFORE its storage pack deserializes — at that point
            // IsDiscovered reads false for everything (v1.3.0 scanned there and re-sent all 429
            // discoverables every load). Characters register on this very database only once the
            // world is actually running — the same signal vanilla pickup-discovery relies on —
            // so wait for one.
            try
            {
                var chars = _blueprintDb.GetRegisteredCharacters();
                if (chars == null || chars.Count == 0) return;   // retry next poll
            }
            catch { return; }

            var sw = System.Diagnostics.Stopwatch.StartNew();
            int recipes = 0, journal = 0, resets = 0, alreadyKnown = 0;
            bool resetDisabled = Plugin.ResetJournalForDisabledCategories.Value;
            for (int i = 0; i < items.Count; i++)
            {
                var info = items[i];
                if (info == null) continue;

                // Managed casts lie for list elements materialized under the base type —
                // identify by native class (chain), then rewrap (project gotcha).
                if ((object)info is not Il2CppObjectBase obj) continue;
                string? family = DiscoverableFamily(obj);
                if (family == null) continue;

                bool enabled = CategoryEnabled(family);
                if (!enabled && !resetDisabled) continue;

                if (!NeedsUnlock(obj, family, out bool discovered))
                    continue;

                if (enabled)
                {
                    if (discovered) { alreadyKnown++; continue; }
                    _unlockQueue.Enqueue((info.id, true));
                    if (family == "CrockpotRecipeInfo") recipes++; else journal++;
                    if (Plugin.DiagnosticsLogItemUnlocks.Value)
                        Plugin.Log.LogInfo($"TaskUnlocker: queueing '{info.name}' (id={info.id}, {family})");
                }
                else if (discovered)
                {
                    // Disabled category + repair switch on: re-hide the journal entry.
                    _unlockQueue.Enqueue((info.id, false));
                    resets++;
                    if (Plugin.DiagnosticsLogItemUnlocks.Value)
                        Plugin.Log.LogInfo($"TaskUnlocker: queueing RESET of '{info.name}' (id={info.id}, {family})");
                }
            }
            sw.Stop();

            Plugin.Log.LogInfo($"TaskUnlocker: discoverables scan done in {sw.Elapsed.TotalMilliseconds:F1} ms — " +
                $"queued {recipes} crockpot recipes + {journal} item journal entries + {resets} resets, " +
                $"{alreadyKnown} already discovered " +
                $"(registered discoverables: {SafeDiscoverableCount(netDb)}/{DiscoverablesNetworkMax}).");
            _scanDone = true;
        }

        // requiresDiscovery / IsDiscovered live on the family base classes, so rewrapping as the
        // matched base is enough for any derived type.
        private bool NeedsUnlock(Il2CppObjectBase obj, string family, out bool discovered)
        {
            discovered = false;
            try
            {
                switch (family)
                {
                    case "CrockpotRecipeInfo":
                    {
                        var t = new CrockpotRecipeInfo(obj.Pointer);
                        if (!t.requiresDiscovery) return false;
                        discovered = t.IsDiscovered(_blueprintDb);
                        return true;
                    }
                    case "ResourceInfo":
                    {
                        var t = new ResourceInfo(obj.Pointer);
                        if (!t.requiresDiscovery) return false;
                        discovered = t.IsDiscovered(_blueprintDb);
                        return true;
                    }
                    case "WearableItemInfo":
                    {
                        var t = new SandSailorStudio.Inventory.WearableItemInfo(obj.Pointer);
                        if (!t.requiresDiscovery) return false;
                        discovered = t.IsDiscovered(_blueprintDb);
                        return true;
                    }
                    case "PlantableItemInfo":
                    {
                        var t = new SandSailorStudio.Inventory.PlantableItemInfo(obj.Pointer);
                        if (!t.requiresDiscovery) return false;
                        discovered = t.IsDiscovered(_blueprintDb);
                        return true;
                    }
                    case "WeaponizedItemInfo":
                    {
                        var t = new SandSailorStudio.Inventory.WeaponizedItemInfo(obj.Pointer);
                        if (!t.requiresDiscovery) return false;
                        discovered = t.IsDiscovered(_blueprintDb);
                        return true;
                    }
                }
            }
            catch { }
            return false;
        }

        private void DrainUnlockQueue()
        {
            var netDb = _blueprintDb!.NetworkLogic;
            if (netDb == null || !netDb.HasStateAuthority) return;

            // Capacity guard, pessimistic per pass: assume every add could append a registration.
            // If adds are really status-only writes the count won't grow, so the next pass re-reads
            // the same base and keeps making progress — the guard only hard-stops genuine growth.
            // (In-game observation 2026-07-14: it doesn't grow — registration is init-time, adds
            // are status-bit writes — but the guard stays as insurance.)
            int baseCount = SafeDiscoverableCount(netDb);
            int sent = 0;
            while (_unlockQueue.Count > 0 && sent < MaxUnlocksPerPass)
            {
                if (baseCount >= 0 && baseCount + sent >= DiscoverablesNetworkMax)
                {
                    if (!_capacityWarned)
                    {
                        Plugin.Log.LogWarning($"TaskUnlocker: discoverables network array near capacity " +
                            $"({baseCount}+{sent}/{DiscoverablesNetworkMax}) — pausing unlocks ({_unlockQueue.Count} left).");
                        _capacityWarned = true;
                    }
                    break;
                }
                var (id, discover) = _unlockQueue.Dequeue();
                if (discover)
                    netDb.Rpc_AddDiscoverable(id);              // proven unlock path (v1.2.0+)
                else
                    netDb.AddDiscoverable(id, false);           // repair path: re-hide the entry
                sent++;
            }

            if (sent == 0)
            {
                // Two stalled passes in a row = the array is genuinely full; dropping the rest is
                // the safe move (better a partial unlock than overflowing Fusion network state).
                if (++_zeroSendPasses >= 2 && _unlockQueue.Count > 0)
                {
                    Plugin.Log.LogWarning($"TaskUnlocker: discoverables array full — dropping {_unlockQueue.Count} remaining unlock(s).");
                    _unlockQueue.Clear();
                }
                return;
            }
            _zeroSendPasses = 0;

            if (Plugin.DiagnosticsLogPassTimings.Value)
                Plugin.Log.LogInfo($"TaskUnlocker: unlock pass sent {sent} discoverable(s), {_unlockQueue.Count} queued.");
            if (_unlockQueue.Count == 0)
                Plugin.Log.LogInfo("TaskUnlocker: unlock queue drained — all discoverables sent; discoverables work is now idle for this world.");
        }

        private static int SafeDiscoverableCount(SSSGame.Network.NetworkBlueprintConditionsDatabase netDb)
        {
            try { return netDb._discoverableIDs?.Count ?? -1; }
            catch { return -1; }
        }

        // ── Fishing grounds ──────────────────────────────────────────────────────────────────

        private void MarkFishingGrounds()
        {
            if (_popMgr == null)
            {
                _popMgr = UnityEngine.Object.FindAnyObjectByType<PopulationManager>();
                if (_popMgr == null) return;
            }

            Il2CppSystem.Collections.Generic.List<FishingGround>? grounds;
            int count;
            try
            {
                grounds = _popMgr._fishingGrounds;
                count = grounds?.Count ?? -1;
            }
            catch
            {
                _popMgr = null;   // re-find next pass
                return;
            }
            if (grounds == null || count < 0) return;

            // Steady state: every known ground handled and the registry hasn't grown (streaming
            // registers grounds late) — the whole pass is this one Count read.
            if (count == _idleGroundCount) return;

            if (!_loggedGroundTotal && count > 0)
            {
                Plugin.Log.LogInfo($"TaskUnlocker: PopulationManager has {count} registered fishing grounds.");
                _loggedGroundTotal = true;
            }

            var sw = Plugin.DiagnosticsLogPassTimings.Value ? System.Diagnostics.Stopwatch.StartNew() : null;
            int requested = 0, unresolved = 0;
            for (int i = 0; i < count; i++)
            {
                var fg = grounds[i];
                if (fg == null) continue;

                int id = fg._id;
                if (_handledGrounds.Contains(id)) continue;

                if (fg.IsMarked)
                {
                    // Done — and deliberately never re-marked, so a player who unmarks a buoy
                    // isn't fought over it.
                    _handledGrounds.Add(id);
                    _markAttempts.Remove(id);
                    continue;
                }

                _markAttempts.TryGetValue(id, out int attempts);
                if (attempts >= MaxMarkAttempts)
                {
                    Plugin.Log.LogWarning($"TaskUnlocker: ground id={id} fish='{fg.fish?.name}' still unmarked after " +
                        $"{MaxMarkAttempts} requests (Discovered={fg.Discovered}, Disabled={fg.Disabled}) — " +
                        "marking may be gated (see FishingGround.UnlockMarking); leaving it alone.");
                    _handledGrounds.Add(id);
                    _markAttempts.Remove(id);
                    continue;
                }

                var net = fg.NetworkCommunicator;
                if (net == null) { unresolved++; continue; }   // not network-ready yet — retry next pass

                if (!fg.Discovered)
                    net.RequestDiscoverFishingGround(id);
                net.RequestMarkFishinGround(id, true);   // game's own typo
                _markAttempts[id] = attempts + 1;
                requested++;

                if (Plugin.DiagnosticsLogItemUnlocks.Value)
                    Plugin.Log.LogInfo($"Fishing: requested discover+mark for ground id={id} fish='{fg.fish?.name}' " +
                        $"(was Discovered={fg.Discovered}, Disabled={fg.Disabled}, attempt {attempts + 1})");
            }

            if (requested > 0)
                Plugin.Log.LogInfo($"TaskUnlocker: fishing pass requested marking of {requested} ground(s).");
            if (sw != null)
            {
                sw.Stop();
                Plugin.Log.LogInfo($"TaskUnlocker: fishing pass over {count} ground(s) took {sw.Elapsed.TotalMilliseconds:F2} ms " +
                    $"(requested={requested}, awaitingNetwork={unresolved}, inFlight={_markAttempts.Count}).");
            }

            if (requested == 0 && unresolved == 0 && _markAttempts.Count == 0)
            {
                // Latch silently at count 0 (registry not populated yet — the world-load window);
                // log the marker on each transition into idle at a real ground count.
                if (count > 0 && _idleGroundCount != count)
                    Plugin.Log.LogInfo($"TaskUnlocker: all {count} fishing grounds handled — fishing pass idle " +
                        "(rescans only if the ground registry grows).");
                _idleGroundCount = count;
            }
        }

        private static string? DiscoverableFamily(Il2CppObjectBase o)
        {
            try
            {
                var cls = IL2CPP.il2cpp_object_get_class(o.Pointer);
                while (cls != System.IntPtr.Zero)
                {
                    string? name = System.Runtime.InteropServices.Marshal.PtrToStringAnsi(IL2CPP.il2cpp_class_get_name(cls));
                    if (name == null) return null;
                    for (int i = 0; i < DiscoverableBases.Length; i++)
                        if (name == DiscoverableBases[i]) return DiscoverableBases[i];
                    cls = IL2CPP.il2cpp_class_get_parent(cls);
                }
            }
            catch { }
            return null;
        }
    }
}
