# Mod 4: TorchFuelMod — COMPLETE

**Goal:** Torches (placed for base lighting/decoration) should never run out of fuel/resin.

**Game subsystem:** [Torch / Fire-Fuel System](../architecture.md#torch--fire-fuel-system) — the
`FireStructure` API, the `Rpc_AddFuel` network-safe path, and why filtering must be by structure name.

**Working approach:**
- Postfix patch on `FireStructure.Initialize(Structure ownerStructure)` — fires for every fire-capable structure; filter by `ownerStructure.DefaultName`/`StructureName` containing a configured substring (default `"Torch"`) so campfires/forges/kilns/cooking stations (which also use `FireStructure`) are untouched
- Matched instances are added to `Plugin.TrackedFireStructures` (plain `List<FireStructure>`)
- `TorchFuelTracker` (registered `MonoBehaviour`, `Update()` polling, same pattern as `DayTracker`/`RegenTracker`) checks the list every `CheckIntervalSeconds` (default 5.0); for any tracked structure with `CurrentFuelVolume < MaxFuelVolume`, calls `Rpc_AddFuel(MaxFuelVolume - CurrentFuelVolume)` to top it off exactly to max (never overfuels, so it can't trigger the "overfire/blazing" state)
- Using the existing `Rpc_AddFuel` RPC (rather than setting `CurrentFuelVolume` directly) was a deliberate choice — it's the same network-safe path the game uses for manual resin refueling, so it works correctly in co-op regardless of which client/host has the mod installed
- Dead/despawned entries (`!fire.IsSpawned` or null) are pruned from the tracked list each tick
- Config: `TorchFuel/TargetStructureNames` (string, default `"Torch"`, comma list, case-insensitive substring match), `TorchFuel/CheckIntervalSeconds` (float, default 5.0)
- Confirmed working in-game: fuel visibly ticks down for a few seconds then jumps back to max on the next check interval; tracked 4 "Flimsy Torch" structures correctly on load
