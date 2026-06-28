# JotunBloodYieldMod

## Overview
Modifies the yield of Jotun Blood obtained from mining "Blood Stone" nodes (and anywhere else items with "Blood" in their name drop).

## How it works
ASKA uses two components for spawning loot: `SSSGame.HarvestSpawner` and `SSSGame.LootSpawner`. Depending on the type of node or the version, one or the other might handle the drop.

1. **HarvestSpawner**: The method `_GetAwardedLootCount(ItemInfo)` determines how many items to drop. We use a Harmony Postfix on this method. If the `ItemInfo.Name` contains "Blood", we intercept the calculated result.
2. **LootSpawner**: The method `GetLootStack(LootData, int)` returns an `ItemInfoQuantity`. We use a Harmony Postfix on this to modify the `quantity` field if the `itemInfo.Name` contains "Blood".

### Scaling Rules
To map the vanilla yields to the user's desired scaling (small rocks: 3-4, large rocks: 5-6), we remap the vanilla quantities as follows:
- Vanilla 1 $\rightarrow$ Modded 3
- Vanilla 2 $\rightarrow$ Modded 5
- Vanilla 3+ $\rightarrow$ Modded 6

This maps a vanilla small rock (1-2) to yield (3-5) and a vanilla large rock (2-3) to yield (5-6).

## Key Files
- `Plugin.cs`: Standard BepInEx entry point.
- `Patches.cs`: Harmony patches for `_GetAwardedLootCount` and `GetLootStack`.

## Known Issues & Notes
- Modifies ANY harvested item whose string name contains "Blood". In vanilla ASKA, Jotun Blood is the only matching item.
