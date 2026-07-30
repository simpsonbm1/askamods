# 2026-07-30: is a workshop's storage-crafting gate per-UNIT or all-or-nothing per WORKSHOP?
#
# User's intended model: default = player crafts from storage at workshop TABLES; toggle 1 = villagers
# too; toggle 2 = villagers also at workshop ADD-ON UNITS (the physical transforms - sawhorses, anvil).
# The mod's gate is currently per-BUILDING, so a workshop containing any add-on is skipped whole,
# which breaks toggle 1 for that workshop's table.
#
# In-game evidence 2026-07-30: one station object reported four surfaces beneath it, none on itself -
#   stationProbe station=Workshop_L2(Clone) outcome=matched class=AnvilInteraction count=4
#     all=[CraftInteraction@descendant; AnvilInteraction@descendant; CarpenterInteraction@descendant;
#          CarpenterInteraction@descendant]
#
# The question: can a fetch request be traced to ONE surface?
#   Q1 Is there one CraftingStation per surface, or one per workshop? (does CraftInteraction hold a
#      back-reference to its CraftingStation, or CraftingStation a collection of interactions?)
#   Q2 Does CraftingStation.CraftingProject reach a blueprint, and does that blueprint name its
#      interaction? CraftBlueprintInfo.interaction is already confirmed to exist - if a project
#      reaches a BlueprintInfo, that is a per-unit route with no per-unit station needed.
#   Q3 Does CrafterFetchQuest carry anything BESIDES craftingStation that identifies the surface?
# Harness copied from cecil_cfs_v092.ps1.
$base = "D:\SteamLibrary\steamapps\common\ASKA\BepInEx"
[void][System.Reflection.Assembly]::LoadFrom("d:\Claude Projects\askamods\_explore\bin\Debug\net10.0\Mono.Cecil.dll")

$allTypes = New-Object System.Collections.Generic.List[object]
function AddRec($t) { $allTypes.Add($t); foreach ($n in $t.NestedTypes) { AddRec $n } }
foreach ($f in Get-ChildItem "$base\interop\*.dll") {
    try { $asm = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($f.FullName) } catch { continue }
    foreach ($t in $asm.MainModule.Types) { AddRec $t }
}
$byName = @{}
foreach ($t in $allTypes) { if (-not $byName.ContainsKey($t.FullName)) { $byName[$t.FullName] = $t } }

function Sig($m) {
    $ps = ($m.Parameters | ForEach-Object { "$($_.ParameterType.FullName) $($_.Name)" }) -join ", "
    $mods = @(); if ($m.IsStatic) { $mods += "static" }; if ($m.IsVirtual) { $mods += "virt" }
    if ($m.IsPublic) { $mods += "pub" } elseif ($m.IsFamily) { $mods += "prot" } else { $mods += "priv" }
    "$($m.ReturnType.FullName) $($m.Name)($ps) [" + ($mods -join ",") + "]"
}
function BaseChain($t) {
    $chain = @(); $cur = $t; $guard = 0
    while ($cur -and $cur.BaseType -and $guard -lt 20) {
        $bn = $cur.BaseType.FullName; $chain += $bn; $cur = $byName[$bn]; $guard++ }
    if ($chain.Count -eq 0) { return "(none)" }
    return ($chain -join "  ->  ")
}
function DumpType($tn) {
    Write-Output ""
    Write-Output "=================================================================="
    Write-Output "TYPE: $tn"
    Write-Output "=================================================================="
    $t = $byName[$tn]
    if (-not $t) { Write-Output "  [NOT FOUND]"; return }
    Write-Output "  BASECHAIN: $(BaseChain $t)"
    if ($t.NestedTypes.Count) { Write-Output ("  NESTED   : " + (($t.NestedTypes | ForEach-Object { $_.FullName }) -join ", ")) }
    Write-Output "  ---- PROPERTIES ----"
    foreach ($p in $t.Properties) { Write-Output "  P: $($p.PropertyType.FullName) $($p.Name)" }
    Write-Output "  ---- METHODS (non-accessor) ----"
    foreach ($m in $t.Methods) { if ($m.Name -notmatch "^(get|set)_") { Write-Output "  M: $(Sig $m)" } }
}

# ---- Q1/Q2: the station, its project type, the quest, and the interaction family ----
foreach ($tn in @(
    "SSSGame.CraftingStation",
    "SSSGame.CraftingStation/CraftingProject",
    "SSSGame.CrafterFetchQuest",
    "SSSGame.CrafterSpecificFetchQuest",
    "SSSGame.CraftingQuest",
    "SSSGame.CraftInteraction",
    "SSSGame.CarpenterInteraction")) { DumpType $tn }

# ---- Q1: who holds a CraftingStation, and does any interaction hold one? ----
Write-Output ""
Write-Output "=================================================================="
Write-Output "Q1: every member ANYWHERE typed CraftingStation (holders + one-vs-many)"
Write-Output "=================================================================="
$h = 0
foreach ($t in $allTypes) {
    foreach ($p in $t.Properties) {
        if ($p.PropertyType.FullName -match "CraftingStation") {
            Write-Output "  $($t.FullName) :: P: $($p.PropertyType.FullName) $($p.Name)"; $h++ } }
    foreach ($m in $t.Methods) {
        if ($m.Name -match "^(get|set)_") { continue }
        if ((Sig $m) -match "CraftingStation") { Write-Output "  $($t.FullName) :: M: $(Sig $m)"; $h++ } }
}
if ($h -eq 0) { Write-Output "  NO HITS" }

# ---- Q2: does anything reach from a project/quest to a BlueprintInfo or an Interaction? ----
Write-Output ""
Write-Output "=================================================================="
Write-Output "Q2: members on the Project/Quest family typed Blueprint* or *Interaction"
Write-Output "=================================================================="
$h = 0
foreach ($t in $allTypes) {
    if ($t.FullName -notmatch "(CraftingProject|CraftingQuest|FetchQuest|CraftingStation)") { continue }
    foreach ($p in $t.Properties) {
        if ($p.PropertyType.FullName -match "(Blueprint|Interaction)") {
            Write-Output "  $($t.FullName) :: P: $($p.PropertyType.FullName) $($p.Name)"; $h++ } }
    foreach ($m in $t.Methods) {
        if ($m.Name -match "^(get|set)_") { continue }
        if ((Sig $m) -match "(Blueprint|Interaction)") { Write-Output "  $($t.FullName) :: M: $(Sig $m)"; $h++ } }
}
if ($h -eq 0) { Write-Output "  NO HITS" }

# ---- Q3: does CraftInteraction hold a back-reference to a station/workstation? ----
Write-Output ""
Write-Output "=================================================================="
Write-Output "Q3: CraftInteraction-family members matching Station|Workstation|Owner|Parent|Unit|Addon"
Write-Output "=================================================================="
$PAT = "(?i)Station|Workstation|Owner|Parent|Unit|Addon|Add_On"
$h = 0
foreach ($tn in @("SSSGame.CraftInteraction", "SSSGame.AnvilInteraction", "SSSGame.CarpenterInteraction", "SSSGame.Interaction")) {
    $t = $byName[$tn]
    if (-not $t) { Write-Output "  [$tn NOT FOUND]"; continue }
    Write-Output "  --- $tn ---"
    foreach ($p in $t.Properties) { if ($p.Name -match $PAT -or $p.PropertyType.FullName -match $PAT) { Write-Output "  P: $($p.PropertyType.FullName) $($p.Name)"; $h++ } }
    foreach ($m in $t.Methods) { if ($m.Name -notmatch "^(get|set)_" -and (Sig $m) -match $PAT) { Write-Output "  M: $(Sig $m)"; $h++ } }
}
if ($h -eq 0) { Write-Output "  NO HITS" }

# ---- Q4: is there a workshop add-on / module concept at all? ----
Write-Output ""
Write-Output "=================================================================="
Write-Output "Q4: types matching Workshop|Addon|Add_On|Module|Upgrade|Extension|Attachment"
Write-Output "=================================================================="
$h = 0
foreach ($t in $allTypes) {
    if ($t.FullName -match "(?i)Workshop|Addon|Add_On|Module|Upgrade|Extension|Attachment" -and $t.FullName -notmatch "^(Il2Cpp|UnityEngine|Fusion|Invector|Pathfinding|Cinemachine|TMPro)") {
        Write-Output "  $($t.FullName)   BASE: $(BaseChain $t)"; $h++ } }
if ($h -eq 0) { Write-Output "  NO HITS" }

Write-Output ""
Write-Output "DONE"
