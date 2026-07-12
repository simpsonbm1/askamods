# Mod 25: OuthouseComposterMod — COMPLETE (v1.0.0, on Nexus as "Outhouse Composter")

**Goal:** food and seeds thrown into the Outhouse structure's storage convert into Compost over
in-game time. Origin: `NEW_MOD_IDEAS_PLAN.md` idea 13, Phase 1. Phases 2–3 (bigger grid, native-decay
conversion) remain unbuilt. All shipped features confirmed in-game 2026-07-12.

## Game subsystem: Storage acceptance — the Outhouse container

All findings confirmed in-game 2026-07-11 (OuthouseComposterMod diagnostics + manual probing).

### Outhouse container identity
- **`Storage_SmallItems_Outhouse`** — unique containerType asset name, only on the Outhouse
  structure, capacity 20 slots. No Fusion networked backing observed (unlike warehouses/storage).
- Pointer-keyed identity cache in `OuthouseGate` (cleared on world leave) — single per-world lookup
  via `SettlementManager` getter methods (never the null `.settlements` field).
- Manual hierarchy walk with per-node singular `GetComponent<ItemContainerComponent>()` (plural
  generic `GetComponents(System.Type)` confirmed MISSING through the interop trampoline
  2026-07-11 — do not retry).

### Acceptance gate
- **Every SmallItem raw food/seed shares identical storageClass pointer with the outhouse
  containerType and with Compost.** The native game's acceptance gate does NOT base decision purely
  on storage-class membership — per-item condition logic exists beyond the class check and is not
  reverse-engineered.
- **Workaround:** override acceptance for the outhouse container only via four independent Harmony
  postfixes on `SandSailorStudio.Inventory.ItemContainer` — `CanStoreItemType`, `Check`,
  `HasSpace`, `GetStackSize` — each scoped strictly to the outhouse container by the pointer-keyed
  cache. `GetStackSize` returns 0 natively for forced inputs → overridden per kind
  (FoodStackSize/SeedStackSize).

## Working mechanism

### Acceptance override (Patches/AcceptancePatches.cs)
Four independent Harmony postfixes on `SandSailorStudio.Inventory.ItemContainer`:
- `CanStoreItemType(ItemInfo)` — force true for food/seeds in the outhouse
- `Check(ItemInfo)` — force true for food/seeds in the outhouse
- `HasSpace(ItemInfo, int)` — force true only when the container still has an empty slot (does not
  count partial stacks — a documented simplification)
- `GetStackSize(ItemInfo)` — return the FoodStackSize/SeedStackSize override instead of native 0

Scoped via pointer-keyed identity cache (`Storage_SmallItems_Outhouse` unique asset name → native
pointer → per-world cache in OuthouseGate). Each patch logs a once-ever "patch alive"
fire-verification marker.

### Classification (OuthouseGate.cs)
- **Seed:** `ItemInfo.storageClass` asset name == `SeedItem`
- **Food:** non-seed whose `ItemInfo.category.Name` contains any CSV substring from FoodCategoryMatch
  (default "Food")
- **Compost item itself** never accepted as input (guard against infinite loops)

### Converter (ComposterDiag.cs, injected MonoBehaviour, Update polling every 2 s)
Host/solo authority gated (`LocalPlayer.NetworkObject.Runner.IsServer ||
IsSharedModeMasterClient`). Outhouse containers re-resolved every 30 s via SettlementManager getter
methods (GetPlayerSettlement/GetCurrentSettlement/worldSettlement — never the null `.settlements`
field) + manual hierarchy walk with per-node singular `GetComponent<ItemContainerComponent>()`;
keyed by structure world position (never UniqueId).

All per-world state (containers, timers, compost ItemInfo, OuthouseGate cache) dropped when
`StorageManager.ActiveSessionID` empties (NoteWorldLeft pattern).

### Timer mechanics
**Per-outhouse food + seed timers**, running on the **in-game clock** (anchored to
`WeatherSystem.NetworkedCurrentGameTime`, elapsed via `GetTimeDifferenceFromCurrentGameTimeInSeconds`).
Threshold = GameHours × dayLength/24 (1 in-game hour ≈ 60 real s at normal speed). A timer arms when
its pool first becomes non-empty; every fire re-arms regardless of whether a conversion happened;
below-ratio pool skips the cycle. **Limitation: timers are NOT persisted across save/load** (restart
on world load).

### Conversion mechanics
**Two modes (simultaneous mode configurable, v0.4.0+):**

1. **Sequential (default, `SimultaneousConversion=false`):** one conversion per timer fire per
   outhouse. Consuming the ratio's worth from the first matching slot(s) in container slot order.
   Pools across slots — observed in-game as a top-left-first, down-the-column-then-right drain. A
   timer fire scans slots 0→19, accumulates matching items until reaching the ratio, converts once,
   proceeds.

2. **Simultaneous (`SimultaneousConversion=true`):** every occupied food/seed slot evaluated
   independently per fire. Each slot holding ≥ ratio converts once (ratio consumed from that slot,
   +1 Compost), all in the same fire, stopping early if the container fills. **NO cross-slot pooling**
   — two stacks of 10 seeds at ratio 20 never combine.

Both modes confirmed in-game 2026-07-12.

### Compost output resolution
`FarmingStation.composts` (singular `FindAnyObjectByType` works) is queried for the compost ItemInfo.
Fallback to name-scan of outhouse contents if not found. Conversions wait until resolved.

## Config (`com.askamods.outhousecomposter.cfg`)

**[Composter] section:**
- `AcceptFood=true`
- `AcceptSeeds=true`
- `FoodToCompostRatio=1`
- `FoodGameHours=5.0` (in-game hours; 1 hour ≈ 60 real s at normal speed)
- `SeedsToCompostRatio=20`
- `SeedGameHours=10.0`
- `CompostItemName="Compost"` (the item to produce)
- `FoodCategoryMatch="Food"` (CSV substrings vs ItemInfo.category.Name; default matches items with
  "Food" in their category)
- `SimultaneousConversion=false` (description carries the **no-pooling WARNING** when true)
- `FoodStackSize=10` (override for GetStackSize; seeds stack to 200 in other storage)
- `SeedStackSize=200` (override for GetStackSize)

**[Diagnostics] section:**
- `EnableDiagnostics=false` (shipped default; true during dev)
- `DumpKey=F10` (hotkey for a log dump of current containers + converter state)
- `StructureNameMatch="Outhouse"` (drives both the diagnostics dump and the converter's structure
  discovery)

**Config-key migration note:** v0.3.0 renamed `FoodMinutes`/`SeedMinutes` → `FoodGameHours`/`SeedGameHours`.
v0.4.0 replaced `InputStackSize` with `FoodStackSize`/`SeedStackSize`. Old keys sit orphaned in
existing cfg files; customized values must be re-set under the new keys.

## Open/unverified

**⚠️ villager-raiding risk:** whether villagers haul food back OUT of the outhouse has not been
observed or tested either way.

## Dead-ends

### Non-generic `GameObject.GetComponents(System.Type)` — CONFIRMED missing (2026-07-11)

**Mechanism (very confident — reproduced v0.1.0 diagnostics):** `GameObject.GetComponents(Type)`
escapes the interop wrapper and throws `MissingMethodException` through the trampoline, aborting any
code path attempting it. Do not retry variants.

**Workaround:** per-node singular `GetComponent<T>()` walk to collect all matches of a target type.

**Also recorded as a universal IL2CPP gotcha in CLAUDE.md and architecture.md.** Same trampoline
family as `FindObjectsByType<T>()` (plural generic) and `GetComponentsInChildren<T>(bool)` (plural
generic).

### Real-time (DateTime.UtcNow) conversion timers — SUPERSEDED by in-game clock (v0.3.0)

Real-time made TimeWarp testing impossible and never got in-game confirmation before being supplanted
by in-game-clock timers. Do not resurrect.

## Version history (compressed)

- **v0.1.x (2026-07-11):** Phase 0 read-only diagnostics (container uniqueness + acceptance-gate
  pointer probe; confirmed in-game 2026-07-11).
- **v0.2.0 (2026-07-11):** real-time converter (never confirmed in-game; superseded by v0.3.0).
- **v0.3.0 (2026-07-12):** in-game-clock timers + GameHours config keys (confirmed in-game
  2026-07-12).
- **v0.4.0 (2026-07-12):** SimultaneousConversion toggle + FoodStackSize/SeedStackSize split
  (confirmed in-game 2026-07-12).
- **v1.0.0 (2026-07-12):** ship polish (config WARNING text; EnableDiagnostics default false).
