# Mod 4: TorchFuelMod — COMPLETE (v1.2.5)

**Goal:** torches (placed for base lighting/decoration) never run out of fuel/resin; optional rain
protection and coverage of built-in building fires.

**Game subsystem:** [Torch / Fire-Fuel System](../architecture.md#torch--fire-fuel-system) — the
`FireStructure` API, the `Rpc_AddFuel` network-safe path, why filtering must be by structure name,
and the coal/kiln + cave-sconce dead-ends in full.

## Working approach

- **Capture:** postfix on `FireStructure.Initialize(Structure ownerStructure)` — fires for every
  fire-capable structure. A fire is tracked when EITHER the owner structure's
  `DefaultName`/`StructureName` OR the fire's own GameObject name contains a `TargetStructureNames`
  substring (the GameObject-name match catches fires whose owner is a whole building, e.g. a
  torch mounted on a tavern). Campfires/forges/kilns/cooking stations also use `FireStructure`, so
  the name filter is what keeps them untouched by default.
- **Name-free alternative (`KeepAllLightSources`, default off):** postfix on
  `LightOutlet.Initialize(Structure)` tracks `LightOutlet.fireStructure` for *every* light-emitting
  fire regardless of name — catches the tavern campfire, braziers, etc. `LightOutlet` is present on
  lighting fires but NOT on cooking stations/forges/kilns (those use `CookingOutlet`/
  `WarmthOutlet`), so crafting fires stay untouched. ⚠️ in-game confirmation for the built-in-fire
  coverage was never explicitly recorded.
- **Top-off:** all tracking funnels through `Plugin.TrackFireStructure(fire, label)` (dedupe +
  single log site) into `Plugin.TrackedFireStructures`; `TorchFuelTracker` (registered
  MonoBehaviour, `Update()` polling — the DayTracker pattern) checks the list every
  `CheckIntervalSeconds`; any tracked structure with `CurrentFuelVolume < MaxFuelVolume` gets
  `Rpc_AddFuel(max - current)` — exactly to max, never overfueling (so the "overfire/blazing"
  state can't trigger). `Rpc_AddFuel` (not a direct `CurrentFuelVolume` write) is the same
  network-safe path manual resin refueling uses, so co-op works regardless of which client hosts.
- Dead/despawned entries (`!fire.IsSpawned` or null) are pruned each tick.
- Confirmed in-game: fuel ticks down for a few seconds then jumps back to max on the next check;
  perf check pass ≤3.7 ms (2026-07-12, healthy).

## Config (`[TorchFuel]`)

- `TargetStructureNames` (string, default `"Torch"`) — comma list, case-insensitive substring match
  vs owner structure names AND the fire's GameObject name. `Fire` also matches `Small Fireplace`
  and the `Cooking Hut` fire.
- `CheckIntervalSeconds` (float, default `5.0`) — top-off cadence (real seconds).
- `PreventRainExtinguish` (bool, default `false`) — matched torches ignore weather entirely.
- `AutoRelightAfterRain` (bool, default `false`) — re-light matched torches when rain stops (no
  effect if `PreventRainExtinguish` is on).
- `KeepAllLightSources` (bool, default `false`) — see working approach.
- `LogAllFireStructures` (bool, default `false`) — diagnostic: log every `FireStructure` and
  `LightOutlet` as it loads (owner names + GameObject name) to discover a specific fire's exact
  name. Also reveals the smithing/coal buildings (forge, bloomery-bellows, charcoal pyre all carry
  a `FireStructure`). Leave off for normal play.

## Smithing/coal fires — confirmed names + the Bloomery warning

Confirmed in-game via `LogAllFireStructures` (2026-06-25); each carries a `FireStructure`, so each
is keepable via the plain `TargetStructureNames` list — no new code:

| In-game display name | FireStructure GameObject | Building |
|---|---|---|
| `Bloomery` / `Improved Bloomery` | `KilnInteractionArea_dmgRec` | the smelter (there is NO "Furnace") |
| `Metalworker` / `Improved Metalworker` | `StorageArea_Forge` | the forge |
| `Coal Maker` | `PyreInteractionArea` | the coalmaker (charcoal pyre) |

- **Forge (`Metalworker`) and `Coal Maker` are confirmed FINE force-fueled** (user-verified).
- 🛑 **Do NOT force-fuel the `Bloomery` — it breaks the smelt (confirmed in-game 2026-06-25).**
  The bloomery's temperature mini-game requires fuel to *deplete* (heat is a function of the fuel
  ratio: `fuelRatioToPower` curve + burn-rate attrs); pinning fuel at max locks the heat output and
  no amount of bellows-pumping reaches the bake range — zero bloom produced. The config description
  warns against it. A future "fueled bloomery" would have to top up the kiln's `_fuelVAttr` only
  when low (mimicking a villager coal delivery), capturing the kiln via `Bloomstation.Spawned()` →
  `.kiln` — NEVER via `KilnInteraction.Initialize` (boot-crash; see Dead-ends). Left unbuilt.

## NOT covered (separate systems)

- **Coal-burning buildings (Kiln / smelting):** the Kiln (`SSSGame.KilnInteraction`) is not a
  `FireStructure` — its fuel is `_fuelVAttr` burned from coal items in an `ItemContainer`, no
  `CurrentFuelVolume`/`Rpc_AddFuel`. Adding "Furnace"/"Smelter"/"Kiln" to the name list does
  nothing (no such structure names). Keeping a kiln fueled is a new sub-feature; co-op replication
  unverified. Full breakdown: architecture.md torch section.
- **Cave wall sconces:** `SSSGame.CaveTorchOutlet` — an equipment-item mechanism (torch
  `EquipmentItemInfo` burning by durability, replaced via villager `LightkeepingQuest`), no fuel
  volume at all. This mod cannot keep them lit; a future feature would block durability decay or
  auto-re-equip. Full breakdown: architecture.md torch section.

## Dead-ends (do not retry)

- **Patching `Initialize(Structure)` on `Interaction` MonoBehaviours** (`KilnInteraction`,
  `BellowsInteraction`, `ForgeInteraction`, `CharcoalStation`, `Bloomstation`) — v1.2.0/v1.2.1
  broke game launch: `__instance` fails to marshal at boot-prefab init (`Handle is not
  initialized`, in the glue before the patch body), so the original `Initialize` never completes
  and boot stalls. v1.2.2 removed the diagnostic entirely, returning to the proven-safe surface
  (`FireStructure.Initialize` + `LightOutlet.Initialize` only). This is the project-wide
  "don't patch Interaction lifecycle methods" gotcha; full write-up in architecture.md.
  Safe discovery alternative: `LogAllFireStructures` (see config).

## Version history

- **v1.0.x:** name-filtered `FireStructure` top-off via `Rpc_AddFuel`, confirmed in-game.
- **v1.1.0:** GameObject-name matching + `LightOutlet`/`KeepAllLightSources` (built-in building
  fires) + `LogAllFireStructures` diagnostic.
- **v1.2.0–v1.2.2:** burnable-building diagnostic attempt — broke game launch; fully reverted in
  v1.2.2 (see Dead-ends).
- **v1.2.4:** Bloomery removed from the recommended fuel list after the smelt-breaking finding.
- **v1.2.5** (2026-07-12): perf stopwatch on the check pass (≤3.7 ms measured — low-cost mod;
  part of the 2026-07-11/12 perf arc, see architecture.md → Mod-side frame hitches).
