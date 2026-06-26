# Mod 4: TorchFuelMod — COMPLETE

**Goal:** Torches (placed for base lighting/decoration) should never run out of fuel/resin.

**Game subsystem:** [Torch / Fire-Fuel System](../architecture.md#torch--fire-fuel-system) — the
`FireStructure` API, the `Rpc_AddFuel` network-safe path, and why filtering must be by structure name.

**Working approach:**
- Postfix patch on `FireStructure.Initialize(Structure ownerStructure)` — fires for every fire-capable structure; filter by `ownerStructure.DefaultName`/`StructureName` containing a configured substring (default `"Torch"`) so campfires/forges/kilns/cooking stations (which also use `FireStructure`) are untouched
- Matched instances are added to `Plugin.TrackedFireStructures` (plain `List<FireStructure>`)
- `TorchFuelTracker` (registered `MonoBehaviour`, `Update()` polling, same pattern as `DayTracker`/`RegenTracker`) checks the list every `CheckIntervalSeconds` (default 5.0); for any tracked structure with `CurrentFuelVolume < MaxFuelVolume`, calls `Rpc_AddFuel(MaxFuelVolume - CurrentFuelVolume)` to top it off exactly to max (never overfuels, so it can't trigger the "overfire/blazing" state)
- Using the existing `Rpc_AddFuel` RPC (rather than setting `CurrentFuelVolume` directly) was a deliberate choice — it's the same network-safe path the game uses for manual resin refueling, so it works correctly in co-op regardless of which client/host has the mod installed
- Dead/despawned entries (`!fire.IsSpawned` or null) are pruned from the tracked list each tick
- Config: `TorchFuel/TargetStructureNames` (string, default `"Torch"`, comma list, case-insensitive substring match), `TorchFuel/CheckIntervalSeconds` (float, default 5.0)
- Confirmed working in-game: fuel visibly ticks down for a few seconds then jumps back to max on the next check interval; tracked 4 "Flimsy Torch" structures correctly on load

## v1.1.0 — built-in / composite-building fires (tavern campfire) + diagnostics
Free-standing torches matched the `"Torch"` name filter; **fires built into buildings did not**, because their owning `Structure` is the building (e.g. `StructureName == "Tavern"`), not a torch. Three changes:
- **`FireStructurePatch` now also matches the fire's own GameObject name**, not just the owner structure's `DefaultName`/`StructureName` — catches fires whose owner is a building but whose object is still named like a torch/fire.
- **New `LightOutletPatch`** (postfix on `LightOutlet.Initialize(Structure)`) + config **`KeepAllLightSources`** (bool, default `false`). When on, it tracks `LightOutlet.fireStructure` for *every* light-emitting fire regardless of name — the name-free way to catch the tavern campfire, braziers, etc. `LightOutlet` is the light-duty dispatcher present on lighting fires but **not** on cooking stations/forges/kilns (those use `CookingOutlet`/`WarmthOutlet`), so crafting fires stay untouched.
- **New `LogAllFireStructures`** (bool, default `false`) diagnostic: logs every `FireStructure` and `LightOutlet` as it loads (`[TorchFuelMod][diag] …` with owner `DefaultName`/`StructureName` + GameObject name) so a specific fire's exact name can be found and added to `TargetStructureNames`. Leave off for normal play.
- All tracking now funnels through `Plugin.TrackFireStructure(fire, label)` (dedupe + single log site).
- ⚠️ Built-in-fire fix is in-game-test pending; the interop facts behind it are confirmed (see architecture.md torch section).

## v1.2.0–1.2.1 burnable-building diagnostic — REVERTED in v1.2.2 (broke game launch)
Goal was a toggle for *any* burnable smithing/coal building. The first step — a read-only diagnostic
(`LogBurnableBuildings`) that postfix-patched `Initialize(Structure)` on `KilnInteraction`,
`BellowsInteraction`, `ForgeInteraction`, `CharcoalStation`, `Bloomstation` — **broke the game**:
v1.2.0 hard-froze during BepInEx chainloader; v1.2.1 (name guard + dropped `GetFuelCount()`) stopped the
freeze but the game still wouldn't finish opening, with `Il2CppInterop … Handle is not initialized … (il2cpp
-> managed) Initialize(...)` trampoline errors. **Cause:** patching `Initialize` on these MonoBehaviour
`Interaction` types fails to marshal `__instance` at boot-prefab init — in the glue, before the patch body —
so the original `Initialize` never completes and boot stalls. Full write-up + the general rule are in
architecture.md (universal interop gotchas + torch-section coal dead-end). **v1.2.2 removed the diagnostic
and the `LogBurnableBuildings` config entirely**, returning the mod to its proven-safe patch surface
(`FireStructure.Initialize` + `LightOutlet.Initialize` only).

**Safe way to discover the smithing/coal buildings instead:** turn on the existing **`LogAllFireStructures`**
flag. The forge fire, the bloomery (via its **bellows** `FireStructure`), and the charcoal pyre all carry a
`FireStructure`, so its already-safe `Initialize` patch logs each with the owner Structure's display name —
no new patch needed. (The kiln's `_fuelVAttr` coal reservoir has no FireStructure, but the bloomery's name
still surfaces via the bellows fire.) For the eventual "keep the kiln fueled" feature, capture instances via
the NetworkBehaviour `Bloomstation.Spawned()` (Fusion lifecycle) and read its `.kiln`, **never** patch
`KilnInteraction.Initialize`.

## NOT covered: coal-burning buildings (Kiln / smelting) — separate fuel system
Adding `"Furnace"`/`"Smelter"`/`"Kiln"` to `TargetStructureNames` does **nothing**. There is no
structure named "Furnace"/"Smelter" in the game at all, and the smelting building — the **Kiln**
(`SSSGame.KilnInteraction`) — is **not a `FireStructure`** (it's a `MonoBehaviour` `Interaction`, not
even a `NetworkBehaviour`), so it never reaches the `FireStructure.Initialize` patch and has no
`CurrentFuelVolume`/`Rpc_AddFuel`. Its fuel is `_fuelVAttr` (a writable `VariableAttribute`) burned
from **coal items** in an `ItemContainer` — a different mechanic end-to-end. Keeping a kiln fueled is a
**new sub-feature** (pin `_fuelVAttr` to max, or refill its coal container), captured at
`KilnInteraction.Initialize(Structure)` with its own tracker list; **co-op replication is unverified**
(no `Rpc_AddFuel` equivalent, plain MonoBehaviour — solo/host safe, co-op clients open). Full breakdown:
architecture.md torch section "DEAD-END — coal-burning buildings". Confirmed via interop dump 2026-06-25;
matches the user's in-game report that `"furnace"` in the name list has no effect.

## NOT covered: cave wall sconces (separate system)
Torches stuck into **cave wall sconces** are **`SSSGame.CaveTorchOutlet`**, not `FireStructure` — an *equipment-item* mechanism (a torch `EquipmentItemInfo` that burns by **durability** and is replaced by a villager `LightkeepingQuest`), with **no fuel volume / no `Rpc_AddFuel`**. This mod's fuel top-off cannot keep them lit. See the architecture.md torch section "DEAD-END — cave wall sconces" for the full breakdown; a future feature would have to block the equipped torch's durability decay or auto-re-equip it — a different approach from this mod.
