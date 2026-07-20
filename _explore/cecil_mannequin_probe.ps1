# Test the mannequin theory: is the 'CharacterBuilder' container type an armor-display mannequin?
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
function Sig($m) {
    $ps = ($m.Parameters | ForEach-Object { "$($_.ParameterType.Name) $($_.Name)" }) -join ", "
    "$($m.ReturnType.Name) $($m.Name)($ps)"
}

Write-Output "=== 1. Types named like CharacterBuilder / Flask / ArmorRack ==="
foreach ($t in $allTypes) {
    if ($t.Name -match "(?i)characterbuilder|charactervisual|flask|armorrack") {
        Write-Output "  $($t.FullName)  : $(if ($t.BaseType) { $t.BaseType.Name } else { '-' })"
    }
}

Write-Output ""
Write-Output "=== 2. Mannequin / display-stand concept types ==="
foreach ($t in $allTypes) {
    if ($t.Name -match "(?i)mannequin|manikin|dummy|displaystand|armorstand|wardrobe|equipdisplay|showcase|podium|pedestal") {
        Write-Output "  $($t.FullName)  : $(if ($t.BaseType) { $t.BaseType.Name } else { '-' })"
    }
}

Write-Output ""
Write-Output "=== 3. Types whose name suggests equipment display on a structure ==="
foreach ($t in $allTypes) {
    if ($t.FullName -match "^SSSGame" -and $t.Name -match "(?i)(equip|armor|outfit|apparel).*(display|rack|stand|slot|holder|container)") {
        Write-Output "  $($t.FullName)  : $(if ($t.BaseType) { $t.BaseType.Name } else { '-' })"
    }
}

Write-Output ""
Write-Output "=== 4. ItemContainerType + StorageClassData shape (what 'type name' actually is) ==="
foreach ($tn in @("SandSailorStudio.Inventory.ItemContainerType", "SSSGame.ItemContainerType")) {
    $t = $allTypes | Where-Object { $_.FullName -eq $tn } | Select-Object -First 1
    if ($t) {
        Write-Output "---- $tn (base: $(if ($t.BaseType) { $t.BaseType.FullName } else { '-' })) ----"
        foreach ($p in $t.Properties) { Write-Output "  P: $($p.Name) : $($p.PropertyType.Name)" }
        foreach ($m in $t.Methods) {
            if ($m.Name -match "^get_|^set_|^add_|^remove_|ctor") { continue }
            Write-Output "  M: $(Sig $m)"
        }
    }
}

Write-Output ""
Write-Output "=== 5. Any type with an 'EquipPoint' / equipment-slot container link ==="
foreach ($t in $allTypes) {
    if ($t.Name -match "(?i)equippoint|equipmentslot|equipslot") {
        Write-Output "  $($t.FullName)  : $(if ($t.BaseType) { $t.BaseType.Name } else { '-' })"
        foreach ($p in $t.Properties) { Write-Output "      P: $($p.Name) : $($p.PropertyType.Name)" }
    }
}

Write-Output ""
Write-Output "DONE"
