# Mod 2: TreeRespawnMod — COMPLETE (v1.2.20)

**Goal:** Respawn felled trees (stump condition) and exhausted gather resources (reeds, berries, etc.)
after configurable in-game days.

**Game subsystems:** [Resource / Tree System](../architecture.md#resource--tree-system) and
[Gather / Press-to-Collect System](../architecture.md#gather--press-to-collect-system) — both carry
the confirmed facts and dead-ends (including why **mining/stone-clump respawn was abandoned**).

## Tree respawn
- Postfix patch on `HarvestInteraction.TakeDamage` — after each hit, check `GetCurrentPieceIndex() == harvestPieces.Count - 1` to detect stump stage
- `_worldInstance.TryCast<BiomeItemInstance>()` to get the biome instance; non-biome resources (rocks etc.) return null and are skipped
- Store `WeatherSystem.NetworkedCurrentGameTime` at fell time; compare elapsed via `GetTimeDifferenceFromCurrentGameTimeInSeconds() / dayLength` against threshold
- Key everything by **world position** string (`GetPosition()` rounded to 0.1m, format `x:y:z`) — `UniqueId` is NOT globally unique
- `BiomeInstancePatch` Postfix on `BiomeItemInstance.Initialize()` populates `ActiveInstances[posKey]` as world loads; `DayTracker` looks up the live instance at check time
  - ⚠️ **`ActiveInstances` is only *added to* / *wiped on world-switch*** — it is never pruned when an individual node unloads, so a streamed-out node leaves a **stale** pointer (`Active=false`/`Destroyed`). For trees this just means a respawn waits until the node streams back in. For gather nodes, **resolved in v1.2.10** — see "Gather resource respawn" below for the deactivated-node refill path.
- Stump-harvested detection: check `inst.Destroyed` at each tick (no event subscription needed)
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
- **Host validation in Co-op (v1.2.13).** The mod previously validated host authority using `WeatherSystem.Instance.Runner.IsServer`, which evaluated to `false` in co-op. It now tracks `Plugin.LocalPlayer` via `PlayerCharacter.Spawned` and uses `LocalPlayer.NetworkObject.Runner.IsServer` as a primary check. (Issue A RESOLVED)
- **Manual Respawn Hotkey (v1.2.14/15).** Added a hotkey (`t` by default) to manually replenish any stump or exhausted gather node within a configurable radius (default `10m`). Bypasses the pending list entirely to help fix manually deforested areas. Scans `ActiveInstances` for depleted gather nodes and `Resources.FindObjectsOfTypeAll<HarvestInteraction>()` for physical stumps in the scene. Configs: `ManualRespawnHotkey`, `ManualRespawnRadius`, `ManualRespawnIncludeGather`. Host check fixed in v1.2.15.

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

## Mining / stone-clump respawn — abandoned
Investigated and abandoned; do not re-attempt. Full reasoning is in
[architecture.md](../architecture.md#mining--stone-clump-respawn--investigated--abandoned-dont-re-attempt)
and the deep write-up in [`../../TreeRespawnMod/STONE_RESPAWN_HANDOFF.md`](../../TreeRespawnMod/STONE_RESPAWN_HANDOFF.md).

## Config-design dead end (don't retry)
- `GatherRespawnDays` + `GatherRespawnOverrides` (single comma-separated string config) — replaced by individual `Config.Bind<float>` per resource in the `[GatherRespawn]` section; much more user-friendly.
