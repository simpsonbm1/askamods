# Food-pipeline probe (SupplyChainMod pre-arm blocker 2, 2026-07-16):
# 1. CookingRecipeInfo hierarchy — all subclasses; does PLAIN CookingRecipeInfo carry an
#    input/source field (barbecue raw->cooked edge)?
# 2. CookingRecipeRequirement + its `type` enum + table reference (resolve '?' Table reqs).
# 3. Gatherer / Hunting / Dining station classes — name search + base chains + task surfaces.
# 4. ItemInfo food/nutrition fields (eating magnitude term).
$asmPath = "D:\SteamLibrary\steamapps\common\ASKA\BepInEx\interop\Assembly-CSharp.dll"
[void][System.Reflection.Assembly]::LoadFrom("d:\Claude Projects\askamods\_explore\bin\Debug\net10.0\Mono.Cecil.dll")
$asm = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($asmPath)
$mod = $asm.MainModule

$allTypes = New-Object System.Collections.Generic.List[object]
foreach ($t in $mod.Types) {
    $allTypes.Add($t)
    foreach ($n in $t.NestedTypes) { $allTypes.Add($n) }
}
$inv = [Mono.Cecil.AssemblyDefinition]::ReadAssembly("D:\SteamLibrary\steamapps\common\ASKA\BepInEx\interop\SandSailorStudio.dll")
foreach ($t in $inv.MainModule.Types) {
    $allTypes.Add($t)
    foreach ($n in $t.NestedTypes) { $allTypes.Add($n) }
}

$byName = @{}
foreach ($t in $allTypes) { if (-not $byName.ContainsKey($t.Name)) { $byName[$t.Name] = $t } }

function BaseChain($t) {
    $chain = @()
    $cur = $t
    $guard = 0
    while ($cur -and $cur.BaseType -and $guard -lt 12) {
        $bn = $cur.BaseType.Name
        $chain += $bn
        if ($byName.ContainsKey($bn)) { $cur = $byName[$bn] } else { $cur = $null }
        $guard++
    }
    $chain -join " -> "
}

function Sig($m) {
    $ps = ($m.Parameters | ForEach-Object { "$($_.ParameterType.Name) $($_.Name)" }) -join ", "
    "$($m.ReturnType.Name) $($m.Name)($ps)"
}

function DumpType($tn) {
    foreach ($t in ($allTypes | Where-Object { $_.Name -eq $tn })) {
        Write-Output "TYPE: $($t.FullName) baseChain=[$(BaseChain $t)]"
        foreach ($f in $t.Fields) { if ($f.Name -notmatch "^Native") { Write-Output "  F: $($f.Name) : $($f.FieldType.Name)" } }
        foreach ($p in $t.Properties) { Write-Output "  P: $($p.Name) : $($p.PropertyType.Name)" }
        foreach ($m in $t.Methods) { if ($m.Name -notmatch "^get_|^set_|^\.") { Write-Output "  M: $(Sig $m)" } }
        Write-Output ""
    }
}

Write-Output "===== 1. CookingRecipeInfo subclass census ====="
foreach ($t in $allTypes) {
    $chain = BaseChain $t
    if ($chain -match "CookingRecipeInfo") { Write-Output "$($t.FullName)  base=[$chain]" }
}
Write-Output ""
DumpType "CookingRecipeInfo"
DumpType "CrockpotRecipeInfo"

Write-Output "===== 2. CookingRecipeRequirement + enums + table types ====="
DumpType "CookingRecipeRequirement"
foreach ($t in $allTypes) {
    if ($t.IsEnum -and ($t.FullName -match "Requirement|CookingRecipe")) {
        Write-Output "ENUM: $($t.FullName)"
        foreach ($f in $t.Fields) { if ($f.Name -ne "value__") { Write-Output "  $($f.Name) = $($f.Constant)" } }
    }
}
Write-Output ""

Write-Output "===== 3a. Type-name census: Gather / Hunt / Dining / Field ====="
foreach ($t in $allTypes) {
    if ($t.Name -match "Gather|Hunt|Dining" -and -not $t.IsEnum) {
        Write-Output "$($t.FullName)  base=[$(BaseChain $t)]"
    }
}
Write-Output ""
Write-Output "===== 3b. Workstation subclass census (which classes exist at all) ====="
foreach ($t in $allTypes) {
    $chain = BaseChain $t
    if ($chain -match "\bWorkstation\b") { Write-Output "$($t.FullName)  base=[$chain]" }
}
Write-Output ""

Write-Output "===== 4. ItemInfo food/nutrition surface ====="
foreach ($t in ($allTypes | Where-Object { $_.Name -eq "ItemInfo" })) {
    foreach ($f in $t.Fields) { if ($f.Name -match "food|hunger|nutri|satia|eat|consum" ) { Write-Output "  F: $($f.FieldType.Name) $($f.Name)" } }
    foreach ($p in $t.Properties) { if ($p.Name -match "food|hunger|nutri|satia|eat|consum") { Write-Output "  P: $($p.PropertyType.Name) $($p.Name)" } }
}
foreach ($t in $allTypes) {
    if ($t.Name -match "^Food|Edible|Nutrition" -and -not $t.IsEnum) {
        Write-Output "$($t.FullName)  base=[$(BaseChain $t)]"
    }
}
