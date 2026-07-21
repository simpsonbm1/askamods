# LocaleAuditMod (Mod 29) — locale-invariant identity probe

**Status: DEV TOOL v0.3.0, NOT for Nexus. Confirmed in-game (2026-07-21).**

**GOAL:** for every game entity our mods identify by its *translated* display string, print the
*locale-invariant* identity sitting next to it, so those gates can be retargeted to something that
matches in every language. One in-world key press produces the whole mapping table.

## Why it exists

Five shipped mods gate behaviour on a display name that resolves through `LocalizationManager`, so
they match only in English. Two were reported broken by non-English players on Nexus
(OuthouseComposter, FishFillet); the other three were found by audit and have never been reported.

## The identity rule (Cecil-confirmed 2026-07-21, compile-verified)

| Entity | TRANSLATED (do not gate on) | INVARIANT (gate on this) |
|---|---|---|
| `ItemInfo` | `.Name`, `.Description`, `.Lore` | `.name` (asset), `.id`, `.storageClass.name` |
| `ItemCategoryInfo` | `.Name`, `.Description` | `.name` (asset), `.id` |
| `CreatureDataSheet` | `.localizedName` | `.name` (asset), `.faction` (enum) |
| `Structure` | `.DefaultName`, `Template.Name` | `Template.name`, `.TemplateID`, `gameObject.name` |

The tell for a translated member is the `Loc()` / `GetKey()` / `Localized` / `localizedName` cluster
on the declaring type. `ItemInfo`, `ItemCategoryInfo` and `CreatureDataSheet` all reach
`AssetNode : UnityEngine.ScriptableObject`, which is what gives them a lowercase `.name`.

Reading lowercase `.name` off a ScriptableObject through interop is an applied pattern, not a new
one — `OuthouseComposterMod/OuthouseGate.cs` already reads `info.storageClass.name` in its seed
gate, which is exactly why seed acceptance works in every language while its food gate does not.

## What it dumps

Sections written to `LogOutput.log` on the audit key:

- **TARGETS** — each English token the affected mods match today, its match count in the current
  language, and the invariant replacement. A `0` count names a token that is dead in that language.
- **CATEGORIES** — deduped `id` / asset name / localized name. Short; most gates key on this.
- **ALL ITEMS** — per item `id`, asset name, localized name, category triple, storage class.
- **STRUCTURES** — one row per structure *template*: `TemplateID`, template asset name, localized
  default name, custom name, GameObject name.
- **CREATURES** — datasheet asset name, localized name, faction, for creatures encountered so far.

## Config reference (`com.askamods.localeaudit.cfg`)

| Key | Default | Meaning |
|---|---|---|
| `AuditHotkey` | `F5` | Dump key. F5 is the only function key not already claimed in this repo. |
| `DumpAllItems` | `true` | Full per-item table. TARGETS/CATEGORIES print regardless. |
| `CaptureCreatures` | `true` | Wires the `Creature.Spawned` postfix. False skips the patch entirely. |
| `MaxTargetExamples` | `12` | Rows printed per probe token. Match counts are always reported in full. |

## How to run

Load a world, press **F5**, send `LogOutput.log`. Creature rows accumulate as creatures spawn, so
walking past a few enemies before pressing F5 gives that section more coverage.

The run's language does not matter for harvesting asset names — they are identical in every
locale. An English run additionally confirms which asset name each current config token maps to;
a non-English run additionally demonstrates the `0`-match failure directly.

## Design notes

- **Creature capture point:** `Creature.Spawned()` — virtual and zero-argument. Creature
  datasheets have no global registry (they appear only inside `PopulationManager._densMap` and a
  deeds condition, neither enumerable), so they are recorded as they spawn. The zero-parameter
  signature keeps this clear of the inventory-family parameter crash family.
- **Only managed strings are retained.** The capture copies asset/localized/faction strings out
  immediately and never stores the `Creature` or `CreatureDataSheet` wrapper, so the stale-wrapper
  native-crash family cannot apply.
- **Item registry (v0.3.0 correction):** the full item database is
  `ItemInfoDatabase.CompleteItemInfoList.itemInfoList` (a `List<ItemInfo>`), reached via
  `UnityEngine.Object.FindAnyObjectByType<ItemInfoDatabase>()` or `ItemDatabaseManager.Database`.
  **`ItemInfo.s_itemInfoList` is NOT the database** — it is a small transient working list (a
  probe build read only 8 unrelated items from it) and must not be used for the full item set.
- Settlement resolution uses `GetPlayerSettlement()` / `GetCurrentSettlement()` / `worldSettlement`,
  since `SettlementManager.settlements` stays null even in a loaded world.

## Version history

- **v0.3.0 (2026-07-21)** — item enumeration fixed to use the real registry
  `ItemInfoDatabase.CompleteItemInfoList.itemInfoList` (prior v0.2.0 used
  `ItemInfo.s_itemInfoList`, a transient working list that missed the vast majority of items);
  confirmed in-game in a German session with structures and creatures working on first build.
- **v0.2.0** — fire-verification on the creature patch: the CREATURES header reports `Spawned`
  invocation count and datasheet-read failures, so an empty section distinguishes "patch never
  fired" from "no creatures encountered" from "datasheet unreadable".
- **v0.1.0** — items, categories, structure templates, creature capture, TARGETS resolution.
