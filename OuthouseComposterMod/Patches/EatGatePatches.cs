using System;
using System.Collections.Generic;
using HarmonyLib;
using SSSGame;
using SSSGame.AI;

namespace OuthouseComposterMod.Patches;

// Villager EAT-gate ("outhouse contents invisible to villager consume-quest whitelist checks"),
// added v1.2.0. SSSGame.AI.SurvivalObjectiveQuest.SatisfyObjectiveQuestData is the per-villager
// consume-quest data; its three IsWhitelistedByStorage/IsWhitelistedByAny overloads are the bool
// gates the villager AI uses to decide whether a given ResourceOutlet/IResourceStorageSite is
// eligible to satisfy a survival need. Without this patch, villagers eat food out of the outhouse
// before the composter converts it — same root cause as the v1.1.0 warehouse haul-gate
// (Patches/HaulGatePatches.cs), different consumer (Nexus/user-observed 2026-07-19). Each of the
// three methods is patched independently, mirroring HaulGatePatches.cs's structure. The two
// IsWhitelistedByStorage overloads are disambiguated via argument-type arrays in the HarmonyPatch
// attribute (Cecil-confirmed 2026-07-19: exactly these two overloads plus IsWhitelistedByAny,
// all Boolean-returning instance methods).
//
// Inventory-family patch-param gotcha does NOT apply here — ResourceOutlet is a MonoBehaviour and
// IResourceStorageSite is a plain interop interface wrapper, neither is Item/ItemCollection/
// ItemEventContext (project-wide gotcha; see VillagerAmmoMod's crash history in CLAUDE.md).
[HarmonyPatch(typeof(SurvivalObjectiveQuest.SatisfyObjectiveQuestData), nameof(SurvivalObjectiveQuest.SatisfyObjectiveQuestData.IsWhitelistedByStorage), new[] { typeof(ResourceOutlet) })]
internal static class IsWhitelistedByStorageOutletPatch
{
    private static bool _aliveLogged;
    private static readonly HashSet<string> _loggedOwners = new();

    static bool Prefix(ResourceOutlet resourceOutlet, ref bool __result)
    {
        try
        {
            if (!_aliveLogged)
            {
                _aliveLogged = true;
                Plugin.Logger.LogInfo("[OuthouseComposter][eatgate] IsWhitelistedByStorage(ResourceOutlet) patch alive.");
            }

            if (!Plugin.ProtectOuthouseContents.Value) return true;
            if (!OuthouseGate.IsOuthouseOutlet(resourceOutlet)) return true;

            __result = false;

            if (Plugin.LogEatGate.Value)
            {
                string owner = SafeOwnerName(resourceOutlet);
                if (_loggedOwners.Add(owner))
                    Plugin.Logger.LogInfo($"[OuthouseComposter][eatgate] denied consume from outhouse: method=IsWhitelistedByStorage(ResourceOutlet) owner='{owner}'");
            }

            return false;
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"[OuthouseComposter] IsWhitelistedByStorageOutletPatch error: {ex}");
            return true;
        }
    }

    private static string SafeOwnerName(ResourceOutlet? outlet)
    {
        if (outlet == null) return "?";
        try
        {
            var s = outlet.Structure;
            if (s != null) return OuthouseGate.SafeStructureName(s);
        }
        catch { }
        return "?";
    }
}

[HarmonyPatch(typeof(SurvivalObjectiveQuest.SatisfyObjectiveQuestData), nameof(SurvivalObjectiveQuest.SatisfyObjectiveQuestData.IsWhitelistedByStorage), new[] { typeof(IResourceStorageSite) })]
internal static class IsWhitelistedByStorageSitePatch
{
    private static bool _aliveLogged;
    private static readonly HashSet<string> _loggedOwners = new();

    static bool Prefix(IResourceStorageSite rss, ref bool __result)
    {
        try
        {
            if (!_aliveLogged)
            {
                _aliveLogged = true;
                Plugin.Logger.LogInfo("[OuthouseComposter][eatgate] IsWhitelistedByStorage(IResourceStorageSite) patch alive.");
            }

            if (!Plugin.ProtectOuthouseContents.Value) return true;
            if (!OuthouseGate.IsOuthouseSite(rss)) return true;

            __result = false;

            if (Plugin.LogEatGate.Value)
            {
                string owner = SafeOwnerName(rss);
                if (_loggedOwners.Add(owner))
                    Plugin.Logger.LogInfo($"[OuthouseComposter][eatgate] denied consume from outhouse: method=IsWhitelistedByStorage(IResourceStorageSite) owner='{owner}'");
            }

            return false;
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"[OuthouseComposter] IsWhitelistedByStorageSitePatch error: {ex}");
            return true;
        }
    }

    private static string SafeOwnerName(IResourceStorageSite? rss)
    {
        var s = OuthouseGate.ResolveOwningStructure(rss);
        return s != null ? OuthouseGate.SafeStructureName(s) : "?";
    }
}

[HarmonyPatch(typeof(SurvivalObjectiveQuest.SatisfyObjectiveQuestData), nameof(SurvivalObjectiveQuest.SatisfyObjectiveQuestData.IsWhitelistedByAny), new[] { typeof(IResourceStorageSite) })]
internal static class IsWhitelistedByAnyPatch
{
    private static bool _aliveLogged;
    private static readonly HashSet<string> _loggedOwners = new();

    static bool Prefix(IResourceStorageSite rss, ref bool __result)
    {
        try
        {
            if (!_aliveLogged)
            {
                _aliveLogged = true;
                Plugin.Logger.LogInfo("[OuthouseComposter][eatgate] IsWhitelistedByAny(IResourceStorageSite) patch alive.");
            }

            if (!Plugin.ProtectOuthouseContents.Value) return true;
            if (!OuthouseGate.IsOuthouseSite(rss)) return true;

            __result = false;

            if (Plugin.LogEatGate.Value)
            {
                string owner = SafeOwnerName(rss);
                if (_loggedOwners.Add(owner))
                    Plugin.Logger.LogInfo($"[OuthouseComposter][eatgate] denied consume from outhouse: method=IsWhitelistedByAny(IResourceStorageSite) owner='{owner}'");
            }

            return false;
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"[OuthouseComposter] IsWhitelistedByAnyPatch error: {ex}");
            return true;
        }
    }

    private static string SafeOwnerName(IResourceStorageSite? rss)
    {
        var s = OuthouseGate.ResolveOwningStructure(rss);
        return s != null ? OuthouseGate.SafeStructureName(s) : "?";
    }
}
