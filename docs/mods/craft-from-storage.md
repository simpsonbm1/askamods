# CraftFromStorageMod (Mod 28) — craft from settlement storage

**GOAL:** crafting at a station draws on materials sitting in ANY non-blacklisted settlement
storage — counted as available AND actually consumed — instead of requiring them to already be in
the station's bin or the crafter's inventory. Applies to the **player** AND to **villager
crafters**, one independent config toggle each. For villagers this means **deleting the supply
walk**: the villager crafts immediately rather than hauling materials to her station first.
(This line is quoted into `SESSION_HANDOFF.md`'s `## GOAL GROUNDING` section — see CLAUDE.md.)

**Status: Phase 1 (player half) feature-complete, confirmed in-game 2026-07-20 (v0.5.1). Phase 2
(villager half) IN PROGRESS — v0.8.0 built, ⚠️ not yet run in-game.** Origin: Nexus request from
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

## Phase 2 — the villager half (in progress)
Villager crafts run the **same pipeline** as the player's (confirmed in-game 2026-07-21):
`BeginCraftingSequence` fires with session `VillagerCraftSession` / agent `Villager`, and
`_OnCraftingSuccess` consumes ~865 ms later from the **station** collections while the crafted
output goes to the villager. So Phase 2 reuses Phase 1's four points rather than inventing a
mechanism — v0.7.0 added a `Villager` branch to each, pulling into the **station inventory**
instead of the agent's, with the ledger re-keyed per agent (7 concurrent villager crafts observed).

**What is still missing: the walk.** The villager fetch quest is scheduled independently of the
craft gate, so widening availability alone changes nothing (see dead-ends). v0.8.0 attacks that by
suppressing the fetch quest itself — ⚠️ built, not yet run in-game.

**Phase 2 diagnostic instrument (v0.6.0, keep enabled while Phase 2 is open):** five read-only
postfixes on `FSM_FetchCraftingSupplies.OnStateEnter`/`.OnStateExit`,
`FSM_UseCraftingStation.OnStateEnter`, `FSM_ReturnCraftingSupplies.OnStateEnter` and
`CrafterFetchQuestData.IsWhitelistedByStorage`. Its `[CFS-P2] CYCLE SUMMARY villager=<name>
verdict=DIRECT|TOURED … modPulls=<n>` line is the success metric. **Baselines to beat (villager
Alva): TOURED at 46.1 s / 21.3 s (v0.6.0) and 42.7 / 45.3 / 20.2 / 28.8 s (v0.7.1).**

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
- **✗ `CheckOwnedRequirements` does NOT schedule or suppress the villager fetch quest** (confirmed
  in-game 2026-07-21, v0.7.1). The availability postfix widened the gate **5619 times** for
  villagers in one session and behavior was unchanged — Alva still toured 4× at 42.7/45.3/20.2/
  28.8 s, matching her pre-change baseline. `modPulls=0` on every cycle, because she walks first
  and arrives stocked, leaving no shortfall by craft time. **Widening availability is necessary
  but nowhere near sufficient for the villager half.** The fetch is driven by the station's own
  supply manifests (`CraftingStation._minimumFetchManifest`, `GetMinimumFetchManifest()`,
  `FetchRequirementManifest`, `Workstation.GetItemsNeededFromSettlement()`) consumed via
  `CrafterFetchQuest.GetNeededSuppliesManifest()`.
- **✗ Widening the fetch-REACH fields is pointless** (confirmed in-game 2026-07-21).
  `FSM_FetchCraftingSupplies` is already permissive at runtime — `searchStorages=True`,
  `storageSearchRange=100`, `worldSearchRange=20`, `searchWorld=True`, `maxSearchDepth=0` — and
  villagers already probe 144–149 distinct containers per cycle across ~16 building types.
  Vanilla villagers reach settlement storage fine; the cost is the WALK, not the reach.
- **The FSM state actions are SHARED ScriptableObjects** (confirmed in-game 2026-07-21): one
  instance pointer served all five villagers across 231 state entries
  (`FSM_QuestAction : vStateAction : UnityEngine.ScriptableObject`; per-villager data lives in
  `QuestData` via `FSM_QuestAction.GetQuestData`). Any field write on one applies settlement-wide.
- **⚠️ Never patch the fetch-depth methods** — `CraftingStation.GetFetchDepth(ItemInfo, Int32&)`,
  `GetPersonalFetchDepth(Villager, ItemInfo, Int32&, Int32&)`, `CraftingQuest.TryGetFetchDepth`.
  All take by-ref primitives, the project's known-fatal trampoline-NRE family. Read, never detour.
- **Cecil cannot answer "who calls this" for this game** — interop method bodies are native
  trampolines (`Workstation`: 138 methods, 3153 IL instructions, **2** game-to-game calls). Use
  Cpp2IL or a runtime probe; see architecture.md → IL2CPP interop gotchas.
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
- **v0.8.0** — Phase 2 lever 2: suppress the crafter fetch quest. Postfixes `GetPriority(QuestData)`
  on `CrafterFetchQuest` AND `CrafterSpecificFetchQuest` (the subclass re-declares it), setting
  `Transfer/FetchQuestSuppressedPriority` (default −1000) **only when the cached settlement
  snapshot covers the entire needed-supplies manifest** — a villager needing something the mod
  cannot supply stays free to fetch. `GetNeededSuppliesManifest()` is CALLED, never patched
  (`ItemManifest` return = the risky family). Logs vanilla priority values so the unknown priority
  scale is learned whichever way the run goes. Also rate-limits the Point A villager line
  (5619 → a few + rollup). ⚠️ Not yet run in-game.
- **v0.7.1** — per-villager correlation: `villager=<name>` on every `[CFS-V]` line, plus
  `modPulls=`/`modItemsPulled=` appended to the `[CFS-P2] CYCLE SUMMARY` line.
- **v0.7.0** — Phase 2 lever 1: `Villager` branch on all four Phase 1 points under
  `Transfer/EnableForVillagers`; villager pulls land in the station inventory; ledger re-keyed to
  `Dictionary<IntPtr, List<LedgerEntry>>` (7 concurrent villager crafts observed). Confirmed
  in-game to be **insufficient on its own** — see dead-ends.
- **v0.6.0** — read-only Phase 2a villager-fetch diagnostic spike (five postfixes; the
  `verdict=DIRECT|TOURED` cycle summary that is now the Phase 2 success metric).
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
