# TreeRespawnMod — Bug Tracker & Handoff

General handoff for **Mod 2 (TreeRespawnMod)**. Supersedes the old `COOP_RESPAWN_HANDOFF.md`
(which only covered the co-op issue). Covers the whole respawn pipeline, every known issue
(resolved + open), the dead-ends, and the recommended next steps. Read alongside
`docs/mods/tree-respawn.md` (shipped recipe/config) and `docs/architecture.md`
(Resource/Tree, Gather, Worldgen/Streaming subsystems).

Current version: **v1.2.5**. v1.2.2 was a version-only bump from v1.2.1 — Smart App Control blocked
the v1.2.1 DLL hash on the second machine with `FileLoadException ... 0x800711C7`; bumping the version
changes the hash so SAC re-evaluates it and lets it load (no logic changed). v1.2.3 implements the
diagnostic logging for Issues C/D described below (was previously just a plan) — see "Diagnostic
playbook" for what's now actually logged. v1.2.4 dedupes `[diag] init` to fire once per position per
world instead of on every re-stream — a single test run logged the same handful of positions 90+ times
each (confirmed in-game 2026-06-28) and caused noticeable hitching; same diagnostic value, far less log
volume. v1.2.5 enriches the `NoResourcesFound` worker-idle diagnostic with the villager's name and the
actual `ItemManifest` they were searching for — needed after 2026-06-28 testing complicated the
distance-only theory for Issue C (see that section).

---

## Status at a glance

| # | Issue | Status |
|---|---|---|
| A | Co-op **client** respawn (client's harvests didn't start a timer on host) | **Fix in place** (DataSyncPatch) — ⚠️ NOT re-confirmed in a live co-op session |
| B | **Cross-world** save contamination (one global file shared by all worlds) | ✅ **FIXED v1.2.1**, confirmed in-game 2026-06-28 |
| C | **Distant villager** gather harvests don't respawn (far foraging markers stay empty) | ⚫ **CLOSED 2026-06-28** — distance hypothesis refuted by direct testing; original incident unreproducible, evidence gone |
| D | **Overdue** respawns not serviced until you stand near the node as the timer elapses | ⚫ **CLOSED 2026-06-28** — not reproduced under direct testing; fix stays designed as a defensive measure only |
| E | Mod's save file isn't tied to the game's own save event (can register a respawn the game never persisted) | 🟡 **PARKED** — confirmed benign 2026-06-28, fix designed, not being chased yet |
| F | Villagers permanently stuck wanting `Fibers x 15` — **NOT a TreeRespawnMod bug**, see dedicated section | 🔵 **TRACKED, OUT OF SCOPE** — confirmed repeatable in 2 independent worlds 2026-06-28; vanilla AI/quota quirk |

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

**Issues C and D are closed (2026-06-28) — see above.** The full arc, for context: re-baseline (clean
per-world files) → round 1 (distance alone didn't explain the general "village short on stuff" symptom)
→ round 2 (the "stuck worker" complaint turned out to be Issue F's fiber lockout, not C) → the marker
test (deliberately provoked the exact race Issue D theorized, at a realistic 0.1-day threshold; nothing
rotted, distance didn't block anything). What's actually left to do:

1. (Optional, low priority) **Verify Issue F's cause in-game** — check the stuck villager's job and look
   for a workstation with a recipe needing exactly 15 fiber. Not a TreeRespawnMod fix either way.
2. **Confirm co-op (Issue A)** in a live session (watch for `(data sync)` log lines from a client's
   harvest). This is the only originally-tracked issue still genuinely open.
3. If a "harvested but never respawns" symptom shows up again under current code (v1.2.1+), treat it as
   a **new, fresh incident** — re-open with its own evidence rather than assuming it's a continuation of
   the closed C/D investigation.

## Diagnostic playbook (concrete)

- **Logging implemented (v1.2.3-v1.2.5, all gated on `EnableDiagnostics` except where noted):**
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

- **Source:** `TreeRespawnMod/Plugin.cs` (state, per-world save, config), `DayTracker.cs` (servicing + world-id poll), `Patches/{HarvestPatch,GatherPatch,DataSyncPatch,BiomeInstancePatch,StumpProtectionPatch,WorkerIdleDiagPatch}.cs`.
- **Save format:** sections `# tree` (`posKey,gameTime`) and `# gather` (`posKey,gameTime,itemName`); legacy `# mining` skipped; headerless old files load as tree entries.
- **Reed config:** `Thatch` (reeds yield item name). All gather keys are substring, case-insensitive (`Fiber` matches `Fibers`). See `docs/mods/tree-respawn.md`.
- **Type inspector:** `_explore/typedump.ps1 -Types @("Name")` dumps a game type's members (Cecil over the interop DLLs) — how `StorageManager.ActiveSessionID` was found.
- **Method signature inspector:** `_explore/methoddump.ps1 -Type "TypeName" -Method "MethodName"` resolves a specific method's exact return type, including generic arguments (`typedump.ps1` collapses generics to e.g. `List`1`) — how `ItemManifest.GetItems() : List<ItemInfoQuantity>` was confirmed for the v1.2.5 diagnostic.
