# FishFilletMod (Mod 20)

**Status:** COMPLETE v1.1.1 — confirmed in-game 2026-07-08.
**What it does:** Shift + Right-click a raw fish in your inventory to fillet it on the spot (consumes the fish, grants its normal meat/blubber yield), instead of dropping it and skinning it on the ground.

## Shipped recipe
- **Trigger:** Harmony PREFIX on `SSSGame.UI.ItemThumbnailPanel.OnPointerClick(PointerEventData)`. Fires only when `eventData.button == PointerEventData.InputButton.Right` AND a Shift key is held (`UnityEngine.Input.GetKey(LeftShift|RightShift)`, legacy input — same pattern as GroundItemVacuumMod) AND the item `Qualifies`. Returns `false` to suppress the native right-click (move-to-container) for that one gesture; every other click passes through untouched (returns `true`).
- **Qualifies(item):** native subclass check that the item is `ResourceInfo`-kind (`il2cpp_class_is_subclass_of`), `CanBeHarvested()` == true, `mainHarvestMoveset != null`, and `mainHarvestMoveset.requiredEqippmentCategory.Name` matches a configured tool category (default "Knives"). Result cached per item id. Generic — not hardcoded to any fish.
- **The drive:** temporarily null `mainHarvestMoveset.requiredEqippmentCategory` (bare-hand so the harvest op's tool gate passes), call `__instance.CommandHarvestItem()` (→ `CharacterInventory.HarvestSelectedItems()`, the game's real harvest executor), then restore the tool req in a `finally`. A `_filleting` re-entrancy guard prevents the driven harvest from re-entering the patch.
- **Yield is the game's own** — because we drive `CommandHarvestItem`, the game produces the exact vanilla loot for each fish. Validated across the game's TWO loot models: Mackerel (whole-harvest, yield baked on the prefab) and Seabass (bit-harvest, yield only materializes post-`InitHarvestLoot`). Both filleted correctly with no hardcoded amounts.
- **PointerEventData / InputButton** come from interop `UnityEngine.UI.dll` (csproj reference).

## Config
- `[Fillet] EnableFilletInInventory` (default true) — master toggle.
- `[Fillet] HarvestToolCategories` (default "Knives") — comma-separated, case-insensitive tool-category names whose harvest tool requirement is unlocked in inventory. Only ResourceInfo items that are ALREADY harvestable and require one of these categories are unlocked; bare-hand items (thatch/reeds) are untouched. Safety valve if some fish type uses a different tool category.

## Why Shift+RMB (and why not Shift+LMB)
The design pivots on one in-game-confirmed fact: the cosmetic **"can't be harvested" toast is the game's NATIVE Shift+LMB harvest router** failing its own knife check. Riding that gesture (the earlier `OnCustomClickActionOverride` approach) filleted correctly but co-existed with the native attempt's error toast — unavoidable because the router is native and fires independent of our hook. **Shift+RMB never invokes that router**, so filleting through it is toast-free. Confirmed in-game 2026-07-08: Shift+RMB fillets cleanly, no toast, no container-move (container-move is plain right-click and doesn't register with Shift held); Shift+LMB on a fish still shows the toast, but that is pure vanilla — the mod no longer touches left-click at all.

## Dead-ends (don't retry)
- **Postfix `CanHarvestCurrentItem → true`** flips only the cursor color; harvest execution is a separate gate. (v1.0.4)
- **Patch/null-tool on `CommandHarvestItem`** — it isn't called by the native harvest-cursor flow, so it never fires on the click. (v1.0.5)
- **Any `ItemThumbnailPanel` click-method as the Shift+LMB harvest trigger** — the working native harvest fires none of them; it calls `CharacterInventory.HarvestSelectedItems()` directly from the native cursor system. (v1.0.6 trace)
- **Persistent bare-hand** (permanently nulling `requiredEqippmentCategory`) — the native inventory-harvest still rejects fish for a reason beyond the tool req. (v1.0.8)
- **The `CanHarvestCurrentItem` cursor-flip was NOT load-bearing** for the RMB drive — removed in v1.1.1 cleanup and confirmed the fillet still works (the drive's temp-null makes the native check pass on its own).

## Confirmed facts (don't re-derive)
- Fish = `SSSGame.ConsumableInfo : ResourceInfo`; `CanBeHarvested()`==true; `exhaustableComponents` EMPTY; yield flows via the `LootSpawner`/`GetPieceLoot` path; `mainHarvestMoveset.requiredEqippmentCategory` = 'Knives' (note the game's misspelling "Eqippment"). spawnObjects: `Item_Food_FishMackerel`, `Item_Food_FishSeabass`.
- Fish don't stack in inventory (no stack-fillet concern).
- Real harvest executor = `CharacterInventory.HarvestSelectedItems()` (→ `_HarvestItemOperation`), reachable via `ItemThumbnailPanel.CommandHarvestItem()`.
- Co-op authority: reuses the game's own networked harvest command (likely host-safe); a client filleting is UNVERIFIED as of shipping.

## Generality
Not hardcoded to Mackerel/Seabass — any `ResourceInfo` item that `CanBeHarvested` and requires a configured tool category will fillet, with yield delegated to the game. Structural confidence it covers all fish is high (two loot models already validated; failure mode is graceful — a non-matching item simply doesn't fillet, no crash — and the tool-category config extends coverage). Full-database census not run (chose to ship; any fish that doesn't fillet = add its tool category to config or a quick follow-up).
