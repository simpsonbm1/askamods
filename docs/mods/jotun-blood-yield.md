# JotunBloodYieldMod

## Overview
Modifies the yield of Jotun Blood obtained from mining "Blood Stone" nodes (and anywhere else items with "Blood" in their name drop).

## How it works
ASKA uses two components for spawning loot: `SSSGame.HarvestSpawner` and `SSSGame.LootSpawner`. Depending on the type of node or the version, one or the other might handle the drop. As of **v1.1.0** the mod hooks both:

1. **HarvestSpawner.Awake** (postfix): the spawner's `pieceLoot` and `bitLoot` lists each hold `HarvestSpawner.Loot` entries. We scan both lists, count entries whose `lootInfo.Name` contains "Blood"/"Jotun", and for each matching entry **add 2 duplicate `Loot` copies** to the list (so a 1-entry node yields 3 rolls, a 2-entry node yields 6). A guard skips lists that already have ≥3 blood entries so a shared prefab list isn't double-multiplied across repeated `Awake` calls. *(This replaced the earlier `_GetAwardedLootCount` postfix, which did not reliably fire for these nodes.)*
2. **LootSpawner.GetLootStack** (postfix): returns an `ItemInfoQuantity`. We remap its `quantity` field when `itemInfo.Name` contains "Blood"/"Jotun" (see scaling rules below).

### Scaling Rules
To map the vanilla yields to the user's desired scaling (small rocks: 3-4, large rocks: 5-6), we remap the vanilla quantities as follows:
- Vanilla 1 $\rightarrow$ Modded 3
- Vanilla 2 $\rightarrow$ Modded 5
- Vanilla 3+ $\rightarrow$ Modded 6

This maps a vanilla small rock (1-2) to yield (3-5) and a vanilla large rock (2-3) to yield (5-6).

## Key Files
- `Plugin.cs`: Standard BepInEx entry point.
- `Patches.cs`: Harmony patches for `HarvestSpawner.Awake` (loot-list duplication) and `LootSpawner.GetLootStack` (quantity remap).

## Known Issues & Notes
- Modifies ANY harvested item whose string name contains "Blood". In vanilla ASKA, Jotun Blood is the only matching item.
