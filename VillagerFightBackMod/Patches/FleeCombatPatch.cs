using System;
using HarmonyLib;
using SSSGame.AI;
using SSSGame.Combat;

namespace VillagerFightBackMod.Patches;

// The villager flee behavior lives in FleeCombatQuest. Its data object decides, via the get-only
// computed property ShouldFight, whether to stand and fight vs run. We post-process that getter:
// if it says "flee" (false) but the currently-engaged enemy is on our whitelist, we flip it to
// "fight" (true). ShouldFight is target-agnostic in vanilla (home-safety / HP / defense score), so
// reading the engaged enemy is what makes this per-enemy. No state write / no RPC -> co-op-safe.
//
// NOTE (unverified until in-game): whether ShouldFight==true actually produces *attacking* for a
// non-warrior villager, vs merely "stop running", can't be read from the IL2CPP binary (FSM state
// bodies aren't dumpable). If they just stand there, the fallback is routing them through the
// warrior combat path. Run with DebugLogging=true first to confirm the flip lands and they swing.
[HarmonyPatch(typeof(FleeCombatQuest.FleeCombatQuestData), nameof(FleeCombatQuest.FleeCombatQuestData.ShouldFight), MethodType.Getter)]
internal static class FleeCombatShouldFightPatch
{
    static void Postfix(FleeCombatQuest.FleeCombatQuestData __instance, ref bool __result)
    {
        try
        {
            if (!Plugin.EnabledCfg.Value) return;
            if (__result) return; // already going to fight — nothing to override

            IAttackTarget enemy = __instance._engagedEnemy;
            if (enemy == null) return;

            // Host/solo authority only: the FSM that consumes this runs on the authoritative sim.
            // Flipping a non-authoritative client's copy would do nothing useful and risks confusion.
            var surv = __instance._survival;
            if (surv != null && !surv._hasAuthority) return;

            if (Plugin.IsWhitelisted(enemy))
            {
                __result = true;
                if (Plugin.DebugLogging.Value && Plugin.ShouldLogFlip())
                    Plugin.Logger.LogInfo(
                        $"[VillagerFightBack] Forced ShouldFight=TRUE vs '{enemy.GetTargetName()}' " +
                        $"(faction={Plugin.FactionName(enemy.Faction)}).");
            }
        }
        catch (Exception ex) { Plugin.Logger.LogError($"[VillagerFightBack] ShouldFight postfix: {ex}"); }
    }
}

// Discovery aid: fires when a villager actually gets spooked by an enemy. Logs the enemy's exact
// name + faction (once per distinct name) so the player can fill FightBackAgainst correctly —
// creature names live in runtime ScriptableObjects and aren't visible in the binary. Log-only.
[HarmonyPatch(typeof(FleeCombatQuest.FleeCombatQuestData), "_GetSpooked")]
internal static class FleeCombatGetSpookedPatch
{
    static void Postfix(IAttackTarget spookyTarget)
    {
        try
        {
            if (!Plugin.DebugLogging.Value) return;
            if (spookyTarget == null) return;

            string name = spookyTarget.GetTargetName();
            if (string.IsNullOrEmpty(name)) name = "(unnamed)";
            if (Plugin.AlreadyLoggedSpook(name)) return;

            string fac = Plugin.FactionName(spookyTarget.Faction);
            bool wl = Plugin.IsWhitelisted(spookyTarget);
            Plugin.Logger.LogInfo(
                $"[VillagerFightBack] Villager spooked by '{name}' (faction={fac}) — " +
                $"{(wl ? "ON whitelist (will fight)" : "not on whitelist (will flee); add this name to FightBackAgainst to make them fight it")}.");
        }
        catch (Exception ex) { Plugin.Logger.LogError($"[VillagerFightBack] GetSpooked postfix: {ex}"); }
    }
}
