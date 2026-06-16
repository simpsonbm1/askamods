using System;
using HarmonyLib;
using SSSGame;
using SSSGame.Combat;

namespace BowDamageMod.Patches;

[HarmonyPatch(typeof(Creature), nameof(Creature.TakeDamage))]
internal static class CreatureDamagePatch
{
    static void Prefix(DamageData damage)
    {
        try
        {
            if (damage == null) return;

            var weaponName = damage.weapon?.info?.Name;
            if (!Plugin.IsTargetWeapon(weaponName)) return;

            float multiplier = Plugin.DamageMultiplier.Value;
            float before = damage.result;
            damage.baseDamage *= multiplier;
            damage.result *= multiplier;

            Plugin.Logger.LogDebug(
                $"[BowDamageMod] {weaponName}: {before:F1} → {damage.result:F1} ({multiplier}x)");
        }
        catch (Exception ex) { Plugin.Logger.LogError($"[BowDamageMod] CreatureDamagePatch: {ex}"); }
    }
}
