# Mod 2: TreeRespawnMod — COMPLETE

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
- `BiomeInstancePatch` Postfix on `BiomeItemInstance.Initialize()` populates `ActiveInstances[posKey]` as world loads; `DayTracker` looks up the live instance at check time — no stale pointers stored
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

## Shared infrastructure
- Persistence: `com.askamods.treerespawn.save` — sections `# tree` and `# gather`; tree format `posKey,gameTime`; gather format `posKey,gameTime,itemName`. Old saves without section headers load as tree entries (backward compatible); a legacy `# mining` section is silently skipped on load.
- Day tracking via registered `MonoBehaviour` (`DayTracker`) with `Update()` polling — avoids IL2CPP delegate subscription issues

## Mining / stone-clump respawn — abandoned
Investigated and abandoned; do not re-attempt. Full reasoning is in
[architecture.md](../architecture.md#mining--stone-clump-respawn--investigated--abandoned-dont-re-attempt)
and the deep write-up in [`../../TreeRespawnMod/STONE_RESPAWN_HANDOFF.md`](../../TreeRespawnMod/STONE_RESPAWN_HANDOFF.md).

## Config-design dead end (don't retry)
- `GatherRespawnDays` + `GatherRespawnOverrides` (single comma-separated string config) — replaced by individual `Config.Bind<float>` per resource in the `[GatherRespawn]` section; much more user-friendly.
