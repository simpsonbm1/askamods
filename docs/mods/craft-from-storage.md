# CraftFromStorageMod (Mod 28) — craft from settlement storage

**Status: Phase 1 (player half) feature-complete, confirmed in-game 2026-07-20 (v0.5.1). Phase 2
(villager half) NOT started — it is core scope, not an extra.** Origin: Nexus request from
rondi112 (2026-07-20). Plan entry: NEW_MOD_IDEAS_PLAN.md → idea 17. Subsystem facts:
docs/architecture.md → "Player crafting pipeline".

## What it does (Phase 1)
Crafting at a station pulls missing ingredients from any non-blacklisted settlement storage into
the player's inventory just in time, then returns anything unconsumed. The crafting menu's
per-ingredient `have/need` counts show settlement-wide totals so the numbers agree with the
now-enabled Craft button.

## How it works
Vanilla already consumes from `{agent inventory} ∪ {station composite}`, choosing by where the
items physically are. **The mod is a reach extension, not a new mechanism** — it puts the materials
somewhere vanilla already looks, then lets vanilla consume normally.

1. **Availability postfix** on `CheckOwnedRequirements`, gated to `PlayerInteractionAgent`, flips
   the gate true when a cached settlement snapshot covers the shortfall.
2. **Pull** on the `BeginCraftingSequence` prefix — consumption lands ~1.0 s later, so there is
   ample headroom. Records a ledger of (source container by world position, ItemInfo, qty moved).
3. **Fail-closed verify**: re-runs vanilla's own gate behind a reentrancy flag (without it, the
   mod's own postfix answers "yes" using storage not yet moved, so the check could never fail).
4. **Abort** if not satisfied: sweep the whole ledger back, cancel the craft, show an on-screen
   message. Letting a partially-supplied craft proceed would hand out a free or half-price item —
   `RemoveOwnedItemManifest` consumes only what is present.
5. **Sweep-back** on the `_OnCraftingSuccess` postfix:
   `sweepBackQty = clamp(packQtyAfterCraft − packQtyBeforePull, 0, ledgerQty)`. This returns the
   pack to its pre-pull level and never claws back the player's own stock. Worked example: player
   had 3, mod pulled 4 (pack 7), craft ate 1 (pack 6) → sweep 3, player keeps their original 3.
6. **Requirement UI** — a `CraftUiPoller` MonoBehaviour (0.2 s tick) rewrites each row's
   `have` number to `vanillaHave + max(0, settlementQty − activeStationQty)`.

## Config
| Key | Default | Notes |
|---|---|---|
| `Transfer/EnablePlayerPull` | `true` | Master switch for the whole Phase 1 behavior |
| `Transfer/SweepBackLeftovers` | `true` | Return unconsumed pulled items to their sources |
| `Transfer/SnapshotTtlSeconds` | `5.0` | Settlement stock snapshot cache lifetime |
| `Transfer/BlacklistContainerTypes` | see below | Never-drain source containers, by type name |
| `Transfer/TransferDiagnostics` | `true` | Per-pull / per-sweep logging |
| `UI/ShowSettlementStockInUI` | `true` | Master switch for the requirement-count rewrite |
| `UI/UiPollSeconds` | `0.2` | Poll tick; also the observed UI update latency |
| `UI/UiDiagnostics` | `true` | Scoping, hierarchy-dump and rewrite logging |
| `Census/CensusHotkey` | `F12` | Read-only settlement storage census dump |
| `Census/CensusTryQuerySettlementResources` | `false` | **Leave false** — that call hangs the game |

Blacklist default: `CharacterFlask`, `CharacterBuilder`, `ArmorRack*` family, `Storage_Core`,
`Storage_DecorationsTop`, `Storage_SmallItems_Outhouse`. It is belt-and-braces only — user-confirmed
2026-07-20 that armor racks hold finished products and no ASKA recipe consumes finished gear as an
input, so the racks are not a real drain risk.

## Dead-ends and traps (all confirmed in-game 2026-07-20)
- **`Settlement.QuerySettlementResources()` HANGS the game** (`AppHangB1`; no managed rescue). Use
  the `GetStructures()` walk.
- **`_OnCraftingSuccess` IS the consumption site** — the ~3–6 ms between its prefix and postfix.
  Snapshotting only `CraftInteraction.ItemInventory` reads PRE==POST whenever the craft draws from
  the agent side, which makes the site look absent; watch both collections.
- **A postfix on `_UpdateAvailablility` / `_UpdateAvailabilityStatus` cannot see the requirement
  text.** At postfix time the label is still the prefab placeholder `"99"`; the real `0/2` is
  written later by other code. Poll instead — no field-name guess can fix a timing problem.
- **`ItemThumbnailPanel.availability` is always null and `checkAvailability` always False** on
  requirement rows. Neither identifies a requirement row. The row's number lives at
  `.../ItemThumbnailMaterial_Medium/Fitter/Quantity`.
- **The details/preview panel's subtree CONTAINS the material rows** (`Quantity` at depth 5), so a
  naive subtree walk from it resolves another row's label and rewrites it with the wrong item's
  stock. Stop descending at any node owning its own `ItemThumbnailPanel`, and cap the walk depth.
- **An unanchored `\d+/\d+` match hits `"Durability 100/100"`.** Selection must require the whole
  detagged string to be the pair.
- **A poller that re-reads its own output compounds** (`0/2` → `20/2` → `40/2`). Record the exact
  string written per label; skip byte-identical text; recompute only when vanilla overwrites it.
- **The EquipPoint structural probe tagged 0 of 651 containers** — blacklist by container type.
- **Vanilla's displayed `have` already includes the station's own storage**, and the settlement
  snapshot walks that same station, so the station quantity must be netted out or every row
  inflates.

## Known limits
- **Host/solo only** (`IsHostOrSolo()` gates the availability, pull and sweep paths); non-host
  clients fall back to vanilla gating, failing closed. Multiplayer client support is requested.
- No timeout for a craft abandoned after the pull — self-heals on the player's next craft via the
  stale-ledger sweep, or the items simply stay with the player on world-leave.
- A rewritten requirement row holds its value until vanilla itself repaints the row.
- The ledger is one flat list, so two players pulling in overlapping windows could cross-attribute
  (single-player unaffected).
- Diagnostics all default `true` — flip before any public release.

## Version history
- **v0.5.1** — nested-panel boundary + depth cap on the label walk (details panel could otherwise
  rewrite a material row). UI confirmed in-game.
- **v0.5.0** — requirement-UI write moved from the postfix to `CraftUiPoller`; strict whole-string
  have/need selection; idempotency guard.
- **v0.4.0–v0.4.3** — requirement-UI feature plus the diagnostics that located the label
  (fire-verification, scoping evidence, hierarchy dump).
- **v0.3.2** — zero-pull no longer skips the fail-closed verify; confirmed in-game.
- **v0.3.0/v0.3.1** — Phase 1 pull/verify/sweep-back; agent gate added to the sweep path so a
  villager craft cannot consume the player's ledger.
- **v0.2.0** — craft delta watcher + census v2; resolved the consumption site.
- **v0.1.x** — read-only diagnostic spike (gate trace + storage census).
