# Craft-from-storage research pass 3: CreateItemsTabPage (recipe-list UI) + InventoryComponent (all interop DLLs)
$base = "D:\SteamLibrary\steamapps\common\ASKA\BepInEx"
[void][System.Reflection.Assembly]::LoadFrom("d:\Claude Projects\askamods\_explore\bin\Debug\net10.0\Mono.Cecil.dll")

$allTypes = New-Object System.Collections.Generic.List[object]
foreach ($f in Get-ChildItem "$base\interop\*.dll") {
    try { $asm = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($f.FullName) } catch { continue }
    foreach ($t in $asm.MainModule.Types) {
        $allTypes.Add($t)
        foreach ($n in $t.NestedTypes) { $allTypes.Add($n) }
    }
}
$byName = @{}
foreach ($t in $allTypes) { if (-not $byName.ContainsKey($t.FullName)) { $byName[$t.FullName] = $t } }

function Sig($m) {
    $ps = ($m.Parameters | ForEach-Object { "$($_.ParameterType.Name) $($_.Name)" }) -join ", "
    $v = ""
    if ($m.IsVirtual) { $v = " [virt]" }
    "$($m.ReturnType.Name) $($m.Name)($ps)$v"
}
function DumpType($tn) {
    $t = $byName[$tn]
    if (-not $t) { Write-Output "  [NOT FOUND] $tn"; return }
    Write-Output ""
    Write-Output "---- $tn (base: $(if ($t.BaseType) { $t.BaseType.FullName } else { '-' })) ----"
    foreach ($p in $t.Properties) { Write-Output "  P: $($p.Name) : $($p.PropertyType.Name)" }
    foreach ($m in $t.Methods) {
        if ($m.Name -match "^get_|^set_|^add_|^remove_") { continue }
        Write-Output "  M: $(Sig $m)"
    }
    if ($t.HasNestedTypes) {
        Write-Output "  NESTED: $(($t.NestedTypes | ForEach-Object { $_.Name }) -join ', ')"
    }
}

Write-Output "=================================================================="
Write-Output "PASS E: CreateItemsTabPage + base chain + nested"
Write-Output "=================================================================="
$tn = "SSSGame.UI.CreateItemsTabPage"
$t = $byName[$tn]
while ($t) {
    DumpType $t.FullName
    if ($t.BaseType -and $t.BaseType.FullName -notmatch "^UnityEngine|^Il2Cpp") {
        $t = $byName[$t.BaseType.FullName]
    } else {
        if ($t.BaseType) { Write-Output ""; Write-Output "(chain stops at $($t.BaseType.FullName))" }
        $t = $null
    }
}

Write-Output ""
Write-Output "=================================================================="
Write-Output "PASS F: UI types matching CreateItem|ItemSlot|BlueprintSlot|RecipeSlot|CraftableItem|ItemTabPage"
Write-Output "=================================================================="
foreach ($t in $allTypes) {
    if ($t.FullName -match "^(SSSGame|SandSailorStudio)" -and $t.Name -match "(?i)createitem|itemslot|blueprintslot|recipeslot|craftable|itemtabpage|createitems") {
        Write-Output "  $($t.FullName)  : $(if ($t.BaseType) { $t.BaseType.Name } else { '-' })"
    }
}

Write-Output ""
Write-Output "=================================================================="
Write-Output "PASS G: InventoryComponent + ItemCollection key surface"
Write-Output "=================================================================="
DumpType "SandSailorStudio.Inventory.InventoryComponent"
Write-Output ""
Write-Output "-- ItemCollection: methods matching manifest|contains|check|quantity --"
$t = $byName["SandSailorStudio.Inventory.ItemCollection"]
if ($t) {
    foreach ($m in $t.Methods) {
        if ($m.Name -match "^get_|^set_|^add_|^remove_") { continue }
        if ($m.Name -match "(?i)manifest|contains|check|quantity|hasitem") { Write-Output "  M: $(Sig $m)" }
    }
}
Write-Output ""
Write-Output "-- ItemManifest: full method surface --"
$t = $byName["SandSailorStudio.Inventory.ItemManifest"]
if ($t) {
    foreach ($m in $t.Methods) {
        if ($m.Name -match "^get_|^set_|^add_|^remove_") { continue }
        Write-Output "  M: $(Sig $m)"
    }
}

Write-Output ""
Write-Output "DONE"
