using System;
using HarmonyLib;
using SandSailorStudio.Inventory;

namespace JotunBloodYieldMod;

[HarmonyPatch(typeof(SSSGame.HarvestSpawner))]
public static class HarvestSpawnerPatches
{
    [HarmonyPatch(nameof(SSSGame.HarvestSpawner.Awake))]
    [HarmonyPostfix]
    public static void Awake_Postfix(SSSGame.HarvestSpawner __instance)
    {
        ProcessLootList(__instance.pieceLoot);
        ProcessLootList(__instance.bitLoot);
    }

    private static void ProcessLootList(Il2CppSystem.Collections.Generic.List<SSSGame.HarvestSpawner.Loot> lootList)
    {
        if (lootList == null) return;

        // Count existing Jotun Blood entries to avoid double-multiplying if it's a shared prefab list
        int bloodCount = 0;
        int originalCount = lootList.Count;
        for (int i = 0; i < originalCount; i++)
        {
            var loot = lootList[i];
            if (loot != null && loot.lootInfo != null && !string.IsNullOrEmpty(loot.lootInfo.Name))
            {
                if (loot.lootInfo.Name.IndexOf("Blood", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    loot.lootInfo.Name.IndexOf("Jotun", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    bloodCount++;
                }
            }
        }

        // If it already has 3 or more, it's likely either already patched or naturally very high yielding.
        // We will assume if bloodCount >= 3, it's already patched (since vanilla small is 1, large is 2).
        if (bloodCount == 0 || bloodCount >= 3) return;

        int copiesToAdd = bloodCount * 2; // 1 -> add 2 (total 3), 2 -> add 4 (total 6)

        for (int i = 0; i < originalCount; i++)
        {
            var loot = lootList[i];
            if (loot != null && loot.lootInfo != null && !string.IsNullOrEmpty(loot.lootInfo.Name))
            {
                if (loot.lootInfo.Name.IndexOf("Blood", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    loot.lootInfo.Name.IndexOf("Jotun", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    for (int j = 0; j < 2; j++)
                    {
                        var newLoot = new SSSGame.HarvestSpawner.Loot();
                        newLoot.lootInfo = loot.lootInfo;
                        newLoot.harvestMask = loot.harvestMask;
                        newLoot.lootFlag = loot.lootFlag;
                        newLoot.chanceComponent = loot.chanceComponent;
                        newLoot.uniqueLoot = loot.uniqueLoot;
                        newLoot.fullLootStack = loot.fullLootStack;
                        lootList.Add(newLoot);
                    }
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
            if (__result.itemInfo.Name.IndexOf("Blood", StringComparison.OrdinalIgnoreCase) >= 0 ||
                __result.itemInfo.Name.IndexOf("Jotun", StringComparison.OrdinalIgnoreCase) >= 0)
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
