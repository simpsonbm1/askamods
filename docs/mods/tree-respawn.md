# Mod 2: TreeRespawnMod — COMPLETE (v1.5.4)

**Goal:** Respawn felled trees (stump condition) and exhausted gather resources (reeds, berries, etc.)
after configurable in-game days — plus, since v1.4.x, a configurable refill rate for **constructed
wells** (Water Well / Rain Collector buildings).

**Game subsystems:** [Resource / Tree System](../architecture.md#resource--tree-system) and
[Gather / Press-to-Collect System](../architecture.md#gather--press-to-collect-system) — both carry
the confirmed facts and dead-ends (including why **mining/stone-clump respawn was abandoned**).
Wells: see architecture.md → "Constructed water structures" under the Gather section.

## Tree respawn
- Postfix patch on `HarvestInteraction.TakeDamage` — after each hit, check `GetCurrentPieceIndex() == harvestPieces.Count - 1` to detect stump stage
- `_worldInstance.TryCast<BiomeItemInstance>()` to get the biome instance; non-biome resources (rocks etc.) return null and are skipped
- Store `WeatherSystem.NetworkedCurrentGameTime` at fell time; compare elapsed via `GetTimeDifferenceFromCurrentGameTimeInSeconds() / dayLength` against threshold
- Key everything by **world position** string (`GetPosition()` rounded to 0.1m, format `x:y:z`) — `UniqueId` is NOT globally unique
- `BiomeInstancePatch` Postfix on `BiomeItemInstance.Initialize()` populates `ActiveInstances[posKey]` as world loads; `DayTracker` looks up the live instance at check time
  - `ActiveInstances` is only *added to* / *wiped on world-switch* — never pruned when an individual node unloads, so a streamed-out node can leave a **stale** pointer. **Resolved for trees in v1.3.0** (previously only gather had this): the cached pointer is validated against the tree's persisted `WorldItemInstanceId` (`Plugin.TreeWid`) before being trusted — a pooled wrapper reused for a different node once its original chunk unloads can no longer be misread as "our stump got cleared" and permanently cancel a live respawn. An unloaded/stale/never-streamed stump is serviced through the same data-handler refill gather nodes use — see below.
- Stump-harvested detection: check `inst.Destroyed` at each tick, but **only on a WID-confirmed-live pointer** (v1.3.0) — trusting `Destroyed` on an unvalidated pointer risks reading an unrelated node's state
- **Unloaded/stale stump refill (v1.3.0, confirmed in-game 2026-07-02).** When a tree's timer elapses and its cached pointer isn't live (or the posKey was never in `ActiveInstances` at all this session), `DayTracker` resolves a fresh instance via `SSSGame.BiomeProceduralDataHandler.GetInstance(tileId, treeWid, onlyIfActive:false, noPooling:true)` — same mechanism as gather's v1.2.9 fix — and calls `Replenish()` on it. If that fresh read shows `Destroyed=true`, the stump was **genuinely** cleared (the one legitimate tree cancel — gather nodes never cancel) and the entry is dropped instead of replenished, even while unloaded. Confirmed in-game: a tree felled far from base respawned unattended while the player waited on the opposite side of the base. Shares the `RefillUnloadedGatherNodes` config flag (name kept for compatibility; now covers trees too).
- An entry is only dropped from the pending list once the replenish **verifiably** took (`IsExhausted()` reads `false` afterward) — previously (pre-v1.3.0) the entry was dropped unconditionally, so a no-op `Replenish()` could silently lose a tree forever.
- Config: `TreeRespawn/RespawnDays` (float, default 3.0)

## Gather resource respawn
- Postfix patch on `GatherInteraction.GatherItemsCharge` — fires when player/villager collects; check `CheckAvailableItemCount() == 0` to detect full exhaustion
- Same `_worldInstance.TryCast<BiomeItemInstance>()` path; same `ActiveInstances` registry; same `Replenish()` call
- `PendingGatherRespawns` stores `(float gameTime, string itemName)` — no stump-cancel condition; resources always respawn unless config disables them (`threshold <= 0` → drop from queue immediately)
- Config: `[GatherRespawn]` section with one `Config.Bind<float>` per known resource (key = yielded item name, value = days). `Default` entry is the fallback for unlisted resources. `0` = disabled.
- Substring match on item name (case-insensitive) — `"Mushroom"` matches `"Gray Mushroom"`, `"Yellow Mushroom"` etc. Note this is `itemName.Contains(configKey)`, so the config key must be a substring of the in-game item name: key `Fiber` matches item `"Fibers"` (the real, plural name — see architecture.md gather table).
- **Confirmed working in-game (2026-06-25):** flax bushes at sub-day thresholds (`0.01`) cycle exhausted→respawned→re-harvested repeatedly at the same position in the log — `Replenish()` genuinely restores gatherable harvestability, not just the visual. Triggered by villager *and* player gathering (patch is on `GatherInteraction.GatherItemsCharge`, the node's own collect method — source-agnostic, same as the tree patch).
- **Respawn days is NOT a "more stock" lever.** It only sets how soon a node is harvestable *again*; it does not change yield-per-harvest or add gatherers. If a raw intermediate (e.g. fiber) still reads ~0 in storage with a `0.01` respawn, the bottleneck is downstream — consumption (weaving/tailoring eats fiber as fast as it's gathered) or gather labor/walk-time — not the mod. Lowering the threshold further can't help once the node is already almost always ready.
- **Distant/villager-only nodes whose chunk has deactivated also respawn (v1.2.10, confirmed in-game 2026-06-30).** When a gather node's timer elapses while its `BiomeItemInstance` is no longer live (chunk streamed out — the stale-`ActiveInstances`-pointer case above), `DayTracker` re-resolves a fresh, writable instance through `SSSGame.BiomeProceduralDataHandler.GetInstance(tileId, widId, onlyIfActive:false, noPooling:true)` — this works *without* force-loading the tile — and calls the game's own `Replenish()` on that handle, then `handler.SetDirty(cell)` + `handler.OnInstanceDataChanged(instance)` to flush/notify. The node's `WorldItemInstanceId` is cached at harvest time and persisted across save/reload so this keeps working after a reload, not just within a session. Config: `TreeRespawn/RefillUnloadedGatherNodes` (bool, default `true`). This is what lets distant forage markers (e.g. shoreline reeds) keep refilling for villagers while you're elsewhere — see `TREERESPAWN_HANDOFF.md` → "RESOLVED 2026-06-30" for the full investigation (Issues C/D).
- **Host validation in Co-op (v1.2.13).** The mod previously validated host authority using `WeatherSystem.Instance.Runner.IsServer`, which evaluated to `false` in co-op. It now tracks `Plugin.LocalPlayer` via `PlayerCharacter.Spawned` and uses `LocalPlayer.NetworkObject.Runner.IsServer` as a primary check. ✅ Confirmed in a live co-op session 2026-07-03 (v1.3.2) — Issue A closed; see `TREERESPAWN_HANDOFF.md`.
- **Co-op gather detection reworked (v1.3.0; ✅ CONFIRMED in co-op 2026-07-03 under v1.3.2 — but only after v1.3.2 removed the v1.3.1 gate regression, see the v1.3.2 section below).** The network data-sync catch (`DataSyncPatch`, fires for a co-op client's replicated harvests) previously required `GetComponent<GatherInteraction>()` on the instance's own GameObject to identify a node — which structurally misses most cases, since the interaction component often isn't on that GameObject at all (same finding as the v1.2.14 manual-hotkey stump issue below). It now classifies nodes from data: a `Plugin.KnownNodes` map is populated authoritatively when `GatherInteraction`/`HarvestInteraction.SetWorldInstance` binds (new postfixes, `Patches/Captures.cs`), with a component search (including children/inactive) only as a fallback for anything not yet seen. Registration itself reads pure instance data (`GetQuantity() <= 0`) — no GameObject required at all. The same bind-time hooks also run a **catch-up registration**: any node found already-depleted-but-untracked when the host streams it in gets a fresh timer immediately, self-healing losses from historical bugs (confirmed in-game 2026-07-02: 125 catch-up registrations healed a backlog with zero orphaned stumps left afterward).
- **Manual Respawn Hotkey (v1.2.14/15).** Added a hotkey (`t` by default) to manually replenish any stump or exhausted gather node within a configurable radius (default `10m`). Bypasses the pending list entirely to help fix manually deforested areas. Scans `ActiveInstances` for depleted gather nodes and `Resources.FindObjectsOfTypeAll<HarvestInteraction>()` for physical stumps in the scene. Configs: `ManualRespawnHotkey`, `ManualRespawnRadius`, `ManualRespawnIncludeGather`. Host check fixed in v1.2.15.

## Well water refill (v1.4.0–v1.4.5 — CONFIRMED in-game 2026-07-04, reload crash fixed 2026-07-06)
Configurable refill rate for **constructed** water structures — the `GatherRespawn` `Water` entry
only covers the wild Natural Water Collector (a biome node); built wells are `Structure`s whose
water is a charge-based `GatherInteraction` (`'Water Well'` max 50, `'Rain Collector'` max 10) and
never touch the biome-instance machinery.

- **Mechanism (`WellRefill.cs`, driven from `DayTracker.Update()` before the pending-respawn
  early-out; host-gated via the existing `TryGetServerWeather`):** every ~30 s, resolve
  `SettlementManager` (`FindAnyObjectByType`) → settlements via the **getter methods**
  (`GetPlayerSettlement()`/`GetCurrentSettlement()`/`worldSettlement`) → `GetStructures()` →
  manual hierarchy-walk for `GatherInteraction`s → keep those yielding item `"Water"`. Per well,
  accumulate elapsed in-game time and grant `ReplenishCharges(n)` clamped to
  `CheckMaximumItemCount() - CheckAvailableItemCount()`. Structures deduped by position; cache
  cleared on world switch; stale wrappers dropped and re-found on the next scan.
- **Time accounting is anchor+carry:** the anchor is only ever assigned from
  `WeatherSystem.NetworkedCurrentGameTime` and only ever consumed through
  `GetTimeDifferenceFromCurrentGameTimeInSeconds(anchor)`; the sub-charge remainder is banked in
  seconds. (v1.4.2 advanced the anchor with `+= seconds` — an unverified-unit assumption on an
  opaque value; don't reintroduce.) While a well is full the anchor re-arms so no burst banks up.
- **Confirmed in-game 2026-07-04:** at `ChargesPerDay=24`, +1 water per 60 s (1440 s day) with
  counts verifiably rising against live villager consumption; the decisive test set
  `ChargesPerDay=1440` (= +1/sec at that day length) and the well **visibly raced up** — proof the
  mod, not vanilla, controls the rate. Villagers drink the refilled stock (real game state).
- **Config `[WellRefill]`:** `ChargesPerDay` (float, default 24 = 1/in-game-hour; 0 = off/vanilla;
  scales with day length automatically), `WellDiagnostics` (bool, default `false` since v1.4.4 —
  scan-state, per-minute progress, and `+N water` lines; NO-OP/error warnings always log).
- **Version trail:** v1.4.0 shipped the feature but the settlement scan threw
  (plural-generic `GetComponentsInChildren<T>(bool)` missing through the trampoline — now a
  universal gotcha); v1.4.1 fixed that with the manual child-walk but discovery still found
  nothing (`SettlementManager.settlements` list stays null — second new gotcha); v1.4.2 switched
  to the settlement getter methods (discovery worked: 6 structures tracked) and added the
  loud scan-state line; v1.4.3 made the time accounting unit-safe + added per-minute tick
  diagnostics + NO-OP detection (refill then confirmed firing); v1.4.4 flipped diagnostics
  default off and diag-gated the per-grant line (at high rates it's ~1 line/sec/well).
- Known scope notes: **Rain Collector is included** (matches the same water-gather signature;
  vanilla fills it only on rain — name-filter it out later if undesired). Co-op client-side
  behavior unverified ⚠️ (host/solo confirmed; refill runs host-side only, and `ReplenishCharges`
  writes the networked count, so replication is expected but untested).

## v1.4.5 — same-world reload crash root-caused & fixed (2026-07-06, confirmed in-game)
**Bug:** quit-to-menu → reload of the same world crashed (WER: `coreclr.dll+0x1d1fdd` fatal error, no managed exception). Happened **4 times in succession** during development; the crash signature recurred weekly since 2026-06-18 (Issues A-E all prior to root cause).

**Root cause:** `Plugin.DataHandler` was cached once per process (only-if-null check in `BiomeInstancePatch` Postfix on `BiomeItemInstance.Initialize`), and `PollWorldId` early-returned unchanged when `ActiveSessionID` didn't change (same world reload). But quit-to-menu doesn't clear `ActiveSessionID` — it only becomes empty/falsy when no world is loaded. So the session ID looked identical, the handler cache never cleared, and `ActiveInstances` held stale freed-world native pointers. On the next respawn check (~30s later), `HandlerReplenishTree` / `DeactivatedReplenish` read through `GetInstance()` on the stale handler, calling `VegetationStudioPro.InstancesDataArrays.FindIndexOfUniqueId+0xc1` in native code with a freed world's memory → native AV beneath managed frames → CLR fatal error.

**Fix:** (1) `DataHandler` is now a read-through property of the game's static `BiomeItemInstance.Handler` (resolved at use time, never cached; the capture block was entirely removed from `BiomeInstancePatch`); (2) new `NoteWorldLeft()` method fires when `ActiveSessionID` transitions to empty (detected via ongoing polling, firing once) — it calls `SavePending()` (commit pending respawns), then `ClearTransientState()` to wipe ALL per-world statics (`ActiveInstances`, `PendingRespawns`, `PendingGatherRespawns`, `RegisteredStumps`, `Biomes`), then signals `DayTracker.ClearTransientState()` to stop timers; (3) `Biomes = null` added to `OnWorldChanged`'s clear block — switching worlds mid-session was also leaving it stale.

**New log marker on quit-to-menu:** `[TreeRespawnMod] World session ended (quit to menu) — per-world state cleared.`

**Confirmed in-game 2026-07-06:** two save→menu→reload cycles on the same world, no crash. The handler now reads fresh each use, and per-world state correctly resets. Historical note: the same `coreclr.dll+0x1d1fdd` WER signature appeared on 6/18, 6/23, 6/27, 6/29 — recurring offsets in coreclr mean "native AV class (beneath managed frames)", not necessarily the same root cause; the TreeRespawn reload bug was just one example of caching per-world native objects incorrectly.

## Mushroom availability — year-round + rain-independent (v1.4.7 — CONFIRMED in-game 2026-07-07)
The game gates seasonal/weather-restricted resources via `WeatherManager._descriptors` (Dictionary keyed by `ItemInfo`, each carrying an `AvailabilityProcess` ScriptableObject). The process ANDs four condition lists; wild mushrooms fail on exactly two: `SeasonAvailabilityConditions` (omits Winter → culls mushrooms winter-only) and `OtherAvailabilityConditions` (mandatory `IsRaining` → rain-gates their growth). The feature enumerates descriptors, matches items by case-insensitive substring filter (default `Mushroom`), snapshots each matched process's original lists **once** (idempotent), then `.Clear()`s the two condition lists to un-gate them. The game's own `replenishWhenAvailable` + `lifespan=1` loop then keeps mushrooms present year-round. Applied once per world ~5s after descriptors register, driven from `DayTracker.Update()` via `MushroomAvailability.MaybeApply()`, un-host-gated; re-armed per world in `DayTracker.ClearTransientState()` via `MushroomAvailability.ResetForWorld()`. Key gotcha: **`AvailabilityProcess` is process-global (NOT per-world state)** — the edit persists for the whole process (survives world reloads) and is naturally idempotent (clearing an already-empty list is a no-op). Originals are snapshotted before the first clear so a double-apply / per-world re-run can't wipe them; there is no runtime restore path (a game restart reverts for free). Because these are process-global assets, caching their wrappers/originals across worlds is safe — the "never cache per-world wrappers" gotcha does NOT apply here. Not host-gated: the SO edit runs locally on every peer; in co-op the host's copy drives biome spawning and client edits are harmless.

**Config `[MushroomAvailability]`:**
- `IgnoreRain` (bool, default **true**) — clears the mandatory `IsRaining` condition.
- `IgnoreSeason` (bool, default **true**) — clears the seasonal restriction (year-round).
- `ItemNames` (string, default `Mushroom`) — comma-separated case-insensitive substring filter for which resources the edits apply to.
- `DumpDiagnostics` (bool, default **false** — was true in v1.4.6 research build, flipped to false since the feature shipped) — read-only diagnostic dump of the availability gate.
- `DumpHotkey` (string, default `F8`) — re-run the read-only dump on demand.

**In-game confirmation (2026-07-07):**
- **Rain half:** in Spring with `raining=False`, mushrooms appeared where the earlier v1.4.6 diagnostic had proven vanilla read `IsAvailable=False` (in-season but rain-gated).
- **Winter/season half:** in a co-op winter save (`season=WeatherSeason_Winter`, `raining=False`, `snowing=False`), all 3 mushrooms (Mushrooms, Grey Mushrooms, Yellow Mushrooms) read `Season[0]`/`Other[0]` and `IsAvailable=True` in both the auto-dump and the F8 dump, and the user saw mushrooms on the ground in winter (vanilla culls them).
- **Minor observed behavior:** `IsAvailable` lags False→True by one weather/season evaluation tick after the clear because the game caches the result; it self-corrects (remainingDays −1→1).

**Implementation reference:** see `NEW_MOD_IDEAS_PLAN.md` → idea 9 for the API research (Mono.Cecil Cecil-confirmed signatures, diagnostic approach, levers+gates).

## Block respawn under player-built structures (v1.5.0–v1.5.4 — CONFIRMED in-game 2026-07-07)
**Bug (Nexus report, Mephisto279 06 Jul 2026):** felled trees regrew **up through buildings** — one through a
house floor, one through a smoker-pen ground. Root cause: the mod had **no durable record that a spot was
cleared**. "Cleared" was inferred once from a live `Destroyed` flag and thrown away; the catch-up scanner
(`Patches/Captures.cs` → `SetWorldInstance` → `TryRegisterTree`) re-scans on every chunk stream-in and re-arms
any `IsExhausted() && !Destroyed` node with a fresh timer. A one-shot reactive cancel structurally can't win
against a repeat re-registrar, so a cleared spot got re-armed and grew a tree through the building. (The
originally-reported tree can also simply be **pre-existing** — grown through the building in an earlier session
and baked into the game's own save; the mod can't retroactively remove that, but the cleanup workflow below
makes it stay gone once cut.)

Two complementary parts:

**1. Durable `BlockedPositions` set (primary).** A new persisted `# blocked` section in the per-world save
(bare `posKey` lines). Absolute: a blocked entry NEVER respawns, and registration refuses to (re)arm it.
Populated whenever a clear is confirmed — the live-`Destroyed` cancel in `DayTracker`, the handler-path
`Destroyed` cancel (`ApplyTreeHandlerResult` Cancelled), AND the structure backstop below — each calls
`Plugin.BlockPosition(posKey)`. `DayTracker` drops any pending entry whose posKey is blocked at the top of the
loop; `Registration.TryRegisterTree` and `HarvestPatch` both refuse a blocked posKey.
- **Blocks are PERMANENT (v1.5.3).** v1.5.0–1.5.2 un-blocked a spot on any physical re-fell (`HarvestPatch`),
  which re-armed cleared spots — visible in the log as fell→re-arm→re-cancel churn. v1.5.3 removed that:
  `HarvestPatch` early-returns on `BlockedPositions.Contains(posKey)`, so **felling a blocked tree is a silent
  no-op** (no log line, no re-registration, stays felled). This is the user-facing cleanup workflow: cut each
  pre-existing through-building tree ONCE → it stays gone.

**2. Reusable `StructureQuery` footprint backstop (`StructureQuery.cs`, new).** `IsBlockedByStructure(x, z,
margin)` — walks `Settlement.GetStructures()` (the WellRefill settlement path), computes each building's real
footprint = union of its non-trigger `Collider` bounds as a horizontal AABB (manual hierarchy walk — plural
`GetComponentsInChildren<T>` is trampoline-broken), and tests point-in-rect ± margin. Cached ~15s, fail-open.
Runs at registration (`TryRegisterTree`) and at respawn-service time (`DayTracker`); if a building now sits on a
due tree's spot it permanently blocks it (`BlockPosition`). Full primitive write-up: architecture.md →
Structures → "Reusable structure-footprint spatial query".
- **Footprint-based (v1.5.1), not center-distance (v1.5.0).** v1.5.0 measured distance to a building's ORIGIN —
  blocked a tree by a small hut but MISSED one at a longhouse corner (confirmed in-game 2026-07-07). The
  footprint approach is validated in-game: base-typed `GetComponent<Collider>()` DOES surface colliders through
  interop.
- **Load-race hold (v1.5.2).** Structures/colliders load a beat after the world, and a prior-session tree is
  overdue the instant the world loads — exactly that window. `StructureQuery.DataReady` (true once ≥1 footprint
  is walked; empty early-load snapshots are never cached) + `DayTracker.StructureDataStillLoading()` HOLD an
  under-structure-eligible respawn (re-check next tick) until footprints load, capped at 45 s so a building-less
  world still respawns.

**Config `[TreeRespawn]`:**
- `BlockRespawnUnderStructures` (bool, default **true**) — the structure backstop; `false` reverts to
  position-only respawn (which can regrow trees inside buildings).
- `StructureBlockMargin` (float, default **1.0 m** since v1.5.4; was 1.5 in v1.5.0–1.5.3) — buffer grown around
  each building's real footprint (0 = exactly the footprint; larger = keep trees further off buildings).

**Confirmed in-game 2026-07-07 (v1.5.3 loaded):** user cut ~5 trees — workshop tree + trees near houses STAYED
DOWN, forest trees away from base CAME BACK. Log corroboration: session-start `Loaded … 12 blocked
position(s)`; the save `# blocked` section held exactly those 12 (incl. the workshop tree `194.3:41.5:575.1`);
re-cutting a blocked tree produced ZERO log lines and added ZERO new blocks (silent `HarvestPatch` early-return
= the v1.5.3 permanent-block working); unblocked forest trees logged normal `respawned` lines. Earlier:
blocked-on-clear + persistence confirmed 2026-07-07 (a felled → `Stump harvested — cancelled` → reload showed
`1 blocked position(s)` and the posKey never re-appeared); footprint check confirmed 2026-07-07 (a
longhouse-corner tree that regrew under v1.5.0 center-distance now fires `a structure occupies this spot`).
Files: `StructureQuery.cs` (new), `Plugin.cs` (BlockedPositions/BlockPosition/config/save-load/world-switch
clears), `Registration.cs` (blocked guard + structure check), `DayTracker.cs` (absolute-block drop,
service-time backstop, load-race hold), `Patches/HarvestPatch.cs` (blocked-spots-stay-blocked).

## Woodcutter stump protection (v1.1.6 — confirmed working in-game 2026-06-26)
- **Problem:** woodcutters harvest leftover **stumps** for **firewood** (stumps drop firewood). Harvesting a
  stump `Destroy`s the instance → `DayTracker` cancels its respawn → slow deforestation (~30 in-game days).
- **What a stump is (confirmed in-game 2026-06-26):** NOT a separate object. A tree is one
  `Harvest_Wood_<species><n>` `BiomeItemInstance` with multiple `harvestPieces` (Fir = 2: trunk, stump). The
  **stump is that same instance at its last piece**; a standing tree is it at an earlier piece; the fallen
  trunk/branches are separate `Item_Wood_*` non-biome instances (the wood haul). The "Tree Stump" you see is
  just a display name.
- **Fix:** `StumpProtectionPatch` — Postfix `HarvestInteraction.CanProvideItem` → `__result = 0` when the
  instance is a **multi-piece `BiomeItemInstance` (`pieces >= 2`) at its last piece**. Structural gate: leaves
  standing trees (still felled) and loose `Item_Wood_*` logs (still hauled) alone — only the stump goes dark
  to the woodcutter's firewood search.
- **Why earlier attempts missed:** every other gate (`GetNonExhaustedDepth`/`IsExhausted`/`IsAvailable`/
  `Check`) already reads depleted for a stump — only `CanProvideItem` leaks the firewood yield. And keying on
  `PendingRespawns` (v1.1.2) failed: the `CanProvideItem` query races fell-time registration, and the
  HarvestInteraction transform position ≠ the biome-instance position. See architecture.md → Resource/Tree →
  "What a stump actually IS" for the full table + dead-ends.
- **Player control preserved:** clearing a stump is axe damage (`TakeDamage`), not this AI query — still works,
  still cancels the respawn (makes the spot permanent).
- **Work-priority interaction (confirmed):** if firewood is prioritized and the only firewood source left is
  protected stumps, woodcutters go idle (`GatherAndHarvestData.ComplainNoResourcesFound`) — that's the vanilla
  priority system, **not** a mod softlock. Felling a fresh tree near them, or keeping logs/long-sticks at the
  same priority as firewood, gets them working again. (Firewood can also come from breaking down logs.)
- Config: `TreeRespawn/ProtectStumpsFromWoodcutters` (bool, default `true`);
  `TreeRespawn/EnableDiagnostics` (bool, default `false`) — verbose logs for the stump-hide and worker-idle
  events (`WorkerIdleDiagPatch` Postfixes `ComplainNoResourcesFound`/`ComplainNoGatherTask`), kept in for
  future troubleshooting.

## Shared infrastructure
- Persistence: **per-world** file `com.askamods.treerespawn.<sanitizedSessionId>-<fnv32>.save` in
  `BepInEx/config` (v1.2.1; per-world isolation **confirmed in-game 2026-06-28** — singleplayer and co-op
  produced two separate files). Sections `# tree` and `# gather`;
  tree format `posKey,gameTime`; gather format `posKey,gameTime,widRaw,itemName` (v1.2.10 adds `widRaw` —
  the node's `WorldItemInstanceId.Raw` — so a deactivated node can still be re-resolved and refilled after
  a reload; older `posKey,gameTime,itemName` lines still parse, `widRaw` defaults to 0/unknown for those).
  Old saves without section headers load as tree entries (backward compatible); a legacy `# mining` section
  is silently skipped on load.
- Day tracking via registered `MonoBehaviour` (`DayTracker`) with `Update()` polling — avoids IL2CPP delegate subscription issues
- ⚠️ **Known caveat (confirmed in-game 2026-06-28, parked as Issue E in `TREERESPAWN_HANDOFF.md`):** the
  `.save` file is written immediately on every registration, independent of whether the *game* actually
  saves. Quit without saving after felling/gathering and the mod's file can disagree with the reloaded
  game state. Confirmed benign so far (self-corrects on re-registration); proper fix would tie commits to
  an actual game-save event, not yet investigated.

## Per-world save isolation (v1.2.1 — confirmed in-game 2026-06-28 on both machines; v1.2.2 is a version-only bump, see below)
- **Why:** before v1.2.0 there was ONE global save file (`com.askamods.treerespawn.save`) shared by every
  world. Entries are keyed only by world position (`x:y:z`), and **positions collide across worlds**
  (worldgen reuses the same coordinate space), so a singleplayer world and a co-op world cross-contaminated:
  an entry registered in world A could `Replenish()` or cancel the wrong node — or get consumed — when world
  B was loaded. This corrupted respawn state and made the save file untrustworthy for diagnosis.
- **World identity = `SandSailorStudio.Storage.StorageManager.ActiveSessionID`** (a `MonoBehaviour`; also
  exposes `_activeSessionName` for a friendly label). This is a unique per-save id that IS populated for
  **loaded** saves. `DayTracker` polls it via `Plugin.PollWorldId()` (`FindAnyObjectByType<StorageManager>()`,
  then a cheap property read; every frame until resolved, then every ~60 frames to catch a world switch
  without a restart). Filename fragment = sanitized id + FNV-1a-32 hash (deterministic across runs, unlike
  `String.GetHashCode`) to stay collision-free.
- **❌ Dead-end — the world SEED is NOT usable as the key (confirmed in-game 2026-06-28, v1.2.0).** v1.2.0
  keyed on the seed via a `RandomGeneratorManager.SetSeedPhrase` Postfix + a `NetworkSession.Parameters.seed`
  fallback. Both are **empty/absent on a LOADED save**: `SetSeedPhrase` only fires during *new-world
  generation*, and `RandomGeneratorManager`/`NetworkSession.seed` read back null/empty once a world is
  loaded (SeedScout sees the same thing — its dump logs `Seed = <rng-null>`). Result: `OnWorldSeedKnown`
  never fired, no per-world file was created, the mod silently did nothing. Don't retry the seed route — use
  `StorageManager.ActiveSessionID`.
- **On world switch (id changes without restart):** `OnWorldChanged` flushes the old world's file, clears
  `PendingRespawns`/`PendingGatherRespawns`/`RegisteredStumps` **and** `ActiveInstances` (drop stale
  cross-world live pointers — repopulated by `BiomeInstancePatch` as the new world streams in). On the FIRST
  resolve it deliberately does NOT clear `ActiveInstances` (there's no previous world, and instances may have
  registered during early load before the poll resolved). Save path is unknown at `BasePlugin.Load()` time
  (no world picked yet), so loading is deferred until the id is known; `Save/LoadPending` no-op while null.
- **Migration:** the old global `com.askamods.treerespawn.save` is **not** migrated (we can't tell which
  world its blended entries belong to) — it's left orphaned and unused; new per-world files start empty.
- **Co-op:** registration/timers stay `IsServer`-gated, so only the host writes; the host's session id is the key.

## v1.2.2 — Smart App Control version-only bump (2026-06-28)
On the second machine, Windows Smart App Control blocked the v1.2.1 DLL hash at load
(`FileLoadException ... An Application Control policy has blocked this file. (0x800711C7)`) — the
known SAC gotcha (see root `CLAUDE.md`). Bumped `PLUGIN_VERSION` + csproj `<Version>` to 1.2.2 and
rebuilt with no logic change; the new hash loaded cleanly. Per-world save isolation re-confirmed
in-game on that machine immediately after.

## v1.2.3 — Issue C/D diagnostic logging (2026-06-28)
Implements the logging plan from `TREERESPAWN_HANDOFF.md` (Issues C/D were previously hypothesis-only):
`BiomeInstancePatch` logs `[diag] init <posKey>` on every `Initialize()` (EnableDiagnostics-gated) so
you can tell whether a distant/villager-only area streams in on the host; `DayTracker` logs a throttled
`[diag] overdue-but-not-loaded` summary when pending respawns are past threshold but their node isn't
currently loaded. Registration logging (GatherPatch/HarvestPatch/DataSyncPatch) already existed
unconditionally — no change needed there. See the handoff doc's "Diagnostic playbook" for the controlled
test procedure.

## v1.2.4 — dedupe the init diagnostic (2026-06-28, confirmed in-game)
A real play session (running from the village to unexplored terrain) logged the same handful of
positions 90+ times each via `[diag] init` — ~114,700 lines for one run, and noticeable hitching while
moving. `BiomeInstancePatch` now only logs a position's first `Initialize()` per world (tracked via
whether the posKey was already in `ActiveInstances`); subsequent re-streams still update the live
pointer but don't re-log. Same diagnostic answer ("did this position ever load on the host"), far less
volume. The controlled Issue C test (host stationary, villager works a distant marker) still hasn't
been run — the runs so far either predate this logging or tested player-driven traversal, which doesn't
isolate the villager-only-no-player hypothesis anyway.

## v1.2.5 — richer NoResourcesFound diagnostic (2026-06-28)
Two controlled Issue C tests (host far from village; host far then back near village) both came back
with **zero** gather registrations and persistent — if anything, worsening — `NoResourcesFound`
worker-idle complaints even while standing at the village. That doesn't fit "distance is the whole
story," so `ComplainNoResourcesDiag` (`Patches/WorkerIdleDiagPatch.cs`) now captures the villager's name
(`GatherAndHarvestData.GetVillager().GetName()`) and the actual `ItemManifest` the worker was searching
for (`finderManifest.GetItems()` → up to 5 `ItemInfoQuantity` entries as `Name x Qty`) instead of just
the bare `bool` it logged before. Full reasoning and next steps in `TREERESPAWN_HANDOFF.md` Issue C.

## v1.2.6 — gather-respawn liveness diagnostic (2026-06-29, awaiting test)
Re-opens Issues C/D after a fresh shoreline-reed recurrence in Session 3 (reeds visually gone; the per-world
save has **zero `Thatch` pending** despite the bare shore → the harvests never registered). Adds a passive,
`EnableDiagnostics`-gated snapshot in `DayTracker`'s gather-respawn block: right before `Replenish()`, logs
`[diag] gather-respawn "X" at <pos>: Destroyed=.. Active=.. avail A->B qty A->B` and tags `<-- FAKE RESPAWN`
when the instance is dead/streamed-out or the stock didn't move. Behavior-unchanged (it's the M2 *probe*, not
the *guard*). Full incident, M1-vs-M2 framing, test procedure, and candidate fixes in
`TREERESPAWN_HANDOFF.md` → "RE-OPENED 2026-06-29".

## v1.2.8 — deactivated-buffer addressability probe (2026-06-29/30, confirmed in-game)
Read-only diagnostic (`Plugin.ProbeBuffer`) confirming a deactivated node's persistent
`InstancesDataArrays` buffer stays addressable (`buf=set len=<N>`) without force-loading its tile — the
finding that unlocked the v1.2.9 fix below.

## v1.2.9 — handler-based replenish for deactivated nodes (2026-06-29/30, confirmed in-game)
`BiomeProceduralDataHandler.GetInstance(tileId, widId, onlyIfActive:false, noPooling:true)` resolves a
fresh, writable instance for a deactivated gather node; `Replenish()` on that handle + `SetDirty`/
`OnInstanceDataChanged` writes real persistent stock. Confirmed in-game: villagers cycled to a distant
shoreline reed marker and back to base twice while the player stayed put; reeds were genuinely there on
inspection. Shipped behind a config flag, defaulted on once verified (v1.2.10 below).

## v1.2.10 — productionized + persisted across reload (2026-06-30, confirmed in-game)
Renamed the config to `RefillUnloadedGatherNodes` (default `true`); persists each pending gather entry's
`WorldItemInstanceId` across save/reload (the original "shoreline empty after loading a save" symptom);
adds a 30s retry/liveness-guard cooldown so an unresolved node is retried, not dropped. Confirmed in-game
across a save→reload→reload sequence: the deactivated-refill path kept working correctly after a reload,
fixing the original bug. Full mechanism + test evidence in `TREERESPAWN_HANDOFF.md` → "RESOLVED 2026-06-30".

## Issues C & D — RESOLVED 2026-06-30 (history: CLOSED 2026-06-28, RE-OPENED 2026-06-29, see v1.2.6-v1.2.10 above)
The 2026-06-28 "marker test" closure didn't catch the real mechanism because it saved+reloaded, which
refreshes every live pointer and masked the actual failure mode (a stale `ActiveInstances` pointer once a
chunk deactivates mid-session). The 2026-06-29 recurrence reopened the investigation; v1.2.8 found the
node's persistent data stays addressable while deactivated, and v1.2.9/v1.2.10 turned that into a working,
reload-safe fix (see above). The original "shoreline reeds never refill" symptom is now fixed and
confirmed in-game. Full writeup: `TREERESPAWN_HANDOFF.md` → "RESOLVED 2026-06-30".

## v1.3.0/v1.3.1 — tree respawn hardened to gather-path parity + co-op gather detection fix (2026-07-02)
Prompted by a mod review plus a co-op report: the host detected a client's tree chops but never their
gathers, and tree respawn "wasn't 100%" either. Root causes and fixes:
- Trees gained everything gather got in v1.2.9/v1.2.10/v1.2.19 — persisted `WorldItemInstanceId`,
  WID-validated liveness, and handler-based refill for unloaded/stale stumps (see "Tree respawn" above)
  — **confirmed in-game**: a tree felled far from base respawned unattended.
- `DataSyncPatch` reworked to classify nodes from data instead of `GetComponent` on the instance's
  GameObject, closing the likely reason client gathers went undetected (see "Gather resource respawn"
  above) — **✅ confirmed in co-op 2026-07-03 (v1.3.2).**
- New catch-up registration self-heals nodes lost to any historical bug the moment the host next
  streams them in — **confirmed in-game**: 125 catch-up registrations, zero orphaned stumps left in a
  full session.
- Bug fix: v1.2.20's live-gather-respawn pre-state reads were accidentally gated behind
  `EnableDiagnostics`, so with diagnostics off the live path always misjudged its own respawn as fake
  and fell through to the handler fallback. Reads are now unconditional.
- v1.3.1: `DataSyncPatch` gained an `IsExhausted()` gate ahead of all other work — the v1.3.0 log showed
  up to ~105k calls/5s during streaming, nearly all healthy/uninteresting instances. Not
  diagnostics-gated (helps normal play too). **Superseded — as shipped it silently filtered every
  depleted gather node out of the data-sync path; widened in v1.3.2 (below).**
- Egg/nest question answered: a ground bird's-nest is an ordinary gather node yielding `"Feathers"` —
  it follows the exact same respawn logic as flax/reeds/berries (already configured). `Wild Egg` is a
  bonus drop bundled with that gather, not its own node (same shape as `Seeds` on plants) — no separate
  timer exists to tune.
- Full mechanism detail, in-game evidence, and the co-op test plan: `TREERESPAWN_HANDOFF.md` →
  "v1.3.0/v1.3.1 — tree hardening, co-op gather fix, self-healing catch-up".

## v1.3.2 — co-op gather detection CONFIRMED; v1.3.1 gate regression fixed (2026-07-03, confirmed in-game)
The first co-op session on v1.3.1 (2026-07-02) reproduced the gather gap one last time: the client's tree
chop registered via `(data sync)`, his flax pick did not, and the `datasync(5s)` summaries showed
`registered gather=0` in **every window of the whole session** — including the world-load flood, where
~50 depleted gather nodes streamed in and all of them had to be rescued by the SetWorldInstance catch-up.
**Root cause: v1.3.1's hot-path gate required `IsExhausted()==true` before classifying anything, but an
empty gather node reads `false`** — that flag is stump/harvest semantics only (see architecture.md →
Gather). Depleted gather nodes were binned "healthy", structurally re-opening the exact client-gather
blindness v1.3.0 was built to close. v1.3.2 widens the gate to `IsExhausted() || GetQuantity() <= 0` and
adds a `qty0=` counter to the summary line.

**Confirmed in-game 2026-07-03 (co-op):** client gathers (flax, sticks) and tree chops register via
`(data sync)` **both near and far from the host**, and the full loop was watched end-to-end — the client
picked flax on his screen and the node respawned seconds later (short test threshold). Issue A closed.
Perf note: `qty0` runs ~6% of `fired` (up to ~12k/5s under heavy streaming) — those fires now do the
PosKey/dictionary work the gate was added to avoid; unnoticeable this session, but it's the first place
to look if perf ever regresses (the `unknown=` bucket it feeds can't be negative-cached without a
GameObject).

## Mining / stone-clump respawn — abandoned
Investigated and abandoned; do not re-attempt. Full reasoning is in
[architecture.md](../architecture.md#mining--stone-clump-respawn--investigated--abandoned-dont-re-attempt)
and the deep write-up in [`../../TreeRespawnMod/STONE_RESPAWN_HANDOFF.md`](../../TreeRespawnMod/STONE_RESPAWN_HANDOFF.md).

## Config-design dead end (don't retry)
- `GatherRespawnDays` + `GatherRespawnOverrides` (single comma-separated string config) — replaced by individual `Config.Bind<float>` per resource in the `[GatherRespawn]` section; much more user-friendly.
