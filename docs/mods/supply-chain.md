# SupplyChainMod — Mod 26

**Status:** WIP v0.1.3, Phase 0 complete (read-only flow diagnostics, dev tool, NOT for Nexus)

**Goal:** Phase 0 read-only diagnostic for the idea-12 supply-chain autopilot — observes game
state (villager complaints, settlement issues, task data, inventory composition), never writes.
Findings absorbed into architecture.md for later controller phases.

## Design & Components

**Diagnostic modules (all v0.1.0→v0.1.3 in-game-verified 2026-07-12):**

1. **ComplaintWatcher** — `VillagerSocial.AddComplaint`/`RemoveComplaint` postfixes, transition-based
   logging (ADD-new / CLEARED with after=Ns + readds=N), periodic stats line (active/newAdds/reAdds/
   clears/unmatchedRemoves/suppressed, only when nonzero). Gated by config `[Diagnostics]
   PatchVillagerComplaints` (safety valve: set false if plugin-load crash). Filter ignores
   configured complaint messages (default "feelUnsafe") via exact string key match + per-world id
   map deduplication.

2. **WidgetWatcher** — polls `SettlementIssueTrackerWidget.Instance` (FindAnyObjectByType fallback
   when null) for deltas in its six aggregation maps; fire-verification postfixes on push methods.
   Confirmed in-game: maps populate under real problems (storageFull cycles per-workstation as
   haulers drain; noProd holds constant with item + needed-by string; others cycle or stay 0).

3. **TaskDump** — F9 hotkey (typing-guarded) + auto ~60 s after world load. Per-station output:
   workstation class, inventory count, per-task item/quota/priority (Int32, all 0 in normal save),
   `quantityRange`, FiltersTaskData per-item tiers.

4. **CompositionSweep** — rolling [Perf]-instrumented scan via `ItemCollection.GetItemInfos()` +
   `GetItemQuantity()`. Per-station 0.2–5.1 ms, full settlement 6–20 ms (31 stations) to 10.8 ms
   (61 stations under invasion). No performance ceiling hit.

5. **BomDump** — table-level blueprint/recipe dump via `CraftInteraction._localBlueprints` +
   per-blueprint manifests via `ItemManifest.CreateFromBlueprint(CraftBlueprintInfo)`. Logs
   per-task `KnowsBlueprintForItem` probe results (all false/null — semantics suspect, dead-end
   confirmed; dictionaries are the reliable path).

## Configuration

**`<Mod>.cfg` after first launch:**

```
[General]
TickSeconds=5               # Sweep and widget-poll interval
DumpHotkey=F9              # Typing-guarded hotkey for TaskDump

[Diagnostics]
EnableDiagnostics=true     # Developer mode (deliberately true; flip false before ship)
PatchVillagerComplaints=true  # Safety valve — set false if load crash
WidgetPollSeconds=10       # Widget map sample rate
SweepStationsPerTick=1     # Rolling station quota per tick
IgnoredComplaintMessages=feelUnsafe  # Suppress listed key-exact messages (comma-sep)
```

## Dead-Ends

- **`KnowsBlueprintForItem` false on valid items:** confirmed dead-end (architecture.md →
  Blueprint lookup section). Always iterate `_knownBlueprints`/`_localBlueprints` dictionaries
  directly.
- **`LoadoutsComplaint.missingItems` has no name:** is `List<IItemFilter>` with no name member —
  logs native class names instead.

## Version History

- **v0.1.0** (2026-07-12) — scaffold (ComplaintWatcher, WidgetWatcher, TaskDump, CompositionSweep,
  BomDump). ⚠️ pending in-game confirmation.
- **v0.1.1** (2026-07-12) — GenericMessageComplaint `message` key now logged; ignore filter
  `[Diagnostics] IgnoredComplaintMessages` (default "feelUnsafe"); FailedObjectiveComplaint logs
  objective native type + `GetDescriptionKey()`; REMOVE dedupe (consecutive same villager+id
  aggregated xN). ⚠️ pending in-game confirmation.
- **v0.1.2** (2026-07-12) — transition-based complaint logging (ADD-new / CLEARED with after=Ns +
  readds=N); BomDump table-level probes + per-blueprint manifests; per-task KnowsBlueprintForItem
  logged. ⚠️ pending in-game confirmation.
- **v0.1.3** (2026-07-12) — widget map-size logging (confirms empty-vs-broken). **Confirmed
  in-game (2026-07-12, 4 test runs):** all Phase 0 unknowns answered (see architecture.md). Phase
  0 complete.
