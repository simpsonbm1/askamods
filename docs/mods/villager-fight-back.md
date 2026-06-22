# Mod 7: VillagerFightBackMod — PENDING IN-GAME VERIFICATION

**Goal:** Let regular (non-warrior) villagers **stand and fight a whitelisted set of enemies** instead
of fleeing — e.g. Wisps, which anything with a pulse can kill — while still fleeing from genuinely
dangerous attackers (draugar, wolves, …). Requested by a Nexus commenter on DynamicVillagerNeedsMod
("villagers can't fight back against the Wisps"). Villager-only; the player is never touched.

**Game subsystem:** [Villager Combat / Fight-vs-Flee System](../architecture.md#villager-combat--fight-vs-flee-system)
— the competing combat quests, warrior status, `IAttackTarget` identification, and the
pending-verification list all live there. Read it first.

**Working approach:**
- Pure Harmony, no polling MonoBehaviour. Two patches in `Patches/FleeCombatPatch.cs`:
  - **`FleeCombatShouldFightPatch`** — Postfix on `FleeCombatQuest/FleeCombatQuestData.get_ShouldFight`.
    When the game decides to flee (`__result==false`) but `__instance._engagedEnemy` is on the whitelist,
    flip `__result=true`. `ShouldFight` is GET-only/computed (home-safety/HP/defense-score), so we
    post-process the getter rather than setting a field. Reading `_engagedEnemy` is what makes it
    per-enemy. Gated on `__instance._survival._hasAuthority` (host/solo). Pure read + return-a-bool →
    no networked write → co-op-safe.
  - **`FleeCombatGetSpookedPatch`** — Postfix on `FleeCombatQuestData._GetSpooked(IAttackTarget)`,
    log-only. Logs each distinct attacker's `GetTargetName()` + `Faction` once (creature names are
    runtime ScriptableObject values, not in the binary) so the player can fill the whitelist.
- **Whitelist:** `FightBackAgainst` = comma-separated **name** substrings (case-insensitive, matched on
  the attacker's display name); `FightBackFactions` = optional comma-separated **faction** names
  (coarser — `Undead` would include draugar). Name matching is the precise lever; faction is the blunt one.

**Config `[VillagerFightBack]`** (`BepInEx/config/com.askamods.villagerfightback.cfg`):
- `Enabled` (true)
- `FightBackAgainst` (`"Wisp"`) — name substrings to fight; empty = vanilla flee
- `FightBackFactions` (`""`) — optional factions to also fight (Critters/Danger/Undead/Vikings/Neutral/Structures/Ignore)
- `DebugLogging` (**true** by default while unverified) — logs what spooks villagers + when a flip happens; turn off once the whitelist is dialed in

**⚠️ NOT yet confirmed in-game** (see the subsystem doc's pending list — the deciding facts can't be read
from the IL2CPP binary):
1. Whether `ShouldFight==true` makes a non-warrior actually *attack* vs merely stop fleeing. If they
   just stand there, fall back to routing whitelisted encounters through the warrior path.
2. Whether `_engagedEnemy` is populated when `ShouldFight` is read (if often null, capture the target
   from `_GetSpooked` instead).
3. The exact Wisp name/faction.

**First-run test plan:** new save, get a villager near the beach where Wisps spawn, set
`DebugLogging=true`. Confirm (a) the spook log prints the Wisp's real name/faction, (b) the
"Forced ShouldFight=TRUE" log fires, (c) the villager actually swings at and kills the Wisp rather
than standing idle. Then verify a draugr/wolf still makes them flee. Adjust `FightBackAgainst` to the
logged name if it isn't literally "Wisp".
