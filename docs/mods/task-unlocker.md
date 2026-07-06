# TaskUnlockerMod (Mod 17) — unlock all cooking + fishing villager tasks at world load

**Status: COMPLETE v1.2.1 — core approach confirmed in-game 2026-07-06 (as v1.2.0)**

Makes every crockpot recipe and every fish assignable to villagers from world load, without
manually discovering recipes or rowing out to mark fishing grounds.

## The two systems are gated completely differently

| | Cooking (CrockpotRecipeInfo) | Fishing |
|---|---|---|
| Gate | Blueprint discoverable system (`BlueprintConditionsDatabase`) | **Marked fishing grounds** — NOT item discovery at all |
| Unlock call | `NetworkBlueprintConditionsDatabase.Rpc_AddDiscoverable(ItemInfo.id)` | `NetworkWorldDataManager.RequestDiscoverFishingGround(id)` + `RequestMarkFishinGround(id, true)` |
| Result | Recipe appears in cooking workstation task list | Game itself creates the fish task (`_OnFishingGroundMarkChanged` → `_UpdateFishStatus`), buoy marked on map |

Full causal chain, evidence, and the six dead-ends that preceded this recipe:
`docs/architecture.md` → "Task Discovery & Bypassing (Fishing/Cooking)".

## Shipped recipe (TaskUnlockTracker, polling MonoBehaviour)

- World gate: `FindAnyObjectByType<BlueprintConditionsDatabase>()` non-null == world loaded;
  null resets per-world state.
- **Cooking pass** (once per world, `NetworkLogic.HasStateAuthority` only): iterate
  `ItemInfoDatabase.CompleteItemInfoList.itemInfoList` (captured via Harmony postfix on
  `ItemInfoDatabase.Initialize`), identify crockpot recipes by **native class name**
  (`il2cpp_object_get_class` — managed casts lie for list elements), rewrap
  `new CrockpotRecipeInfo(ptr)`, and for each with `requiresDiscovery == true` call
  `netDb.Rpc_AddDiscoverable(info.id)`.
- **Fishing pass** (every 5 s): iterate `PopulationManager._fishingGrounds`; for each ground with
  `!IsMarked`, call `ground.NetworkCommunicator.RequestDiscoverFishingGround(ground._id)` (if
  `!Discovered`) then `RequestMarkFishinGround(ground._id, true)` — the game's own client-safe
  request path ("FishinGround" typo is the game's). Max 3 attempts per ground, then a one-time
  warning. `UnlockMarking()` was not needed.
- The periodic re-pass also catches grounds that register late (streaming).

## Config (`com.askamods.taskunlocker.cfg`)

| Key | Default | Meaning |
|---|---|---|
| `UnlockCookingRecipes` | `true` | Unlock all crockpot recipes at world load |
| `UnlockFish` | `true` | Discover + mark all fishing grounds at world load |
| `DiagnosticsLogItemUnlocks` | `false` (since v1.2.1) | Per-recipe / per-ground log lines |

## Gotchas hit while building it (all now in the universal list / architecture doc)

- `FindAnyObjectByType<FishableItemsConfig>()` silently returns null (ScriptableObject asset,
  not a scene object) — use `FishingStation.fishables` if the fish list is ever needed.
- `FishingStation.CheckUndiscoveredTasks(Int32&, String&)` and `WorkstationTaskData.ShouldBeHidden`
  are by-ref/trampoline crash territory — never patch them.
- `FishingGround.Discovered` is NOT the task gate (`IsMarked` is), and a getter postfix on it
  likely never fires for native callers (AOT inlining).

## History

- v1.0.x–v1.1.6 (Antigravity, 2026-07-05): cooking worked; six fishing dead-ends from attacking
  the discovery system instead of the marking system, plus a silently-null config lookup that made
  every fishing test a no-op. Diagnosed 2026-07-06.
- v1.2.0: mark-based rewrite — confirmed in-game 2026-07-06.
- v1.2.1: diagnostics default flipped to `false` for shipping (no behavior change).
