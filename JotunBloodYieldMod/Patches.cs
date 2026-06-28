using System;
using HarmonyLib;
using SandSailorStudio.Inventory;

namespace JotunBloodYieldMod;

[HarmonyPatch(typeof(SSSGame.HarvestSpawner))]
public static class HarvestSpawnerPatches
{
    [HarmonyPatch(nameof(SSSGame.HarvestSpawner._GetAwardedLootCount))]
    [HarmonyPostfix]
    public static void GetAwardedLootCount_Postfix(ItemInfo __0, ref int __result)
    {
        if (__0 != null && !string.IsNullOrEmpty(__0.Name))
        {
            if (__0.Name.Contains("Blood", StringComparison.OrdinalIgnoreCase))
            {
                int oldResult = __result;
                if (__result == 1)
                {
                    __result = 3;
                }
                else if (__result == 2)
                {
                    __result = 5;
                }
                else if (__result >= 3)
                {
                    __result = 6;
                }
                
                if (oldResult != __result)
                {
                    Plugin.Log.LogInfo($"HarvestSpawner: Increased Jotun Blood yield from {oldResult} to {__result}.");
                }
            }
        }
    }
}

[HarmonyPatch(typeof(SSSGame.LootSpawner))]
public static class LootSpawnerPatches
{
    [HarmonyPatch(nameof(SSSGame.LootSpawner.GetLootStack))]
    [HarmonyPostfix]
    public static void GetLootStack_Postfix(ref ItemInfoQuantity __result)
    {
        if (__result.itemInfo != null && !string.IsNullOrEmpty(__result.itemInfo.Name))
        {
            if (__result.itemInfo.Name.Contains("Blood", StringComparison.OrdinalIgnoreCase))
            {
                int oldQuantity = __result.quantity;
                if (__result.quantity == 1)
                {
                    __result.quantity = 3;
                }
                else if (__result.quantity == 2)
                {
                    __result.quantity = 5;
                }
                else if (__result.quantity >= 3)
                {
                    __result.quantity = 6;
                }
                
                if (oldQuantity != __result.quantity)
                {
                    Plugin.Log.LogInfo($"LootSpawner: Increased Jotun Blood yield from {oldQuantity} to {__result.quantity}.");
                }
            }
        }
    }
}
