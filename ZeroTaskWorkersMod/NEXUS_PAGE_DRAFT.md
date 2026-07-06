# Nexus Page Draft — ZeroTaskWorkersMod v1.0.0

Working title: **"Assigned Workers Start Idle"** (user still workshopping the final name).
Page must be created manually on the website (the upload API can't create pages). After the page +
first file exist, add the file Group ID to `docs/nexus-upload.md` and wire the mod into
`.github/workflows/nexus-upload.yml` (case block + `mod` input option).

---

## Short Description (must stay under 350 characters)

Newly assigned villagers no longer inherit every task their workstation offers — they start with all task boxes unchecked, and you enable exactly the jobs you want. Your choices stick and survive save/reload. Build sites are unaffected, so unassigned villagers keep building. Works in solo and co-op (host runs the logic).

---

## Main Page BBCode Description

```bbcode
[size=4][b]Description[/b][/size]
[size=3][color=#D4D4D8]Tired of unchecking half a dozen task boxes every time you staff a building? With this mod, a villager you assign to a workstation starts with [b]no tasks checked[/b]. Open the station's worker panel and tick exactly the jobs you want them doing — nothing more. The tasks you pick are the ones that stick, and they survive saving and reloading.

Unassigning a villager works like vanilla: they return to the builder pool, and build sites and boat yards are never affected by the mod — so construction and firekeeping always keep working.

Works in solo and co-op (the host's game enforces it for the settlement).[/color][/size]

[size=4][b]Installation instructions[/b][/size]
[size=3][color=#D4D4D8]1. Install BepInEx 6 (IL2CPP build).
2. Place the ZeroTaskWorkersMod.dll into your [i]ASKA/BepInEx/plugins/[/i] folder.[/color][/size]

[size=4][b]Main features[/b][/size]
[size=3][color=#D4D4D8]- New workers inherit zero tasks — every task checkbox starts unchecked
- The tasks you tick manually are respected and survive save/reload
- Build sites and boat yards are always exempt, so unassigned villagers keep building
- Configurable: apply everywhere (default) or only to buildings whose name matches your list
- Load-safe: task assignments saved before installing the mod are untouched[/color][/size]

[size=4][b]Requirements[/b][/size]
[size=3][color=#D4D4D8]- BepInEx 6 (IL2CPP)[/color][/size]

[size=4][b]Configuration[/b][/size]
[size=3][color=#D4D4D8]BepInEx/config/com.askamods.zerotaskworkers.cfg (created on first launch):
- ApplyToAllBuildings (default true) — apply to every workstation, or only those matching BuildingNameList
- BuildingNameList (default empty) — comma-separated, case-insensitive building-name fragments; only used when ApplyToAllBuildings is false
- LoadGraceSeconds (default 10) — safety window around world load so saved assignments restore untouched
- LogTaskEvents (default false) — verbose troubleshooting log[/color][/size]
```
