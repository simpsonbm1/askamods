# Mod 11: MineRefreshMod — COMPLETE (v1.0.9)

**Goal:** Safely and fully refresh/regenerate a mine and all its sub-hallways and mine-able resources on-demand via a configurable hotkey.

**Game subsystem:** [Caves, Mines & Hallway Excavation](../architecture.md#caves-mines--hallway-excavation)
— the classes, data structures, and state managers governing the ASKA cave and excavation systems, along with the IL2CPP interop quirks (like missing inheritance and casting limitations) discovered and solved.

**Working approach:**
- **On-Demand Hotkey**: A persistent MonoBehaviour (`MineRefreshTracker`) polls for a configurable hotkey (default: `U`). When pressed near a mine entrance (default: <20m), it triggers the refresh.
- **Proximity Safety Check**: Scans all active characters in the world using our high-performance local `Plugin.ActiveCharacters` list. If any player or worker (excluding the player triggering the refresh) is within `SafetyRadius` (default: 25m) of *any* hallway/node in the target mine, the refresh is blocked, and their name is displayed in-game to prevent trapping them.
- **Host-Only Execution**: The refresh is gated on server authority (`Plugin.LocalPlayer.NetworkObject.Runner.IsServer == true`) to prevent multiplayer desyncs.
- **Recursive Cave Traversal**: Recursively traverses the mine tree starting from the `CaveEntrance` (which inherits from `CaveNode`) using the `connections` list on `SandSailorStudio.Procedural.LSystemNode`.
- **Wall & State Reset (`DigData`)**:
  - Accesses the persistent `DigData` for each node using `caveData.GetDigData(node.index, DataAccessMode.FETCH)`.
  - Resets the wall health and excavation progress by calling `digData.ResetCrackData()`, setting `wallIndexLeft = 0` and `wallIndexRight = 0`, and calling `digData.SetDirty()` for network replication.
  - Locates all `CaveWallInteraction` components inside the node's children and forces them to reload and update their renderers: `wall.RefreshDigData(digData)`, `wall.RefreshRendererData()`, and `wall.SetRenderersVisible(true)`.
- **Rubble & Cave-In Clearing**: If any hallways were collapsed, clears the rubble and reopens them by setting `node.open = true`, `node._isCollapsed = false` (writing directly to the backing field), and calling `node.UpdateCollapsedState()`.
- **Loose Item Spawning**: If enabled, runs all `CaveItemSpawner`s on the nodes (`spawner.Run()`) to regenerate chests, iron deposits, and mushrooms.
- **Dropshadow HUD Overlay**: Draws beautiful, high-visibility yellow on-screen notification text with a black drop shadow at the top of the screen using a self-contained Unity `OnGUI` method on the tracker.

**Key IL2CPP Interop Learnings:**
- **Zero-Search Lifecycle-Patched Architecture**: 
  - In this Unity 6 build, **all** Unity scene-scanning queries (e.g. `FindObjectOfType`, `FindObjectsOfType`, `FindAnyObjectByType`, `FindObjectsByType`) are highly prone to throwing `System.MissingMethodException` at runtime due to missing linked bindings in the game's native binary.
  - **Fix**: Implement a 100% passive, zero-search architecture. Write Harmony patches on `CavesManager.Awake()` to cache the manager instance, and on `Character.Spawned()` / `Character.Despawned()` to maintain a local, thread-safe C# list of all active characters in the world. Read directly from these cached references to bypass all Unity scene-scanning APIs entirely.
- **Custom MonoBehaviour Constructor**: Custom `MonoBehaviour` classes registered via `ClassInjector.RegisterTypeInIl2Cpp<T>()` in modern BepInEx 6 IL2CPP do **not** require an `IntPtr` constructor. Writing one throws a compiler error (`does not contain a constructor that takes 1 arguments`).
- **Standard C# Casting**: Standard C# casting (`node as CaveNode`, `obj as Character`) works perfectly for mirrored classes in the interop assembly. Avoid calling `.TryCast<T>()` on `UnityEngine.Object`-derived types as it throws compile-time errors.
- **Missing Interface Inheritance**: `Il2CppSystem.Collections.Generic.IReadOnlyList<T>` lacks metadata showing it inherits from `IReadOnlyCollection<T>` in the interop assembly. As a result, `.Count` and `GetEnumerator` are unavailable. **Fix**: Cast the collection to `Il2CppSystem.Collections.Generic.IReadOnlyCollection<T>` using `.TryCast<T>()` to safely retrieve `.Count`, and access elements by index `list[i]`.

**Config Options (`com.askamods.minerefresh.cfg`):**
- `General/TriggerHotkey` (string, default: `"u"`): The key to trigger the refresh.
- `General/SafetyRadius` (float, default: `25.0`): Safe clearance distance from all mine nodes.
- `General/TriggerOnlyNearEntrance` (bool, default: `true`): Restrict trigger to mine entrances.
- `General/MaxEntranceDistance` (float, default: `20.0`): Maximum distance from entrance allowed.
- `General/RespawnItems` (bool, default: `true`): Respawn chests, loose ore, and mushrooms.
