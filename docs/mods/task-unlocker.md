# TaskUnlockerMod (Mod 17) — unlock cooking recipes, fishing grounds + item-journal tasks

**Status: COMPLETE v1.4.1, on Nexus ("Task and Journal Unlocker", renamed 2026-07-14, group ID 7623785).**
Core journal unlock + perf rework confirmed in-game 2026-07-14 (as v1.3.0/v1.4.0); v1.4.1 =
ship defaults only.

Unlocks all crockpot recipes, marks all fishing grounds, and discovers all item-gated building
tasks (tavern, harbor, storage, workshops) at world load.

## The three gates are different systems

| System | Gate | Unlock Call | Result |
|---|---|---|---|
| Cooking (CrockpotRecipeInfo) | Blueprint discoverable (BlueprintConditionsDatabase) | `NetworkBlueprintConditionsDatabase.Rpc_AddDiscoverable(ItemInfo.id)` | Recipe appears in cooking workstation task list |
| Item Journal (all other discoverables) | Same: Blueprint discoverable system | `NetworkBlueprintConditionsDatabase.Rpc_AddDiscoverable(ItemInfo.id)` | Journal entries appear + task gates lift at buildings with CheckTaskDiscovery |
| Fishing | Marked fishing grounds (NOT discovery) | `NetworkWorldDataManager.RequestDiscoverFishingGround(id)` + `RequestMarkFishinGround(id, true)` | Game creates fish task; buoy marked on map |

**Discoverable item families** (implement IDiscoverableItem: requiresDiscovery / IsDiscovered(db)
/ Discover(db)): CrockpotRecipeInfo; ResourceInfo (+ derived BiomeResourceInfo, ConsumableInfo,
PopulationInfo, VegetationResourceInfo); WearableItemInfo; PlantableItemInfo; WeaponizedItemInfo
(+ derived AmmoItemInfo, RangedWeaponInfo, FishingItemInfo, TootItemInfo). Cecil-confirmed
2026-07-14.

## Shipped recipe (TaskUnlockTracker, polling MonoBehaviour, 1 Hz on unscaled time)

- **World gate:** persistent SandSailorStudio.Storage.StorageManager cached once; poll
  ActiveSessionID — empty = world left → drop ALL per-world state incl. cached
  BlueprintConditionsDatabase/PopulationManager wrappers (stale-wrapper gotcha). TreeRespawn
  pattern. Replaced v1.2.x's per-second FindAnyObjectByType world gate.
- **Discoverables scan** (once per world, host = NetworkLogic.HasStateAuthority only): waits for
  save-data load gate — blueprintDb.GetRegisteredCharacters().Count > 0 — because the DB, its
  NetworkLogic and ItemInfoDatabase all exist during the load screen BEFORE the DB's storage pack
  deserializes, and IsDiscovered reads false for everything there (confirmed in-game 2026-07-14;
  v1.3.x scanned early and re-sent all 429 discoverables every load). Characters register on the
  DB only once the world runs (vanilla's own pickup-discovery signal).
- Scan classifies each ItemInfoDatabase.CompleteItemInfoList entry by walking the native class
  chain (IL2CPP.il2cpp_object_get_class → il2cpp_class_get_parent, name match against the 5
  family bases — managed casts lie for list elements), rewraps as the matched base via new T(ptr),
  checks requiresDiscovery and IsDiscovered(db), and queues (id, discover) pairs per the category
  toggles.
- **Drain:** ≤64 sends per 1 Hz pass (spreads the burst; ~430 items ≈ 7 s). Unlock =
  Rpc_AddDiscoverable(id); reset (repair switch, see config) = AddDiscoverable(id, false) ⚠️
  UNTESTED path.
- **Capacity guard:** NetworkBlueprintConditionsDatabase._discoverablesMaxSize = 768, 1 bit per
  discoverable (Cpp2IL diffable-cs 2026-07-14). Pessimistic per-pass guard vs
  _discoverableIDs.Count; in practice registration is init-time (519 on the test save) and adds
  are status-bit writes that do NOT append (confirmed in-game 2026-07-14 — count stable across
  7×64 adds), so the guard is insurance only.
- **Fishing pass** (5 s): per-world handled-HashSet (marked once, or 3 failed attempts + warn-once)
  + idle gate — once every known ground is handled, the pass is a single grounds.Count read until
  the registry grows (streaming registers grounds late: 0→101 observed). Confirmed in-game
  2026-07-14: 101 grounds, ~1 ms confirm pass, then idle. Player-unmarked buoys are never
  re-marked (deliberate: don't fight the player).
- **Discovery status persists only when the game saves;** unlocks from an unsaved session simply
  re-apply next load (self-healing).

## Config (com.askamods.taskunlocker.cfg)

| Key | Default | Meaning |
|---|---|---|
| UnlockCookingRecipes | true | all crockpot recipes |
| UnlockFish | true | discover + mark all fishing grounds |
| UnlockResourceJournalEntries | true | resources/food/consumables → tavern, harbor, storage tasks |
| UnlockSeedJournalEntries | true | seeds/saplings → farming tasks |
| UnlockWearableJournalEntries | false | armor/clothing — DEFAULT OFF: reveals workshop tasks for higher-tier gear whose station attachments aren't built |
| UnlockWeaponJournalEntries | false | weapons/tools/ammo/fishing gear — same workshop caveat, DEFAULT OFF |
| ResetJournalForDisabledCategories | false | ⚠️ UNTESTED repair switch: every load while true, re-hides journal entries of ALL items in disabled categories (incl. legitimately discovered ones; they return on next pickup); big warning in its description; use once then turn off |
| DiagnosticsLogItemUnlocks | false | per-item queue/mark log lines |
| DiagnosticsLogPassTimings | false (since v1.4.1) | per-pass duration + work-count lines |

## Gotchas / dead-ends

- **Scan before storage-pack deserialize reads IsDiscovered=false for everything** (see load gate
  above). GetStage() is a per-client constant (SETUP/INIT/START/END/PLAYER), not a live loaded
  signal.
- **Interop wrappers do NOT declare IDiscoverableItem in metadata** (Cecil: empty interface lists;
  only explicit-interface-named properties exist) — GetDiscoveryStatus/SetDiscoveryStatus(
  IDiscoverableItem,...) are uncallable from a mod; use the per-type IsDiscovered(db)
  /Rpc_AddDiscoverable(id) instead.
- ⚠️ **ResetJournalForDisabledCategories (AddDiscoverable(id,false)) has never been exercised
  in-game** (user declined 2026-07-14 — test save had all gear legitimately unlocked). If a user
  reports it dead, the fallback research lead is SetDiscoveryStatus via interface boxing.
- **FishableItemsConfig lookup always returns null** (ScriptableObject asset, not a scene
  object) — use FishingStation.fishables if the fish list is ever needed.
- **FishingStation.CheckUndiscoveredTasks(Int32&, String&) and WorkstationTaskData.ShouldBeHidden
  are by-ref/trampoline crash territory** — never patch them.
- **FishingGround.Discovered is NOT the task gate (IsMarked is),** and a getter postfix on it
  likely never fires for native callers (AOT inlining).

## History

- v1.0.x–v1.1.6 (Antigravity, 2026-07-05): cooking worked; six fishing dead-ends from attacking
  the discovery system instead of the marking system, plus a silently-null config lookup that made
  every fishing test a no-op. Diagnosed 2026-07-06.
- v1.2.0 (2026-07-06): mark-based fishing rewrite — confirmed in-game.
- v1.2.1/v1.2.2 (2026-07-06): diagnostics default + 1 Hz gate.
- v1.3.0 (2026-07-14): all-item journal unlock + steady-state perf rework (session-id world gate,
  cached managers, fishing idle gate, 64/pass drain, 768 capacity guard), confirmed in-game.
- v1.3.1: fishing idle-marker log fix.
- v1.4.0 (2026-07-14): per-category toggles (gear families default off — workshop tier-gating),
  save-data load gate (fixes early-scan re-sends), reset repair switch, confirmed in-game.
- v1.4.1: ship defaults (DiagnosticsLogPassTimings→false) + reset warning text.
