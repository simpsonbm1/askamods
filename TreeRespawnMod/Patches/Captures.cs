using HarmonyLib;
using SSSGame;

namespace TreeRespawnMod.Patches;

// Capture-only (no behavior change). Holds the BiomesManager so the gather-registration
// diagnostic can read the local player's position (BiomesManager._localPlayerTransform) and
// log how far the player was from a node at harvest time. This is the v1.2.7 probe for the
// open question in TREERESPAWN_HANDOFF.md: do villagers harvest a node whose tile is NOT
// streamed in near the host? Mirrors the capture WarpTour/SeedScout already use (repo rule:
// never FindObjectsByType — grab the instance from its own lifecycle).
[HarmonyPatch(typeof(BiomesManager), "Awake")]
internal static class BiomesAwakePatch
{
    static void Postfix(BiomesManager __instance) => Plugin.Biomes = __instance;
}

[HarmonyPatch(typeof(BiomesManager), "OnDisable")]
internal static class BiomesOnDisablePatch
{
    static void Postfix(BiomesManager __instance)
    {
        if (ReferenceEquals(Plugin.Biomes, __instance)) Plugin.Biomes = null;
    }
}

[HarmonyPatch(typeof(PlayerCharacter), nameof(PlayerCharacter.Spawned))]
internal static class PlayerSpawnedPatch
{
    static void Postfix(PlayerCharacter __instance)
    {
        try
        {
            if (!__instance.HasAuthority) return;
            Plugin.LocalPlayer = __instance;
        }
        catch { }
    }
}

[HarmonyPatch(typeof(PlayerCharacter), nameof(PlayerCharacter.Despawned))]
internal static class PlayerDespawnedPatch
{
    static void Postfix(PlayerCharacter __instance)
    {
        try
        {
            if (Plugin.LocalPlayer == __instance)
                Plugin.LocalPlayer = null;
        }
        catch { }
    }
}

// SetWorldInstance is the moment an interaction MonoBehaviour is bound to its world-instance data
// object — the one place both halves are in hand. Each bind records the position→kind/item mapping
// (Plugin.KnownNodes, consumed by DataSyncPatch's GameObject-free classification) and runs a CATCH-UP
// registration: if the node is already depleted but nothing is pending for it (depleted while the
// chunk was unloaded, a co-op miss, or an entry consumed by an older bug), it gets a fresh timer the
// moment the host sees it again. This is what makes historical losses self-heal instead of staying
// bare forever. Both patches are Postfixes on methods declared per-type (verified via typedump) —
// NOT Interaction lifecycle methods, so the "Handle is not initialized" gotcha doesn't apply (the
// HarvestInteraction variant has shipped since v1.2.14 without incident).
[HarmonyPatch(typeof(HarvestInteraction), nameof(HarvestInteraction.SetWorldInstance))]
internal static class HarvestInteractionSetWorldInstancePatch
{
    static void Postfix(HarvestInteraction __instance, WorldItemInstance instance)
    {
        try
        {
            if (__instance == null) return;
            Plugin.LiveHarvestInteractions.Add(__instance);

            var bi = instance?.TryCast<BiomeItemInstance>();
            if (bi == null) return; // non-biome harvestable (loose logs, stone) — not respawnable
            var pieces = __instance.harvestPieces;
            if (pieces == null || pieces.Count < 2) return; // single-piece — no stump, not a tree

            string posKey = Plugin.PosKey(bi.GetPosition());
            Plugin.KnownNodes[posKey] = (NodeKind.Tree, "");
            Registration.TryRegisterTree(bi, posKey, "catch-up");
        }
        catch { }
    }
}

[HarmonyPatch(typeof(GatherInteraction), nameof(GatherInteraction.SetWorldInstance))]
internal static class GatherInteractionSetWorldInstancePatch
{
    static void Postfix(GatherInteraction __instance, WorldItemInstance instance)
    {
        try
        {
            if (__instance == null) return;
            var bi = instance?.TryCast<BiomeItemInstance>();
            if (bi == null) return;

            string posKey = Plugin.PosKey(bi.GetPosition());
            string itemName = "";
            try { itemName = __instance.GetGatherableItemInfo()?.Name ?? ""; } catch { }
            Plugin.KnownNodes[posKey] = (NodeKind.Gather, itemName);
            Registration.TryRegisterGather(bi, posKey, itemName, "catch-up");
        }
        catch { }
    }
}
