# TerrainLevelerMod — "Bulldozer" Build-Menu Entry: Design & Implementation Plan (2026-07-01)

**Goal:** Stop overriding the vanilla Terrain Level Field. Instead add a **4th square** to the
TERRAFORMING build category (bulldozer icon) that places a **separate, mod-behavior field**:
- Vanilla "Terrain Level Field" square → 100% vanilla behavior (vanilla drag range, vanilla
  validation/red states, incremental hoe leveling).
- New "Bulldozer" square → the mod's behavior (20 m drag, place over obstacles, 256-tile cap,
  one-hit flatten + obstacle-clearing blast on E).

**Status: FEATURE COMPLETE — confirmed in-game (2026-07-01) at v1.4.7; shipped v1.4.8.** All runbook
steps landed: v1.4.5 = 4th square appears/places/flattens; v1.4.6 = Phase 2 per-template gating
(vanilla square fully vanilla again: native range, 25-tile cap, red-when-obstructed, incremental
leveling — mod behavior only on the bulldozer square) + first-open fix (additionalInfos append moved
to the Init prefix); v1.4.7 = localized name/desc/lore via LocalizationManager dictionary injection;
v1.4.8 = diagnostics default off + dead-diag cleanup. Every step user-verified in-game 2026-07-01.
The failure/fix chain below is kept because it produced several project-wide IL2CPP gotchas (now in
`docs/architecture.md`, including the new "Build Menu / Structure Templates & Localization" section).

---

## How the build menu actually works (verified from decompile, 2026-07-01)

Decompile source: Cpp2IL diffable-cs (regenerate per Appendix A of `DRAG_CRASH_PLAN.md`; add
`callanalyzer` before `attributeinjector` in `--use-processor` to get `[Calls]` annotations).

1. **Templates are ItemInfos.** Inheritance: `DynamicDimensionTemplate : NodeTemplate :
   StructureTemplate : BlueprintInfo (SSSGame wrapper) : SandSailorStudio.Inventory.BlueprintInfo :
   ItemInfo : AssetNode : ScriptableObject`. The "Terrain Level Field" entry is a
   `DynamicDimensionTemplate` asset.
2. **`ItemInfo` carries the identity + skin** (`SandSailorStudio/Inventory/ItemInfo.cs`):
   `int id`, `ushort databaseIndex`, `ItemCategoryInfo category` (→ which tab it appears under),
   `string localizedName` (+ `bool _localized` / `Localized` property; `Name` resolves through a
   localization lookup when `_localized` is true), `Sprite icon`, `Sprite previewImage`.
3. **Registry:** `ItemInfoDatabase` (MonoBehaviour) holds `ItemInfoList CompleteItemInfoList`
   (ScriptableObject with a plain `List<ItemInfo> itemInfoList`) and builds
   `_itemsMap (Dictionary<int, ItemInfo>)` + `_categoryMap (Dictionary<ItemCategoryInfo,
   List<ItemInfo>>)` in `Initialize()`. Lookup APIs: `GetItemInfoFromID(int)`,
   `GetItemInfo(string itemAssetName)`, `GetItemInfoFromIndex(ushort)`.
   `ItemDatabaseManager` (a `GameManagers` singleton) owns the database + a serialized
   "user items" `ItemCollection` (`GetUserItems()`, `Seed()`, BSON `Serialize`/`Deserialize`).
4. **The menu squares come from an ITEM COLLECTION, not the database directly.**
   `SSSGame.UI.BuildItemsTabPage.ShowPage` (call-analyzer verified) does:
   `TabButtonCategory.GetCategoryItemFilter()` → `InventoryComponent.GetItemCollection()` →
   **`ItemCollection.GetAllItems(IItemFilter)`** → `_SetupNodeTemplate(NodeTemplate)` per result.
   I.e. each unlocked buildable exists as an `Item` whose info IS the NodeTemplate, inside some
   collection (the page also has a `PlayerInventory _pi` field; `ItemDatabaseManager.GetUserItems()`
   is the other candidate). **Which exact collection instance it is = Phase 0 diagnostic.**
5. **Category tab:** `TabButtonCategory.categoryInfos : List<ItemCategoryInfo>` filters by the
   info's `category` — a clone keeps the original's `category`, so it lands in TERRAFORMING
   automatically.
6. **Placed structures know their template:** `Structure.Template : StructureTemplate` and
   `Structure.TemplateID : int` (also serialized into saves as `templateID`/`uTId` — see Risks).
7. **Unlocks are computed live, not possessed** (call-graph verified): `NodeTemplate.IsUnlocked()`
   just runs `BlueprintConditionsRule.CheckWithData()` each call, and `OnUnlock` only shows a
   notification (`NotificationMenu.DisplayImportant`) — no item is granted on unlock.
   `ItemDatabaseManager.Seed()`/`Save()` are **empty stubs** (deduplicated no-op bodies), so the
   menu's item collection is NOT seeded from the info database either.
8. **`InventoryComponent`** (`SandSailorStudio/Inventory/InventoryComponent.cs`) holds
   `ItemCollection items` + an authored `ItemInfoList startItems` seed — the buildables catalog is
   an authored collection somewhere; which component owns it is discovered at runtime (see grant
   design below). `ItemCollection.AddItems(ItemInfo, int quantity = 1, …)` creates + adds an item
   directly from an info; `GetFirstItem(ItemInfo)` makes grants idempotent.
9. **Icon loading is possible but NOT USED** (user decision: reuse vanilla icon):
   `UnityEngine.ImageConversionModule.dll` exists in `BepInEx\interop\` →
   `Texture2D` + `ImageConversion.LoadImage(bytes)` + `Sprite.Create`, if custom art is ever wanted.

---

## Architecture of the feature

**A. Clone + register (boot):** Harmony **prefix on `ItemInfoDatabase.Initialize()`**:
find the vanilla `DynamicDimensionTemplate` in `CompleteItemInfoList.itemInfoList` (scan for the
entry castable to `DynamicDimensionTemplate` — expected to be unique; Phase 0 logs all candidates),
then `UnityEngine.Object.Instantiate(original)` (plain, non-generic — ScriptableObject clone),
re-skin it (below), append to `CompleteItemInfoList.itemInfoList`, and let the original
`Initialize()` build `_itemsMap`/`_categoryMap` with the clone included. One injection point, all
maps consistent. Guard against double-injection (check the list for our id first — `Initialize`
may run more than once per session).

Clone re-skin:
- `name` (Unity asset name) = `"TerrainLevelField_Bulldozer"` (used by `GetItemInfo(string)`).
- `id` = a fixed constant, e.g. `919191001` — **must be stable forever** (saves reference it) and
  collision-checked at inject time (log error if taken; realistically won't be).
- `databaseIndex` = its appended position in the list (`(ushort)(list.Count - 1)` before append →
  verify with `GetItemInfoFromIndex` in diag).
- `localizedName` = `"BULLDOZER FIELD"` and try `Localized = false` so `Name` returns the raw
  string (⚠️ pending in-game check — if it shows blank/key-gibberish, fall back to leaving the
  vanilla localization untouched, i.e. same display name as vanilla, and revisit).
- `icon` / `previewImage` = custom sprite (Phase 1b; vanilla sprites as interim fallback — the
  entry is still distinguishable by position/name).
- `showUnlockNotification = false` (avoid a spurious "new structure unlocked!" popup).
- Do **NOT** touch `nodeStructure`/`nodePreview`/`ResultStructureTemplate` — the result structure
  stays the vanilla one, so completed/flattened plots remain 100% vanilla-safe in saves.

**B. Menu visibility (self-locating item grant) — AS IMPLEMENTED in v1.4.0:** the menu lists items
from a collection, so an `Item` of the clone info must exist there. Instead of hunting for the
owning component, the grant self-locates: a postfix on `BuildItemsTabPage.ShowPage` arms a 5-second
window; while armed, a postfix on `InventoryComponent.GetItemCollection` inspects each collection
the menu queries — if it contains the **vanilla** field's item (`GetFirstItem(vanillaTemplate)` ≠
null ⇒ this is the buildables catalog AND terraforming is unlocked) and lacks ours, it calls
`AddItems(clone, 1)` once. Idempotent, rides the vanilla unlock gate for free, and the diag logs
name every component the menu touches (so if `AddItems` is refused — e.g. `canAddItems` false —
the log tells us exactly where to graft instead).

**C. Behavior gating (re-route existing patches per-template):**

| Existing patch | New behavior |
|---|---|
| `Begin` prefix (maxRange 20, maxHeightDifference 10000) | Only when the drag is a bulldozer field: `previewData` param → cast to `DynamicDimensionStructurePreviewData` → `.DynamicTemplate` id == clone id. Set a static `_bulldozerDrag` flag here; clear it in an `End` postfix. Vanilla template → don't touch the tool. |
| `_OnBuildRayChanged` prefix (maxHeightDifference) | Only if `_bulldozerDrag`. |
| `_ValidateFootprint` always-true bypass | Only if `_bulldozerDrag` (restores vanilla red-when-obstructed for the vanilla square). The overflow crash it prevents only occurs on our oversized grids. |
| `CheckLevelDifference` always-true bypass | Only for bulldozer grids: `TerraformingGrid` → owning `Structure` (`GetComponentInParent<Structure>()` or grid's structure link) → `TemplateID == clone id`. |
| `CreatePreview`/`Create` maxNumberOfTiles=256 | Only when `__instance` (the template) id == clone id. Vanilla keeps its native cap (believed 100 — Phase 0 logs it). |
| `Use` postfix (flatten + bomb) | Only when the interaction's structure `TemplateID == clone id`. |
| `ChangeGridSize` tile clamp + `UpdateGridValidityOnNetwork` safety nets | **Keep unconditional** — pure crash prevention for any dynamic building; vanilla sizes never trip them. |
| `_OnSnap` / `OnDynamicBuildingDimensionsChanged` >256 backstops | Keep unconditional (same reason). |

---

## Phases

### How the menu ACTUALLY selects the hoe squares (ground truth, confirmed v1.4.4/v1.4.5)
The v1.4.0–v1.4.4 iterations proved, by elimination with in-game logs, that NONE of the obvious
layers select the squares:
- clone in `CompleteItemInfoList` ✓, in `_itemsMap` (GetItemInfoFromID resolves) ✓, in
  `_categoryMap['Categ_Blueprints_Structures']` (which holds **75** structures — the hoe tab shows
  **3**!) ✓, blueprint item granted & present in the player collection ✓ — and still no square.
- **The hoe tab is a `TabButtonCategory` whose `additionalInfos` is an EXPLICIT list of the 3
  terraforming templates** — that's the real gate. `TabButtonCategory` implements `IItemFilter`;
  its `Check` combines category ids with `additionalInfos`/`excludedInfos`. Appending the clone to
  the tab's `additionalInfos` (v1.4.5, done in the `Show` postfix via the `button` param) made the
  square appear. **This is the load-bearing step; everything DB-side was necessary but not
  sufficient.**

### New IL2CPP gotchas learned here (→ architecture.md at the commit checkpoint)
1. **Managed `as`/`is` casts LIE for interop objects materialized under a base declared type**
   (e.g. elements of `List<ItemInfo>`): the wrapper's managed class IS the declared type, so
   `info as DynamicDimensionTemplate` returns null even when the native object is one. Identify by
   asset `name`/`id` (or native class name) and construct the derived wrapper via the generated
   `new T(IntPtr)` ctor. For runtime type tests use
   `IL2CPP.il2cpp_object_get_class(ptr)` + `il2cpp_class_get_name` (see `GetNativeClassName` in
   Plugin.cs).
2. **Inlining strikes small/single-caller members constantly** — confirmed victims this feature:
   `BuildItemsTabPage.ShowPage` (private, 1 caller), `InventoryComponent.GetItemCollection` and
   `ItemCollection.GetAllItems(IItemFilter)` at the menu callsite, `TabButtonCategory._OnSelect`
   (postfix never fired despite being virtual-declared). **Patch virtual/vtable-dispatched methods**
   (`Init`, `Show`) or multi-caller publics, and ALWAYS fire-verify with a log line.
3. `ItemInfoDatabase.Initialize` **re-sorts/re-indexes** `CompleteItemInfoList` (vanilla field's
   `databaseIndex` moved 0→483 between calls) and runs multiple times per session — injection must
   be idempotent (re-find by id+name).
4. Items whose `ItemInfo.id` is unknown at load appear to be **silently dropped** from serialized
   collections (the granted item survived a save/load in one run — with injection running before
   world deserialize — and simply vanished in another) → mod-removal is save-safe for granted items.
5. `Localized = false` does NOT make `Name` return the raw `localizedName`: the UI still routes
   through the localization lookup and displays the KEY (screenshot evidence:
   `ITEM.BLUEPRINTS_TERRAINLEVELFIELD_BULLDOZER_NAME` — note the key template embeds the clone's
   asset name and a `Blueprints_` segment that does NOT come from the asset name; derivation is in
   `GetKey(token)`). Custom display strings need entries injected into the game's localization
   table (see runbook).
6. Useful runtime facts: vanilla terrain-field template = asset `Item_Structures_TerrainLevelField`,
   **id 12664862**, vanilla `maxNumberOfTiles` **25** (docs previously said 100 — wrong), category
   `Categ_Blueprints_Structures`; blueprint items live in the PLAYER's inventory collection
   (component on `CharacterRagnar(Clone)`, `startItems='StartItems_Aska_Vanilla'`).

### Phases 0+1 — COMBINED & IMPLEMENTED (v1.4.0, built + deployed, awaiting in-game run)
One build carries both the diagnostics and the injection, self-configuring from runtime discovery
(no hardcoded vanilla identity needed). All logging behind `PlacementDiagnostics` (default true for
this phase per the diagnostics rule):
1. **Prefix `ItemInfoDatabase.Initialize()`** (`ItemInfoDatabase_Initialize_Prefix`): logs every
   `DynamicDimensionTemplate` (name/id/dbIndex/maxTiles/category), collision-checks
   `BulldozerTemplateId` (919191001 — NEVER change once shipped), clones the vanilla template
   (`Object.Instantiate` + `as` cast), re-skins it (asset name `TerrainLevelField_Bulldozer`,
   id, `databaseIndex = list position`, `Localized=false` + raw name "Bulldozer Field" + raw
   description, `showUnlockNotification=false`, vanilla icon/category/result kept), appends to
   `CompleteItemInfoList.itemInfoList` before the original builds its maps. Idempotent across
   re-Initializes (re-finds the clone by id+name); aborts safely on any anomaly.
2. **Grant** (`BuildItemsTabPage_ShowPage_Postfix` + `InventoryComponent_GetItemCollection_Postfix`):
   as described in section B above.
3. **Use-time key probe**: the existing `Use` postfix now logs
   `structure.TemplateID` + `Template.name` (`[Diag:Use]`) — the Phase 2 gating key.
Success = a 4th square appears in TERRAFORMING named "Bulldozer Field" (vanilla icon), placeable,
its field flattens on E (behavior still global). The log answers every remaining unknown either way.

### Phase 2 — Gate behaviors per the table above
Vanilla square must now behave fully vanilla. **Fire-verification is mandatory** for the new
`Begin`-param and template-id paths (log which branch each patch takes) — the IL2CPP inlining
precedent means silent non-firing is possible; if `Begin`'s `previewData` route fails, fall back
to keying off `_currentPreviewData.DynamicTemplate` inside each tool prefix.

### Phase 3 — Test matrix (user, in order)
1. Loaded version check (SAC — bump version every build).
2. Vanilla square: drag past vanilla range → vanilla red/stop (no 20 m reach); place on obstacle →
   red; E-press → vanilla incremental leveling, no bomb, no instant flatten.
3. Bulldozer square: 20 m drag, placeable over trees/rocks, grid caps at 256 tiles, E → full
   flatten + obstacle clear + UI dismiss.
4. Both squares coexist; save + reload with an unfinished bulldozer field → still present and
   functional; its completed plot is vanilla.
5. `[Diag]` lines confirm every gate takes the intended branch for both template ids.

### Phase 4 — Ship checklist (commit-gated rituals)
- Flip `PlacementDiagnostics` default back to `false`.
- Version → v1.4.x final; update BOTH `CLAUDE.md` + `.agents/AGENTS.md` (status line → feature),
  `docs/mods/terrain-leveler.md` (new UI-gated design + template-injection recipe), and
  `docs/architecture.md`: **add the build-menu/ItemInfo pipeline facts** (templates are ItemInfos;
  ItemInfoDatabase/CompleteItemInfoList structure; menu reads an ItemCollection via
  BuildItemsTabPage; Structure.Template/TemplateID; clone-inject recipe + whatever Phase 0
  corrected) — this is a whole new subsystem section ("Build Menu / Structure Templates").
- Add this file + any new findings to both Documentation Maps. Commit+push with user go-ahead.

---

## Risks & caveats
- **Save-format exposure:** a *placed, unfinished* bulldozer field serializes `templateID` = our
  custom id. Loading that save without the mod ⇒ that one structure fails to resolve (likely
  silently dropped). Exposure is small (fields live seconds before being flattened) but document it
  in the mod doc. Completed plots use the vanilla result template — zero exposure.
- **Co-op:** `databaseIndex` (ushort) smells like a network item reference — both players should run
  the same mod version. Same standing assumption as the rest of the mod.
- **Localization:** `Localized=false` raw-name display is ⚠️ unverified; have the fallback ready.
- **Menu source uncertainty:** if Phase 0 shows the collection isn't reachable/grantable, fallback
  injection point: postfix `BuildItemsTabPage.ShowPage`/`_SetupNodeTemplate` to append our button
  manually (uglier; only if needed).
- Standing gotchas apply: no `Action` subscriptions (use the polling tracker), never patch
  `Copy*State` methods, never patch by-ref-primitive signatures
  (`PlacementGrid.SetDimensions(Int32&, Int32&)` is off-limits). ⚠️ The old note that "C# `as`
  casts work on interop types" is now known to be WRONG for base-declared materializations — see
  gotcha #1 above.
- Don't touch the v1.3.22 crash fixes while refactoring — they ship as-is, unconditional.

---

## NEXT SESSION RUNBOOK — ✅ ALL STEPS DONE (v1.4.6–v1.4.8, confirmed in-game 2026-07-01)

Kept for the record; see the status block at the top for what shipped in which version. One extra
build-time gotcha from Step 1: `LocalizationManager._allKeys` is a **static** member and its
`HashSet<string>` type needs a project reference to `Il2CppSystem.Core.dll`.

### Step 1 — Localized display strings for the 4th square
The UI displays `item.Blueprints_TerrainLevelField_Bulldozer_{name,desc,lore}` keys (screenshot
2026-07-01). Approach: find the game's localization store and inject 3 entries at load.
- Investigate `LocalizationManager` (`Loc(string, bool)` / `Loc(string, Object[])`, called by
  `NodeTemplate.OnUnlock` etc.) in the Cpp2IL dump — look for its dictionary field(s) and when
  they're populated; add our 3 keys after population (or postfix its lookup to special-case our
  keys — less clean, only if the dictionary is unreachable).
- First log `clone.GetKey("name")` and `vanilla.GetKey("name")` at runtime to confirm exact key
  strings (the `Blueprints_` segment is not from the asset name — don't guess the template).
- Fallback if localization proves nasty: rename the clone asset so its keys COLLIDE with vanilla's
  (shows "TERRAIN LEVEL FIELD" texts on both squares — cosmetically confusing but shippable), and
  revisit later. NOTE: the asset `name` currently also drives our injection idempotence check and
  the key derivation — if renaming, keep `id`-based re-find logic intact.

### Step 2 — First-open UX
The `additionalInfos` append currently happens in the `Show` postfix (page already built), so the
first open shows 3 squares and the rebuild lands on the next open. Move the append earlier: `Init`
has the same `button` param — do the native-class check + append in the **Init PREFIX** (keep the
Show path as self-repair). Expected result: square present on first open.

### Step 3 — Phase 2 behavior gating (the actual goal)
Follow the gating table in "Architecture C" above with these now-confirmed keys:
- Bulldozer id = `919191001` (`Plugin.BulldozerTemplateId`), vanilla id = `12664862`.
- Use-time: `interaction.GetComponentInParent<Structure>().TemplateID` (probe already in the Use
  postfix, `[Diag:Use]` — fires and reports correctly).
- Placement-tool time: `Begin(previewData)` → preview's `DynamicTemplate.id`, or set a static
  `_bulldozerDrag` flag in `Begin` / clear in `End`.
- `maxNumberOfTiles`: **set `clone.maxNumberOfTiles = 256` once at injection** and DELETE the
  `CreatePreview`/`Create` prefixes (they currently mutate the SHARED vanilla asset to 256 for the
  whole session — that's why square 1 can also drag past vanilla's 25-tile cap; removing them
  restores vanilla's native red-at-25 for square 1 for free).
- Keep unconditional: `ChangeGridSize` clamp, `UpdateGridValidityOnNetwork` nets, `_OnSnap` +
  `OnDynamicBuildingDimensionsChanged` 256 backstops (crash prevention, not behavior).

### Step 4 — Cleanup (same build as Step 3 or after confirmation)
- DELETE dead diagnostics that never fire (inlined): `ItemCollection_GetAllItems_Postfix`,
  `TabButtonCategory_OnSelect_Postfix`. Demote/remove the thumbnail logger and the
  `GetItemCollection` window logger once gating is confirmed.
- Keep: injection prefix, grant paths (window + Show/_pi), additionalInfos append, DB audit
  (cheap, runs once), rebuild-on-change.
- Flip `PlacementDiagnostics` default → false at ship.

### Step 5 — Test matrix, then ship rituals
Per Phase 3 table earlier in this doc (vanilla square must red-out at vanilla range/25 tiles, no
bomb, incremental leveling; bulldozer square = full mod behavior; save/load with unfinished
bulldozer field). Then the Phase 4 ship checklist (docs dual-write incl. the new gotchas above,
`docs/mods/terrain-leveler.md` update, version, commit+push with user go-ahead).
