# Mod 11: MineRefreshMod — COMPLETE (v1.3.4)

**Goal:** Safely and fully refresh/regenerate a mine, its sub-hallways, resource nodes, and item/chest spawners on-demand via a configurable hotkey.

**v1.3.5 — patches applied individually instead of through `PatchAll()` (confirmed working
in-game 2026-07-30; does NOT resolve Makeway's crash)**
`Plugin.Load` calls a guarded `ApplyPatch` helper once per target and never calls
`harmony.PatchAll()`. Each target's game type is resolved through a lambda, which keeps the
`typeof` inside a try/catch. The `[HarmonyPatch(typeof(...))]` attributes are gone from
`Patches/LifecyclePatches.cs` and `Patches/PlayerCharacterPatch.cs`, so this assembly's attribute
metadata carries no game-type tokens at all. `PatchAll()` makes the CLR materialize every custom
attribute on every type in the assembly, and a game type that fails to resolve during that sweep
kills the process with `Fatal error. Internal CLR error. (0x80131506)` and no catchable exception.
Each target now logs `Patched <label>` on success, or a warning naming the target it skipped, so a
resolution failure disables one hook and leaves the mod loaded. The five targets are
`CavesManager.Start`, `Character.Spawned`, `Character.Despawned`, `PlayerCharacter.Spawned` and
`PlayerCharacter.Despawned`.

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
- **goblinhood88** (2026-07-07, v1.3.1 — the version live 2026-06-29 → 07-10): "GAME WONT LOAD."
  The game fails to start whenever the mod is installed. Asked for the `.dll.off` isolation test
  and `LogOutput.log`; never responded, bug closed. ⚠️ PENDING — no cause identified.
- **Makeway** (v1.3.4 and v1.3.5, logs supplied 2026-07-30): the game closes during plugin load,
  no window ever appears, and a fresh `Aska.exe` dump lands in `%LOCALAPPDATA%\CrashDumps`. Normal
  Steam branch, not beta. His v1.3.4 isolation test: the mod alone works, and adding
  `askaplus.bepinex.mod.dll` (Aska Plus 0.5.2) alone is enough to crash it.
  ⚠️ NOT YET ROOT-CAUSED — the failing hook is named, the enforcing condition is not.

**The failing hook is `PlayerCharacter.Spawned` (confirmed from his v1.3.5 log, 2026-07-30).**
v1.3.5 attaches its five hooks one at a time and logs each success, so his log names the stopping
point directly. It ends at `[MineRefreshMod] Patched Character.Despawned`, having already logged
`Patched CavesManager.Start` and `Patched Character.Spawned`. The fourth attach,
`PlayerCharacter.Spawned`, wrote no success line, no skip warning and no error before the process
died, and the fifth was never reached. `CavesManager` and `Character` therefore both resolve and
patch cleanly on his build; `PlayerCharacter` is what kills the process. His description of the
symptom change: "it continues loading for longer before shutting down."

**The v1.3.4 crash signature, for comparison.** His v1.3.4 `ErrorLog.log` opens
`Fatal error. Internal CLR error. (0x80131506)` with the stack `MineRefreshMod.Plugin.Load` →
`Harmony.PatchAll()` → `HarmonyLib.PatchClassProcessor..ctor` →
`HarmonyMethodExtensions.GetFromType(System.Type)` → `RuntimeType.GetCustomAttributes(Boolean)` →
`CustomAttribute._CreateCaObject`, and his v1.3.4 `LogOutput.log` ends at
`Registered mono type MineRefreshMod.MineRefreshTracker in il2cpp domain`, the line immediately
before `harmony.PatchAll()`. That frame is the CLR materializing an attribute object out of
metadata. Two facts follow from v1.3.5 still dying. Attribute materialization is the site of the
v1.3.4 crash but not the whole cause, since v1.3.5 carries no game-type tokens in attribute
metadata at all. The per-attach try/catch does not intercept the v1.3.5 death either, so whatever
fails inside that fourth attach is not a managed exception. `ErrorLog.log` for the v1.3.5 run has
been requested and is the next piece of evidence owed.

**The Aska Plus pairing does not reproduce (2026-07-30).** Aska Plus 0.5.2 was installed beside
MineRefreshMod 1.3.4 on the dev desktop and the game started normally, logging
`MineRefreshMod v1.3.4 loaded successfully`. Three candidate differences are ruled out by that
run. Both runs load Aska Plus first and inject its four mono types (`GrassTool`,
`AskaPlusSpawner`, `VillagerBonusSpawn`, `PlayerBonusSpawn`) ahead of
`MineRefreshMod.MineRefreshTracker`, so injection order is not it. The Il2CppInterop warning
`Class::Init signatures have been exhausted, using a substitute!` sits at line 12 of BOTH logs,
so the substitute-injection path is not it. `BepInEx 6.0.0-be.755`, `Unity 6000.3.12f1` and
`.NET 6.0.7` are identical across the two machines, so the loader stack is not it.

**Both machines run identical game code — game-build skew is ruled out (2026-07-30).** The two
logs show different dates in BepInEx's preloader header: the dev desktop reads
`BepInEx 6.0.0-be.755 - Aska (6/15/2026 8:53:18 PM)` and Makeway's reads
`BepInEx 6.0.0-be.755 - Aska (27-04-2026 14:51:25)`. **That date is the file's last-write time,
which is when Steam wrote it to disk, NOT the game's build date.** The desktop's `Aska.exe`
LastWriteTime is `6/15/2026 8:53:18 PM`, matching its header exactly, while the PE link timestamp
inside `GameAssembly.dll` on the same install is `2026-04-23 15:42:10` UTC. Steam's news API
(`api.steampowered.com/ISteamNews/GetNewsForApp/v2/?appid=1898300`) lists `April 23rd Hotfix`,
dated April 23 2026, as the newest entry with nothing after it. So a June 15 install delivered the
April 23 build, and Makeway's April 27 install postdates that same hotfix by four days. Both
machines therefore run the April 23 build, their interop assemblies are generated from the same
`GameAssembly.dll`, and `PlayerCharacter` exists identically on both. Any explanation resting on a
type that moved between game builds is dead.

**Exposure beyond this mod.** Nine other mods in this repo hook `PlayerCharacter.Spawned` and
`PlayerCharacter.Despawned` exactly the same way: CraftFromStorageMod, DenRespawnMod,
GroundItemVacuumMod, HealthRegenMod, NoNeedsMod, OuthouseComposterMod, TreeRespawnMod,
VillagerAmmoMod and WarpTourMod. Makeway has none of them installed, so his logs say nothing about
whether they crash on his build. ⚠️ UNTESTED — do not change any of them until his v1.3.5
`ErrorLog.log` names the actual failure.

**Load-failure reports — what the version range can rule out.** Both load-failure reports name
this mod (not a different one), three weeks apart, with different reporters. Between v1.3.1 and
v1.3.4 the load path is git-verified unchanged apart from two lines: `Patches/` is byte-identical
(all four Harmony targets), and `Plugin.cs` differs only by an added `ForceAllowRefresh`
`Config.Bind` plus the `SafetyRadius` default 25.0f → 10.0f.
`ClassInjector.RegisterTypeInIl2Cpp<MineRefreshTracker>()`, the `DontDestroyOnLoad` GameObject +
`AddComponent`, and `harmony.PatchAll()` resolving `CavesManager.Start`, `Character.Spawned`,
`Character.Despawned` and `PlayerCharacter.Spawned/Despawned` are identical across that range.
So the `Loading [MineRefreshMod x.y.z]` line is the first thing to read in any log a reporter
sends: **later than 1.3.1** means the typing guard, authority-gate rework and SafetyRadius change
are all excluded and the unchanged load path is the remaining surface; **also 1.3.1** excludes
nothing. `ErrorLog.log` matters as much as `LogOutput.log` here — an unhandled chainloader abort
takes down every plugin and leaves `LogOutput.log` ending clean at `Chainloader initialized`, with
the real exception only in `ErrorLog.log` (see architecture.md → Startup chainloader abort).
