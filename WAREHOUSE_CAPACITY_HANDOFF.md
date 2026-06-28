# Warehouse Capacity Mod — Design & Handoff

## Objective
Increase the maximum stack size of items in the settlement warehouse (to reduce warehouse sprawl) without causing network desyncs or breaking AI crafting/hauling logic.

## Architecture Context & Discovery Trail
To understand why this approach is taken, here is the trail of discovery from inspecting the IL2CPP assemblies (specifically Photon Fusion networking in ASKA):

1. **Network Array Limits (The Byte Overflow)**
   - The game uses `SSSGame.Network.NetworkItemStorage` to replicate inventory over the network.
   - It relies on generated fixed-capacity structs like `NetworkInventory_35` or `NetworkInventory_80`, which contain `NetworkArray<NetworkItem> _Items`.
   - The remote procedure call used to synchronize stack count changes is:
     `void Rpc_ChangeNetworkItemCount(Byte itemPosition, Byte newCount)`
   - **Critical finding:** Because `newCount` is a `Byte` (unsigned 8-bit integer), the maximum value it can hold is 255.
   - If a stack size mod pushes `ItemInfo.stackSize` to 300, the local game works (because `SandSailorStudio.Inventory.Item.count` is an `Int32`), but the network RPC overflows. 300 hits the network as `44`. The Host sees 300, Clients see 44, and AI state machines crash due to massive desynchronization.

2. **The Workstation "Eats the Whole Stack" Bug**
   - Workstations (like `KilnInteraction` or `ForgeInteraction`) use standard `ItemContainer`s for inputs.
   - A global mod that changes `ItemInfo.stackSize = 999` allows these workstation inputs to accept 999 ore.
   - The workstation logic often assumes small, fixed input capacities. When processing, it may consume the entire container's stack to yield a single output (e.g., 50 ore becomes 1 bloom block yielding 1 iron). 

3. **Player Encumbrance Limits**
   - ASKA enforces physical hauling by restricting large items (Logs, Stones) to `RestrictedCarryContainer` (shoulder slot), which evaluates its max stack size as 1.
   - A global `ItemInfo.stackSize` override bypasses this, allowing players and villagers to carry 200 logs at once, breaking game balance.

## The Solution: Targeted Container Interception
Instead of changing the global `ItemInfo` or breaking network constraints, we intercept the specific physical container's query for how large a stack can be. We limit this strictly to `ResourceStorage` (the warehouse building) and strictly cap it at 200 to stay safely below the 255 network byte limit.

### Key Hook Points
- **Target Method:** `SandSailorStudio.Inventory.ItemContainer.GetStackSize(ItemInfo itemInfo)`
- **Patch Type:** `Postfix`
- **Logic:**
  1. Check if `__instance.Owner` exists and can be cast to `SSSGame.ResourceStorage`.
  2. If true, and `__result` (the vanilla stack size) is less than 200, override `__result = 200`.

```csharp
[HarmonyPatch(typeof(SandSailorStudio.Inventory.ItemContainer), nameof(SandSailorStudio.Inventory.ItemContainer.GetStackSize), typeof(ItemInfo))]
public static class ItemContainer_GetStackSize_Patch {
    public static void Postfix(SandSailorStudio.Inventory.ItemContainer __instance, ItemInfo itemInfo, ref int __result) {
        // Leave it alone if it's already >= 200 or isn't owned by a structure
        if (__instance == null || __instance.Owner == null || __result >= 200) return;

        // Only override for the Warehouse (ResourceStorage)
        // This explicitly ignores player shoulder slots, kilns, forges, etc.
        if (__instance.Owner.TryCast<SSSGame.ResourceStorage>() != null) {
            __result = 200;
        }
    }
}
```

### Why This is 100% Safe
- **Network Safe:** 200 fits perfectly into the `Byte` parameter of `Rpc_ChangeNetworkItemCount`, eliminating the overflow desync.
- **AI/Player Safe:** Because the expanded limit only applies inside the warehouse bin, when a villager pulls a log out, their own shoulder container still evaluates its max stack as `1`. They are physically forced to haul logs one at a time, preserving vanilla hauling balance.
- **Workstation Safe:** The Kiln, Forge, and Cooking Station continue evaluating their own containers at vanilla limits, so you cannot accidentally load 200 ore into a forge.

## Next Steps When Ready to Build
1. Scaffold `WarehouseCapacityMod` from the standard `.csproj` template.
2. Implement the `GetStackSize` Postfix patch.
3. Test in-game: Verify a warehouse can hold 200 logs in a single slot, and verify a player can still only pick up 1 log at a time from that slot.
