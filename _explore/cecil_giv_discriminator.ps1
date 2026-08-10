# GroundItemVacuumMod: what distinguishes a LOOSE DROPPED ITEM record from a world-placed
# asset record inside WorldDataSlot.INVENTORY? (2026-08-10 defect: whole-map removal deleted
# Iron Deposit / Jotun Blood / Cave Fingers Growth / Crawler Egg / Wight)
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
    $mods = @()
    if ($m.IsStatic) { $mods += "static" }
    if ($m.IsVirtual) { $mods += "virt" }
    if ($m.IsPublic) { $mods += "pub" } elseif ($m.IsFamily) { $mods += "prot" } else { $mods += "priv" }
    "$($m.ReturnType.Name) $($m.Name)($ps) [" + ($mods -join ",") + "]"
}

function DumpType($tn) {
    $t = $byName[$tn]
    if (-not $t) { Write-Host "### NO TYPE $tn`n"; return }
    Write-Host "### TYPE $($t.FullName)  base=$($t.BaseType)"
    Write-Host "  -- fields --"
    foreach ($f in $t.Fields) {
        if ($f.Name -match "^(NativeFieldInfoPtr|NativeMethodInfoPtr|NativeClassPtr)") { continue }
        Write-Host "    $($f.Name) : $($f.FieldType.Name)"
    }
    Write-Host "  -- properties --"
    foreach ($p in $t.Properties) { Write-Host "    $($p.Name) : $($p.PropertyType.Name)" }
    Write-Host "  -- methods --"
    foreach ($m in $t.Methods) { Write-Host "    $(Sig $m)" }
    Write-Host ""
}

Write-Host "==================== 1. WorldItemInstance FAMILY (all subclasses) ===================="
foreach ($t in $allTypes) {
    if ($t.BaseType -and $t.BaseType.Name -match "WorldItemInstance|WorldInstance|Instance$") {
        Write-Host "  $($t.FullName)  : $($t.BaseType.Name)"
    }
}

Write-Host "`n==================== 2. Types named *ItemInstance* / *ItemDescriptor* ===================="
foreach ($t in $allTypes) {
    if ($t.FullName -match "ItemInstance|ItemDescriptor|InstancesBuffer" -and $t.FullName -notmatch "<") {
        Write-Host "  $($t.FullName)  base=$($t.BaseType)"
    }
}

Write-Host "`n==================== 3. Key type dumps ===================="
DumpType "SSSGame.WorldItemInstance"
DumpType "SSSGame.InventoryItemInstance"
DumpType "SSSGame.WorldItemDescriptor"
DumpType "SSSGame.InventoryItemDescriptor"
DumpType "SSSGame.DynamicItemObject"
DumpType "SSSGame.WorldItemObject"

Write-Host "`n==================== 4. Types named *Decay* / *Despawn* / *Cleanup* / *Lifetime* ===================="
foreach ($t in $allTypes) {
    if ($t.FullName -match "Decay|Despawn|Cleanup|Lifetime|Rot|Perish" -and $t.FullName -notmatch "<|>") {
        Write-Host "  $($t.FullName)  base=$($t.BaseType)"
    }
}

Write-Host "`n==================== 5. InstanceDestructionLevel / InstanceActivationContext enums ===================="
foreach ($tn in @("SSSGame.InstanceDestructionLevel","SSSGame.InstanceActivationContext","SSSGame.WorldDataSlot")) {
    $t = $byName[$tn]
    if ($t) {
        Write-Host "### $tn"
        foreach ($f in $t.Fields) { if ($f.HasConstant) { Write-Host "    $($f.Name) = $($f.Constant)" } }
        Write-Host ""
    } else { Write-Host "### NO TYPE $tn`n" }
}

Write-Host "`n==================== 6. InventoryItemDataHandler ===================="
DumpType "SSSGame.InventoryItemDataHandler"
