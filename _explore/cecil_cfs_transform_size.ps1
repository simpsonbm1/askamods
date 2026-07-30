# 2026-07-29: three questions for CraftFromStorageMod after the v0.9.5 run.
#   Q1 DISCRIMINATOR - what separates a PHYSICAL-TRANSFORM recipe (Hardwood Long Stick placed on
#      sawhorses, chopped, shafts picked up) from an ordinary bin recipe? Forging is already
#      excluded by recipe class; the wood transforms report CraftBlueprintInfo /
#      WorkshopBlueprintInfo, which ordinary bin recipes also use, so class alone is not enough.
#      Lead: CraftBlueprintInfo carries an `interaction` property - if a transform names a
#      different interaction type, that IS the discriminator.
#   Q2 ITEM SIZE - a Hardwood Long Stick is LARGE and crafting-station bins accept up to MEDIUM
#      (user, 2026-07-29). Does an item declare a size, and what are the values?
#   Q3 CONTAINER LIMIT - does a container declare a maximum accepted size, so the mod can stop
#      attempting moves that cannot succeed?
# Harness copied from cecil_cfs_v092.ps1 (same helpers, plus a field dump for enums).
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
    $ps = ($m.Parameters | ForEach-Object {
        $pt = $_.ParameterType; $s = "$($pt.FullName) $($_.Name)"
        if ($pt.IsByReference) { $s += " <<BY-REF>>" }; $s }) -join ", "
    $mods = @()
    if ($m.IsStatic) { $mods += "static" }
    if ($m.IsVirtual) { $mods += "virt" }
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
    Write-Output "  ASSEMBLY : $($t.Module.Assembly.Name.Name)"
    Write-Output "  BASECHAIN: $(BaseChain $t)"
    Write-Output ("  INTERFACES: " + (($t.Interfaces | ForEach-Object { $_.InterfaceType.FullName }) -join ", "))
    if ($t.NestedTypes.Count) { Write-Output ("  NESTED   : " + (($t.NestedTypes | ForEach-Object { $_.FullName }) -join ", ")) }
    if ($t.IsEnum) {
        Write-Output "  ---- ENUM VALUES ----"
        foreach ($fl in $t.Fields) { if ($fl.HasConstant) { Write-Output "  E: $($fl.Name) = $($fl.Constant)" } }
        return
    }
    Write-Output "  ---- PROPERTIES ----"
    foreach ($p in $t.Properties) { Write-Output "  P: $($p.PropertyType.FullName) $($p.Name)" }
    Write-Output "  ---- METHODS ----"
    foreach ($m in $t.Methods) { if ($m.Name -notmatch "^(get|set)_(Native|field_Compiler)") { Write-Output "  M: $(Sig $m)" } }
}

# ---- Q1a: every *BlueprintInfo type in the game, with its base chain ----
Write-Output "=================================================================="
Write-Output "Q1a: ALL types whose name ends in BlueprintInfo (or Blueprint)"
Write-Output "=================================================================="
foreach ($t in $allTypes) {
    if ($t.FullName -match "(BlueprintInfo|Blueprint)$" -and $t.FullName -notmatch "^Il2Cpp") {
        Write-Output "  $($t.FullName)   BASE: $(BaseChain $t)"
    }
}

# ---- Q1b: full dump of the blueprint-info family + the interaction types ----
foreach ($tn in @(
    "SSSGame.BlueprintInfo",
    "SSSGame.CraftBlueprintInfo",
    "SSSGame.WorkshopBlueprintInfo",
    "SSSGame.ForgingBlueprintInfo",
    "SSSGame.CookingRecipeInfo",
    "SSSGame.CraftInteraction",
    "SSSGame.AnvilInteraction")) { DumpType $tn }

# ---- Q1c: any BlueprintInfo member naming a WORLD/PLACED item rather than a manifest ----
Write-Output ""
Write-Output "=================================================================="
Write-Output "Q1c: BlueprintInfo-family members matching World|Placed|Transform|Spawn|Prop|"
Write-Output "     Sawhorse|Carpenter|Station|Interaction|Requires"
Write-Output "=================================================================="
$PAT1 = "(?i)World|Placed|Transform|Spawn|Prop|Sawhorse|Carpenter|Station|Interaction|Requires"
$h = 0
foreach ($t in $allTypes) {
    if ($t.FullName -notmatch "Blueprint") { continue }
    foreach ($p in $t.Properties) {
        if ($p.Name -match $PAT1 -or $p.PropertyType.FullName -match $PAT1) {
            Write-Output "  $($t.FullName) :: P: $($p.PropertyType.FullName) $($p.Name)"; $h++ } }
    foreach ($m in $t.Methods) {
        if ($m.Name -match "^(get|set)_") { continue }
        if ((Sig $m) -match $PAT1) { Write-Output "  $($t.FullName) :: M: $(Sig $m)"; $h++ } }
}
if ($h -eq 0) { Write-Output "  NO HITS" }

# ---- Q1d: is there a Carpenter/Sawhorse interaction at all? ----
Write-Output ""
Write-Output "=================================================================="
Write-Output "Q1d: types matching Carpenter|Sawhorse|Sawbuck|Chop|Woodwork|Transform"
Write-Output "=================================================================="
$h = 0
foreach ($t in $allTypes) {
    if ($t.FullName -match "(?i)Carpenter|Sawhorse|Sawbuck|Chop|Woodwork|Transform" -and $t.FullName -notmatch "^Il2Cpp") {
        Write-Output "  $($t.FullName)   BASE: $(BaseChain $t)"; $h++ } }
if ($h -eq 0) { Write-Output "  NO HITS" }

# ---- Q2: item size ----
Write-Output ""
Write-Output "=================================================================="
Write-Output "Q2: ITEM SIZE - types and members matching Size|Volume|Bulk|Large|Weight"
Write-Output "=================================================================="
$h = 0
foreach ($t in $allTypes) {
    if ($t.FullName -match "(?i)(ItemSize|SizeCategory|ItemVolume|SizeType)" -and $t.FullName -notmatch "^Il2Cpp") {
        Write-Output "  TYPE: $($t.FullName)   ENUM=$($t.IsEnum)"; $h++ } }
if ($h -eq 0) { Write-Output "  no dedicated size TYPE found by name" }

$PAT2 = "(?i)Size|Volume|Bulk|Weight"
foreach ($tn in @("SandSailorStudio.Inventory.ItemInfo", "SSSGame.ItemInfo")) {
    $t = $byName[$tn]
    if (-not $t) { Write-Output "  [$tn NOT FOUND]"; continue }
    Write-Output ""
    Write-Output "  --- $tn members matching $PAT2 ---"
    foreach ($p in $t.Properties) { if ($p.Name -match $PAT2 -or $p.PropertyType.FullName -match $PAT2) { Write-Output "  P: $($p.PropertyType.FullName) $($p.Name)" } }
    foreach ($m in $t.Methods) { if ($m.Name -notmatch "^(get|set)_" -and (Sig $m) -match $PAT2) { Write-Output "  M: $(Sig $m)" } }
}

# full ItemInfo dump so nothing size-like is missed by the name filter
DumpType "SandSailorStudio.Inventory.ItemInfo"

# ---- Q3: container capacity / accepted size ----
Write-Output ""
Write-Output "=================================================================="
Write-Output "Q3: CONTAINER LIMITS - ItemContainer + container-type members matching"
Write-Output "    Size|Max|Accept|Allow|CanAdd|Capacity|Filter|Restrict"
Write-Output "=================================================================="
$PAT3 = "(?i)Size|Max|Accept|Allow|CanAdd|Capacity|Filter|Restrict"
foreach ($tn in @(
    "SandSailorStudio.Inventory.ItemContainer",
    "SandSailorStudio.Inventory.ItemCollection",
    "SandSailorStudio.Inventory.ContainerType",
    "SandSailorStudio.Inventory.ItemContainerType")) {
    $t = $byName[$tn]
    if (-not $t) { Write-Output ""; Write-Output "  [$tn NOT FOUND]"; continue }
    Write-Output ""
    Write-Output "  --- $tn ---"
    Write-Output "  BASECHAIN: $(BaseChain $t)"
    foreach ($p in $t.Properties) { if ($p.Name -match $PAT3 -or $p.PropertyType.FullName -match $PAT3) { Write-Output "  P: $($p.PropertyType.FullName) $($p.Name)" } }
    foreach ($m in $t.Methods) { if ($m.Name -notmatch "^(get|set)_" -and (Sig $m) -match $PAT3) { Write-Output "  M: $(Sig $m)" } }
}

Write-Output ""
Write-Output "DONE"
