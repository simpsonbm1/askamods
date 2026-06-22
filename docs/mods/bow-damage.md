# Mod 1: BowDamageMod — COMPLETE

**Goal:** Buff "Flimsy Shortbow" and its arrows without touching mid/late-game weapons.

**Game subsystem:** [Damage Pipeline](../architecture.md#damage-pipeline-projectiles--bow) — read it
(and its dead-ends list) before changing the patch point.

**Working approach:**
- Prefix patch on `SSSGame.Creature.TakeDamage(DamageData damage)` — the actual HP-reduction call
- `DamageData.weapon` is the arrow ("Wood Arrow"); match via `damage.weapon.info.Name`
- Multiply both `baseDamage` and `result` by `DamageMultiplier` config (default 3.0x)
- Config: `BowDamage/DamageMultiplier` (float), `BowDamage/TargetWeaponNames` (comma list)
- Confirmed working: 4 shots to kill a Wight vs 8 baseline; club damage unaffected

Note: because `Creature.TakeDamage` only fires for monsters (see
[Player vs. Creature](../architecture.md#player-character-vs-creature)), this patch never affects the
player — which is exactly what we want here.
