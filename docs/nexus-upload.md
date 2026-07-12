# Publishing to Nexus Mods (automated upload)

Published mods can be updated via the **Nexus Mods Upload API** (v3, open beta) — no manual web
upload. Driven by a reusable GitHub Actions workflow: **`.github/workflows/nexus-upload.yml`**
(manual `workflow_dispatch`).

**How it works:** pick a mod + version; the workflow zips that mod's **committed** `<Mod>/<Mod>.dll`
(bare DLL inside the zip — matches how the pages were packaged manually) and uploads it as a new
version to the mod's Nexus **file group** via the official
[`Nexus-Mods/upload-action`](https://github.com/Nexus-Mods/upload-action) (pinned `v1.0.0-beta.8`).
The API key is the **Personal API Key** (bottom of <https://www.nexusmods.com/settings/api-keys>;
NOT the per-app keys above it), stored as the repo secret **`NEXUSMODS_API_KEY`**.

**Run it** — GitHub UI: Actions → *Upload to Nexus Mods* → Run workflow (mod / version / category / archive / changelog). Or CLI:
```
gh workflow run nexus-upload.yml -f mod=DynamicVillagerNeedsMod -f version=1.1.0 \
  -f category=main -f archive_existing=true -f description="…changelog…"
gh run watch <run-id> --exit-status     # then: gh run view <run-id> --log  → "version_id=…" on success
```

**Nexus file group IDs** (the "API Info → Group ID" on each mod page; = the action's `file_id`):

| Mod | Nexus name | Group ID |
|---|---|---|
| DynamicVillagerNeedsMod | Dynamic Villager Needs | `7567346` |
| HealthRegenMod | Passive Health Regen | `7551800` |
| TreeRespawnMod | Tree and Gatherable Respawner | `7551668` |
| MineRefreshMod | Mine Refresh | `7586480` |
| VillagerFightBackMod | Villager Fight Back | `7587134` |
| TerrainLevelerMod | Bulldozer Field / Terrain Leveler | `7606994` |
| SeedScoutMod | World Seed Scout | `7619375` |
| TaskUnlockerMod | Cooking and Fishing Task Unlocker | `7623785` |
| ZeroTaskWorkersMod | Assigned Workers Start Idle | `7626437` |
| GroundItemVacuumMod | Ground Item Vacuum Cleaner | `7632863` |
| FishFilletMod | Inventory Fish Fillet | `7633997` |
| DenRespawnMod | Enemy Den Respawner | `7638170` |
| OuthouseComposterMod | Outhouse Composter | `7651913` |

BowDamageMod and TorchFuelMod aren't on Nexus yet — to add one: create its page + first file, then
add a line to the workflow's `case` block and an option to the `mod` input.

**Gotchas (confirmed during the first upload, DynamicVillagerNeedsMod 1.1.0, 2026-06-22):**
- **Uploads the COMMITTED DLL, not a fresh build.** CI can't build (the game's IL2CPP interop DLLs
  aren't redistributable, so they're not in the repo). Flow: build locally → commit the updated
  `<Mod>/<Mod>.dll` → push → run the workflow.
- The Upload API only **updates an existing** mod/file group — it can't create a mod page (do that
  once on the website first). A successful run publishes a **live, public** version; there's no
  dry-run/sandbox in the beta.
- `archive_existing=true` archives the prior version instead of leaving a duplicate main file.
- Action is still open beta — if a run fails on an input, bumping the pinned tag is the first thing to check.

**Per-version changelog (Files tab) — automated:** always pass the `description` input (the `-f
description=` arg, or the "Changelog / file description" field in the UI). It's the one bit of page
text the API sets — it lands on the file in the Files tab. Keep a consistent style:
```
<version> — <one-line summary>. Adds: <item>; <item>. Fixes: <item>.
```
(e.g. the 1.1.0 note: *"1.1.0: Villagers no longer get stuck at an empty food store … Also adds
optional hunger/thirst drain-rate multipliers (default 1.0 = vanilla)."*)

**Main description page (Description tab) — MANUAL, but don't forget it:** the API can't edit it
(the mod endpoint is GET-only; verified against the v3 OpenAPI schema). So whenever a
pushed/uploaded version adds a **user-facing feature worth calling out on the main page**, the
assistant should — after the upload — **ask the user to paste the current Description-tab source**,
then regenerate the **full** description block with the new Features/Configuration bits inserted in
place (the DynamicVillagerNeedsMod 1.1.0 edit is the template: two Features bullets + the matching
Configuration entries) and hand it back for them to paste over the whole description. The upload
workflow prints this same reminder in its run summary. Browser-automating the mod-edit form
(session-cookie scraping) is the only "API" path and is deliberately avoided (fragile +
ToS-sensitive).

---

## Initial Mod Page Setup & Formatting (New Mod Page Drafts)

When creating a new mod page on Nexus Mods, the upload API cannot be used (the page must be created manually first on the website). 

When the user asks to **"draft the mod page"**, you must generate two blocks:

### 1. Short Description (Max 350 characters)
A punchy, player-facing summary under 350 characters that summarizes what the mod does without technical jargon.

### 2. Main Page BBCode Description
Use the following standard template and colors to ensure visual consistency across all ASKA mods:

```bbcode
[size=4][b]Description[/b][/size]
[size=3][color=#D4D4D8]Describe the main purpose of your mod in player-facing terms. Explain the core mechanics simply without technical dev jargon (e.g. use "Active Retaliation" instead of "FSM behavior swap").[/color][/size]

[size=4][b]Installation instructions[/b][/size]
[size=3][color=#D4D4D8]1. Install BepInEx 6 (IL2CPP build).
2. Place the [ModName].dll into your [i]ASKA/BepInEx/plugins/[/i] folder.[/color][/size]

[size=4][b]Main features[/b][/size]
[size=3][color=#D4D4D8]- Bullet point describing core feature 1
- Bullet point describing core feature 2
- Bullet point describing core feature 3[/color][/size]

[size=4][b]Requirements[/b][/size]
[size=3][color=#D4D4D8]- BepInEx 6 (IL2CPP)
(Note: Do not list ASKA itself as a requirement, as it is implied by being on the ASKA page)[/color][/size]
```

