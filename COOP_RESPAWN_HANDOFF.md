# TreeRespawnMod — Co-op Client Respawn Bug Fix (v1.1.7)

## The Issue
During co-op gameplay, when the Host (server) cut down a tree or gathered a resource, the `TreeRespawnMod` correctly started a regrowth timer. When a villager NPC did the same, the timer also started. However, when a **Client player** (e.g., the host's brother) cut down a tree or gathered a resource, the respawn timer never started, and no logs were generated on the Host machine.

## The Cause
The original patches hooked the interaction methods: `HarvestInteraction.TakeDamage` and `GatherInteraction.GatherItemsCharge`. 
In Photon Fusion's networking architecture:
- When the Client interacts with an object, the interaction logic (`TakeDamage`/`GatherItemsCharge`) executes locally on the Client's machine.
- The Client's game then calculates the resulting state (e.g., tree HP dropped, piece destroyed) and replicates that raw data state back to the Host.
- The Host receives the state update and silently applies the new data to the visual instance, **without re-executing `TakeDamage` or `GatherItemsCharge`**.
- As a result, the Host's BepInEx hooks for those methods never fired for the Client's actions. 

## The Solution
Instead of solely relying on interaction method hooks, we needed to intercept the exact moment when the network synchronization applied the Client's state changes to the Host's game world.

### 1. Data Sync Hooks (The Network Catch)
We added `HarmonyPatch` Postfixes to the internal network data synchronization callbacks:
- `HarvestInteraction._OnWorldInstanceDataChanged(WorldItemInstance)`
- `GatherInteraction.OnWorldInstanceDataChanged(WorldItemInstance)`

These methods fire automatically on the Host whenever a tree or gatherable's state updates over the network. Inside these patches, we evaluate the newly updated state (e.g., "is it a stump?" or "is the available count 0?"). If the condition is met, we register the node in the `PendingRespawns` dictionary and begin the timer.

### 2. Authority Gating (The "IsServer" Check)
Because the `TreeRespawnMod` runs on both the Host and the Client machines, we want to avoid situations where the Client attempts to run authoritative timers or writes to its own local save file when receiving network updates. 
We explicitly gated the registration, timer ticking, and save mechanisms by checking:
```csharp
var weather = WeatherSystem.Instance;
if (weather == null || weather.Runner == null || !weather.Runner.IsServer) return;
```
This ensures that **only the Host** maintains the authoritative list of pending respawns and triggers the `Replenish()` calls.

## Next Steps / Pending Verification
The fix is compiled into **v1.1.7** and currently loaded. 
During the next co-op session, monitor the `LogOutput.log` on the Host machine when the Client player fells a tree or exhausts a resource. You should see a diagnostic line that reads:
> `[TreeRespawnMod] Tree felled (data sync) at <pos>...`

If this log appears, the sync hook successfully caught the Client's action, and the respawn cycle has begun.

### [UPDATE 2026-06-27] FAILURE NOTED
The current `v1.1.7` patch crashes the game on startup. The patches to `GatherInteraction.OnWorldInstanceDataChanged` and `HarvestInteraction._OnWorldInstanceDataChanged` violate rule #5 in `AGENTS.md` (Do not patch Initialize/lifecycle methods on MonoBehaviours). The native engine invokes these before the GC handle is set up, resulting in a fatal `System.InvalidOperationException: Handle is not initialized` in the native->managed trampoline. The `DataSyncPatch` hooks have been temporarily commented out to allow the game to boot. Further work is required to fix the co-op client respawn issue safely.
