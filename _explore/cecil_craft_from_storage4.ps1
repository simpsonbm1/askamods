# Craft-from-storage research pass 4: Settlement resource/storage query surface + ItemCollection.GetItemInfos
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
    "$($m.ReturnType.FullName) $($m.Name)($ps)$v"
}
function DumpType($tn) {
    $t = $byName[$tn]
    if (-not $t) { Write-Output "  [NOT FOUND] $tn"; return }
    Write-Output ""
    Write-Output "---- $tn (base: $(if ($t.BaseType) { $t.BaseType.FullName } else { '-' })) ----"
    foreach ($p in $t.Properties) { Write-Output "  P: $($p.Name) : $($p.PropertyType.FullName)" }
    foreach ($m in $t.Methods) {
        if ($m.Name -match "^get_|^set_|^add_|^remove_") { continue }
        Write-Output "  M: $(Sig $m)"
    }
}

Write-Output "=================================================================="
Write-Output "PASS H: Settlement full surface"
Write-Output "=================================================================="
DumpType "SSSGame.Settlement"

Write-Output ""
Write-Output "=================================================================="
Write-Output "PASS I: ItemManifest.GetTotalQuantity / GetItems / storage-site related types"
Write-Output "=================================================================="
foreach ($tn in @("SandSailorStudio.Inventory.ItemManifest","SandSailorStudio.Inventory.ItemInfoQuantity")) {
    $t = $byName[$tn]
    if ($t) {
        Write-Output ""
        Write-Output "-- ${tn}: GetTotalQuantity/GetItems/item info surface --"
        foreach ($m in $t.Methods) {
            if ($m.Name -match "^get_|^set_|^add_|^remove_") { continue }
            if ($m.Name -match "(?i)GetTotalQuantity|GetItems|Info") { Write-Output "  M: $(Sig $m)" }
        }
    }
}

Write-Output ""
Write-Output "=================================================================="
Write-Output "PASS J: ItemCollection full method surface (incl GetItemInfos)"
Write-Output "=================================================================="
$t = $byName["SandSailorStudio.Inventory.ItemCollection"]
if ($t) {
    foreach ($m in $t.Methods) {
        if ($m.Name -match "^get_|^set_|^add_|^remove_") { continue }
        Write-Output "  M: $(Sig $m)"
    }
}

Write-Output ""
Write-Output "=================================================================="
Write-Output "PASS K: types matching StorageSite (interface/impl) + IResourceStorageSite"
Write-Output "=================================================================="
foreach ($t in $allTypes) {
    if ($t.FullName -match "(?i)StorageSite") {
        Write-Output "  $($t.FullName)  : $(if ($t.BaseType) { $t.BaseType.FullName } else { '-' })"
    }
}

Write-Output ""
Write-Output "DONE"

Write-Output ""
Write-Output "=================================================================="
Write-Output "PASS L: IResourceStorageSite full interface surface"
Write-Output "=================================================================="
DumpType "SSSGame.IResourceStorageSite"

Write-Output ""
Write-Output "=================================================================="
Write-Output "PASS M: Blueprint / CraftBlueprint full surface"
Write-Output "=================================================================="
DumpType "SandSailorStudio.Inventory.Blueprint"
DumpType "SSSGame.CraftBlueprint"

Write-Output ""
Write-Output "=================================================================="
Write-Output "PASS N: IInteractionAgent surface (for agent identification)"
Write-Output "=================================================================="
DumpType "SSSGame.IInteractionAgent"

Write-Output ""
Write-Output "=================================================================="
Write-Output "PASS O: Item base class (name/info surface for Blueprint)"
Write-Output "=================================================================="
$t = $byName["SandSailorStudio.Inventory.Item"]
if ($t) {
    Write-Output ""
    Write-Output "---- SandSailorStudio.Inventory.Item (base: $(if ($t.BaseType) { $t.BaseType.FullName } else { '-' })) ----"
    foreach ($p in $t.Properties) { Write-Output "  P: $($p.Name) : $($p.PropertyType.FullName)" }
}

Write-Output ""
Write-Output "=================================================================="
Write-Output "PASS P: TabButton namespace resolution"
Write-Output "=================================================================="
foreach ($t in $allTypes) {
    if ($t.Name -eq "TabButton") { Write-Output "  $($t.FullName)" }
}

Write-Output ""
Write-Output "=================================================================="
Write-Output "PASS Q: ItemInfoQuantity full surface + Structure name-ish members"
Write-Output "=================================================================="
DumpType "SandSailorStudio.Inventory.ItemInfoQuantity"
$t = $byName["SSSGame.Structure"]
if ($t) {
    Write-Output ""
    Write-Output "-- SSSGame.Structure: name/title members --"
    foreach ($p in $t.Properties) {
        if ($p.Name -match "(?i)name|title") { Write-Output "  P: $($p.Name) : $($p.PropertyType.FullName)" }
    }
}
