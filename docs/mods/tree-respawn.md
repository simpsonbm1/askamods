# Mod 2: TreeRespawnMod — COMPLETE (v1.7.1)

**Goal:** Respawn felled trees (stump condition) and exhausted gather resources (reeds, berries,
etc.) after configurable in-game days — plus a configurable refill rate for **constructed wells**
(Water Well / Rain Collector), year-round rain-independent **mushrooms**, and **woodcutter stump
protection**. This file describes the mod as it exists at v1.6.1; version history is compressed
into the appendix. Deep investigation history: `docs/archive/TREERESPAWN_HANDOFF.md`.

**Game subsystems:** [Resource / Tree System](../architecture.md#resource--tree-system) and
[Gather / Press-to-Collect System](../architecture.md#gather--press-to-collect-system) — both carry
the confirmed facts and dead-ends (including why **mining/stone-clump respawn was abandoned**).
Wells: architecture.md → "Constructed water structures" under the Gather section.

## Tree respawn

- Postfix on `HarvestInteraction.TakeDamage` — after each hit, `GetCurrentPieceIndex() ==
  harvestPieces.Count - 1` detects stump stage; `_worldInstance.TryCast<BiomeItemInstance>()` gets
  the biome instance (non-biome resources return null and are skipped).
- Store `WeatherSystem.NetworkedCurrentGameTime` at fell time; compare elapsed via
  `GetTimeDifferenceFromCurrentGameTimeInSeconds() / dayLength` against the threshold.
- Key everything by **world position** string (`GetPosition()` rounded to 0.1 m, `x:y:z`) —
  `UniqueId` is NOT globally unique.
- `BiomeInstancePatch` Postfix on `BiomeItemInstance.Initialize()` populates
  `ActiveInstances[posKey]` as the world streams in. The registry is only added-to / wiped on
  world-switch — a streamed-out node leaves a **stale pointer**, so the cached pointer is validated
  against the tree's persisted `WorldItemInstanceId` before being trusted (a pooled wrapper reused
  for a different node must not be misread as "our stump got cleared").
- Stump-harvested detection reads `inst.Destroyed` at tick time, **only on a WID-confirmed-live
  pointer**.
- **Unloaded/stale stump refill (confirmed in-game 2026-07-02):** when a tree's timer elapses and
  its cached pointer isn't live, `DayTracker` resolves a fresh instance via
  `BiomeProceduralDataHandler.GetInstance(tileId, treeWid, onlyIfActive:false, noPooling:true)` —
  works WITHOUT force-loading the tile — and calls `Replenish()`, then `handler.SetDirty(cell)` +
  `handler.OnInstanceDataChanged(instance)` to flush. If the fresh read shows `Destroyed=true`, the
  stump was genuinely cleared (the one legitimate tree cancel) and the entry drops. Confirmed: a
  tree felled far from base respawned unattended.
- An entry is only dropped from the pending list once the replenish **verifiably took**
  (`IsExhausted()` reads `false` afterward) — an unconditional drop lets a no-op `Replenish()`
  silently lose a tree forever.
- Config: `[TreeRespawn] RespawnDays` (float, default 3.0).

## Gather resource respawn

- Postfix on `GatherInteraction.GatherItemsCharge` (the node's own collect method —
  source-agnostic: player AND villager gathers register); `CheckAvailableItemCount() == 0` detects
  exhaustion. Same `ActiveInstances` registry, same `Replenish()` call. No cancel condition —
  gather nodes always respawn unless config disables them.
- **Config `[GatherRespawn]`:** one `Config.Bind<float>` per known resource (key = **yielded item
  invariant asset name**, value = days; `0` = disabled; `Default` = fallback for unlisted
  resources). v1.7.1+ matches both asset name AND translated display name (case-insensitive
  substring — key `Mushroom` matches asset `Item_Food_BiomeMushroom*` AND display names
  "Gray/Grey/Yellow Mushrooms"; key `Fiber` matches asset prefixes AND `"Fibers"`). Dual matching
  enables locale-safe configuration in every language (prior v1.6.x matched only the translated
  yield name and applied zero overrides in non-English, falling back to the `Default` rate for
  every non-English node). The yield-name↔node table lives in architecture.md → Gather.
- **Respawn days is NOT a "more stock" lever** — it only sets how soon a node is harvestable
  again. If a raw intermediate still reads ~0 in storage at a `0.01` threshold, the bottleneck is
  consumption or gather labor, not the mod.
- **Unloaded-node refill (`RefillUnloadedGatherNodes`, default true; confirmed in-game
  2026-06-30):** same handler-based fresh-resolve as trees (the flag name predates tree coverage
  and now covers both). Each pending gather entry persists its `WorldItemInstanceId` across
  save/reload; a 30 s retry cooldown means an unresolved node is retried, not dropped. This is what
  keeps distant forage markers (e.g. shoreline reeds) refilling for villagers while you're
  elsewhere.
- **Co-op client gather/chop detection (`DataSyncPatch`, confirmed in co-op 2026-07-03):** a
  client's harvests replicate to the host as `WorldItemInstance` data changes. Nodes are classified
  from data: `Plugin.KnownNodes` is populated at bind time by `SetWorldInstance` postfixes
  (`Patches/Captures.cs`), and registration reads pure instance data — `GetQuantity() <= 0` for
  gather, `IsExhausted() && !Destroyed` for trees — no GameObject needed. Detects client actions
  both near and far from the host. Gate for the depletion check is
  `IsExhausted() || GetQuantity() <= 0` (see dead-ends — `IsExhausted()` alone is wrong for
  gather). Perf note: the `qty0=` bucket runs ~6% of fires under heavy streaming; first place to
  look if data-sync perf regresses.
- **Catch-up registration (confirmed in-game 2026-07-02):** the same bind-time hooks re-register
  any node found already-depleted-but-untracked when the host streams it in, self-healing losses
  from historical bugs (125 catch-up registrations healed a backlog, zero orphaned stumps left).
- **Manual respawn hotkey:** `t` by default replenishes any stump or exhausted gather node within
  `ManualRespawnRadius` (10 m), bypassing the pending list — for fixing manually deforested areas.
  Configs: `ManualRespawnHotkey`, `ManualRespawnRadius`, `ManualRespawnIncludeGather`. Host-gated.

## Co-op host validation

Host authority is validated via `Plugin.LocalPlayer` (tracked by a `PlayerCharacter.Spawned`
postfix) → `LocalPlayer.NetworkObject.Runner.IsServer`. Confirmed in a live co-op session
2026-07-03. (`WeatherSystem.Instance.Runner.IsServer` reads `false` in co-op — dead-end below.)

## Well water refill (`[WellRefill]` — confirmed in-game 2026-07-04, locale-safe 2026-07-21)

Built wells are `Structure`s whose water is a charge-based `GatherInteraction` (`'Water Well'` max
50, `'Rain Collector'` max 10) — the `[GatherRespawn]` `Water` entry only covers the wild Natural
Water Collector biome node.

- **Mechanism (`WellRefill.cs`, its own 30 s accumulator `RunWellRefillTick`, host-gated):**
  resolve `SettlementManager` (`FindAnyObjectByType`) → settlements via the **getter methods** →
  `GetStructures()` → manual hierarchy-walk for `GatherInteraction`s → keep those yielding the
  water gatherable. v1.7.1+ matches both invariant asset name (`Item_Elements_NaturalWaterCollector1`)
  and translated display name (`"Water"`). Dual matching enables locale-safe well identification
  in every language (prior v1.6.x matched only the translated name and matched zero water
  interactions in non-English, so refill silently did nothing). Per well, accumulate elapsed
  in-game time and grant `ReplenishCharges(n)` clamped to `CheckMaximumItemCount() -
  CheckAvailableItemCount()`. Structures deduped by position; cache cleared on world switch; stale
  wrappers dropped and re-found next scan.
- **Time accounting is anchor+carry:** the anchor is only ever assigned from
  `WeatherSystem.NetworkedCurrentGameTime` and only ever consumed through
  `GetTimeDifferenceFromCurrentGameTimeInSeconds(anchor)`; the sub-charge remainder banks in
  seconds. While a well is full the anchor re-arms so no burst accumulates.
- **Confirmed:** at `ChargesPerDay=24`, +1 water per 60 s (1440 s day), counts rising against live
  villager consumption; at 1440 the well visibly raced up — the mod, not vanilla, controls the
  rate. Villagers drink the refilled stock (real game state).
- **Config:** `ChargesPerDay` (float, default 24 = 1/in-game-hour; 0 = off; scales with day length
  automatically), `WellDiagnostics` (bool, default false; NO-OP/error warnings always log).
- Scope notes: **Rain Collector is included** (same water-gather signature; vanilla fills it only
  on rain). Co-op client-side behavior unverified ⚠️ (refill runs host-side; `ReplenishCharges`
  writes the networked count, so replication is expected but untested).

## Mushroom availability — year-round + rain-independent (`[MushroomAvailability]` — verified
end-to-end 2026-07-11, locale-safe 2026-07-21)

The game gates seasonal/weather resources via `WeatherManager._descriptors` (~22 entries; each
carries an `AvailabilityProcess` ScriptableObject ANDing four condition lists). **Vanilla mushroom
gating (deterministic, confirmed in-game 2026-07-10/11):** Grey Mushrooms (id 16793609) =
Season[Spring,Summer,Autumn] + Other[IsRaining mandatory]; Yellow Mushrooms (id 16793610) =
Season[Autumn] + Other[IsRaining mandatory]; plain Mushrooms (id 16793608) = **ungated** (both
lists empty).

- **Mechanism (`MushroomAvailability.MaybeApply()`, driven from `DayTracker.Update()`):** enumerate
  descriptors, match by invariant asset name substring (`Item_Food_BiomeMushroom*`, checked
  case-insensitively). v1.7.0+ also checks invariant asset names, enabling locale-safe matching
  (prior v1.6.x matched only the translated display name and found zero mushrooms in non-English).
  Snapshot each matched process's original lists once (only from a populated read, so a raced-empty
  read is never recorded as vanilla data), then `.Clear()` the Season and Other lists. First sweep
  ~5 s after descriptors register, then **re-sweeps every 10 s all session** (late-caught entries
  log an unconditional `LATE clear:` marker — never observed locally; kept as hardening for slower
  machines). Not host-gated: the SO edit runs locally on every peer; the host's copy drives biome
  spawning and client edits are harmless.
- **`AvailabilityProcess` is process-global, NOT per-world** — the edit persists across world
  reloads, is naturally idempotent, and caching its wrappers across worlds is safe (the
  "never cache per-world wrappers" gotcha does NOT apply). No runtime restore path; a game restart
  reverts for free.
- **`remainingDays` lifecycle (confirmed in-game 2026-07-11):** arms to process `Lifespan`
  (observed 1) on availability evaluation, decrements per day, **parks at −1**, and does NOT
  re-arm from instance consumption. With gates cleared there are no availability transitions, so
  the game's own replenish countdown goes dormant — **an availability un-gate must be paired with a
  node-respawn mechanism**; here the existing `[GatherRespawn]` machinery is the restore engine
  (its `Default` fallback covers mushrooms out of the box).
- **Grey↔Gray plant/yield pairing (confirmed in-game 2026-07-11):** the weather table registers
  the PLANT ItemInfo "Grey Mushrooms"; world gather nodes YIELD a distinct ItemInfo "Gray
  Mushrooms". Both spellings are raw `ItemInfo.Name` strings, not localization — name filters are
  locale-safe as far as observed, but plant vs yield can be different ItemInfos.
- **F8 diagnostic dump** (`DumpHotkey`, typing-guarded): per-entry gate state plus a `census:` line
  walking `BiomeItemAvailabilityData.itemDescriptors` → per-descriptor
  `IsAvailable`/`IsHarvestable`/`._instances` → per-instance `Active`/`Destroyed`/`GetQuantity()`.
  Instance lists reflect the currently-streamed region.
- **Verification:** rain half + winter half confirmed in-game 2026-07-07 (mushrooms present in
  winter and in dry spring where vanilla read `IsAvailable=False`). Two TimeWarp soaks 2026-07-11:
  idle census identical across ~6 in-game days (zero attrition with gates cleared); forced-harvest
  soak (30 exhaustions, 24 mod respawns at 0.1-day thresholds, remainder pending and reload-safe)
  read full strength at every dump, zero errors. Minor: `IsAvailable` lags False→True by one
  weather-evaluation tick after the clear (game caches the result; self-corrects).
- **Config:** `IgnoreRain` (true), `IgnoreSeason` (true), `ItemNames` (`Mushroom`),
  `DumpDiagnostics` (false), `DumpHotkey` (F8). ⚠️ `AvailabilityProcess.CanRun` reads False when
  all condition lists are empty (both cleared and vanilla-empty) — semantics unconfirmed.

## Block respawn under player-built structures (`[TreeRespawn]` — confirmed in-game 2026-07-07)

Trees must not regrow through buildings (Nexus report: through a house floor and a smoker pen).
Root cause was structural: "cleared" was inferred once from a live `Destroyed` flag and thrown
away, while the catch-up scanner re-arms any exhausted node on every chunk stream-in — a one-shot
cancel can't win against a repeat re-registrar. Two complementary parts:

1. **Durable `BlockedPositions` set (primary).** A persisted `# blocked` section in the per-world
   save. Absolute: a blocked entry never respawns and registration refuses to re-arm it. Populated
   by every confirmed clear (live-`Destroyed` cancel, handler-path cancel, structure backstop).
   **Blocks are permanent:** felling a blocked tree is a silent no-op (no re-registration) — this
   is the user-facing cleanup workflow for pre-existing through-building trees: cut once, stays
   gone. (Un-blocking on re-fell re-armed cleared spots — dead-end below.)
2. **`StructureQuery` footprint backstop** (`StructureQuery.cs`, reusable). `IsBlockedByStructure(x,
   z, margin)` — building footprint = union of non-trigger `Collider` AABBs from a manual
   hierarchy walk, point-in-rect ± margin, cached ~15 s, fail-open, `DataReady` + a 45 s
   `StructureDataStillLoading` hold covers the load race (a prior-session tree comes due the
   instant the world loads, before footprints exist). Runs at registration and at respawn-service
   time. Full primitive write-up: architecture.md → Structures → "Reusable structure-footprint
   spatial query".

Config: `BlockRespawnUnderStructures` (true), `StructureBlockMargin` (float, default 1.0 m).
Confirmed in-game 2026-07-07: workshop/house trees stayed down (silent re-cut, zero new log
lines), forest trees respawned normally; save held exactly the expected 12 blocked positions.

## Woodcutter stump protection (confirmed in-game 2026-06-26)

Woodcutters harvest leftover stumps for firewood, destroying the instance and cancelling the
respawn (slow deforestation). Fix: `StumpProtectionPatch` — Postfix
`HarvestInteraction.CanProvideItem` → `__result = 0` when the instance is a multi-piece
`BiomeItemInstance` (`pieces >= 2`) at its **last piece** (= a stump; structural gate — standing
trees still felled, loose `Item_Wood_*` logs still hauled). Only `CanProvideItem` leaks the stump's
firewood yield — every other candidate gate already reads depleted (full table: architecture.md →
Resource/Tree → "What a stump actually IS"). Player stump-clearing is axe damage (`TakeDamage`),
not this query — still works, still cancels the respawn.

- **Work-priority interaction (vanilla, not a mod softlock):** if firewood is prioritized and the
  only source left is protected stumps, woodcutters idle via `ComplainNoResourcesFound`. Fell a
  fresh tree near them or keep logs/long-sticks at equal priority.
- Config: `ProtectStumpsFromWoodcutters` (true); `EnableDiagnostics` (false) — verbose stump-hide +
  worker-idle logging (`WorkerIdleDiagPatch`).

## Performance shape (v1.5.5–v1.6.1)

- Well-refill and respawn-queue servicing gated out of the per-frame path (confirmed in-game
  2026-07-07); world-poll, mushroom re-sweep timer, and hotkeys remain cheap per-frame checks.
- v1.6.0–v1.6.1 (⚠️ pending in-game confirmation — the 2026-07-11/12 perf arc, see architecture.md
  → Mod-side frame hitches): the ~1 Hz service tick cost 27–52 ms nearly every second, dominated by
  `WellRefill.Tick` (avg 29 / max 57 ms of structure-hierarchy walking). Fixes: cheap-first
  reordering in the tree loop (BlockedPositions → due-check → only then pointer-trust/interop
  reads; the stump-harvested cancel moved AFTER the due-check — semantics preserved, the cancel
  only matters at respawn execution); due-entry work sliced to 8/tick/queue with rotating cursors;
  `WellRefill.Tick` on its own 30 s accumulator; `ServiceInterval` 1 s→3 s (respawn timing is
  day-gated, so no observable latency). Separate `trees`/`gather` stopwatch attribution.
- Typing guard (confirmed in-game 2026-07-10): both DayTracker hotkey paths ignored while a game
  text field is focused.

## Shared infrastructure

- **Persistence:** per-world file `com.askamods.treerespawn.<sanitizedSessionId>-<fnv32>.save` in
  `BepInEx/config` (isolation confirmed in-game 2026-06-28 — SP and co-op produced separate
  files). Sections `# tree` (`posKey,gameTime`), `# gather` (`posKey,gameTime,widRaw,itemName`;
  older 3-field lines still parse), `# blocked` (bare posKeys). Headerless legacy saves load as
  tree entries; a legacy `# mining` section is skipped. The old global
  `com.askamods.treerespawn.save` is not migrated (blended worlds can't be separated) — orphaned.
- **World identity = `StorageManager.ActiveSessionID`** polled by `Plugin.PollWorldId()` (every
  frame until resolved, then ~every 60 frames). Filename fragment = sanitized id + FNV-1a-32 hash
  (deterministic across runs, unlike `String.GetHashCode`).
- **World switch:** `OnWorldChanged` flushes the old world's file and clears all per-world state
  including `ActiveInstances` and `Biomes`; the FIRST resolve deliberately does NOT clear
  `ActiveInstances` (instances register during early load before the poll resolves).
- **Quit-to-menu (the v1.4.5 reload-crash fix, confirmed in-game 2026-07-06):** quit-to-menu does
  NOT change `ActiveSessionID` (it only goes empty at the main menu), so same-ID checks can't
  detect a reload. `NoteWorldLeft()` fires when the id transitions to empty: `SavePending()` →
  `ClearTransientState()` (all per-world statics) → `DayTracker.ClearTransientState()`. The data
  handler is a read-through property of the game's static `BiomeItemInstance.Handler` — resolved
  at use time, never cached. Log marker: `World session ended (quit to menu) — per-world state
  cleared.` Before this fix, respawn checks read a freed world's memory through the stale cached
  handler → native AV → `coreclr.dll+0x1d1fdd` fatal (the universal stale-wrapper gotcha; full
  forensics: architecture.md → Native Crash Diagnosis).
- **Co-op:** registration/timers host-gated; the host's session id is the save key.
- ⚠️ **Known caveat (parked; confirmed benign so far):** the `.save` file writes immediately on
  every registration, independent of whether the *game* saves. Quit-without-saving can make the
  mod's file disagree with the reloaded game state. Why it stays benign: `RegisteredStumps` is not
  persisted, so a genuine re-fell always overwrites the stale entry; worst case is a no-op
  `Replenish()` on an intact node plus a misleading "respawned" log line. Designed-but-unbuilt
  fix: hold registrations as volatile until an actual game-save event fires, then commit — the
  missing piece is a host-side "game just saved" patch target (never identified). Read the mod's
  `.save` as "what the mod has seen this process lifetime", not "what the game persisted".

## Dead-ends (don't retry)

- **World SEED as the per-world key** — empty/absent on a LOADED save (`SetSeedPhrase` only fires
  during generation; `RandomGeneratorManager.seedPhrase`/`NetworkSession.Parameters.seed` read
  null/empty). The mod silently did nothing. Use `StorageManager.ActiveSessionID`.
- **`IsExhausted()` alone as the gather-depletion gate** — an empty gather node reads `false`
  (stump/harvest semantics only); this silently filtered every depleted gather node out of the
  data-sync path for a whole co-op session (`registered gather=0` in every window). Gate on
  `IsExhausted() || GetQuantity() <= 0`.
- **`WeatherSystem.Instance.Runner.IsServer` for host validation** — reads `false` in co-op. Track
  the local player and use `LocalPlayer.NetworkObject.Runner.IsServer`.
- **`GetComponent<GatherInteraction>()` on the instance's own GameObject to classify nodes** — the
  interaction component often isn't on that GameObject. Classify from bind-time data
  (`SetWorldInstance` postfixes → `KnownNodes`).
- **Advancing the well-refill anchor with `+= seconds`** — `NetworkedCurrentGameTime`'s internal
  unit is opaque; only assign the anchor from the property and measure through
  `GetTimeDifferenceFromCurrentGameTimeInSeconds(anchor)`.
- **Un-blocking a blocked position on physical re-fell** — re-armed cleared spots (visible as
  fell→re-arm→re-cancel churn). Blocks are permanent; re-felling a blocked tree is a silent no-op.
- **Gating stump protection on `PendingRespawns` membership** — the `CanProvideItem` query races
  fell-time registration, and the HarvestInteraction transform position ≠ the biome-instance
  position. Gate structurally on piece index.
- **Center-distance structure blocking** — inconsistent by building size (blocked next to a hut,
  missed a longhouse corner). Use the collider-AABB footprint.
- `GatherRespawnDays` + a comma-separated overrides string — replaced by per-resource
  `Config.Bind<float>` entries; much more user-friendly.
- **Methodology:** a save+reload "closure test" masks stale-pointer failure modes (reload
  refreshes every live pointer) — the original Issues C/D were wrongly closed this way. Test
  mid-session streaming (walk away/back) before believing a stale-pointer fix.

## Version history (compressed)

| Version(s) | Date | What changed |
|---|---|---|
| v1.0–v1.1.x | 2026-06 | Tree respawn core; stump identity diagnostic; woodcutter stump protection (v1.1.6). |
| v1.2.1–v1.2.2 | 2026-06-28 | Per-world save isolation via `ActiveSessionID` (seed route failed); SAC bump. |
| v1.2.3–v1.2.10 | 2026-06-28/30 | Issues C/D chase → handler-based replenish for deactivated nodes, persisted WIDs, reload-safe (RESOLVED 2026-06-30). |
| v1.2.13–v1.2.15 | 2026-07 | Co-op host validation fix; manual respawn hotkey. |
| v1.3.0–v1.3.2 | 2026-07-02/03 | Tree respawn hardened to gather parity (WID validation + handler refill); data-driven co-op gather detection + catch-up registration; v1.3.1 `IsExhausted` gate regression fixed in v1.3.2; co-op confirmed end-to-end. |
| v1.4.0–v1.4.4 | 2026-07-04 | Well refill (iterations: plural-generic gotcha, settlements-list gotcha, unit-safe anchor+carry). |
| v1.4.5 | 2026-07-06 | Same-world reload crash root-caused (stale cached handler) → `NoteWorldLeft` per-world state flush. |
| v1.4.6–v1.4.7 | 2026-07-07 | Mushroom availability feature (gate-clearing). |
| v1.5.0–v1.5.4 | 2026-07-07 | Block-respawn-under-structures: BlockedPositions + StructureQuery footprint (center-distance and unblock-on-refell dead-ends fixed en route). |
| v1.5.5 | 2026-07-07 | Per-frame work throttled to 1 Hz. |
| v1.5.6 | 2026-07-10 | Typing guard on hotkeys. |
| v1.5.7–v1.5.8 | 2026-07-10/11 | Mushroom re-sweep hardening; gating ground-truth CORRECTED (plain Mushrooms vanilla-ungated); F8 instance census; two-soak end-to-end verification. |
| v1.6.0–v1.6.1 | 2026-07-12 | Perf hardening: cheap-first + 8/tick slicing, WellRefill on 30 s cadence, ServiceInterval 3 s (⚠️ pending in-game confirmation). |
| v1.7.0 (2026-07-21) | Mushroom availability gate now matches invariant asset name (`Item_Food_BiomeMushroom*`) + translated display name, enabling year-round mushroom availability in every language; confirmed in-game in German. |
| v1.7.1 (2026-07-21) | Per-item gather-respawn rate override (`GetGatherThreshold`) now keys on invariant asset name + translated yield name (dual matching); well-refill "Water" filter now matches invariant asset name (`Item_Elements_NaturalWaterCollector1`) + translated name, enabling locale-safe identification in every language. ⚠️ pending in-game confirmation. |
