# Spike follow-up: ItemContainer <-> ItemCollection linkage + drop recipe surfaces
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

function Sig($m) {
    $ps = ($m.Parameters | ForEach-Object { "$($_.ParameterType.Name) $($_.Name)" }) -join ", "
    $flags = @()
    if ($m.IsVirtual) { $flags += "virtual" }
    if ($m.IsStatic) { $flags += "static" }
    $fs = if ($flags) { " [" + ($flags -join ",") + "]" } else { "" }
    "$($m.ReturnType.Name) $($m.Name)($ps)$fs"
}

function DumpType($tn) {
    foreach ($t in ($allTypes | Where-Object { $_.Name -eq $tn })) {
        $base = if ($t.BaseType) { $t.BaseType.FullName } else { "none" }
        Write-Output "TYPE: $($t.FullName) base=$base"
        foreach ($f in $t.Fields) { if ($f.Name -notmatch "^Native") { Write-Output "  F: $($f.Name) : $($f.FieldType.Name)" } }
        foreach ($p in $t.Properties) { Write-Output "  P: $($p.Name) : $($p.PropertyType.Name)" }
        foreach ($m in $t.Methods) { if ($m.Name -notmatch "^get_|^set_") { Write-Output "  M: $(Sig $m)" } }
        Write-Output ""
    }
}

Write-Output "########## ItemContainer full ##########"
DumpType "ItemContainer"

Write-Output "########## ItemContainerComponent full ##########"
DumpType "ItemContainerComponent"

Write-Output "########## ItemObjectSpawnContext ##########"
DumpType "ItemObjectSpawnContext"

Write-Output "########## ItemCollection: members typed ItemContainer / container-ish ##########"
foreach ($t in ($allTypes | Where-Object { $_.Name -eq "ItemCollection" })) {
    foreach ($f in $t.Fields) { if ($f.FieldType.Name -match "Container" -and $f.Name -notmatch "^Native") { Write-Output "  F: $($f.Name) : $($f.FieldType.Name)" } }
    foreach ($p in $t.Properties) { if ($p.PropertyType.Name -match "Container|List|Item") { Write-Output "  P: $($p.Name) : $($p.PropertyType.Name)" } }
    foreach ($m in $t.Methods) { if ($m.Name -match "Container|Remove|TakeOut|Extract" -and $m.Name -notmatch "^get_|^set_") { Write-Output "  M: $(Sig $m)" } }
}

Write-Output ""
Write-Output "########## StorageInteraction: container/collection surface ##########"
foreach ($t in ($allTypes | Where-Object { $_.Name -eq "StorageInteraction" })) {
    $base = if ($t.BaseType) { $t.BaseType.FullName } else { "none" }
    Write-Output "TYPE: $($t.FullName) base=$base"
    foreach ($f in $t.Fields) { if ($f.Name -notmatch "^Native") { Write-Output "  F: $($f.Name) : $($f.FieldType.Name)" } }
    foreach ($p in $t.Properties) { Write-Output "  P: $($p.Name) : $($p.PropertyType.Name)" }
    foreach ($m in $t.Methods) { if ($m.Name -match "Drop|Container|Collection|Inventory|Item" -and $m.Name -notmatch "^get_|^set_") { Write-Output "  M: $(Sig $m)" } }
    Write-Output ""
}

Write-Output "########## ItemThumbnailPanel.CommandDropItem + ContextMenu.Drop context ##########"
foreach ($t in ($allTypes | Where-Object { $_.Name -eq "ItemThumbnailPanel" })) {
    foreach ($f in $t.Fields) { if ($f.FieldType.Name -match "Item|Collection|Container" -and $f.Name -notmatch "^Native") { Write-Output "  F: $($f.Name) : $($f.FieldType.Name)" } }
    foreach ($p in $t.Properties) { if ($p.PropertyType.Name -match "Item|Collection|Container") { Write-Output "  P: $($p.Name) : $($p.PropertyType.Name)" } }
}
