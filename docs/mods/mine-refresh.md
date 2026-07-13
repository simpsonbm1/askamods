# Mod 11: MineRefreshMod — COMPLETE (v1.3.4)

**Goal:** Safely and fully refresh/regenerate a mine, its sub-hallways, resource nodes, and item/chest spawners on-demand via a configurable hotkey.

**v1.3.4 — SafetyRadius default lowered to 10m (confirmed in-game 2026-07-13)**
Lowered `SafetyRadius` default from 25.0 to 10.0 meters. The 25m default caused
frequent false-positive "player/worker too close" blocks. Caveat: BepInEx preserves
existing `.cfg` values, so new default applies only to fresh configs — existing users
must manually edit or delete the SafetyRadius line to adopt it.

**v1.3.3 — Authority gate rework (confirmed in-game 2026-07-12)**
Reworked the authority gate following a Nexus bug report (user pdp2010, self-hosted co-op).
The old gate conflated three conditions into one misleading block. The new gate in
`MineRefreshTracker` evaluates 4 steps: (1) if `[General] ForceAllowRefresh` is true, skips
authority entirely (logs warning about co-op sync risk); (2) null NetworkObject/Runner → auto-
allowed as offline/solo session; (3) allowed if `Runner.IsServer || Runner.IsSharedModeMasterClient
|| Runner.IsSinglePlayer`; (4) otherwise blocked, logging full runner diagnostics (GameMode, Mode,
IsServer, IsClient, IsSharedModeMasterClient, IsSinglePlayer, IsSceneMaster, IsConnectedToServer)
and pointing at `ForceAllowRefresh` escape hatch. Solo in-game test confirms refresh works; log
shows "[MineRefreshMod] Refresh authorized: host/server authority confirmed."

**v1.3.2 — Typing guard (confirmed in-game 2026-07-10)**
Trigger key now ignored while a game text field is focused. Confirmed: hotkey works again after the rename window closes.

**Game subsystem:** [Caves, Mines & Hallway Excavation](../architecture.md#caves-mines--hallway-excavation)
— the classes, data structures, and state managers governing the ASKA cave and excavation systems,
along with the IL2CPP interop quirks (like missing inheritance and casting limitations) discovered
and solved.

**Working approach:**
- **On-Demand Hotkey**: A persistent MonoBehaviour (`MineRefreshTracker`) polls for a configurable
  hotkey (default: `U`). When pressed near a mine entrance (default: <20m), it triggers the refresh.
- **Proximity Safety Check**: Scans all active characters in the world using our high-performance
  local `Plugin.ActiveCharacters` list. If any player or worker (excluding the player triggering the
  refresh) is within `SafetyRadius` (default: 10m) of *any* hallway/node in the target mine, the
  refresh is blocked, and their name is displayed in-game to prevent trapping them.
- **Authority Gate (v1.3.3)**: The refresh uses a 4-step authority check to handle all session
  types safely: (1) if `[General] ForceAllowRefresh` is enabled, skip the authority gate entirely
  (logs a warning about potential co-op sync issues); (2) if NetworkObject or Runner is null, treat
  as offline/solo and allow; (3) allow if `Runner.IsServer || Runner.IsSharedModeMasterClient ||
  Runner.IsSinglePlayer`; (4) otherwise block with full runner diagnostics and a pointer to the
  `ForceAllowRefresh` config escape hatch. This replaces the v1.3.2 simple IsServer check, which
  wrongly blocked offline sessions and missed IsSinglePlayer.
- **Recursive Cave Traversal**: Recursively traverses the mine tree starting from the `CaveEntrance`
  (which inherits from `CaveNode`) using the `connections` list on
  `SandSailorStudio.Procedural.LSystemNode`.
- **Global & Local DigVolume Discovery**: 
  - Searches recursively under the global `CavesManager.cavesRoot` (where the game instantiates all
    cave instances) and the entrance's parent transform for persistent `DigVolume` components using
    a custom recursive child traversal.
  - Combines discoveries into a `HashSet` to ensure all active and streamed-in `DigVolume`
    components are found. Because `DigVolume` components are the persistent managers for each
    hallway section (and are not destroyed when walls are mined out), they are guaranteed to be
    found.
- **DigVolume Association & Filtering**: 
  - Filters matching `DigVolume`s by checking `volume._entrance == closestEntrance` or if `volume._node` belongs to the cave's logical nodes list.
- **Native Wall Regeneration via DigVolume**:
  - Accesses and resets the `DigData` associated with the volume (clears crack damage lists, sets
    left/right wall indices to 0, flags dirty state for network synchronization).
  - Triggers the game's native wall reset and refresh on the volume by calling
    `volume.ResetWalls(true)` and `volume.ForceUpdateCaveWallStateAndRefreshWalls()`. This mimics
    the game's native cave-in refresh pathway perfectly, reconstructing the physical hallway walls
    instantly without requiring the player to deal with rubble or collapses.
- **Rubble & Cave-In Clearing**: If any hallways were collapsed, clears the rubble and reopens them
  by setting `node.open = true`, `node._isCollapsed = false` (writing directly to the backing
  field), and calling `node.UpdateCollapsedState()`.
- **Loose Item Spawning**: If enabled, runs all `CaveItemSpawner`s on the nodes (`spawner.Run()`) to regenerate chests, iron deposits, and mushrooms.
- **Dropshadow HUD Overlay**: Draws beautiful, high-visibility yellow on-screen notification text
  with a black drop shadow at the top of the screen using a self-contained Unity `OnGUI` method on
  the tracker.

**Key IL2CPP Interop Learnings:**
- **Zero-Search Lifecycle-Patched Architecture**: 
  - In this Unity 6 build, **all** Unity scene-scanning queries (e.g. `FindObjectOfType`,
    `FindObjectsOfType`, `FindAnyObjectByType`, `FindObjectsByType`) are highly prone to throwing
    `System.MissingMethodException` at runtime due to missing linked bindings in the game's native
    binary.
  - **Fix**: Implement a 100% passive, zero-search architecture. Write Harmony patches on
    `CavesManager.Start()` (with scene-validity checks to ignore prefab assets) to cache the manager
    instance, and on `Character.Spawned()` / `Character.Despawned()` to maintain a local,
    thread-safe C# list of all active characters in the world. Read directly from these cached
    references to bypass all Unity scene-scanning APIs entirely.
- **Custom MonoBehaviour Constructor**: Custom `MonoBehaviour` classes registered via
  `ClassInjector.RegisterTypeInIl2Cpp<T>()` in modern BepInEx 6 IL2CPP do **not** require an
  `IntPtr` constructor. Writing one throws a compiler error (`does not contain a constructor that
  takes 1 arguments`).
- **Standard C# Casting**: Standard C# casting (`node as CaveNode`, `obj as Character`) works
  perfectly for mirrored classes in the interop assembly. Avoid calling `.TryCast<T>()` on
  `UnityEngine.Object`-derived types as it throws compile-time errors.
- **Missing Interface Inheritance**: `Il2CppSystem.Collections.Generic.IReadOnlyList<T>` lacks
  metadata showing it inherits from `IReadOnlyCollection<T>` in the interop assembly. As a result,
  `.Count` and `GetEnumerator` are unavailable. **Fix**: Cast the collection to
  `Il2CppSystem.Collections.Generic.IReadOnlyCollection<T>` using `.TryCast<T>()` to safely retrieve
  `.Count`, and access elements by index `list[i]`.
- **PowerShell Backtick Gotcha**: When using Mono.Cecil in PowerShell scripts, generic types like
  `NetworkInteractable`1` contain a backtick, which is PowerShell's escape character. Always use
  single quotes or escape the backtick as `` to prevent the shell from stripping it.

**Config Options (`com.askamods.minerefresh.cfg`):**
- `General/TriggerHotkey` (string, default: `"u"`): The key to trigger the refresh.
- `General/SafetyRadius` (float, default: `10.0`): Safe clearance distance from all mine nodes.
- `General/TriggerOnlyNearEntrance` (bool, default: `true`): Restrict trigger to mine entrances.
- `General/MaxEntranceDistance` (float, default: `20.0`): Maximum distance from entrance allowed.
- `General/RespawnItems` (bool, default: `true`): Respawn chests, loose ore, and mushrooms.
- `General/ForceAllowRefresh` (bool, default: `false`): If true, skip the authority gate
  entirely (refreshes even on non-host clients in co-op). Logs a warning about potential
  sync issues — use this only if the host gate is blocking you incorrectly.

**Nexus Reporter Status**
- **pdp2010** (self-hosted co-op, v1.3.2): Got "Only the host/server can refresh the mine!"
  despite being the host, blocking refresh entirely. Stated he IS the host; dedicated-server
  hypothesis ruled out (2026-07-12). v1.3.3 reworked the authority gate to evaluate 4 steps
  instead of conflating conditions — should fix this. Remaining hypotheses for his v1.3.2 block:
  null NetworkObject/Runner on his host (now auto-allowed in v1.3.3 step 2), or his runner lacks
  both IsServer and IsSharedModeMasterClient flags (possibly invite flow makes host join as client
  with IsSceneMaster). v1.3.3's step 4 logs full runner diagnostics to reveal his actual state;
  awaiting feedback. Workaround: set `[General] ForceAllowRefresh = true`.
