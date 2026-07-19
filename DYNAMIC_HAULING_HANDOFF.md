# Mod 6 — Warehouse Hauling Filter — Handoff (SHELVED — vanilla covers it; design ready if revived)

**Status as of 2026-07-19:** Reverse-engineering done, fix designed, **no code written — and the
core use case turned out to be covered in vanilla:** every building with storage has its own
villager access whitelist (user-confirmed from gameplay 2026-07-19), which is the game's intended
control for warehouse-worker resource theft. That whitelist path was never reverse-engineered here.
The `CanCreateStorageTaskForItemInfo` gate below remains valid and was adopted (item-scoped) by
OuthouseComposterMod v1.1.0. If reviving this mod, pick it up fresh. Tentative mod name: **`WarehouseFilterMod`** (GUID `com.askamods.warehousefilter`) — rename if you
prefer. The reusable architecture facts are already in `CLAUDE.md` under "Settlement Hauling / Storage /
Task-Dispatcher System" and "Structures, Workstations & the AI Quest/Task system" — read those first.

## The problem (user's report)
The central **warehouse worker** correctly pulls finished/gathered resources from gathering stations
(woodcutter, mining hut, etc.) into central storage. **But it also pulls the raw materials out of
crafting-station input bins** (linen, leather, fibers, …). The crafter then re-fetches them from the
warehouse back to its station → the warehouse hauls them away again → **infinite loop**, so the crafter
never makes progress and haulers waste all their time.

## Root cause (confirmed via interop dump)
- The warehouse is a **`SSSGame.ResourceStorage`** building. It generates "haul to me" tasks by walking
  every **`SSSGame.StorageSupply`** in the settlement and gating each item through
  `ResourceStorage.CanCreateStorageTaskForItemInfo(StorageSupply ss, ItemInfo info) → bool`, then building
  the task in `CreateOrUpdateTasksFromStorageSupply(StorageSupply, List, bool)`.
- Every structure registers `StorageSupply` dispatchers. **`SSSGame.CraftingStation` registers supplies for
  its INPUT material bins** (`stationInventory` / `_storageSupplies`) as well as its output. The warehouse
  treats the input supplies as haulable surplus → the loop.
- A `StorageSupply` knows its owner: `StorageSupply.OwnerStructure` (inherited from
  `SSSGame.AI.StructureTaskDispatcher`). From the owner: `OwnerStructure.TryGetStructureComponent<CraftingStation>(out cs)`
  and `OwnerStructure.StructureName` / `DefaultName` for filtering.
- The crafting station's **output** is separately wrapped in `CraftingStation/OutputStorageWrapper`
  (`._cs` = the station, `._ss` = the output `StorageSupply`) — so input vs output IS distinguishable.

## The fix (planned)
**Harmony prefix on `ResourceStorage.CanCreateStorageTaskForItemInfo(StorageSupply ss, ItemInfo info)`**
(clean per-item bool chokepoint, no ref params). When the supply should be skipped, set `__result = false`
and return `false` to suppress the original — so **no haul task is ever created for that item/supply**.
This only blocks the warehouse's *outbound* haul; the crafter's own `CrafterFetchQuest` is untouched, so
the loop simply stops.

Two filter layers (build both):
1. **Smart default — protect crafting-station INPUTS, still haul OUTPUTS.** If
   `ss.OwnerStructure.TryGetStructureComponent<CraftingStation>(out cs)` is true AND `ss` is NOT the
   station's output supply → skip. Identify the output supply via `cs`'s `OutputStorageWrapper._ss`
   (need to find how to reach the wrapper from the station — see open questions). Config toggle
   `ProtectCraftingInputs` (default true). If output detection turns out unreliable, fall back to
   "skip ALL crafting-station supplies" as the default behavior and lean on layer 2.
2. **Name ignore-list (escape hatch, the user's explicit ask).** Config string
   `IgnoredStructureNames` (comma list, case-insensitive substring match on
   `ss.OwnerStructure.StructureName`/`DefaultName`). Warehouse never hauls from a matched building.

## Build diagnostic-first (same method that worked for Mod 5)
Ship a first build that, with `DebugLogging=true`, logs from the prefix **without filtering** (or filtering
behind a flag): for each call log `owner=<StructureName> isCrafting=<bool> item=<ItemInfo.Name> supply=<input/output?>`.
Run a session with a workshop that has materials, confirm:
1. The prefix actually fires for the crafter's input items (i.e. this IS the gate the loop uses).
2. How to tell input from output at runtime (does comparing `ss` to the `OutputStorageWrapper._ss` work?
   what do input vs output supplies look like?).
Then enable the real filtering.

## Open questions to resolve at runtime
- **Is `CanCreateStorageTaskForItemInfo` the only gate?** There may be a second path (e.g.
  `SSSGame.AI.SupplyPatrolQuest` + `PatrolStation` is a separate "patrol and supply" worker, and
  `StorageSupply.IsAvailable()` is another possible chokepoint). If the prefix doesn't catch the loop, try
  a prefix on `StorageSupply.IsAvailable()` / `DependendTaskStorageSupply.IsAvailable()` (return false for
  crafting inputs) or on `CreateOrUpdateTasksFromStorageSupply`.
- **Output detection:** how to get the `OutputStorageWrapper` from a `CraftingStation` (it has
  `_storageWrappers` Dictionary and `FindStorage(Predicate<StorageSupply>)`); confirm `ss == cs output _ss`
  reliably separates output from input.
- **Multiple `ResourceStorage` buildings** (several warehouses/storage tents) — the patch is on the class,
  so it covers all; confirm that's desired.
- Co-op: hauling is host-authoritative like the rest; gate writes/decisions on the host if needed (check
  whether `CanCreateStorageTaskForItemInfo` only runs host-side already).

## Config design (`[WarehouseFilter]`)
| Key | Default | Meaning |
|---|---|---|
| `Enabled` | `true` | Master switch. |
| `ProtectCraftingInputs` | `true` | Don't let the warehouse haul raw materials out of crafting-station input bins (still hauls their finished output). |
| `IgnoredStructureNames` | `""` | Comma list, case-insensitive substring on structure name; warehouse never hauls from these buildings at all. |
| `DebugLogging` | `false` | Log every gated haul decision (owner/item/supply) — turn on to diagnose/tune. |

## Build / deploy / tooling (same as the other AskaMods)
- New project `WarehouseFilterMod/WarehouseFilterMod.csproj` targeting `net6.0`, same references + the
  `CopyToPlugins` target as `DynamicVillagerNeedsMod` (copy that csproj and adjust names).
- `_explore/` Mono.Cecil scripts: `search.ps1 -TypeKeywords @(...)`, `dump.ps1 -Types @("Full.Name", ...)`.
- Key confirmed API (don't re-derive):
  - `SSSGame.ResourceStorage.CanCreateStorageTaskForItemInfo(StorageSupply, ItemInfo) → bool`
  - `SSSGame.ResourceStorage.CreateOrUpdateTasksFromStorageSupply(StorageSupply, List, bool)`
  - `SSSGame.StorageSupply` (`: SSSGame.AI.StructureTaskDispatcher`) → `.OwnerStructure`, `.IsAvailable()`
  - `SSSGame.Structure.TryGetStructureComponent<CraftingStation>(out cs)`, `.StructureName`, `.DefaultName`
  - `SSSGame.CraftingStation` → `stationInventory`, `_storageSupplies`, `_storageWrappers`, `FindStorage(...)`
  - `SSSGame.CraftingStation/OutputStorageWrapper` → `._cs`, `._ss` (output supply)
