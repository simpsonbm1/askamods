# TreeRespawnMod — Bug Tracker & Handoff

General handoff for **Mod 2 (TreeRespawnMod)**. Supersedes the old `COOP_RESPAWN_HANDOFF.md`
(which only covered the co-op issue). Covers the whole respawn pipeline, every known issue
(resolved + open), the dead-ends, and the recommended next steps. Read alongside
`docs/mods/tree-respawn.md` (shipped recipe/config) and `docs/architecture.md`
(Resource/Tree, Gather, Worldgen/Streaming subsystems).

Current version: **v1.2.1**.

---

## Status at a glance

| # | Issue | Status |
|---|---|---|
| A | Co-op **client** respawn (client's harvests didn't start a timer on host) | **Fix in place** (DataSyncPatch) — ⚠️ NOT re-confirmed in a live co-op session |
| B | **Cross-world** save contamination (one global file shared by all worlds) | ✅ **FIXED v1.2.1**, confirmed in-game 2026-06-28 |
| C | **Distant villager** gather harvests don't respawn (far foraging markers stay empty) | 🔴 **OPEN** — hypothesis stage, needs diagnostics |
| D | **Overdue** respawns not serviced until you stand near the node as the timer elapses | 🔴 **OPEN** — fix designed, not implemented |

---

## How respawn works (mental model — read first)

Three moving parts, all keyed by **world position** (`PosKey` = `x:y:z` rounded to 0.1m; `UniqueId` is
NOT globally unique):

1. **Registration** — adds a position to a pending dict:
   - `Patches/HarvestPatch.cs` — Postfix `HarvestInteraction.TakeDamage`; registers a felled tree when at its last harvest piece (stump).
   - `Patches/GatherPatch.cs` — Postfix `GatherInteraction.GatherItemsCharge`; registers a gather node when `CheckAvailableItemCount() == 0`.
   - `Patches/DataSyncPatch.cs` — Postfix `WorldItemInstance._OnDataChanged`; the **host-side network catch** (see Issue A). Re-evaluates the same stump/exhaustion conditions when data syncs.
   - All gated on `WeatherSystem.Instance.Runner.IsServer` (host only writes).
2. **Live-instance registry** — `Patches/BiomeInstancePatch.cs` Postfix `BiomeItemInstance.Initialize` populates `Plugin.ActiveInstances[posKey] = instance` as the world streams in.
3. **Servicing** — `DayTracker.cs` `Update()` polls the pending dicts each frame. For each entry it looks up the live instance in `ActiveInstances`; if found and `elapsedDays >= threshold`, it calls `inst.Replenish()` and drops the entry. Trees also cancel if `inst.Destroyed` (stump cleared).

**Per-world isolation (v1.2.1):** the pending dicts persist to a **per-world** file keyed by
`SandSailorStudio.Storage.StorageManager.ActiveSessionID`. `DayTracker.PollWorldId()` resolves it
(`FindAnyObjectByType<StorageManager>()`, polled until known, then every ~60 frames to catch a world
switch). File: `BepInEx/config/com.askamods.treerespawn.<sanitizedSessionId>-<fnv32>.save`.

---

## RESOLVED / IN-PLACE

### A. Co-op client respawn — fix in place, ⚠️ not re-confirmed in co-op
**Symptom:** in co-op, when a **client** (not host, not a villager) felled a tree or exhausted a node,
no respawn timer started and nothing logged on the host.

**Cause (Photon Fusion):** the client runs `TakeDamage`/`GatherItemsCharge` locally and replicates only
the resulting *data state* to the host; the host applies that data **without** re-running those methods,
so the host's Harmony hooks on them never fired for client actions.

**Fix:** Postfix `WorldItemInstance._OnDataChanged` (`Patches/DataSyncPatch.cs`) — fires on the host
whenever a world instance's networked data changes (incl. client actions). It re-checks the stump /
`CheckAvailableItemCount()==0` conditions and registers the respawn host-side.

**Dead-end (do NOT retry):** the first attempt patched `HarvestInteraction._OnWorldInstanceDataChanged`
and `GatherInteraction.OnWorldInstanceDataChanged`. Those are **MonoBehaviour lifecycle-ish methods** the
native engine invokes before the GC handle is ready → fatal `System.InvalidOperationException: Handle is
not initialized` on startup (IL2CPP gotcha #5). The fix was to move to the **pure C# object**
`WorldItemInstance._OnDataChanged` (not a MonoBehaviour) — safe to patch.

**Still pending:** confirm in a real co-op session that a *client's* harvest now starts a timer on the
host (look for `... (data sync) ...` log lines). Note Issues C/D below would still undermine far-from-host
nodes even once the catch fires.

### B. Cross-world save contamination — FIXED v1.2.1 (confirmed in-game 2026-06-28)
**Symptom/cause:** before v1.2.1 there was ONE global save file (`com.askamods.treerespawn.save`) shared by
every world. Entries are keyed by world position, and **positions collide across worlds** (worldgen reuses
the same coordinate space), so a singleplayer world and a co-op world cross-contaminated — an entry from
world A could `Replenish()`/cancel the wrong node, or get consumed, when world B was loaded.

**Fix:** per-world save file keyed by `StorageManager.ActiveSessionID` (see mental model above).
Confirmed in-game: loading singleplayer vs co-op produced **two separate files**.

**Dead-end (do NOT retry): the world SEED is unusable as the world key.** v1.2.0 keyed on the seed via a
`RandomGeneratorManager.SetSeedPhrase` Postfix + a `NetworkSession.Parameters.seed` fallback. Both are
**empty/absent on a LOADED save**: `SetSeedPhrase` only fires during *new-world generation*, and
`RandomGeneratorManager.seedPhrase` / `NetworkSession.seed` read null/empty once a world is loaded
(SeedScout sees the same — its dump logs `Seed = <rng-null>`). v1.2.0 therefore silently did nothing — no
file created, no log line. Use `StorageManager.ActiveSessionID` instead (see architecture.md → "Identifying
the loaded world / save").

**⚠️ Important consequence for diagnosing C/D:** the *old* blended save (65 tree + 110 gather entries) was a
mix of BOTH worlds, so it can't be trusted for analysis. **Re-baseline with the clean per-world files**
before quantifying anything below.

---

## OPEN ISSUES

### C. Distant villager gather harvests don't respawn 🔴
**Symptom:** two shoreline foraging markers where villagers farm **reeds** (item `Thatch`) get wiped clean
and never refill, despite `Thatch = 0.01` days (effectively instant). Reeds the **player** picks while
standing there (just outside the marker) respawn fine.

**Evidence gathered (from the pre-v1.2.1 blended save + current log):**
- The save had **zero `Thatch` entries** and **zero gather entries at all with z > 600** (the shoreline
  region is ~z 660). So those reed nodes were never on the pending list.
- Gather registration demonstrably **works inland**: ~100 veggie/mushroom/feather entries at z 150–495.
- Tree registration **works near the shoreline**: tree entries at z 600–605 exist.
- Player-picked reeds at `182–183 : 34 : 660–661` registered AND respawned in the log (player present).
- So the patch works; the gap is specifically the **distant, villager-driven** gather path.

**Hypotheses (in order):**
1. When a villager (host-authoritative) harvests far from the host *player*, the host may not have that
   area's `GatherInteraction`/`BiomeItemInstance` GameObjects streamed in, so neither `GatherItemsCharge`
   (GatherPatch) nor `WorldItemInstance._OnDataChanged` (DataSyncPatch) fires for it → never registered.
   The big unknown: **does the host instantiate biome nodes around a villager-only (no player) area?** If
   not, the respawn must be driven from the data layer, not GameObjects.
2. (Lower probability) Reeds *are* registered but consumed by a fake respawn vs a stale/unloaded instance
   within the same session before any revisit (see Issue D's liveness gap). Less likely than #1 because the
   inland veggie entries DID persist while these never appeared at all — but a 0.01-day threshold makes the
   consume-window very short, so don't fully rule it out.

**Diagnosis plan (do this first — it's cheap and decisive):**
- Add `EnableDiagnostics`-gated logging (config flag already exists) to log, on every registration attempt:
  which patch fired (GatherPatch / DataSyncPatch.CheckGather), the posKey, and the item name; and in
  `BiomeInstancePatch`, log when a shoreline reed position calls `Initialize` (tells us if the host streams
  that area when only a villager is there).
- **Controlled test:** host stays at base, assign villagers to clear a distant reed marker, `EnableDiagnostics=true`.
  - If **no** `Thatch exhausted` lines appear at shoreline posKeys → registration isn't firing for distant
    villager gather (hypothesis 1). Next: determine whether the area is streamed on the host at all
    (`Initialize` lines?). If it is, find why the gather data-change isn't caught; if it isn't, the fix has
    to hook a host-side data path that fires regardless of GameObject streaming.
  - If lines **do** appear but the node stays empty → it's a servicing/liveness problem → fold into Issue D.

### D. Overdue respawns not serviced until you're near the node at tick time 🔴
**Symptom:** gather entries registered days ago sit in the pending list unrespawned. In the old blended
save, ~100 entries were registered at game-time ~29 and ~38 with current time ~56 — i.e. 18–27 days overdue
at 0.5–1.0 day thresholds — yet still pending.

**Cause:** `DayTracker` only respawns an entry if its live instance is in `ActiveInstances` **at the moment
the timer elapses**. Once you walk away, the area unloads, the `ActiveInstances` lookup fails (`continue`),
and the entry rots. Nothing re-fires the respawn when the area reloads — `BiomeInstancePatch.Initialize`
only refreshes the pointer; it never checks the pending lists.

**Secondary bug (same area):** when `ActiveInstances` *does* still hold a **stale** pointer (within-session,
after streaming-out), `DayTracker` removes the gather entry **before/irrespective of** whether `Replenish()`
actually succeeded (`toRemoveGather.Add(posKey)` runs unconditionally once overdue — see `DayTracker.cs`).
So a respawn can be "consumed" against a dead instance — a **fake respawn** that logs success but restores
nothing. Liveness is never verified.

**⚠️ Confound:** because the old save was cross-world blended, an unknown fraction of those 100 "stuck"
entries actually belonged to the *other* world and could never match the loaded world's geometry. So the
quantity is suspect — **re-measure with clean per-world files**. The structural limitation, though, is real.

**Proposed fix (two parts):**
1. **Respawn-on-reload:** in `BiomeInstancePatch.Initialize`, when a node loads, check
   `PendingRespawns`/`PendingGatherRespawns` for its posKey; if overdue, `Replenish()` immediately against
   that freshly-loaded (guaranteed-live) instance and clear the entry. This is what makes a far node
   actually come back when you return to it — and would also fix Issue C if C turns out to be a
   consumed-while-away problem rather than a never-registered one.
2. **Liveness guard in `DayTracker`:** only remove a pending entry when `Replenish()` ran on a confirmed-live
   instance; otherwise leave it pending. For gather there's no legitimate "cancel" so just skip when not
   loaded. For trees, be careful to distinguish a **real stump-clear** (legit cancel) from an instance that's
   merely **unloaded by streaming** (must NOT cancel) — today the `inst.Destroyed` check can't tell them
   apart, which risks permanently cancelling distant trees.

---

## Recommended next-session order

1. **Re-baseline (v1.2.1).** Play each world briefly; confirm per-world files populate cleanly and
   independently. Throw away conclusions drawn from the old blended save.
2. **Diagnose Issue C** with the `EnableDiagnostics` logging + the distant-villager reed test above. This
   tells us whether the problem is *registration* (host doesn't see the gather) or *servicing* (fake
   respawn while away).
3. **Implement Issue D's fix** (respawn-on-reload + liveness guard). It's self-contained, low-risk, helps
   the inland veggies immediately, and may also resolve C.
4. **Fix Issue C** based on step 2's finding (most likely a host-side data-path hook, or accepting that the
   host must stream the area — in which case respawn-on-reload from step 3 covers the revisit).
5. **Confirm co-op (Issue A)** in a live session while you're at it (watch for `(data sync)` log lines from
   a client's harvest).

## Diagnostic playbook (concrete)

- **Logging to add (all `EnableDiagnostics`-gated):**
  - `GatherPatch` / `DataSyncPatch.CheckGather` / `HarvestPatch` / `DataSyncPatch.CheckHarvest`: `[diag] register <source> <posKey> <item>`.
  - `BiomeInstancePatch`: `[diag] init <posKey>` (so you can see whether a far area streams in on the host).
  - `DayTracker`: when an entry is overdue but `ActiveInstances` lacks the key, log `[diag] overdue-but-not-loaded <posKey>`; when it Replenishes, log whether the instance was confirmed live.
- **Test C:** host at base → villagers clear a distant reed marker → grep the log for `Thatch` + the shoreline z (~660) and for `[diag] init` at those positions.
- **Test D:** harvest/exhaust a node far from base → walk away for > one threshold → return → it should respawn on arrival (with fix) vs stay empty (without).

## Reference

- **Source:** `TreeRespawnMod/Plugin.cs` (state, per-world save, config), `DayTracker.cs` (servicing + world-id poll), `Patches/{HarvestPatch,GatherPatch,DataSyncPatch,BiomeInstancePatch,StumpProtectionPatch,WorkerIdleDiagPatch}.cs`.
- **Save format:** sections `# tree` (`posKey,gameTime`) and `# gather` (`posKey,gameTime,itemName`); legacy `# mining` skipped; headerless old files load as tree entries.
- **Reed config:** `Thatch` (reeds yield item name). All gather keys are substring, case-insensitive (`Fiber` matches `Fibers`). See `docs/mods/tree-respawn.md`.
- **Type inspector:** `_explore/typedump.ps1 -Types @("Name")` dumps a game type's members (Cecil over the interop DLLs) — how `StorageManager.ActiveSessionID` was found.
