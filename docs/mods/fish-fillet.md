# FishFilletMod (Mod 20)

**Status:** COMPLETE v1.5.0 — confirmed in-game 2026-08-01. v1.2.0 core filleting confirmed
in-game 2026-07-21 in German.
**What it does:** Shift + Right-click a raw fish in your inventory to fillet it on the spot
(consumes the fish, grants its normal meat/blubber yield), instead of dropping it and skinning it on
the ground. The gesture is rebindable via config.

## Shipped recipe
- **Gesture binding (`Binding.cs`, v1.4.0):** `FilletKey` parses to ANY Unity `KeyCode` and
  `FilletModifierKey` to a `KeyCode[]` (empty = no modifier required). Re-parsed only when a raw
  string changes, so a live config reload rebinds without a restart and the per-click path does no
  work in the common case. Unparseable values warn and fall back to Shift / Mouse1.
- **Two trigger paths, chosen by the bound key.** Unity's EventSystem only dispatches pointer clicks
  for buttons 0–2, so a single click hook cannot cover keyboard keys or side mouse buttons:
  - **Click path (Mouse0/1/2)** — Harmony PREFIX on
    `SSSGame.UI.ItemThumbnailPanel.OnPointerClick(PointerEventData)`. Fires when `eventData.button`
    matches, the modifier is held, and the item `Qualifies`; returns `false` to suppress the native
    handling of that one gesture, every other click passing through untouched. **Suppression is only
    possible on this path** — that's why the default stays a mouse button.
  - **Hover path (everything else)** — `FilletTracker.Update()` polls
    `Input.GetKeyDown(Binding.Key)` and fillets the hovered thumbnail. Hover is tracked by a
    Harmony postfix on `ItemThumbnailPanel._OnHighlighted(bool)`, patched manually via
    `AccessTools.Method` name lookup so an absent method disarms the hover path (logged) instead of
    throwing out of `PatchAll` and killing the mod. The bool parameter indicates highlight (true)
    or un-highlight (false); un-highlight clears the recorded panel only on a pointer match, since
    highlight(B) can precede un-highlight(A) when sliding between thumbnails.
- **Fillet drive (`Fillet.Execute`)** is shared by both paths: temp-null the tool requirement, call
  `CommandHarvestItem()`, restore in a `finally`, with a `Busy` re-entrancy guard.
- **Qualifies(item):** native subclass check that the item is `ResourceInfo`-kind
  (`il2cpp_class_is_subclass_of`), `CanBeHarvested()` == true, `mainHarvestMoveset != null`, and
  `mainHarvestMoveset.requiredEqippmentCategory.Name` matches a configured tool category (default
  "Knives"). Result cached per item id. Generic — not hardcoded to any fish.
- **The drive in detail:** temporarily null `mainHarvestMoveset.requiredEqippmentCategory` (bare-hand
  so the harvest op's tool gate passes), call `panel.CommandHarvestItem()` (→
  `CharacterInventory.HarvestSelectedItems()`, the game's real harvest executor), then restore the
  tool req in a `finally`. The `Fillet.Busy` guard keeps the driven harvest from re-entering the
  click prefix.
- **Yield is the game's own** — because we drive `CommandHarvestItem`, the game produces the exact
  vanilla loot for each fish. Validated across the game's TWO loot models: Mackerel (whole-harvest,
  yield baked on the prefab) and Seabass (bit-harvest, yield only materializes
  post-`InitHarvestLoot`). Both filleted correctly with no hardcoded amounts.
- **PointerEventData / InputButton** come from interop `UnityEngine.UI.dll` (csproj reference).

## Config
- `[Fillet] EnableFilletInInventory` (default true) — master toggle.
- `[Fillet] HarvestToolCategories` (default "Knives") — comma-separated, case-insensitive
  tool-category names whose harvest tool requirement is unlocked in inventory. Only ResourceInfo
  items that are ALREADY harvestable and require one of these categories are unlocked; bare-hand
  items (thatch/reeds) are untouched. Safety valve if some fish type uses a different tool category.
- `[Fillet] FilletModifierKey` (default "Shift") — `Shift`/`Ctrl`/`Alt` accept either the left or
  right key; `None` requires no modifier; anything else is parsed as a Unity `KeyCode` name
  (e.g. `LeftAlt`, `CapsLock`).
- `[Fillet] FilletKey` (default "Mouse1") — ANY Unity `KeyCode` name: mouse buttons
  (`Mouse0` left, `Mouse1` right, `Mouse2` middle, `Mouse3`/`Mouse4` side buttons) or keyboard keys
  (`F`, `X`, `Delete`, …). `Left`/`Right`/`Middle`, `LMB`/`RMB`/`MMB` and `0`/`1`/`2` are aliases for
  `Mouse0/1/2`. Mouse0/1/2 fire on click and suppress the game's own click action; every other key
  fires on press while hovering the item and leaves clicks alone.

**Why it's rebindable:** a Nexus comment (2026-08-01) reported other ASKA mods binding inventory
right-click, so the two gestures collided. Moving the modifier (e.g. `Ctrl`), the button
(e.g. `Mouse2`), or off the mouse entirely (e.g. `F`) sidesteps the conflict without touching the
fillet drive.

## Why Shift+RMB by default (and why not Shift+LMB)
The design pivots on one in-game-confirmed fact: the cosmetic **"can't be harvested" toast is the
game's NATIVE Shift+LMB harvest router** failing its own knife check. Riding that gesture (the
earlier `OnCustomClickActionOverride` approach) filleted correctly but co-existed with the native
attempt's error toast — unavoidable because the router is native and fires independent of our hook.
**Shift+RMB never invokes that router**, so filleting through it is toast-free. Confirmed in-game
2026-07-08: Shift+RMB fillets cleanly, no toast, no container-move (container-move is plain
right-click and doesn't register with Shift held); Shift+LMB on a fish still shows the toast, but
that is pure vanilla — the mod doesn't touch left-click at its default binding.

Consequence for rebinding: configuring `FilletKey = Mouse0` with `FilletModifierKey = Shift`
re-enters that native router, so the fillet succeeds but the cosmetic toast comes back. The config
description says so; changing the modifier, or moving to a keyboard/side-button key, avoids it.

## Dead-ends (don't retry)
- **Postfix `CanHarvestCurrentItem → true`** flips only the cursor color; harvest execution is a
  separate gate. (v1.0.4)
- **Patch/null-tool on `CommandHarvestItem`** — it isn't called by the native harvest-cursor flow,
  so it never fires on the click. (v1.0.5)
- **Any `ItemThumbnailPanel` click-method as the Shift+LMB harvest trigger** — the working native
  harvest fires none of them; it calls `CharacterInventory.HarvestSelectedItems()` directly from
  the native cursor system. (v1.0.6 trace)
- **Persistent bare-hand** (permanently nulling `requiredEqippmentCategory`) — the native
  inventory-harvest still rejects fish for a reason beyond the tool req. (v1.0.8)
- **The `CanHarvestCurrentItem` cursor-flip was NOT load-bearing** for the RMB drive — removed in
  v1.1.1 cleanup and confirmed the fillet still works (the drive's temp-null makes the native check
  pass on its own).
- **`ItemThumbnailPanel.OnPointerEnter`/`OnPointerExit` as hover-tracking targets** — the panel
  does not declare these methods at all (Cecil-confirmed 2026-08-01). Hover reaches the panel only
  through `ItemHighlightBehaviour.OnHighlighted` and its `_OnHighlighted(bool)` subscriber. Cost:
  v1.4.0.

## Confirmed facts (don't re-derive)
- Fish = `SSSGame.ConsumableInfo : ResourceInfo`; `CanBeHarvested()`==true; `exhaustableComponents`
  EMPTY; yield flows via the `LootSpawner`/`GetPieceLoot` path;
  `mainHarvestMoveset.requiredEqippmentCategory` = 'Knives' (note the game's misspelling
  "Eqippment"). spawnObjects: `Item_Food_FishMackerel`, `Item_Food_FishSeabass`.
- Fish don't stack in inventory (no stack-fillet concern).
- Real harvest executor = `CharacterInventory.HarvestSelectedItems()` (→ `_HarvestItemOperation`),
  reachable via `ItemThumbnailPanel.CommandHarvestItem()`.
- Co-op authority: reuses the game's own networked harvest command (likely host-safe); a client
  filleting is UNVERIFIED as of shipping.
- `ItemThumbnailPanel._OnHighlighted(Boolean)` receives the inventory's hover events,
  confirmed in-game 2026-08-01 by the mod's own hover-live log line. Hover tracking reaches the
  panel via `SSSGame.ItemHighlightBehaviour : UnityEngine.UI.Selectable`, which exposes
  `OnHighlighted : Il2CppSystem.Action<bool>`. Which GameObject carries the
  `ItemHighlightBehaviour` is not established — the mod never needs to know, because it patches
  `_OnHighlighted` on the panel itself.

## Generality
Not hardcoded to Mackerel/Seabass — any `ResourceInfo` item that `CanBeHarvested` and requires a
configured tool category will fillet, with yield delegated to the game. Structural confidence it
covers all fish is high (three types observed: Mackerel, Seabass, and Perch, the latter filleted
successfully on 2026-08-01 through Shift+F binding, ten times in one run; failure mode is graceful
— a non-matching item simply doesn't fillet, no crash — and the tool-category config extends
coverage). Full-database census not run (chose to ship; any fish that doesn't fillet = add its
tool category to config or a quick follow-up).

## Version history (compressed)

- **v1.0–v1.1.1:** core filleting (Shift+RMB drive via `ItemThumbnailPanel.OnPointerClick`).
- **v1.2.0 (2026-07-21):** tool-category gate now matches invariant asset name
  (`Categ_Tools_Knives`) in addition to translated `.Name`, enabling filleting in every language;
  confirmed in-game in German.
- **v1.4.0 (2026-08-01):** fillet gesture rebindable to any Unity `KeyCode` via `FilletModifierKey`
  + `FilletKey` config. Added to resolve a reported inventory-right-click clash with other Nexus
  mods. Default gesture unchanged (Shift+Mouse1). Never released: hover path targeted
  `OnPointerEnter`/`OnPointerExit`, which `ItemThumbnailPanel` does not declare.
- **v1.5.0 (2026-08-01):** hover tracking moved to `ItemThumbnailPanel._OnHighlighted(bool)`, which
  is the actual subscriber to hover events. Now keyboard and side-button bindings are reachable.
  Rebindable gesture + hover path confirmed in-game 2026-08-01 on Perch with Shift+F binding.
