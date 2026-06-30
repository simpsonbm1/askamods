# TreeRespawnMod — Bug Tracker & Handoff

General handoff for **Mod 2 (TreeRespawnMod)**. Supersedes the old `COOP_RESPAWN_HANDOFF.md`
(which only covered the co-op issue). Covers the whole respawn pipeline, every known issue
(resolved + open), the dead-ends, and the recommended next steps. Read alongside
`docs/mods/tree-respawn.md` (shipped recipe/config) and `docs/architecture.md`
(Resource/Tree, Gather, Worldgen/Streaming subsystems).

Current version: **v1.2.15**. v1.2.2 was a version-only bump from v1.2.1 — Smart App Control blocked
the v1.2.1 DLL hash on the second machine with `FileLoadException ... 0x800711C7`; bumping the version
changes the hash so SAC re-evaluates it and lets it load (no logic changed). v1.2.3 implements the
diagnostic logging for Issues C/D described below (was previously just a plan) — see "Diagnostic
playbook" for what's now actually logged. v1.2.4 dedupes `[diag] init` to fire once per position per
world instead of on every re-stream — a single test run logged the same handful of positions 90+ times
each (confirmed in-game 2026-06-28) and caused noticeable hitching; same diagnostic value, far less log
volume. v1.2.5 enriches the `NoResourcesFound` worker-idle diagnostic with the villager's name and the
actual `ItemManifest` they were searching for — needed after 2026-06-28 testing complicated the
distance-only theory for Issue C (see that section). v1.2.6 (2026-06-29) added a passive liveness/stock
snapshot to the gather-respawn path (the **M2** probe) and v1.2.7 added a complementary registration-time
probe (player→node distance + liveness at harvest) — both diagnostic-only, see "v1.2.6 RESULT + v1.2.7
probe" below for what they found. **v1.2.8 (2026-06-29/30) confirmed a deactivated node's persistent data
buffer stays addressable without force-loading the tile**, which unlocked **v1.2.9**'s handler-based
replenish (`BiomeProceduralDataHandler.GetInstance(onlyIfActive:false)` + `Replenish()`) — confirmed
in-game refilling a distant shoreline marker while the player stayed at base. **v1.2.10 (2026-06-30)
productionizes that mechanism** (`RefillUnloadedGatherNodes`, default ON) with `WorldItemInstanceId`
persistence across save/reload and a retry/liveness-guard cooldown. **v1.2.14 (2026-06-30)** adds a **manual respawn hotkey** (`'t'`) to instantly refill stumps and exhausted nodes near the player. **v1.2.15** fixes the host check for the hotkey so it works in co-op.
**Issues C and D are RESOLVED** — see "RESOLVED 2026-06-30" below.

---

## Status at a glance

| # | Issue | Status |
|---|---|---|
| A | Co-op **client** respawn (client's harvests didn't start a timer on host) | **Fix in place** (DataSyncPatch) — ⚠️ NOT re-confirmed in a live co-op session |
| B | **Cross-world** save contamination (one global file shared by all worlds) | ✅ **FIXED v1.2.1**, confirmed in-game 2026-06-28 |
| C | **Distant villager** gather harvests don't respawn (far foraging markers stay empty) | ✅ **RESOLVED v1.2.10**, confirmed in-game 2026-06-30 — `GetInstance(onlyIfActive:false)` + `Replenish()` refills the node through the persistent data handler without streaming the tile in; see "RESOLVED 2026-06-30" below |
| D | **Overdue** respawns not serviced until you stand near the node as the timer elapses | ✅ **RESOLVED v1.2.10**, confirmed in-game 2026-06-30 — same fix as C (the overdue-but-unloaded case is exactly what the handler-based replenish services); retry/liveness-guard cooldown added so an unresolved node is retried, not dropped |
| E | Mod's save file isn't tied to the game's own save event (can register a respawn the game never persisted) | 🟡 **PARKED** — confirmed benign 2026-06-28, fix designed, not being chased yet |
| F | Villagers permanently stuck wanting `Fibers x 15` — **NOT a TreeRespawnMod bug**, see dedicated section | 🔵 **TRACKED, OUT OF SCOPE** — confirmed repeatable in 2 independent worlds 2026-06-28; vanilla AI/quota quirk |

---

## RE-OPENED 2026-06-29 — shoreline reeds gone again (fresh incident, the live investigation)

**This is the current focus.** Per the closure note's own instruction ("if a *harvested but never respawns*
symptom shows up again under current code, treat it as a **new, fresh incident**"), C/D are re-opened. The
2026-06-28 "CLOSED" writeups further down are kept for history; **read this section first.**

### The incident
User spent a whole session parked at the **Session 3** base (`e7915_240626011713`). The shoreline reed
marker (reeds yield item `Thatch`) was respawning fine "for a while," then **suddenly stopped** — the
shoreline is **visually empty again** (user-confirmed in-game 2026-06-29).

### Evidence (this is solid)
- **Log:** `Thatch exhausted`/`respawned` pairs cycle rapidly early (shoreline z≈613–666), then **vanish
  entirely** partway through — zero `Thatch` lines of any kind afterward — while fibers/trees/veggies keep
  registering and respawning at **inland** coords (z 480–632).
- **Save (`...e7915_240626011713-18ea39d0.save`):** holds trees + 1 Fibers + 1 Onion pending and **zero
  Thatch entries.** So the mod has **no record those reeds need respawning** → not a "timer hasn't elapsed"
  case; they will *never* return on their own.
- **Confirmed in-game 2026-06-29:** walking to the marker and standing next to the empty reeds does **not**
  refill them — consistent with "nothing is registered," not "waiting on a timer."
- Registration is logged **unconditionally** (`GatherPatch` etc.), so the absence of late `Thatch exhausted`
  lines means the emptying harvests **never reached the mod's hook** on this machine.

### The reframe that narrows everything (important)
1. **Villager gathers DO register — when the chunk is loaded.** The same log shows villagers farming flax
   across x 100–284 (a 280m spread the player wasn't walking), every one caught by `GatherItemsCharge`. So
   this is **not** "villagers use a different code path." When loaded, our pipeline sees villager harvests.
2. **A loaded reed cannot stay empty.** `Thatch = 0.01d` ⇒ respawns in seconds while loaded. So an empty
   shoreline *proves* the reeds were emptied during a window when their chunk was **not loaded on the host**
   — or that the respawn was silently undone (M2). There is no "loaded but stuck empty" state.

→ The failure is **specifically and only the unloaded-chunk case.**

### Two mechanisms fit the evidence — different fixes. The diagnostics-off log can't separate them:
- **M1 — never registered.** Villagers (or an abstract village sim) deplete reeds while the chunk is
  unloaded on the host → `GatherItemsCharge` never runs here → no registration → no respawn. The final
  emptying would have **no log line at all.**
- **M2 — fake respawn on unload.** A reed is harvested while loaded → registered → its chunk unloads within
  the pending window → on the next tick `DayTracker` finds the **stale, never-pruned** `ActiveInstances`
  pointer to the dead instance, calls `Replenish()` on garbage, and **drops the entry anyway**. Bookkeeping
  thinks it respawned; the node never refills. Final emptying would show an `exhausted`+`respawned` pair.

**Code fact backing M2 (verified 2026-06-29):** `ActiveInstances` is only ever *added to*
(`BiomeInstancePatch.cs:19`, every re-stream) or *wiped wholesale on a world switch* (`Plugin.cs:174`).
**Nothing prunes it when an individual node unloads** — so dead pointers linger, and the gather loop in
`DayTracker` has **no liveness/`Destroyed` check** (unlike the tree loop, which checks `inst.Destroyed`).
A streamed-out `BiomeItemInstance` reports `Active=false` (and/or `Destroyed`) even while still in the
registry. So M2 is *structurally possible*, independent of whether it's the cause here.

**Why the 2026-06-28 marker test missed M2:** that test **saved + reloaded**, which re-streams the scene and
**repopulates `ActiveInstances` with fresh live pointers** — so it serviced reeds against live instances (a
real respawn). M2 only bites in a **continuous (no-reload) session** where the pointer goes stale and the
timer crosses while it's still in the registry. That condition was never created.

### v1.2.6 diagnostic (the M2 probe — built 2026-06-29, awaiting test)
Purely additive, `EnableDiagnostics`-gated, behavior-unchanged. In the gather-respawn block, right before
`Replenish()`, it snapshots the instance and logs (reads wrapped so a throw on a dead instance can't disturb
the `Replenish`/removal path):
```
[diag] gather-respawn "Thatch" at <pos>: Destroyed=False Active=True avail False->True qty 0->3
```
- `Active=false` ⇒ streamed-out/deactivated (the stale-pointer case `Destroyed` alone would miss).
- `avail X->Y` / `qty X->Y` ⇒ stock **before vs. after** `Replenish` — proves functionally whether it did
  anything. A non-live instance or a no-op refill gets tagged:
  `... avail False->False qty 0->0  <-- FAKE RESPAWN (instance not live / did not refill)`.

That tag is the **M2 fingerprint** — no distance-guessing; just grep the log.

### TEST INSTRUCTIONS (run next session)
1. Set `EnableDiagnostics = true` in `com.askamods.treerespawn.cfg` (before launch or at main menu). Confirm
   `TreeRespawnMod 1.2.6` loads in `LogOutput.log` (SAC — if blocked, bump version again).
2. Load Session 3 and **play one continuous session** at the base. **Do NOT save/reload mid-session** — a
   reload refreshes every pointer and masks M2.
3. Play normally **with movement** — there must be **streaming churn** (chunks unloading while entries are
   pending) or M2 can't show. The diagnostic fires on **every** gather type, so **reeds are not required**:
   the 0.5-day **veggies** (Beetroot/Carrot/Cabbage/Onion — an Onion is already pending) are the best probe
   (long pending window = more chance to catch an unload), and the **far plots** (x≈200–280) are prime
   candidates. Optional reed amplifier: set `Thatch = 0.1` for the session (revert after) so reed entries
   live long enough to catch an unload. Optional M1 freebie: set up a *distant* reed marker and watch
   whether it appears in `[diag] init` (does it stream in at all?) and whether `Thatch exhausted` ever fires.
4. Quit, hand over the log.

### Decision tree from the log
- **Any `<-- FAKE RESPAWN` lines** → **M2 confirmed.** Fix = small surgical **liveness guard** in the gather
  loop: don't drop an entry unless `Replenish` hit a confirmed-live instance (and prune/validate
  `ActiveInstances`). Reuses the existing pipeline, no new semantics; correct hardening regardless of cause.
- **No FAKE RESPAWN tags but reeds still empty** → **M1.** Pivot to the root-cause routes below.

### Candidate fixes (do NOT build until the test forks M1/M2)
- **M2 → liveness guard** (smallest, in-architecture). Also fixes the latent `ActiveInstances` stale-pointer
  defect + slow per-session leak.
- **M1 → option A: keep the marker's chunk streamed-in** while a villager works it (force-load / add a
  streaming source via `WorldStreamingManager` — WarpTour already captures its `Awake`; read-only API recon
  first). Most root-cause, deepest unknown, perf/stability risk — scope narrowly.
- **M1 → option B: hook a persistent data object** that changes when a reed is consumed while unloaded
  (forage-marker record / village resource ledger) — the `DataSyncPatch`-analogous catch. Note villagers are
  **host-authoritative** (not a co-op replication gap), so the only gap is the unloaded state; this only
  works if *some* persistent host-side data still changes while unloaded.
- **Last resort: proactive refill-on-load** for instant resources. Biggest blast radius — parallel mechanism,
  breaks timer semantics for non-instant resources unless carved out, can't tell "depleted" from
  "intentionally cleared," and only tops up when *you* arrive (doesn't supply villagers while you're away).

### v1.2.6 RESULT (2026-06-29) — `FAKE RESPAWN` fires heavily, but the *interpretation* is now in doubt
Test run under v1.2.6 (`EnableDiagnostics=true`, Session 3, continuous with movement). Confirmed in the log:
- **46 of 62** gather-respawn attempts tagged `<-- FAKE RESPAWN`. **All 46 were the `!preActive` branch**
  (`Active=False`); **zero** were the "live-but-didn't-refill" variety. So the tag is driven *entirely* by
  `Active=False`, not by stock failing to move.
- **34 of those 46** had `qty` actually move on the `Active=False` instance (`0->1`, `0->6`, …) — i.e.
  `Replenish()` *did* write to the object; it just wasn't (we assumed) the live one.
- **0** `overdue-but-not-loaded` lines — entries were NOT rotting unserviced; they were being actively
  serviced (falsely) and dropped. That distinguishes this from the old Issue-D "rot" framing.
- Shoreline reed marker (neg-x, z≈613–648, item `Thatch`): **32 fake / 10 real** — directly matches the
  "shoreline empty again" symptom.

**But two findings block calling this a clean M2 win:**
1. **All 60 gather registrations were direct `GatherItemsCharge` (zero `(data sync)`).** So depletions are
   NOT an abstract-ledger deduction — every harvest ran a real, instantiated `GatherInteraction`
   MonoBehaviour. If villagers are draining far markers, they do it through **live work-GameObjects**, not a
   spreadsheet.
2. **The type dump (2026-06-29) shows `WorldItemInstance.Active` is backed by an instance-activation-CONTEXT
   system** (`ActivateContext(InstanceActivationContext)`/`DeactivateContext`, `HasActiveContext`,
   `c_flagInstanceActive`), **not confirmed to mean "tile streamed in near the player."** The whole
   "`Active=False` ⇒ streamed-out ⇒ fake" reading is an *assumption baked into the code comment*, not a
   verified fact. Also note the **harvest object ≠ respawn object**: registration fires on
   `GatherInteraction` (MonoBehaviour); respawn calls `Replenish()` on `BiomeItemInstance` (a pure data
   object that *holds* a `gameObject` and exposes `ForceRefreshGameObject()`, `IsAvailable()`,
   `IsExhausted()`, `GetQuantity()`).

**Net:** the *symptom* (registered reeds never refill) is reproduced and traceable, but the **premise that a
villager can't harvest an unloaded tile is UNCONFIRMED**, and so is the meaning of `Active`. Do **not** build
the liveness guard yet — gating "is this instance live?" on a wrong reading of `Active` could keep entries
pending forever (never serviced → worse than today).

**Lead worth testing:** `BiomeItemInstance.ForceRefreshGameObject()`. If `Replenish()` fails on a
non-active instance only because the GameObject isn't refreshed, forcing that may make the respawn *stick*
without needing the tile to stream in — sidestepping the streaming problem entirely. Speculative.

### v1.2.7 probe — instrument the REGISTRATION path (built 2026-06-29, awaiting test)
Purely additive, `EnableDiagnostics`-gated, behavior-unchanged. New capture `Patches/Captures.cs` grabs
`BiomesManager` (Awake/OnDisable) so `Plugin.TryGetPlayerPos` can read the local player's position
(`BiomesManager._localPlayerTransform` — same source WarpTour trusts). In `GatherPatch`, right after a node
registers as exhausted, it logs **at harvest time**:
```
[diag] gather-reg "Thatch" at -150.0:33.0:620.0: playerDist=212m inActiveInst=True instActive=False destroyed=False interactGO=active
```
- `playerDist` — horizontal (x,z) distance from the host player to the node. **The headline measure**,
  immune to the `Active` ambiguity.
- `inActiveInst` — was the node ever in `ActiveInstances` (did `BiomeInstancePatch.Initialize` fire for it).
  `False` here = harvested a node the host never streamed in → strong M1 evidence.
- `instActive` / `destroyed` — the `BiomeItemInstance`'s own state at harvest (compare against the
  `gather-respawn` snapshot's `Active` for the same posKey to learn what `Active` tracks across the two paths).
- `interactGO` — the `GatherInteraction` MonoBehaviour's own `activeInHierarchy`.

### TEST INSTRUCTIONS (v1.2.7 — run next session)
1. Confirm `TreeRespawnMod 1.2.7` loads in `LogOutput.log` (SAC — bump again if blocked). `EnableDiagnostics=true`.
2. **Place a fresh reed (or veggie) marker FAR from base**, then immediately run back to base and **stay there.**
   The goal is to have villagers walk out and harvest a node while you remain at base (the exact scenario the
   user described). Optionally bump `Thatch=0.1` so reed entries live long enough to also catch the respawn side.
3. Let villagers work the far marker. Quit, hand over the log.

### Decision tree (v1.2.7)
- **Registrations show large `playerDist` (and/or `inActiveInst=False` / `instActive=False`)** → villagers
  DO exhaust nodes far from / not streamed near the host. The premise is wrong; a liveness guard alone can't
  give hands-off respawn — you'd need force-load (`RequestLoadWorldTile`, confirmed working — see
  architecture.md → Worldgen/Streaming) for active far markers, or the `ForceRefreshGameObject` trick.
- **Registrations only ever show small `playerDist` with `instActive=True`** → harvests happen near the host;
  the premise holds, and the liveness guard (now safely gateable on a validated `Active`) is sufficient for
  the home case. Calibrate the guard's "live" condition from the harvest-vs-respawn `Active` comparison.

---

## RESOLVED 2026-06-30 — the `GetInstance(onlyIfActive:false)` breakthrough

### v1.2.8 probe — is a deactivated node's persistent buffer still addressable? (2026-06-29/30)
Read-only, `EnableDiagnostics`-gated (`Plugin.ProbeBuffer`, captured before `Replenish()`). Answers what
the v1.2.7 decision tree left open: for a node whose `BiomeItemInstance` reports `Active=false`, is its
**persistent** `InstancesDataArrays` buffer (`_buffer.instances`) still a valid native array — i.e. is
"deactivated" just an activation-CONTEXT flag (data intact, not streamed), or is the slot itself gone while
unloaded (which would force a force-load approach)?

**Confirmed in-game:** `buf=set len=<N> active=<M>` on deactivated instances — the persistent buffer is
alive and the slot is still there. **Force-loading the tile is NOT required.**

### v1.2.9 — "Schrödinger's reeds": handler-based replenish for deactivated nodes (built+tested 2026-06-29/30)
Instead of calling `Replenish()` on the stale cached pointer (`ActiveInstances[posKey]` — deregistered once
the chunk unloads, `RegisteredInstanceIdx` negative, `UniqueId` reset to 0), re-resolve a **fresh, valid**
instance handle through the data handler itself, without streaming the tile in:
```
SSSGame.BiomeProceduralDataHandler.GetInstance(WorldTileId tileId, WorldItemInstanceId widId,
    onlyIfActive:false, noPooling:true) : WorldItemInstance
```
`onlyIfActive:false` is the load-bearing part — it returns a usable handle for a deactivated node instead of
`null`. Cast to `BiomeItemInstance`, call the game's own `.Replenish()` on it, then `handler.SetDirty(cell)`
+ `handler.OnInstanceDataChanged(instance)` to flush/notify so the AI/streaming system picks it up.

**Confirmed in-game (v1.2.9 single-session test):** watched the woodcutters cycle to the distant shoreline
reed marker and back to base **twice** while staying at base; walked out afterward and the reeds were
genuinely there. Log: **37/37** `[diag] exp-replenish ... OK (stock written)`, zero errors, zero
`NO-CHANGE`. First time the distant-shoreline-reed symptom — the original motivation for this whole
investigation — was watched succeeding end-to-end with the player never near the node.

### v1.2.10 — productionized: persistence across reload + retry/liveness guard (built+tested 2026-06-29/30)
- **Config renamed** `ExperimentalDeactivatedReplenish` → `RefillUnloadedGatherNodes`, **default `true`**
  (shipped default-on once written, per explicit instruction — not gated behind a second approval round).
- **`WorldItemInstanceId` persistence across save/reload.** v1.2.9's `GatherWid` cache was session-only —
  it would NOT have survived a reload, which is exactly the original "shoreline empty after loading a
  save" symptom. v1.2.10 persists each pending gather entry's `WorldItemInstanceId.Raw` (`ulong`) to the
  per-world `.save` file (`posKey,gameTime,widRaw,itemName`) and reconstructs it via
  `new WorldItemInstanceId(widRaw)` on load. Old 2-/3-field lines still parse (disambiguated by attempting
  `ulong.TryParse` on the field after `gameTime` — a real item name is never purely numeric).
- **Retry/liveness-guard cooldown.** If `DeactivatedReplenish` can't resolve a node yet (handler null, no
  cached wid, `GetInstance` null, etc.) the entry is **kept pending** and retried after a 30s cooldown
  (`DayTracker._lastDeactAttempt`) instead of being silently dropped.
- `[diag] exp-replenish` lines gated behind `EnableDiagnostics`; a real `Replenish()` exception always
  logs via `LogError` regardless.

**Confirmed in-game (multi-session test spanning `save.txt`→`load.txt`→`load2.txt`):**
1. Played, watched the distant marker refill successfully, saved (`save.txt`).
2. Reloaded that save (`load.txt`) — log shows `Loaded 16 tree + 1 gather pending`, i.e. the
   `WorldItemInstanceId` round-tripped through the save file correctly. The game crashed partway through
   this session — see "Open item" below; ruled non-mod-related.
3. Reloaded the SAME save again (`load2.txt`) — woodcutters went back out to the shore and successfully
   re-gathered reeds. **This is the original bug, fixed**: a gather node whose deactivated-refill state was
   persisted across a save/reload continued to work correctly post-reload. Every `[diag] exp-replenish`
   line in both `load.txt` and `load2.txt` read `OK (stock written)` — zero `NULL`, zero `no cached wid`,
   zero `NO-CHANGE`, zero `Replenish threw`.

**Open item: `load.txt` crash (2026-06-29, unresolved but considered low-priority/non-mod).** During the
`load.txt` session the game crashed with no exception attributable to any mod in the visible log excerpt —
it ends cleanly mid-diagnostic-output, no stack trace, no error from any patch. TreeRespawnMod's one
unconditional error path (`LogError` on a `Replenish()` throw) never fired in any of the three session
logs. The identical save + identical DLL reloaded cleanly in the very next session (`load2.txt`) and ran
the same deactivated-write path extensively without recurrence, and the user reported continued play
afterward with no further crashes. Treated as a transient engine/streaming hiccup, not a TreeRespawnMod
defect — revisit only if it recurs with a specific repro.

**Status: Issues C & D — RESOLVED 2026-06-30, confirmed in-game across both a single session (v1.2.9) and
a save/reload boundary (v1.2.10).** The mechanism directly fixes the original "distant villager-gathered
resources never come back" symptom that motivated this entire investigation.

### Issue A: Co-op Host Detection Failure (FIX ATTEMPTED 2026-06-30, v1.2.13, PENDING CONFIRMATION)
**Symptom:** In co-op, the mod's log did not realize the user was the host, stopping the entire respawn process.
**Root Cause:** `WeatherSystem.Instance.Runner.IsServer` evaluated to `false` in co-op.
**Fix:** Mirrored `HealthRegenMod` and `MineRefreshMod`'s pattern by tracking `Plugin.LocalPlayer` via `PlayerCharacter.Spawned`/`Despawned`. Updated `TryGetServerWeather` to verify authority using `LocalPlayer.NetworkObject.Runner.IsServer` and importantly `LocalPlayer.NetworkObject.Runner.IsSharedModeMasterClient` (as `IsServer` alone returns false in Fusion Shared Mode).

### Manual Respawn Hotkey (v1.2.14/15, PENDING IN-GAME CONFIRMATION)
**Goal:** Provide a hotkey (`t` by default) to manually trigger `Replenish()` on any exhausted node (stump or gather) within a configurable radius (default `10m`) of the player. Intended to fix deforested areas where bugs previously prevented timers from triggering. Host check fixed in v1.2.15 to use `IsSharedModeMasterClient`.
**Implementation details:**
- Scans `Plugin.ActiveInstances` to find depleted gather nodes (`!inst.IsAvailable() && inst.GetQuantity() <= 0`) and replenishes them.
- Scans `Resources.FindObjectsOfTypeAll<HarvestInteraction>()` to find physical stumps (`pieces >= 2 && GetCurrentPieceIndex() == pieces.Count - 1`), then works backward to its `_worldInstance.TryCast<BiomeItemInstance>()` and triggers `Replenish()`.
**Why this split approach?** The diagnostic log showed that for trees, `BiomeItemInstance.gameObject.GetComponent<HarvestInteraction>()` was returning `null`. The physical `HarvestInteraction` object is disconnected hierarchically from the abstract `BiomeItemInstance` data layer (or they don't sit on the same GameObject). Doing a global scene search for `HarvestInteraction` completely bypasses the need to traverse the complex prefab hierarchy.
**Verification pending:** The user will test this on their second machine. Press `t` while standing near a deforested area to see if physical stumps properly regrow into trees.

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
3. **Servicing** — `DayTracker.cs` `Update()` polls the pending dicts each frame. For each entry it looks up the live instance in `ActiveInstances`; if found and `elapsedDays >= threshold`, it calls `inst.Replenish()` and drops the entry. Trees also cancel if `inst.Destroyed` (stump cleared). **Gather entries whose node isn't live** (chunk deactivated) go through `DeactivatedReplenish` instead (v1.2.10, `RefillUnloadedGatherNodes`) — re-resolves a fresh instance via `BiomeProceduralDataHandler.GetInstance(onlyIfActive:false)` using the node's persisted `WorldItemInstanceId`, and `Replenish()`s that — see "RESOLVED 2026-06-30" above.

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

### B. Cross-world save contamination — FIXED v1.2.1 (confirmed in-game 2026-06-28 on both machines)
**Symptom/cause:** before v1.2.1 there was ONE global save file (`com.askamods.treerespawn.save`) shared by
every world. Entries are keyed by world position, and **positions collide across worlds** (worldgen reuses
the same coordinate space), so a singleplayer world and a co-op world cross-contaminated — an entry from
world A could `Replenish()`/cancel the wrong node, or get consumed, when world B was loaded.

**Fix:** per-world save file keyed by `StorageManager.ActiveSessionID` (see mental model above).
Confirmed in-game: loading singleplayer vs co-op produced **two separate files**. Re-confirmed on the
second machine 2026-06-28 under v1.2.2 — two fresh per-world files populated cleanly (one tree entry,
one with several gather entries), no cross-contamination.

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

## CLOSED — INVESTIGATION EXHAUSTED (2026-06-28)

Both issues below are closed **not because they're resolved, but because the evidence needed to settle
them no longer exists**, and the current respawn mechanism has been directly tested and works. If a
similar symptom shows up again under current code, treat it as a fresh, traceable incident — not a
continuation of this one.

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

**Background:** this is the original motivation for the whole respawn-investigation effort — the user
reports villagers complaining about missing resources is what prompted looking into respawn behavior to
begin with, well before the shoreline-marker framing below. Keep that in mind: the "two shoreline
markers" symptom may be one visible instance of a broader pattern, not necessarily the whole story.

**Hypotheses:**
1. **Distance/streaming (original hypothesis, now WEAKENED by 2026-06-28 testing below — see why it
   can't be the sole cause).** When a villager (host-authoritative) harvests far from the host *player*,
   the host may not have that area's `GatherInteraction`/`BiomeItemInstance` GameObjects streamed in, so
   neither `GatherItemsCharge` (GatherPatch) nor `WorldItemInstance._OnDataChanged` (DataSyncPatch) fires
   for it → never registered.
2. (Lower probability) Reeds *are* registered but consumed by a fake respawn vs a stale/unloaded instance
   within the same session before any revisit (see Issue D's liveness gap).
3. **NEW, promoted after 2026-06-28 testing:** workers are fixated on a specific resource (by priority or
   by whatever the game's gather-target search is keying on) that's unavailable/misidentified, and this
   is **independent of host distance** — they starve the same way whether the host is far away or
   standing right at the village. This would mean the "distant shoreline marker" symptom is a special
   case (that resource genuinely never streams in), layered on top of a separate, more general problem.

**Evidence gathered (pre-v1.2.1 blended save):**
- The save had **zero `Thatch` entries** and **zero gather entries at all with z > 600** (the shoreline
  region is ~z 660). So those reed nodes were never on the pending list.
- Gather registration demonstrably **works inland**: ~100 veggie/mushroom/feather entries at z 150–495.
- Tree registration **works near the shoreline**: tree entries at z 600–605 exist.
- Player-picked reeds at `182–183 : 34 : 660–661` registered AND respawned in the log (player present).
- So the patch works (when it fires); the gap is specifically the **distant, villager-driven** gather path.

**Controlled tests run 2026-06-28 (v1.2.3/v1.2.4, clean per-world data, "Session 3"):**
- **Test 1 — host stayed far from the village the entire session** (per-world save started this session
  at "3 tree + 0 gather pending"): zero `Gather resource exhausted` lines, zero `Tree felled` lines, zero
  `[diag] overdue-but-not-loaded` lines. 41 `NoResourcesFound` worker-idle complaints (pre-existing
  diagnostic), clustering into a dense unbroken burst of 23 in a row (every 1.5s throttle window firing,
  ~34+ continuous seconds) right before the session ended.
- **Test 2 — host stayed far for ~1-2 min, ran back to the village, stayed there ~1-2 min:** still
  **zero** `Gather resource exhausted` lines the entire session, even while standing at the village. Only
  2 `Tree felled` lines, both at z 600-605 (the near-base range per the evidence above). `NoResourcesFound`
  complaints did not stop or thin out near the village — if anything the densest, most continuous burst
  (~28 in a row, nearly every consecutive log line) is at the very end, i.e. *while at the village*.
- **Why this weakens hypothesis 1 as the sole explanation:** if the problem were purely "the host doesn't
  stream in distant villager-only areas," returning to the village should have unstarved at least the
  *gather* (reed/berry/mushroom) workers immediately — tree-felling partially resumed (2 events), gathering
  did not (0 events), and the idle complaints, if anything, got worse while at base. That pattern doesn't
  match "distance is the blocker."

**Diagnosis plan:**
- ✅ **Registration logging already existed** before any new code — `GatherPatch` and
  `DataSyncPatch.CheckGather` both log unconditionally (not even gated) on every registration:
  `Gather resource "X" exhausted at <posKey>...` (GatherPatch — direct call) vs
  `...exhausted (data sync) at <posKey>...` (DataSyncPatch — the `(data sync)` suffix tells you
  which patch fired without needing a new log format).
- ✅ **v1.2.3:** `BiomeInstancePatch` logs `[diag] init <posKey>` on first `Initialize()` per position
  per world (deduped in v1.2.4 — see version history above), gated on `EnableDiagnostics`.
- ✅ **v1.2.5 (2026-06-28):** `ComplainNoResourcesFound`'s Postfix (`Patches/WorkerIdleDiagPatch.cs`) now
  captures `__instance` and the `finderManifest` parameter the game itself passes — previously the patch
  only saw the `bool value` return. Now logs **which villager** (`GetVillager().GetName()`) and **what
  they were searching for** (`finderManifest.GetItems()` → up to 5 `ItemInfoQuantity` entries as
  `Name x Qty`). This is what's needed to tell hypothesis 1 (distance/streaming) apart from hypothesis 3
  (fixated on something unavailable/misidentified regardless of distance): if the manifest names a
  resource that's plainly sitting nearby and available, that's not a streaming problem.
- ❌ **Not yet run:** a session with v1.2.5's richer complaint logging. That's the next concrete step —
  read what `finderManifest` actually contains next time `NoResourcesFound` fires, both far from and near
  the village, and check whether the named resource(s) are genuinely absent/unreachable or just sitting
  there unused.

**Closing test 2026-06-28 (the marker test — see Issue D for the same test's full detail):** placed a new
resource marker near fresh reeds in Session 3 (the struggling base), bumped `Thatch` to a 0.1-day
threshold for a realistic window, harvested, ran far away, saved, reloaded, waited, returned. Every
registered entry respawned cleanly — confirmed registration and servicing both work correctly regardless
of host distance.

**Verdict — closing this investigation:** the distance/streaming hypothesis is refuted by direct
evidence today. The original "two shoreline markers never refill" symptom can't be reproduced under
current code. The evidence that could explain its *original* cause — the pre-v1.2.1 global save, where a
cross-world-contaminated registration could have lived (see Issue B) — no longer exists; it was deleted
during this investigation's own cleanup before anyone thought to check it. Compounding that: gather
resources leave **no visual trace when harvested** (no stump-equivalent — confirmed in-game 2026-06-28),
so there's no way to inspect the original bare spots and learn anything further either. This is a genuine
evidentiary dead end, not a testing gap.

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

**Tested 2026-06-28, not reproduced (the marker test):** placed a new resource marker near fresh reeds
in Session 3, bumped `Thatch` to a 0.1-day threshold (long enough to be a realistic walkable window,
unlike the original 0.01-day/~6-second threshold which can't be outrun on foot), harvested it, ran far
away, saved, reloaded, waited, and returned. Result: every pending entry — the original 6 plus several
more registered later in the same session — eventually got a matching `respawned` line. None rotted;
`[diag] overdue-but-not-loaded` never fired once. The marker's entire area showed up in `[diag] init`
within the first ~450 lines of the *fresh reload* — `ActiveInstances` was repopulated almost immediately,
well before there was time to walk there, so the theorized race (timer crosses while genuinely unloaded)
never got a chance to occur.

**Verdict:** the rot-while-unloaded mechanism did not manifest under direct, deliberate testing designed
to provoke it. That doesn't prove it's impossible in every conceivable case (a much larger explored map,
a much longer real-world absence, or a slower reload could still in principle expose it), so the
two-part fix above remains a reasonable defensive improvement — just not one with confirmed,
reproducible impact backing it. Demoting from active investigation; revisit only if a fresh, traceable
instance of this exact symptom shows up under current code.

---

## PARKED / LOW PRIORITY

### E. Mod save file isn't tied to the game's own save event 🟡
**Symptom:** load a save, fell a tree (or exhaust a gather node), then quit **without** using the game's
own save — the mod still writes that registration to its `.save` file immediately. Reload the same
(unchanged, since you never saved) game save, and the mod's pending-respawn file disagrees with reality:
it thinks that node is felled/exhausted and pending respawn, while in-game the resource is fully intact.

**Confirmed in-game 2026-06-28** — exact sequence that produced it: chopped the tree at
`176.9:44.1:562.5` in an earlier session, quit without saving the game, then reloaded that same save
under v1.2.3 diagnostics. Log trace:
- `[diag] init 176.9:44.1:562.5` fires almost immediately on world load — the tree instance is **intact**
  (proof the chop never reached the actual game save).
- The stale `PendingRespawns` entry (`gameTime=56.056004`, carried over from the mod's own `.save` file)
  then sits through 73 more `[diag] init` re-streams with **no** `respawned` or `Stump harvested —
  cancelled` line — `DayTracker` never acted on it.
- A villager (it's right next to the base) chops the same tree for real later in the session →
  registers cleanly with a fresh timestamp, overwriting the stale entry.

**Cause:** `Plugin.SavePending()` is called synchronously inside every registration patch
(`HarvestPatch`, `GatherPatch`, `DataSyncPatch`), completely decoupled from whether the *game* ever
commits that session to disk. The mod's persistence model assumes "registered = real," but a quit
without saving means the underlying world state reverts while the mod's bookkeeping doesn't.

**Why it stayed benign here (not by design, just lucky in the common case):** `RegisteredStumps` (the
guard against double-registering the same stump) is **not** persisted — only `PendingRespawns` /
`PendingGatherRespawns` are. So on reload, `RegisteredStumps` comes back empty, meaning a genuine
re-felling of the same node is never blocked by the stale entry — it just overwrites it with a fresh
timestamp before anything bad happens. If the node had *not* been re-felled before the stale entry's
elapsed time crossed the respawn threshold, `DayTracker` would have called `inst.Replenish()` on an
already-intact resource (a no-op-ish call — can't make a full resource "more full") and logged a
misleading "respawned" line for an event that never actually occurred. Annoying for log-reading, not
destructive.

**Proposed fix direction (not yet investigated — deferred, low priority given the benign impact):**
commit/confirm pending respawns only once an actual in-game save event fires; track anything registered
since the last save as **volatile** (kept in memory, used for live `DayTracker` servicing same as today,
but not trusted as "real" yet); on the next save event, commit the volatile entries into the persisted
file (and presumably discard volatile entries if a session ends without ever saving, since the game
itself discarded that progress too). The missing piece is finding a host-side hook for "the game just
saved" — no such patch target has been identified yet. Until then, the mod's `.save` file should be
read as "what the mod has *seen* this process lifetime," not "what's actually persisted in the game
save" — keep that distinction in mind when using save-file contents as evidence for Issues C/D.

---

## TRACKED, OUT OF SCOPE FOR TreeRespawnMod

### F. Villagers permanently stuck wanting `Fibers x 15` 🔵
**Not a TreeRespawnMod bug.** Tracked here only because it was discovered *through* this mod's
diagnostics (v1.2.5's enriched `NoResourcesFound` logging) and was initially tangled up with Issue C —
keeping it separate so it doesn't get re-conflated with the distance/respawn investigation above.
Nothing in TreeRespawnMod controls villager quest target quantities or the resource-search algorithm;
the mod only governs post-harvest respawn *timing*.

**Symptom:** a villager assigned to gather fiber gets permanently stuck — `ComplainNoResourcesFound`
fires repeatedly, forever, even when flax (which yields `Fibers`) is visibly present and reachable.

**Confirmed in-game 2026-06-28, in two independent worlds:**
- Session 7 (a healthy, non-depleted save): `Wilhelmina` — 32/32 `NoResourcesFound` complaints that
  session were her, all `Fibersx15`, while in the same session 15+ *other* gather resources (mushrooms,
  cabbage, garlic, berries, feathers, thatch) registered and respawned normally. The user directly
  observed her icon touring and stopping frequently in a player-marked flax-rich area — yet zero
  `Fiber`/`Fibers` gather-exhausted lines ever appeared.
- Session 3 (the user's struggling base, a completely unrelated world): `Aili` — same exact complaint,
  `Fibersx15`, repeated throughout a separate short session.
- **The identical "15" across two unrelated worlds and two different villagers is the key signal** — a
  per-world/per-character *computed* deficit would be expected to vary with each save's specific
  storage levels; an exact match instead points to a fixed game constant (most likely a workstation or
  recipe's configured stockpile target, not a per-attempt harvest goal).

**Leading hypothesis (NOT fully confirmed — flagging clearly as inference, not fact):** the gather
quest holds a fixed target `ItemManifest` (mirroring an already-confirmed pattern elsewhere in this
game's quest system: `CookingStockpileQuestData.NeededSpecificSupplies`/`NeededFillerSupplies` are
also fixed stockpile-target manifests, not per-action yields — see architecture.md → Cooking Station
Pipeline), and the resource-search requires a *single* eligible source able to satisfy the entire
manifest in one shot. Since a flax harvest tops out at 6-7 fiber (skill-dependent — the user's own
figure, flagging it as unverified-by-us but directly reported), no single plant or patch can ever
satisfy a request for 15, so the search fails forever regardless of flax abundance or proximity.

**Why this isn't nailed down further:** `SSSGame.AI.FSM.*` quest-behavior nodes are ScriptableObjects
whose **method bodies are not dumpable via Cecil** (signatures only — see architecture.md → Structures/
Workstations/AI Quest system), so the actual search algorithm can't be confirmed without a full IL
decompile (a much bigger undertaking — comparable to why SeedHarvesterMod's deep-dive was parked; see
`SEED_HARVESTER_HANDOFF.md`).

**Cheap in-game verification (no further code needed):** check the stuck villager's job/profession and
look for a workstation (loom, weaver, etc.) with a queued recipe needing exactly 15 fiber — would
directly confirm where "15" comes from.

**Relevance to the broader investigation:** this gives Issue C's original "shoreline reeds never
refill" symptom a plausible *alternative* explanation that has nothing to do with distance — if
whichever villager is on fiber/reed duty in a given base has an analogous oversized target, that
resource can never restock no matter how fast it respawns, simply because the assigned worker never
completes a single successful harvest. That said, the user's Session-3 shoreline-reed symptom is
specifically about **Thatch**, and a successful harvest **was** observed before it failed to respawn —
a different failure mode than "never harvests at all." So F likely does NOT explain the original Thatch
symptom directly; treat C/D (registration succeeded, respawn-servicing didn't) and F (search never
succeeds in the first place) as two separate, unrelated mechanisms that happen to produce the same
surface symptom ("the village is short on X").

---

## Recommended next-session order

**Issues C/D are RESOLVED as of v1.2.10 (2026-06-30)** — see "RESOLVED 2026-06-30" above for the full
mechanism and in-game confirmation. What's left, in order:

1. **Confirm co-op (Issue A)** in a live session (watch for `(data sync)` log lines from a client's
   harvest). Still genuinely open, independent of C/D.
2. (Optional, low priority) **Verify Issue F's cause in-game** — check the stuck villager's job and look
   for a workstation with a recipe needing exactly 15 fiber. Not a TreeRespawnMod fix either way.
3. (Optional, low priority) Watch for any recurrence of the `load.txt` crash (see "Open item" under
   "RESOLVED 2026-06-30") — no action needed unless it reproduces with a specific repro.

## Diagnostic playbook (concrete)

- **Logging implemented (v1.2.3-v1.2.6, all gated on `EnableDiagnostics` except where noted):**
  - `GatherPatch` / `DataSyncPatch.CheckGather`: registration logged **unconditionally** (predates
    v1.2.3) — `Gather resource "X" exhausted [(data sync)] at <posKey>...`.
  - `HarvestPatch` / `DataSyncPatch.CheckHarvest`: same, unconditional — `Tree felled [(data sync)] at
    <posKey>...`.
  - `BiomeInstancePatch`: `[diag] init <posKey>` once per position per world (v1.2.4 deduped this — was
    every `Initialize()` call, which flooded the log and caused hitching).
  - `DayTracker`: throttled (max once/5s) summary when entries are past threshold but their node isn't
    in `ActiveInstances` — `[diag] overdue-but-not-loaded (tree|gather): <count> — posKey(Xd), ...`
    (capped to first 5 in the line; check the count if more are stuck). This does NOT yet implement
    the "confirmed live before Replenish" liveness guard from Issue D's proposed fix #2 below — that's
    still open, separate from the logging.
  - `DayTracker` gather-respawn snapshot (**v1.2.6**, the M2 probe): right before each gather `Replenish()`,
    `[diag] gather-respawn "X" at <posKey>: Destroyed=.. Active=.. avail A->B qty A->B`, tagging
    `<-- FAKE RESPAWN (instance not live / did not refill)` when the instance is dead/streamed-out
    (`Destroyed`/`Active=false`) or the stock didn't move. Behavior-unchanged; reads are wrapped so a
    throw on a dead instance can't disturb the `Replenish`/removal path. **This is the liveness *probe*,
    not the *guard*** — it still removes the entry exactly as before; the guard (keep the entry when the
    instance isn't live) is the fix to write IF the test confirms M2.
  - `GatherPatch` gather-registration snapshot (**v1.2.7**, the M1/premise probe): right after a node
    registers as exhausted, `[diag] gather-reg "X" at <posKey>: playerDist=Nm inActiveInst=.. instActive=..
    destroyed=.. interactGO=..`. Reads the host player position via the captured `BiomesManager`
    (`Patches/Captures.cs` → `Plugin.TryGetPlayerPos`). Tells whether the harvest happened far from / not
    streamed near the host. Behavior-unchanged; all reads wrapped so a throw can't disturb registration.
  - `ComplainNoResourcesDiag` (v1.2.5): `DIAG worker idle complaint: NoResourcesFound: '<villager>' wants
    <ItemxQty, ItemxQty, ...> (searched...)`. Reads `GatherAndHarvestData.GetVillager().GetName()` and
    the `finderManifest` parameter the game passes into `ComplainNoResourcesFound` (`ItemManifest.GetItems()`
    → `ItemInfoQuantity[]`, capped to 5 shown). This is the tool for round 2 of Issue C below.
- **Test C, round 1 (done 2026-06-28, see Evidence above):** host at base vs. far away — gather never
  registers either way; idle complaints persist/worsen near base too. Result didn't isolate a single
  cause; promoted hypothesis 3.
- **Test C, round 2 (not yet run):** repeat with v1.2.5. When `NoResourcesFound` fires, read the villager
  name + wanted-item list in the log.
  - If the named item is something you can see plenty of nearby/in storage → it's a priority/targeting
    bug independent of distance (hypothesis 3) — next step is finding why the search rejects available
    stock (work-priority config, a stale "depleted" flag the AI search caches, wrong category, etc.).
  - If the named item genuinely doesn't exist in range at all (and the marker is far from the host) →
    back to hypothesis 1 (distance/streaming) — cross-check against `[diag] init` for that area.
  - If it happens with the item readily available **even when the host is standing right next to it** →
    that's a different, more fundamental targeting bug, separate from both distance hypotheses — treat
    as a new issue once confirmed rather than folding it into C.
- **Test D:** harvest/exhaust a node far from base → walk away for > one threshold → watch for
  `[diag] overdue-but-not-loaded` while away → return → it should respawn on arrival (once the
  respawn-on-reload fix below is implemented) vs stay empty (current behavior, no fix yet).

## Reference

- **Source:** `TreeRespawnMod/Plugin.cs` (state, per-world save, config, `TryGetPlayerPos`), `DayTracker.cs` (servicing + world-id poll), `Patches/{HarvestPatch,GatherPatch,DataSyncPatch,BiomeInstancePatch,StumpProtectionPatch,WorkerIdleDiagPatch,Captures}.cs`.
- **Save format:** sections `# tree` (`posKey,gameTime`) and `# gather` (`posKey,gameTime,widRaw,itemName` as of v1.2.10; older `posKey,gameTime,itemName` and `posKey,gameTime` still parse); legacy `# mining` skipped; headerless old files load as tree entries.
- **Reed config:** `Thatch` (reeds yield item name). All gather keys are substring, case-insensitive (`Fiber` matches `Fibers`). See `docs/mods/tree-respawn.md`.
- **Type inspector:** `_explore/typedump.ps1 -Types @("Name")` dumps a game type's members (Cecil over the interop DLLs) — how `StorageManager.ActiveSessionID` was found.
- **Method signature inspector:** `_explore/methoddump.ps1 -Type "TypeName" -Method "MethodName"` resolves a specific method's exact return type, including generic arguments (`typedump.ps1` collapses generics to e.g. `List`1`) — how `ItemManifest.GetItems() : List<ItemInfoQuantity>` was confirmed for the v1.2.5 diagnostic.
