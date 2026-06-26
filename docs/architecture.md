# ASKA Game Architecture & Interop Knowledge Base

The single reference for **how ASKA's internals work** and **what approaches don't work**, from
Assembly-CSharp interop inspection. Organized by subsystem; each subsystem carries its own
"Dead ends (don't retry)" list. **Before touching any subsystem in a new mod, read its section here
first** — most failed approaches were game/interop facts, not mod-specific quirks, so they will bite
again if a new mod re-treads them.

The universal interop gotchas that bite *every* mod are pinned in `CLAUDE.md` (always loaded); their
full detail is under [IL2CPP interop gotchas](#il2cpp-interop-gotchas-universal) below.

---

## Namespaces
- `SSSGame.*` — ASKA's own code (Sand Sailor Studio)
- `Invector.*` / `Invector.vShooter.*` — third-party character controller framework ASKA is built on
- `SandSailorStudio.Inventory.*` — custom inventory system

---

## IL2CPP interop gotchas (universal)
These apply regardless of subsystem — they're properties of the BepInEx 6 / Il2CppInterop layer, not
of any one mod. Condensed copy lives in `CLAUDE.md`.

- **`Il2CppSystem.Action(methodRef)` event subscriptions don't work** — the constructor only accepts
  an `IntPtr` in this interop version, so you cannot subscribe a managed lambda to game `Action`
  events (`_onNewDay`, `OnFullyHarvested`, `OnBehaviorTypeChanged`, `OnTakeDamage`, etc.). **Use a
  registered `MonoBehaviour` with `Update()` polling instead** (the `DayTracker`/`RegenTracker`/
  `TorchFuelTracker`/`NeedsController` pattern). This is the single most-repeated dead end across mods.
- **`UnityEngine.Object.FindObjectsByType<T>()` (generic overload) throws `MissingMethodException`**
  through the IL2CPP-to-managed trampoline on every call (AOT generic instantiation isn't supported).
  Called from `Update()` it logs thousands of times/sec. Get instances from a Harmony patch's
  `__instance` instead of searching.
- **Never Harmony-patch an IL2CPP method that takes a by-ref primitive parameter** (`Single&`, `Int32&`,
  a `bool`/`Boolean&` out-param, etc.). Marshaling the by-ref value type through the patch trampoline
  throws a `NullReferenceException` **on every call** — logged as `During invoking native->managed
  trampoline … at (il2cpp -> managed) Method(IntPtr, Single&, Byte, …)` — and since the original body
  never runs, whatever calls that method breaks hard (Mod 7 v1.0.12 patched
  `QuestRunner.FindNextBestQuest(Single& topPriority, …)` and every villager went totally inert, thousands
  of NREs/frame). The exception is **outside** your `try/catch` (it's in the glue), so you won't catch it.
  Patch a sibling method with a safe signature instead — e.g. the void/no-arg `Update()` — and call the
  game's own `Void M(Quest)`-style methods to do the work.
- **`.TryCast<T>()` is NOT available on `UnityEngine.Object`** — its wrapper's base chain is just
  `System.Object`, not `Il2CppObjectBase` (unlike normal game types e.g.
  `WorldItemInstance : Il2CppSystem.Object : Il2CppObjectBase`). Avoid by obtaining an
  already-correctly-typed instance from a patch parameter rather than from a `UnityEngine.Object[]`.
- **`UniqueId`-style indices are not globally unique** — e.g. `BiomeItemInstance.UniqueId` is a
  per-buffer index that restarts at 0 in every spatial chunk. Key dictionaries by **world position**
  (`GetPosition()` rounded to 0.1m, `x:y:z`) — positions are genuinely unique and stable across saves.
- **Gate all state writes on authority** — `HasAuthority` (Character/PlayerCharacter) or `_hasAuthority`
  (CharacterSurvival/VillagerSurvival). True only for the avatar/instance this client owns; other
  players' objects are visible locally with authority `false`. Writing without the gate desyncs co-op.
- **Prefer the game's own RPCs over direct networked-state writes.** Whether a direct field/inventory
  write on a networked object replicates in co-op is unverified; the game's RPCs (e.g.
  `Rpc_AddFuel`, `Rpc_ChangeSchedule`) are the network-safe path used by the game itself.

---

## Damage Pipeline (Projectiles / Bow)
```
vShooterWeapon.Shot()
  → vBowControl.OnInstantiateProjectile()
  → SSSGame.Combat.Projectile.Shoot(powerLevel, origin, shootRay, aimRay,
                                     speed, drag, gravity, damageMask,
                                     DamageData damageData, Action onDamageDealt)
       └─ DamageData
            .weapon          → WeaponizedItem  (the bow item)
            .baseDamage      → float  ← what we multiply
            .damageMultiplier → float
            .attributeBonus  → float
            .result          → float (computed final)
```

**Best patch point for bow damage:** the **actual HP-reduction call** is
`SSSGame.Creature.TakeDamage(DamageData damage)` (Prefix). `DamageData` is passed by reference and can
be modified before damage is applied. (`Creature.TakeDamage` only fires for monsters — see
[Player Character vs. Creature](#player-character-vs-creature).)

In ranged `DamageData`, `.weapon` = the **arrow** (e.g. `"Wood Arrow"`), **not the bow** — match on
arrow name via `damage.weapon.info.Name`.

Other useful classes:
- `Invector.vShooter.vProjectileControl` — `minDamage` (int), `maxDamage` (int), `damageByDistance` (bool), `DropOffStart`/`DropOffEnd` (float)
- `Invector.vShooter.vShooterWeapon` — `chargeDamageMultiplier` (float), `chargeVelocityMultiplier` (float)
- `Invector.vDamage` — `damageValue` (int), `reducedDamage` (bool)

### Dead ends (don't retry)
- `SSSGame.Combat.Projectile.Shoot()` — IL2CPP trampoline crash (mixed ref value-type + nullable object params). Don't patch it; patch `Creature.TakeDamage` instead.
- `RangedManager.OnProjectileDamage()` — post-damage *notification*; modifying `result` here only changes the UI damage numbers, not actual HP.
- `Creature.TakeDamage` / `Character.TakeDamage` via the **Invector** path — never fired. The live path is `SSSGame.Creature.TakeDamage`.
- `vBowControl.OnInstantiateProjectile`, `vProjectileControl.Start()` — ASKA doesn't use these Invector hooks.

---

## Player Character vs. Creature
`SSSGame.Character` (base `Fusion.NetworkBehaviour`) and `SSSGame.Creature` (also base
`Fusion.NetworkBehaviour`) are **independent class hierarchies**, not subtype/supertype.
`PlayerCharacter : Character` is used for player avatars; `Creature` is used for monsters/NPCs. Both
implement the same `IDamageReceiver`-style contract (`TakeDamage(DamageData)`,
`CurrentHealth`/`MaxHealth`, `GetDamageTakenHistory()`, `IsPlayer()`, `IsAlive()`) but as separate,
non-overlapping implementations.

**Consequence:** a Harmony patch on `Creature.TakeDamage`/`Creature.X` only ever fires for monsters —
it never sees player damage. To affect the player, patch `SSSGame.PlayerCharacter`. `PlayerCharacter`
overrides `TakeDamage`/`Spawned`/`Despawned`/`IsPlayer` itself, so a patch on the **base** `Character`
method does **not** intercept calls dispatched to the `PlayerCharacter` override — **patch the
most-derived type.**

Useful `Character`/`PlayerCharacter` members:
```
CurrentHealth / MaxHealth   (float, public get+set)
HasAuthority                (bool, get-only) — true only for the avatar this client controls;
                             other players' characters in co-op are visible locally but HasAuthority == false
GetDamageTakenHistory()     → DamageHistory
  .LastDamageTime  (float)  — updated internally on every hit; poll this instead of subscribing
                               to OnTakeDamage (Action-typed IL2CPP delegate — see interop gotchas)
IsInCombat()                — PlayerCharacter's own built-in combat-timeout check (fixed window,
                               not configurable — implement your own timer via LastDamageTime instead)
```

`PlayerManager.LocalPlayer` (property on the scene's `PlayerManager` singleton) is the game's own
"get the local player" accessor, but no static `PlayerManager.Instance` was found — a Harmony Postfix
on `PlayerCharacter.Spawned()`/`Despawned()` (gated on `HasAuthority`) is the simpler way to capture
the local avatar.

### Dead ends (don't retry)
- `SSSGame.Creature.TakeDamage`/`CurrentHealth` for **player** health — `Creature` only covers monsters/NPCs. Use `Character`/`PlayerCharacter`.

---

## Resource / Tree System

**In-game day / time system — `SSSGame.Weather.WeatherSystem` (Fusion NetworkBehaviour, singleton)**
```
WeatherSystem.Instance           ← singleton accessor
  ._onNewDay  (Action)           ← new-day callback (Action — can't subscribe; poll instead)
  .GetDaysPassed()  (Int32)      ← total days elapsed since game start
  .DayOfYear  (Int32)            ← current day within the year
  .dayLength  (Single)           ← configurable day length in seconds
  .NetworkedCurrentGameTime (Single) ← persists across saves; valid for elapsed-time math after restart
  .GetTimeDifferenceFromCurrentGameTimeInSeconds()  ← elapsed-since helper
  .IsNight (bool) / .DayNightValue (Single, ~1=midday ~0=deep night)
```
In-game hour = `dayLength / 24`.

**Respawn countdown — `SSSGame.BiomeItemAvailabilityData`** (the game's own built-in per-resource countdown; reference)
```
BiomeItemAvailabilityData
  .itemDescriptors  (List)       ← the BiomeItemDescriptors this countdown controls
  .remainingDays  (Int32)        ← days remaining before replenish
  .ReplenishOnAvailable  (bool)  ← if true, calls Replenish() when remainingDays hits 0
  ._OnNewDay()                   ← called each day; decrements remainingDays
```

**Vegetation / resource classes**
```
BiomeItemDescriptor              ← represents a vegetation/resource type in the world
  .Replenish()                   ← call this to respawn the item
  .OnReplenish  (Action)         ← event fired when replenishment happens
  ._instances  (List)            ← active BiomeItemInstance list
  .IsHarvestable  (bool) / .IsAvailable  (bool)

HarvestInteraction               ← attached to harvestable world objects (trees, rocks, etc.)
  .harvestPieces  (List)         ← harvest stages in order (trunk pieces, then stump LAST)
  ._currentPieceIdx  (int)       ← which piece is currently active (GetCurrentPieceIndex())
  ._currentVegeData  (uint)      ← bitmask of which pieces have been harvested
  ._worldInstance  (WorldItemInstance) ← link back to the world resource instance
  .OnFullyHarvested  (Action)    ← fires when ALL pieces done INCLUDING stump
  .OnPieceLootPieceHarversted    ← Action<3> fires per trunk piece
  .TakeDamage(DamageData)        ← damage entry point (no ref params — safe to patch)
  .SetHarvestBits(int) / ClearHarvestBits(int) ← directly manipulate vege data

HarvestSpawner                   ← spawns loot; has ref to HarvestInteraction (_hi)
  ._OnFullyHarvested()           ← fires when entire tree incl. stump is done
  ._OnPieceHarvested(DamageData, ResourcePieces, Vector3) ← per piece

WorldItemInstance                ← a specific instance of a resource in the world
  .Destroyed  (bool) / .Active  (bool)
BiomeItemInstance                ← .Initialize() fires per instance as the world loads;
  .Replenish()                   ← fully respawns the resource in place (confirmed working)
  .GetPosition()                 ← world position; use as the stable dictionary key
```

**Confirmed facts (HarvestInteraction / trees):**
- `HarvestInteraction._worldInstance.TryCast<BiomeItemInstance>()` gives the biome instance directly; non-biome resources (rocks etc.) return null and are skipped. `BiomeItemInstance.Replenish()` fully respawns in place — confirmed in-game.
- The **last** entry in `harvestPieces` is reliably the **stump** (`List<HarvestInteraction.HarvestPiece>`; element is a nested class with a `pieceId ResourcePieces` enum — no explicit stump flag, position-based). Detect stump stage via `GetCurrentPieceIndex() == harvestPieces.Count - 1`.
- `WeatherSystem.NetworkedCurrentGameTime` persists across saves — valid for elapsed-time calculations after a full game restart.

### Dead ends (don't retry)
- Subscribing to `HarvestInteraction.OnFullyHarvested` per-instance — IL2CPP `Action` delegate issue (see interop gotchas). Checking `BiomeItemInstance.Destroyed` at tick time is simpler and equivalent.
- `BiomeItemInstance.UniqueId` as a dictionary key — NOT globally unique (per-chunk index restarting at 0). Key by world position instead.

### Mining / stone-clump respawn — INVESTIGATED & ABANDONED (don't re-attempt)
Stone clumps (`Harvest_Stone4`) are **Fusion scene objects** baked into the streamed world/chunk data
— not `WorldItemInstance`s, not in the 364-entry Fusion prefab table, not spawned via `NetworkSpawner`,
and they invoke **no** managed `HarvestInteraction` method while alive (only a post-despawn
`TakeDamage` with everything nulled). Fusion has no runtime "respawn scene object" API, and the binary
can't be dumped for deeper RE (Unity 6 → IL2CPP **metadata v39**, unsupported by Il2CppDumper). Full
write-up + the three confirming tests: **`TreeRespawnMod/STONE_RESPAWN_HANDOFF.md`**. The only
achievable-but-unbuilt alternative is respawning the loose `Item_Stone_Raw`/`Item_Wood_*` nodes (those
ARE `InventoryItemInstance` and managed-visible). All mining code was removed from TreeRespawnMod 2026-06-18.

---

## Gather / Press-to-Collect System
```
GatherInteraction (SSSGame.GatherInteraction : SSSGame.Interaction)
  .GatherItemsCharge(IInteractionAgent, Int32 charges, ItemContainer) → Int32
                                   ← safe Postfix patch point; no ref params; fires when items actually collected
  .CheckAvailableItemCount()  → Int32   ← returns 0 when resource is fully exhausted (register respawn here)
  ._worldInstance  (WorldItemInstance)  ← same path as HarvestInteraction; TryCast<BiomeItemInstance>() works
  .GetGatherableItemInfo()    → ItemInfo ← yields the ITEM name (not the node/resource name — see below)
```

**Critical distinction:** `GetGatherableItemInfo().Name` is the **yielded item**, not the world node name.
- All names confirmed in-game against the inventory/storage UI (2026-06-17):
  - Reeds → `"Thatch"`, Berry Bush → `"Berries"`, Dwarf Spruce → `"Stick"`, Flax Bush → `"Fibers"` (PLURAL — verified live in the respawn log 2026-06-25; the config key `Fiber` still matches it via the case-insensitive `itemName.Contains(key)` substring test, so the override applies regardless)
  - `"Small Stone"` (ground pickup), `"Mussels"`, `"Feathers"` (Bird's Nest), `"Water"` (Natural Water Collector)
  - Vegetables: `"Carrot"`, `"Cabbage"`, `"Onion"`, `"Garlic"`, `"Beetroot"`
  - Mushrooms: `"Mushroom"` substring matches Gray/Grey/Yellow Mushrooms
- **Not their own GatherInteraction** (bonus drops bundled with parent gather — can't trigger respawn): `"Seeds"` (plants), `"Wild Egg"` (Bird's Nest)

---

## Item Naming
- Item names live in Unity ScriptableObject assets, not in code — read at runtime via `item.info.Name` (the `ItemInfo.Name` property)
- Known item names: **"Flimsy Shortbow"** (first bow), **"Wood Arrow"** (early arrows)
- In ranged `DamageData`, `weapon` = the **arrow**, not the bow — match on arrow name

---

## Structures, Workstations & the AI Quest/Task system (general)
- **`SSSGame.Structure`** is the base building (a `NetworkBehaviour`). Useful members: `StructureName` / `DefaultName` / `GetName()` (display name, same role as `ItemInfo.Name` — use for substring filtering), `storageSupplies` (array of the structure's `StorageSupply` dispatchers), `Settlement`, `Parent`/`Root`, `OnSpawned`/`OnStructureDeath` actions.
- **Get a typed component off a structure:** `structure.TryGetStructureComponent<T>(out T comp)` (bool) or `GetStructureComponent<T>()` / `FindStructureComponent(predicate)`. This is how you ask "is this building a `CraftingStation`/`ResourceStorage`/…".
- **Workstations** (`ResourceStorage`, `CraftingStation`, `CookingStation`, etc.) are `Workstation : NetworkBehaviour` structure-components. Each owns a set of AI **Quests** (`SSSGame.AI.*Quest`, e.g. `CrafterFetchQuest`, `SupplyPatrolQuest`, `GatherAndHarvestQuest`) exposed as `vFSMBehaviour` fields, and runs them through **FSM nodes** under `SSSGame.AI.FSM.*` (ScriptableObjects — their method bodies are NOT dumpable in IL2CPP, only signatures). Active work is tracked by `SSSGame.AI.TaskRunner`.
- **Per-villager whitelisting** exists on workstations: `IsWhitelisted(Villager)`, `Rpc_ChangeWhitelistedVillager(id, state)`, `WhitelistNewVillagers` — the game's built-in "which villagers may use this station."

---

## Settlement Hauling / Storage / Task-Dispatcher System (Mod 6 groundwork)
How resources get moved into the central **warehouse** (a `ResourceStorage` building) — the system
behind the "warehouse worker steals crafter materials" loop:
```
SSSGame.AI.TaskDispatcher (MonoBehaviour)
  → SSSGame.AI.StructureTaskDispatcher
       .OwnerStructure (Structure)   ← the building this dispatcher belongs to (key back-reference)
       .Initialize(Structure) / _LinkToStructure(Structure)
       → SSSGame.StorageSupply        ← "here are items available to be hauled from this structure"
            .IsAvailable() (bool), .interaction (StorageInteraction), .taskMaxQuota
       → SSSGame.DependendTaskStorageSupply : StorageSupply  (gather-dependent variant)

SSSGame.ResourceStorage (the warehouse/storage building; a Workstation)
  .CreateOrUpdateTasksFromStorageSupply(StorageSupply, List taskAgentsToVillagers, bool addForChildren)
                                        ← builds the "haul these items to me" tasks from a supply
  .CanCreateStorageTaskForItemInfo(StorageSupply, ItemInfo) → bool   ← clean per-item GATE (prefix here)
  .GetGatheredItemQuantity / GetGatheredItemPriority / FillResourceStorageTaskDatas
  .excludedResourcesList (ItemInfoList), onlyGatherNativeItems / _nativeItemsFilter — built-in exclusions
```
- **The warehouse pulls from every `StorageSupply` in the settlement.** A `CraftingStation` registers supplies for BOTH its **input** material bins (`stationInventory` / `_storageSupplies`) and its **output**; the output is additionally wrapped in **`CraftingStation/OutputStorageWrapper`** (`._cs`, `._ss` = the output `StorageSupply`). The warehouse hauling the *input* supplies is the theft loop (crafter re-fetches → warehouse re-hauls).
- **To filter what the warehouse hauls:** prefix `ResourceStorage.CanCreateStorageTaskForItemInfo(StorageSupply ss, ItemInfo)` and inspect `ss.OwnerStructure`: skip (`__result=false`) when the owner is a `CraftingStation` (protect its inputs) and/or its `StructureName`/`DefaultName` matches a user ignore-list. This only suppresses the warehouse's *outbound* haul tasks — the crafter's own fetch is untouched, so the loop simply stops. (See `DYNAMIC_HAULING_HANDOFF.md` for the Mod 6 plan.)

---

## Inventory, Settlement Queries & Recipes (general — storage/crafting toolkit)
Confirmed via interop dump 2026-06-21 while scoping a (cancelled) fisher-bait mod. None of this is
mod-specific — reuse it for any storage / crafting / needs idea.

**Settlement-wide access & enumeration**
```
SSSGame.SettlementManager (MonoBehaviour)
  .GetPlayerSettlement() / .GetCurrentSettlement() / .GetClosestSettlement(Vector3) → Settlement
  .settlements (List) / .worldSettlement / .OnSettlementLoaded (Action)  ← wait for load before querying
SSSGame.Settlement (MonoBehaviour)
  .QuerySettlementResources() → ItemManifest   ← ONE call = total item counts across the WHOLE village
                                                 (the clean "does the settlement have X, and how many?" query)
  .GetStructures() (List) / .FindStructures(pred) / .FindStructure(pred)        ← enumerate buildings
  .FindStructuresInRange(pos, range, result, pred) / .FindClosestStructure(pos, range, pred)
  .GetStorageSites() (IReadOnlyList) / .FindItemStorage(filter, …) → Interaction (storage holding an item)
  .FindStorageToDeposit(ItemInfo, pos, range, pred, ignoreLimits) → Interaction (where to drop an item)
  .OnAddStructure / .OnRemoveStructure / .OnActivateStructure (Action<Structure>) ← track buildings live
```
No static `Settlement.Instance` — reach it via a `SettlementManager` instance, or via any `Structure.Settlement` back-ref.

**Inventory read / write — `SandSailorStudio.Inventory.ItemCollection` / `ItemContainer`**
A workstation's own stock: `Workstation.GetInventory() → ItemCollection`; what a station is asking the village to deliver: `Workstation.GetItemsNeededFromSettlement() → List`.
```
ItemCollection (a logical inventory = a set of ItemContainers)
  .GetItemQuantity(ItemInfo) / .HasItem(ItemInfo) / .GetFirstItem(ItemInfo)
  .GetCategoryItems(ItemCategoryInfo) / .GetCategoryItemsCount(...) / .HasCategoryItems(...)   ← by category
  .AddItems(ItemInfo, count) / .RemoveItems(ItemInfo, qty)  → int actually added/removed
  .GetTotalRemainingCapacity(ItemInfo) / .FindLargestMatchingContainer(ItemInfo)
  .OnItemAdded / .OnItemRemoved (ItemEventHandler)
ItemContainer (one physical bin)  .AddItems / .RemoveItem / .HasSpace(ItemInfo, qty) / .GetItemCount(...)
```
⚠️ Whether a direct `AddItems`/`RemoveItems` on a **networked** `ResourceStorage` replicates in co-op is UNVERIFIED. TorchFuelMod deliberately used the game's own RPC (`Rpc_AddFuel`) to stay network-safe — look for an analogous item RPC before writing storage state directly. Host/solo is unaffected either way.

**`SandSailorStudio.Inventory.ItemManifest` — the counts / requirements struct**
```
.GetQuantity(ItemInfo) / .GetItems() / .Check(ItemInfo) / .GetTotalQuantity()
.Contains(ItemManifest) / .Overlaps(...) / .AddItem / .RemoveItem
static .CreateFromBlueprint(BlueprintInfo)        ← READ A RECIPE'S REQUIRED INGREDIENTS as a manifest
static .CreateFromContainer(ItemContainer) / .CreateFromFilter(ItemCollection, filter)
.Transfer(ItemContainer↔ItemCollection / collection→collection)   ← bulk move helpers
```

**Recipes / blueprints**
```
SandSailorStudio.Inventory.BlueprintInfo (base "recipe") — ingredients via ItemManifest.CreateFromBlueprint(bp)
  SSSGame.CraftBlueprintInfo : BlueprintInfo   .GetItemInfo() → produced ItemInfo, .craftVolume, .interaction (CraftInteraction)
  SSSGame.CookingRecipeInfo : BlueprintInfo  /  SSSGame.CrockpotRecipeInfo : CookingRecipeInfo
     .cookingRequirements : CookingRecipeRequirement[] { ItemInfo acceptedInfo; int count; type; ItemTableConfig tableConfig }
  Forging / Dyeing / Painting / Workshop / Structure variants also derive from Blueprint(Info)
```
`ItemInfo`s are ScriptableObjects (not constructable) — get a reference by name (`item.info.Name`) off a live item, by category via `ItemCategoryInfo`, or from a blueprint's `GetItemInfo()`.

**Fishing/bait (the trigger for the above — mod cancelled, game does it natively)**
`SSSGame.FishingStation : ResourceStorage : Workstation`; bait = `SSSGame.BaitItem : EquipmentItem` (category `VillagerFishingInteractionConfig.BaitCategInfo`); the "no bait" complaint is `FishingComplainQuest : CrafterComplainQuest`. **Villagers self-craft bait from warehouse ingredients with no mod**; the "no bait" bark actually means "no *ingredients* to make bait." Don't build a fisher-bait mod — confirmed in-game 2026-06-21 (also recorded in session memory `fisher-bait-native`).

---

## Cooking Station Pipeline

How a cook turns raw ingredients into meals, and why a cook can stand at the hut doing nothing. Structure is
confirmed from the binary; runtime facts marked **confirmed in-game (2026-06-23)** were verified live with the
`CookingStationFixMod` diagnostic — a read-only FSM/priority logger, parked at `CookingStationFixMod.dll.off`
(rename back to `.dll` to revive it if cooking ever needs re-investigating).

**Building + project state — `SSSGame.CookingStation : Workstation`**
```
CookingStation
  .cookingProjects (List<CookingProject>)      ← active cook JOBS; 0 = nothing is being cooked right now
  .CookingTargetManifest (ItemManifest)        ← what the hut is trying to cook (qty); 0 = no cook target
  .GlobalInventory / .GlobalInventoryManifest  ← raw ingredients stockpiled at the hut
  .cookingBehaviour/.stockpileBehaviour/.returnBehaviour/.complainBehaviour/.idleCrafterBehaviour/
   .firekeeperBehaviour/.overcookingBehaviour (vFSMBehaviour)   ← the per-quest FSMs
  .GetAllKnownCookingRecipes() / .IsWhitelisted(Villager) / Rpc_ChangeWhitelistedVillager(id, state)
CookingStation/CookingProject  ← one active cook job: .cooks (List), .mainQuest (CookingQuest), .taskData
CookingInteraction : StorageInteraction  ← the physical cooker; ._activeCooksList, ._cookingContainers,
   ._activeRecipeContainer, KnowsRecipe(...), IsCookCooking(agent), _GetOverallCookingProgress()
```

**Quests (all `WorkstationQuest : Quest`; the QuestRunner selects by priority — scale below)**
```
CookingStockpileQuest          ← gather RAW ingredients into the hut stockpile (the "Fetch Supplies" job)
  CookingStockpileQuestData : WorkstationQuestData
    .NeedsMoreSpecificSupplies / .NeedsMoreFillerSupplies / .NeedsMoreGatheredSupplies (bool)
    .GatheredStockpileSupplies / .NeededSpecificSupplies / .NeededFillerSupplies (ItemManifest)
    ._nextRecipeInfo (CrockpotRecipeInfo)   ← null when no recipe is queued
    ._RecheckSupplies(Item)                 ← its own re-eval, fired by station inventory add/remove events
CookingQuest : CookingPlaceQuest            ← the ACTUAL cook; exists ONLY while a CookingProject does
  CookingQuestData : CookingPlaceQuestData : WorkstationQuestData
    .RecipeInfo (CrockpotRecipeInfo), .CookingStation, .LocalPromiseManifest, ._noFillers
  CookingPlaceQuestData stall latches: ._noSupplies/._noFuel/._noTool/._noStorageSpace/._badWeather + .IsWorking
CookingComplainQuest, WorkstationIdleQuest/CookingStationIdleQuestData, OvercookingQuest, CookingReturnQuest
```

**The FSM chain a WORKING cook runs (states in `SSSGame.AI.FSM`, all take `IFSMBehaviourController`):**
```
FSM_FetchCookingStockpile (stockpile raw)  →  [a CookingProject/recipe exists]  →
FSM_FetchCookingSupplies (fetch recipe-specific)  →  FSM_SetCookingPlacePosition (place each ingredient in
its oven slot — the "which ingredient goes where" step)  →  FSM_CanStartCooking.Decide (gate)  →
cook (SandSailorStudio.Inventory.CookingProcess.Run, on a timer)  →  FSM_ReturnCookingResults (deliver food)
```
- Every cooking FSM state derives from `FSM_QuestDecision`/`FSM_QuestAction`, which expose
  `GetQuestData(fsmBehaviour)` / `GetQuest` / `GetQuestRunner` — the clean way to reach the cook's quest data
  from an FSM patch (then `QuestData.TryCast<CookingQuestData/CookingStockpileQuestData>()`; TryCast works,
  QuestData is `Il2CppSystem.Object`-derived). `IFSMBehaviourController` gives `.gameObject`,
  `.NameOfCurrentState`, `.AiTargeting`.
- **Quest priority scale (confirmed in-game):** `CookingStockpileQuest` = **15** (`c_WorkstationWork`);
  `CookingQuest` ≈ **15.5–15.6** (just above stockpile, so a real cook job out-ranks gathering); `GetPriority`
  returns **-1** when the quest isn't currently eligible; idle = 0.

### Cook stands at the hut / loops sit-down-stand-up and won't make food — CONFIRMED CAUSE (2026-06-23)
**Almost always: no recipe is designated for that station** (recipe selection is buried in a nested submenu,
easy to miss). Diagnostic signature: `cookingProjects=0`, `CookingTargetManifest=0`, `_nextRecipeInfo=null`,
and **`CookingQuest.GetPriority` never fires** (no cook quest exists). With no cook target, the only runnable
job is `CookingStockpileQuest` at priority 15 (beats idle 0), so the cook walks to the hut, enters
`FSM_FetchCookingStockpile` ("Fetch Supplies"), finds nothing needed (`NeedsMore*`=false), bails, and re-fires
every ~3.5s — the visible sit/stand loop. A **full stockpile with `target=0`** (e.g. 127 raw items, nothing to
cook) is the tell. **Fix = designate recipes in-game**: the project/target then exists, `CookingQuest` appears
(~15.6, out-ranking stockpile), and the cook runs the full chain — `FSM_ReturnCookingResults` fires once per
completed dish. This is a UI-discoverability issue, **not a code or mod bug.** (A Nexus user reported the same;
"cooks mixing up which ingredients go where" is the same class — nothing designated.)

### Dead ends (don't retry)
- **`WorkstationQuest.SetDirty()` / `_RecheckSupplies()` as the cook-loop fix** — the eating-stall fix (force
  re-eval of a *parked* quest) does NOT apply: a looping cook is already re-evaluating every ~3.5s, so SetDirty
  just makes it re-loop faster. The loop is "no target," not "parked, won't re-check."
- **Suspecting DynamicVillagerNeedsMod (Mod 5) of breaking cooking** — ruled out twice: the loop reproduces with
  Mod 5 `Enabled=false`, and with Mod 5 `Enabled=true` cooks complete dishes fine (9 dishes, 0 errors,
  2026-06-23). `WorkstationQuestData._OnVillagerBehaviorChanged(ScheduleType)` means Mod 5's schedule flips DO
  notify the cook's quest, but in practice this does not interrupt cooking.

---

## Torch / Fire-Fuel System
```
FireStructure (Fusion.NetworkBehaviour)   ← the actual networked fuel state, one per fire-capable structure
  .CurrentFuelVolume / .MaxFuelVolume / .CurrentFuelRatio  (Single, public get+set)
  .fuelBurnVolumePerSecond  (Single)       ← configured burn rate; actual decrement happens via the
                                              Attribute/VariableAttribute modifier system, not a visible
                                              per-frame method on FireStructure itself
  .Rpc_AddFuel(Single fuel)                ← the network-safe RPC the game itself calls when a player
                                              manually refuels with an item (e.g. resin) — safe to call
                                              from any client/host, replicates correctly in co-op
  .Initialize(Structure ownerStructure)    ← fires once per instance as it spawns/loads; ownerStructure
                                              gives access to the structure's name for filtering
  .IsSpawned  (bool, get-only)             ← false once despawned/destroyed

RefuelableInteraction (abstract) → FireInteraction → (wrapped by LightOutlet / WarmthOutlet)
  - mirrors CurrentFuelVolume/MaxFuelVolume by forwarding to the wrapped FireStructure
  - LightOutlet and WarmthOutlet are SEPARATE AI-dispatcher components (light vs. warmth duty) but
    both wrap a FireInteraction → FireStructure — campfires/forges/kilns/cooking stations all use
    FireStructure too, so filtering by **structure name** keeps non-torch fires unaffected
LightOutlet (StructureTaskDispatcher) .fireStructure / .fireInteraction / .Initialize(Structure)
  - the light-duty dispatcher; attached to EVERY fire that exists to give off light — free-standing
    torches AND fires built into buildings (tavern campfire, braziers). Cooking stations/forges/kilns
    use CookingOutlet/WarmthOutlet and have NO LightOutlet, so patching LightOutlet.Initialize and
    grabbing __instance.fireStructure is a name-free way to catch all *lighting* fires while leaving
    crafting fires alone (TorchFuelMod's KeepAllLightSources path).
```
**Confirmed:** `Structure.DefaultName` / `Structure.StructureName` give the structure's display name (e.g. "Flimsy Torch") for substring matching, same pattern as `ItemInfo.Name`.

**Built-in / composite-building fires (e.g. tavern campfire):** these ARE `FireStructure`s and DO fire `Initialize`, but their owning `Structure` is the *building* ("Tavern"), so a name filter of `"Torch"` misses them. Two ways to catch them: add the building/fire name to the name list, or use the `LightOutlet.Initialize` patch above (catches any light-emitting fire by component, no name needed).

**⚠️ DEAD-END — cave wall sconces are NOT FireStructures.** Sticking a torch into a cave wall sconce goes through `SSSGame.CaveTorchOutlet` (a plain `MonoBehaviour`, *not* a NetworkBehaviour and *not* a FireStructure). It has **no fuel volume and no `Rpc_AddFuel`** — `IsLit()`/`IsAvailable()` are get-only, the actual light is a torch **equipment item** (`CaveTorchOutlet.torchReplacements[] : {GameObject replacementPrefab, EquipmentItemInfo torchInfo}`) held in an `EquipmentDisplaySlot` via `EquipmentManager`, that burns down by **item durability** and is *replaced* by a villager `LightkeepingQuest` (see `FSM_BuilderLightkeep`, `CaveLightingFilter`). Keeping sconces perpetually lit therefore is a *different* problem from fuel top-off — it needs blocking the equipped torch's durability decay or auto-re-equipping, not `Rpc_AddFuel`. The fuel-volume TorchFuelMod cannot touch them. (confirmed via interop dump 2026-06-25; in-game behavior pending)

**⚠️ DEAD-END — coal-burning buildings (Kiln, etc.) do NOT use the FireStructure fuel model.** TorchFuelMod's name filter cannot reach them, and there is **no structure named "Furnace"/"Smelter"** in the binary at all (searched the whole interop surface 2026-06-25 — only `KilnInteraction`, `ForgeInteraction`, `CharcoalStation` exist). The smelting building is the **Kiln** (`SSSGame.KilnInteraction : CookingInteraction : StorageInteraction : Interaction : MonoBehaviour`) — it is **not a `FireStructure` and not even a `NetworkBehaviour`**, so it never comes through the `FireStructure.Initialize` patch and has **no `CurrentFuelVolume`/`Rpc_AddFuel`**. Its fuel is a fundamentally different mechanic:
- `_fuelVAttr` (`VariableAttribute`, writable — same type as `_happinessVAttr`/survival attrs, so `SetValue()` is available) = the current fuel/heat reservoir, drained by `_fuelBurnRateAttr` (`Attribute`) × `_burnRateMultiplier` (`CustomOperationAttributeModifier`).
- Fuel arrives as **coal items** dropped into an `ItemContainer`, gated by `static Boolean _IsFuelPredicate(Item)`; `GetFuelCount()` = count of fuel items present; energy per item from `_fuelCaloricMass`/`_fuelCaloricPower`; `fuelRatioToPower` (AnimationCurve) maps fuel ratio → smelting power. (`_oreCaloricMass`/`GetOreCount`/`GetBakedOreCount` are the ore side.)

So "keep coal buildings fueled" is a **separate feature** from TorchFuelMod, with two candidate levers (both untested in-game / co-op): **(a)** pin `_fuelVAttr` to `max` each tick via `SetValue()` (the torch-equivalent "never runs dry"), or **(b)** keep the coal `ItemContainer` topped with coal items. **Co-op replication is the open risk** — there is **no `Rpc_AddFuel`-style network-safe call** on the kiln and it's a plain MonoBehaviour, so writes are guaranteed to work solo/host but co-op-client replication is unverified (same caveat class as direct inventory writes). Patch point for capture is `KilnInteraction.Initialize(Structure ownerStructure)` + an own tracker list (parallel to, not reuse of, the FireStructure path).

**Related coal buildings (for reference):** `ForgeInteraction` *does* wrap a `FireStructure fireStructure` (secondary heat — the FireStructure path could see it by name), and `CharcoalStation : Workstation` owns a `PyreStructure pyre` where `PyreStructure : FireStructure` — but the pyre's job is converting **wood→charcoal** (`woodInputContainer`→`coalOutputContainer`, `_InputFuelToCoalConversion`, `fuelToCoalConversionBaseline`), i.e. it *produces* coal rather than consuming it, so topping its fuel volume isn't "keep it supplied with coal." (confirmed via interop dump 2026-06-25; in-game behavior pending)

---

## Villager Schedule / Needs / Happiness System

**Survival components**
```
SSSGame.CharacterSurvival (base) → SSSGame.VillagerSurvival
  GetNormalizedFood() / GetNormalizedWater() / GetNormalizedWarmth()  (Single 0..1, lower = more urgent)
  IsStarving / IsDehydrated / IsFreezing / IsSuffering  (bool — the game's own critical-need flags)
  _foodVAttr / _waterVAttr / _warmthVAttr / _energyVAttr / _healthVAttr  (VariableAttribute — writable)
VillagerSurvival
  _restVariableAttribute  (VariableAttribute, range 0..24 "hours of rest"; drains awake, fills asleep)
  GetVillager() → Villager
  _hasAuthority  (bool, inherited from CharacterSurvival) — gate all writes on this (host-authoritative)
  Spawned()  — Postfix here to register each villager into a tracked list
  IsSleeping  (bool) — true while the game has the villager in bed (incl. the forced night sleep)
```

**Happiness — it's a writable VariableAttribute (key finding)**
```
Villager._happinessVAttr  (VariableAttribute, writable) ← the REAL happiness store; SetValue() sticks
Villager.NormalizedHappiness (Single 0..1, GET-ONLY) / Villager.HappinessCap (GET-ONLY) — derived from it
VillagerHappiness (via Villager.GetHappinessManager()): SleepRate/WorkRate/LeisureRate are GET-ONLY —
  you CANNOT scale the leisure happiness rate; instead write _happinessVAttr directly each tick.
```
`VariableAttribute` API: `GetValue()`, `SetValue(Single)`, `GetNormalizedValue()`, `min`, `max`. Boost a need by adding `addNormalized * (max - min)` to the value, clamped to `max`; the game re-clamps happiness to `HappinessCap` (so a housing-capped villager shows up as a plateau — happiness stops rising despite the write).

**Schedule (the thing that actually dispatches tasks)**
```
enum SSSGame.UI.ScheduleType { Sleep, Work, Leisure }   (use the enum values; never hardcode the ints)
Villager.scheduleMaxHourCount  (STATIC — access via the type, CS0176 if via an instance)
Villager._ScheduleToNetworkSchedule(Il2CppStructArray<int>) → long   (array holds ScheduleType underlying ints)
Villager.Rpc_ChangeSchedule(long packed)   (network-safe; host/authority only)
Villager.__NetworkedSchedule  (Int64) — current packed schedule; snapshot for restore-on-shutdown
Villager.overrideSchedule (bool), scheduleOverride (ScheduleType), CurrentBehaviorType (ScheduleType)
```

**Day length** — `WeatherSystem.Instance.dayLength` (Single, seconds/day) → in-game hour = `dayLength / 24`; also `IsNight` (bool), `DayNightValue` (Single, ~1=midday ~0=deep night).

**Eating / drinking — the survival-quest FSM (separate from the schedule pipeline)**
A villager's eat/drink behavior is NOT driven by the schedule a mod rewrites — it's a survival-quest
FSM that runs on top of whatever schedule mode is active. This is why eating happens during Work mode.
```
SSSGame.VillagerSurvival
  GetReplenishFoodQuest() / GetReplenishWaterQuest()  → SSSGame.AI.SurvivalObjectiveQuest
  _replenishFoodQuest / _replenishWaterQuest / _replenishHealthQuest  (same objects, as fields)
  _foodObjectiveLow / _foodObjectiveSafe  (VariableAttributeValueObjective) — the GAME's own eat thresholds
  foodItems / emergencyFoodItems / drinkItems / emergencyDrinkItems  (ItemTableConfig) — TWO-TIER food
SSSGame.AI.SurvivalObjectiveQuest
  SetDirty()                       ← re-evaluate primitive (the same call the AI's FSM_QuestSetDirty state uses)
  HasItemsToConsume(SatisfyObjectiveQuestData) → bool   ← the "is there food" check
  consumableItems / emergencyConsumableItems  (ItemTableConfig)
  priorityHigh / priorityLow ; highPriorityObjective / lowPriorityObjective
  autoRetryTimeHighPriority / autoRetryTimeLowPriority  (float) — vanilla's OWN retry timers (too slow/unreliable)
  _TryNotifySurvivalObjectivesChanged() / RefreshComplaints()   ← alternatives to SetDirty if it's ever insufficient
SSSGame.AI.FSM.FSM_SurvivalConsume       ← the FSM state that walks to the store and consumes
  ConsumeStatus { IDLE, WAIT, COMPLETE, ERROR_NO_ITEM }   ← ERROR_NO_ITEM = arrived, store empty
```
- **Two food tiers:** normal (`foodItems` — cooked meals) and emergency (`emergencyFoodItems` — raw/poisonous). A starving villager (high-priority objective) eats the emergency tier; a mildly-hungry one only normal food. Remove all cooked food and villagers fall *down* the tiers — raw-but-safe (eggs) when low, poison only when truly starving. **This is vanilla, not a mod effect** (confirmed by testing with hunger ×50 → constant emergency-tier eating).
- **The "stuck at empty store" bug:** a villager commits to a food target and stops re-evaluating across tiers; if the cooked store is empty when they arrive (`ERROR_NO_ITEM`) they park there and ignore *available raw food*, until something forces a re-evaluation (hand-feeding, or `SetDirty()`). Vanilla's `autoRetryTime*` did not recover them in practice. DynamicVillagerNeedsMod v1.1.0 fixes this by calling `SetDirty()` — see that mod's doc.

### Game-mechanic facts worth knowing before re-modeling villager behavior
- **The night-sleep race:** the game forces villagers to bed at **nightfall** regardless of their schedule, catching them at whatever rest they have then. Any mod-driven sleep control that only runs in its *own* Sleep mode will be pre-empted unless it **adopts** the game-forced sleep (detect `IsSleeping` + low rest, take it over). See DynamicVillagerNeedsMod for the adopt fix.
- **Sleep duration is a rate problem, not a threshold problem:** total sleep *fraction* = drain-rate / (drain + gain). Lowering the wake threshold only shortens each cycle, not the fraction — to make villagers sleep *less*, raise the rest-gain rate.
- **Warmth is best left to the game.** The game's own `warmUpBehaviour`/`_warmUpQuest` warms villagers *fully* during work. Forcing a leisure trip for warmth only warms partway → thrash between fire and job. Tune the game's warm-up *speed* (multiply warmth only while it's already rising) rather than taking over the *when*.

### Dead ends (don't retry)
- `Villager.overrideSchedule` + `scheduleOverride` + `CurrentBehaviorType` — drives the FSM behavior **label** (and suppresses sleep) but **bypasses the task-dispatch pipeline**: villagers stand idle (`TaskRunner` active-task null) even at midday with a valid workstation. Use `Rpc_ChangeSchedule` (the real schedule) — the only thing that dispatches work.
- `VillagerHappiness.LeisureRate`/`WorkRate`/`SleepRate` — **get-only**, can't scale the rate. Write `_happinessVAttr` directly.
- Warmth/cold as a leisure trigger (incl. the `IsFreezing` flag) — villagers thrash between a fire and their job (each warmth-leisure trip only warms to the exit threshold, then they immediately get cold again). Hand cold back to the game.
- Lowering `WakeWhenRestAboveHours` to "sleep less" — only shortens each sleep's cycle, not the total sleep *fraction*; raise the rest-gain rate instead (see facts above).
- The `sched=match/REVERTED` readback diagnostic shows `REVERTED` for all-Sleep/all-Leisure but `match` for all-Work — a packing/representation quirk of comparing `__NetworkedSchedule` to the packed value, **not** a functional revert: behavior always followed the schedule. Don't chase it.

---

## Villager Combat / Fight-vs-Flee System

How a villager decides to **run from** vs **fight** an attacker. Used by Mod 7 (VillagerFightBackMod).
Structure below is **confirmed from the binary**; the runtime *behavior* notes flagged ⚠️ are
**pending in-game verification** (FSM state method bodies aren't dumpable in IL2CPP — only signatures).

**Competing combat Quests (all derive from `SSSGame.AI.CombatQuest`, ranked by `TriggerPriority`)**
```
SSSGame.AI.CombatQuest                 ← base; .fsmBehaviour, .GetPriority(QuestData), .SetDirty(), .TriggerPriority
  CombatQuest/CombatQuestData          ← .ShouldFight (GET-only, computed), ._engagedEnemy (IAttackTarget, GET+SET),
                                          ._survival (VillagerSurvival), ._villager, ._targeting (ITargetAI),
                                          ._combatTimeRemaining, ._lowHealth
  SSSGame.AI.FleeCombatQuest           ← what a NON-warrior villager gets: "get spooked & run"
    FleeCombatQuest/FleeCombatQuestData
        .ShouldFight (GET-only)        ← its OWN computed override (home-safety / low-HP / defense-score complaints)
        ._GetSpooked(IAttackTarget)    ← entry point when an attacker spooks the villager (safe to patch; 1 ref param)
        ._homeSafetyAttribute, ._lowHpComplaint, ._low{Defense}AtHome/AtWork complaints, ._fleeingComplaint
  SSSGame.AI.WarriorCombatQuest        ← what an equipped warrior gets: stand & fight
  SSSGame.AI.PartyCombatQuest / HunterCombatQuest / PetCombatQuest   ← other fight variants
SSSGame.AI.DefensiveCombatTask         ← party/villager OnTakeDamage→combat-timer task
```

**Warrior status (the game's own "this villager fights" flag)**
```
Villager.IsWarrior  (bool)             ← set by VillagerSurvival._CheckWarriorStatus() from equipped warrior gear
VillagerSurvival._warriorCombatQuest (WarriorCombatQuest), ._warriorQuestAdded, ._warriorBuffAdded
VillagerDataSheet.warriorCombatTime / warriorDetectionRange / warriorReactRange / warriorStatusEffect
WeaponizedItemInfo.warriorGear / WearableItemInfo.warriorGear  (bool)  ← gear that confers warrior status
Villager.IsOnDefendDuty / ._isOnDefendDuty / .OnDefendDutyChanged   ← separate "assigned to a DefensiveStation" flag
VillagerSocial.SendSpookedNotification(IAttackTarget) / .Rpc_SendSpookedNotification(NetworkId)
```

**Identifying an attacker — `SSSGame.Combat.IAttackTarget` (every attackable thing implements it)**
```
.GetTargetName() → String              ← display name (e.g. match "Wisp"); names live in runtime SOs, not the binary
.Faction → SSSGame.Combat.Faction      ← enum { Critters, Danger, Ignore, Neutral, Structures, Undead, Vikings }
.IsAggressive() / .IsAlive() / .IsViable() / .IsPlayer() / .ThreatScore / .transform
SSSGame.Creature : IAttackTarget       ← monsters; .Faction, .GetName(), .dataSheet (CreatureDataSheet.faction/localizedName)
SSSGame.Combat.Threat                  ← per-target threat record: .ThreatScore, .Damage, .target (IAttackTarget), .Seen
SSSGame.Combat.CombatManager (MonoBehaviour singleton-ish) ← RegisterCombatant / faction context unwinding
```
- **Faction is coarser than name:** wisps and wolves may share `Danger`; draugar are `Undead`. For a precise
  "fight wisps, flee draugar/wolves" split, match by **name substring**, not faction.
- A `Creature`'s name is a runtime ScriptableObject value (same as item names) — **not** in the binary;
  discover it live by logging `IAttackTarget.GetTargetName()` + `.Faction` from a patch.

**The fight-vs-flee override (VillagerFightBackMod's approach):** Postfix
`FleeCombatQuest/FleeCombatQuestData.get_ShouldFight`; when `__result==false` (would flee) and
`__instance._engagedEnemy` matches a name/faction whitelist, set `__result=true`. Read + return-a-bool only
(no networked write → co-op-safe); gate the flip on `__instance._survival._hasAuthority`.

### Confirmed in-game (session 2, 2026-06-22) — supersedes the old "pending" notes
Verified live while building Mod 7 (see [`VILLAGER_FIGHTBACK_HANDOFF.md`](../VILLAGER_FIGHTBACK_HANDOFF.md)
for the full lever-by-lever log). **These are facts now, with dead-ends called out so they aren't re-tried:**
- **Wisp:** `GetTargetName()` == `"Wisp"`, `Faction` == `Undead`. Name-whitelist only (draugar are also `Undead`).
- **`ShouldFight` is a DEAD END for fight-vs-flee.** In a real Wisp encounter it already returns `True`, and
  the villager flees anyway. The flee is **not** gated on `ShouldFight`. Forcing it does nothing.
- **`_engagedEnemy` is usually `null`** when `ShouldFight`/`GetPriority` are read — capture the attacker from
  `_GetSpooked(IAttackTarget)` and remember it per quest-data instance (key by `Pointer`) as a fallback. (Done.)
- **Flee is FSM-driven, in layers:** `_GetSpooked` = *notification only* (suppressing it removes the spook
  icon/fanfare but NOT the flee); `FSM_SetVillagerIsFleeing.OnStateEnter` sets `Villager.IsFleeing`;
  `FSM_RunFromTarget` does the run movement. Blocking each removes its piece but not the overall retreat.
- **`FSM_CheckVillagerWarrior` is NOT the melee-vs-run branch** — forcing its `Decide` true has no effect.
- **A regular (non-warrior) villager CAN reach `FSM_MeleeCombat`** and kill a Wisp — confirmed when **idle**.
- **The real blocker is QUEST ARBITRATION:** a villager **with an active work quest** (e.g. `TerraformingQuest`)
  gets pulled back to her job marker; combat only fully wins once the job resets. `QuestRunner._activeQuestPriority`
  oscillates between the work quest and `FleeCombatQuest`. **Combat time is NOT the issue** (topping
  `_combatTimeRemaining` works but doesn't fix it).
- **Priority-number boosts via getter postfixes DON'T fix it — both tried and failed in-game:**
  - Postfix `CombatQuest.GetPriority` → 41 **fired** yet the runner still re-selected `TerraformingQuest`
    (`activePrio=15`) — impossible for a `GetPriority`-ranked selector, so `GetPriority` (Single) is only the
    *reported* priority of the already-chosen quest, not the selector.
  - Postfix `FleeCombatQuest.get_TriggerPriority` → 40 (v1.0.11): the boost log **never fired** → the getter
    is almost certainly **cached** (a stored field the runner reads, not the property; `CombatQuest.SetDirty()`
    exists) — so postfixing the property is inert. (`FleeCombatQuest` *does* override `TriggerPriority`, and
    it is **per-villager** — `VillagerSurvival._fleeCombatQuest` — but that doesn't help if the value is cached.)
  - **Takeaway: don't fight the arbiter with priority numbers. Patch the selection method.**
- **v1.0.12 tried overriding `QuestRunner.FindNextBestQuest` → CRASHED** (its `Single&` by-ref out-param is
  the IL2CPP trampoline hazard above; villagers went inert). Dead end as a patch target.
- **v1.0.13 suspended the work quest** (`QuestRunner.Update` poll → `RemoveQuest` the active work quest,
  `AddQuest` back later). **Mechanically worked** (`Suspended work quest 'TerraformingQuest'` → active became
  `FleeCombatQuest` → reached `FSM_MeleeCombat`) **but she STILL ran.** Decisive: with the job removed, the
  run can't be job-pathing.
- **CONFIRMED ROOT CAUSE (session 3): she runs because the FleeCombatQuest executes the FLEE combat
  _behaviour_.** `VillagerSurvival` has two combat FSMs: **`fleeCombatBehaviour`** (kite/run) and
  **`naturalCombatBehaviour`** (stand & fight) — plus `avoidDangerBehaviour`, `callToArmsBehaviour`, etc.
  `CombatQuest.GetFSMBehavior(QuestData) → vFSMBehaviour` selects which one runs; for `FleeCombatQuest` it
  returns the flee one. Idle villagers fight Wisps because they use the *natural* behaviour. Every lever that
  poked the flee quest (suppress spook, block flee-flag/run-action, force `ShouldFight`, boost priority,
  remove job) was de-fanging the wrong FSM.
- **The fix under test (v1.0.14): postfix `CombatQuest.GetFSMBehavior` → return `_survival.naturalCombatBehaviour`
  for a whitelisted enemy.** `FleeCombatQuest` does **not** override `GetFSMBehavior`, so patching `CombatQuest`'s
  covers it. Safe sig (reference types). Get `naturalCombatBehaviour` + `_engagedEnemy`/`_survival` off the
  `CombatQuest.CombatQuestData`. If the swap doesn't take, the heavier fallback is real **warrior status**
  (`VillagerSurvival._warriorCombatQuest`/`_warriorQuestAdded`/`_CheckWarriorStatus()`) — the game's own
  stand-and-fight path. (`vFSMBehaviour` is on the Unity object chain → use `Plugin.PtrOf(object)` to compare.)
- **Scope FSM-action patches by target** via `IFSMBehaviourController.AiTargeting.CurrentTarget`.
- **Quest back-refs (confirmed):** `QuestData.Quest` + `QuestData.QuestRunner`, and `CombatQuestData.GetVillager()`
  — so from a `FleeCombatQuestData` patch you can reach the villager's runner/quest without `FindObjectsByType`.
  `QuestRunner` exposes `FindNextBestQuest(out topPriority, excludeActiveQuest)`, `ReevaluateQuest`,
  `AddQuest`/`RemoveQuest`, `StopAndRemoveAllQuests`, `ActiveQuest`, `_activeQuestPriority`, and a
  `_pendingQuest*` set. **Pointer gotcha:** `QuestRunner` is on the UnityEngine.Object chain and the csproj
  references the *stripped* unity-libs CoreModule, so `QuestRunner.Pointer` won't compile — cast through
  `object` to `Il2CppObjectBase` at runtime (Il2CppSystem.Object-derived types like `QuestData`/`Quest` are fine).
- `QuestPriority` scale (runtime): `Flee`=40, `SelfDefense`=42 (warrior combat), `AvoidImminentDanger`=46,
  `WorkstationWork`=15, `ImportantWork`=22, `Idle`=0.

## Worldgen / World Streaming
How the world loads around the player, and how to make distant tiles stream **without moving the player**.
Used by Mod 9 (SeedScoutMod) — full recipe in [`SEED_SCOUT_HANDOFF.md`](../SEED_SCOUT_HANDOFF.md).

- **Worldgen is deterministic, seed-phrase driven**, and runs at world **LOAD** (not in the create-world UI).
  Caves (mines) are fully known at load (`BiomesManager._worldGenerator.GetDataMap()._areaInstances`, filter
  `AreaInstance.TryCast<CaveAreaInstance>()`); ~3 per world. **Lakes, dens (hostiles), etc. spawn per-tile as
  the world streams** near the tracked actor — that's why they only "fill in" as you walk.
- **`AreaInstance.position` (Vector2Int) = WORLD METERS (x, z)**, not tile coords (verified twice, ~10 m).
- **`SandSailorStudio.Streaming.WorldStreamingManager`** is the streamer (capture via its `Awake`). It follows
  one `IStreamingActor TrackedActor` — which is the player's **`PlayerDrive`** (it implements `Teleport(Vector3)`
  / `IsTeleporting()`, a first-class game teleport, not a raw transform write).
- **Force-stream a tile without the player — CONFIRMED in-game (2026-06-24):**
  `WorldStreamingManager.RequestLoadWorldTile(int requestId, Transform anchor, WorldTileId tileId)`. Each
  `CaveAreaInstance` carries its own `WorldTileId` (no need to compute). Calling this for a **distant** tile
  **does instantiate that tile's biome/den/lake entities** (their spawn hooks — `BiomeLakeStampMask._OnSpawnBiome`,
  `Den.Start` — fire) while the player stays put. Verified: player never moved (`-209.9,33,-13.7`) yet dens +
  a lake spawned at cave tiles 468–1120 m away. So a *resident/requested* tile spawns content; it is **not**
  gated on the tile being *visible* near the player.
- **Coverage:** `RequestLoadWorldTile` loads only the **one tile** containing that position (~128 m square).
  To cover a cave's surroundings, request a **ring of neighbour tiles** — CONFIRMED working (2026-06-24,
  SeedScoutMod radius config). Addressing rules (these took two iterations to get right):
  - **A cave's assigned `WorldTileId` does NOT necessarily contain its entrance `position`.** e.g. a cave at
    `(-426,-722)` was assigned tile grid `(-3,-5)` = `[-384,-256)×[-640,-512)`, which excludes that point. So
    **never derive a cave's tile from its entrance** via floor/round/ceil — it fails for some caves.
  - **Address neighbours off the cave tile's own centre:** `mid = caveTile.GetWorldMidPosition((int)tileSize)`,
    then neighbour tile = `WorldTileId.GetLowest(mid.x + dx·ts, mid.y + dy·ts, ts)`.
  - **Tile grid convention is FLOOR:** tile `g` spans `[g·ts,(g+1)·ts)`, centre `(g+0.5)·ts`; `GetLowest`
    (floor) maps a centre→its tile with no rounding tie. (`GetClosest`=round and `GetHighest`=ceil exist too;
    round is tie-prone at a tile centre, so prefer `GetLowest`. Best practice: probe all three and keep the one
    that round-trips every cave's centre→known id, so a wrong tileSize/convention self-corrects.)
  - **`tileSize`** = `WorldConfiguration.GetActive().tileSize` (Int32, =128).
- **WorldTileId helpers:** `GetClosest/GetHighest/GetLowest(worldX, worldZ, worldTileSize)`, `WorldXY` (grid),
  `GetWorldPosition(tileSize)`. Manager also exposes `GetTile(int x,int y)` / `GetTile(float worldX,float worldZ)`,
  `RegisterWorldTile`, `CancelLoading`, `StopAllStreaming`, `SetTrackTransform(Transform)`.
- **Alternative force-stream primitive:** `SSSGame.StreamingInstigator` (MonoBehaviour, `Request()`) — the game's
  own "stream at this point." Not needed once `RequestLoadWorldTile` was confirmed working.
- **POI "discovery" / native map pins (confirmed to EXIST; reveal-without-walking NOT yet working):**
  `AreaInstance.isExplored` (bool, settable) + `OnDiscovered` (Action) + `areaInstanceMarkerHandler`
  (`IAreaInstanceMarkerHandler` → `RefreshExploration()`, holds `MarkerInfo`). `TerrainAreaMarkerHandler`/
  `BiomeAreaMarkerHandler` poll player vs `GetDiscoveryRadius()` to flip the flag and show the pin.
  **Gotcha:** `areaInstanceMarkerHandler` is **null on cave AreaInstances even after force-loading their
  tiles** — the handler/marker object isn't created by tile streaming (likely tied to `CaveAreaInstance.Root`,
  instantiated only at close range). Reveal-without-walking is unsolved; see SeedScout handoff for leads
  (CaveData.SetExploredState, ExplorationDataHandler, ObjectiveMarkerContainer). Dens/lakes aren't
  AreaInstances → separate marker path (`WorldObjectiveMarker`/`StructureObjectiveMarker`).
- **Den type/tier:** a `Den : Creature`'s own sheet is empty (`faction=Ignore`, `baseThreatScore=0`); the
  type/tier is what its `alphaSpawner` (PopulationSpawner) spawns — `_populations[i].config`
  (CreaturePopulationConfiguration) `.name` + `.populationInfo.name` (e.g. `WightBoss`, `DraugarAlpha`).
  Full den→creature table in the SeedScout handoff.
- **Dead end (still unresolved):** the runtime seed reads `<rng-null>` —
  `RandomGeneratorManager.seedPhrase`/`SetSeedPhrase` are never the path the seed travels at load. Non-blocking
  for a scorer (you type the seed); required only for an auto-finder. See the SeedScout handoff for the chase.
