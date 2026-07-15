# Idea 12 Phase 2 reshape research spike (2026-07-14):
# Q1 drop-to-ground path  Q2 taskMaxQuota  Q3 per-unit capacity  Q4 FiltersTaskData  Q5 storage tier write path
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
        Write-Output "TYPE: $($t.FullName) base=$base enum=$($t.IsEnum)"
        if ($t.IsEnum) {
            foreach ($f in $t.Fields) { if ($f.HasConstant) { Write-Output "  E: $($f.Name) = $($f.Constant)" } }
        } else {
            foreach ($f in $t.Fields) { if ($f.Name -notmatch "^Native") { Write-Output "  F: $($f.Name) : $($f.FieldType.Name)" } }
            foreach ($p in $t.Properties) { Write-Output "  P: $($p.Name) : $($p.PropertyType.Name)" }
            foreach ($m in $t.Methods) { if ($m.Name -notmatch "^get_|^set_") { Write-Output "  M: $(Sig $m)" } }
        }
        Write-Output ""
    }
}

Write-Output "########## Q1a: methods named *Drop* (excl. Dropdown) ##########"
foreach ($t in $allTypes) {
    if ($t.Name -match "Dropdown") { continue }
    foreach ($m in $t.Methods) {
        if ($m.Name -match "Drop" -and $m.Name -notmatch "Dropdown|^get_|^set_") {
            Write-Output "  $($t.FullName) :: $(Sig $m)"
        }
    }
}

Write-Output ""
Write-Output "########## Q1b: methods named *Discard*/*Throw*/*Eject* ##########"
foreach ($t in $allTypes) {
    foreach ($m in $t.Methods) {
        if ($m.Name -match "Discard|Throw|Eject" -and $m.Name -notmatch "^get_|^set_|ThrowIfNull") {
            Write-Output "  $($t.FullName) :: $(Sig $m)"
        }
    }
}

Write-Output ""
Write-Output "########## Q1c: WorldItemObject / DynamicItemObject creation surface ##########"
foreach ($tn in @("WorldItemObject","DynamicItemObject")) {
    foreach ($t in ($allTypes | Where-Object { $_.Name -eq $tn })) {
        $base = if ($t.BaseType) { $t.BaseType.FullName } else { "none" }
        Write-Output "TYPE: $($t.FullName) base=$base"
        foreach ($m in $t.Methods) {
            if ($m.Name -match "Create|Spawn|Init|Setup|Drop|Instantiate" -or $m.IsStatic) { Write-Output "  M: $(Sig $m)" }
        }
        Write-Output ""
    }
}

Write-Output "########## Q1d: who can spawn items into the world (ItemsManager-ish) ##########"
foreach ($t in $allTypes) {
    if ($t.Name -match "^Items?Manager$|WorldItemManager|ItemSpawner") { DumpType $t.Name }
}

Write-Output ""
Write-Output "########## Q2: ResourceStorageTaskData + WorkstationTaskData + tier enum ##########"
DumpType "ResourceStorageTaskData"
DumpType "WorkstationTaskData"
DumpType "WorkstationTaskPriority"

Write-Output "########## Q3: StorageSupply full + capacity surfaces ##########"
DumpType "StorageSupply"
foreach ($tn in @("ResourceStorage","ItemCollection")) {
    foreach ($t in ($allTypes | Where-Object { $_.Name -eq $tn })) {
        Write-Output "CAPACITY MEMBERS of $($t.FullName):"
        foreach ($f in $t.Fields) { if ($f.Name -match "apacit|Room|Max|Slot|Size|Space|Weight") { Write-Output "  F: $($f.Name) : $($f.FieldType.Name)" } }
        foreach ($p in $t.Properties) { if ($p.Name -match "apacit|Room|Max|Slot|Size|Space|Weight") { Write-Output "  P: $($p.Name) : $($p.PropertyType.Name)" } }
        foreach ($m in $t.Methods) { if ($m.Name -match "apacit|Room|Max|Slot|Size|Space|Weight|Full|CanAdd|Fit|HasSpace" -and $m.Name -notmatch "^get_|^set_") { Write-Output "  M: $(Sig $m)" } }
        Write-Output ""
    }
}

Write-Output "########## Q4: FiltersTaskData + item filter types ##########"
DumpType "FiltersTaskData"
DumpType "ItemFilter"
DumpType "IItemFilter"

Write-Output "########## Q5a: storage network components - RPC/task surface ##########"
foreach ($t in $allTypes) {
    if ($t.Name -match "^NetworkCompositeResourceStorage$|^NetworkSimpleResourceStorage|^NetworkResourceStorage") {
        $base = if ($t.BaseType) { $t.BaseType.FullName } else { "none" }
        Write-Output "TYPE: $($t.FullName) base=$base"
        foreach ($m in $t.Methods) {
            if ($m.Name -match "^Rpc_|HostUpdate|Task|Priority|Quantity") { Write-Output "  M: $(Sig $m)" }
        }
        Write-Output ""
    }
}

Write-Output "########## Q5b: NetworkWorkstation generic base - RPC surface ##########"
foreach ($t in $allTypes) {
    if ($t.Name -match "^NetworkWorkstation") {
        $base = if ($t.BaseType) { $t.BaseType.FullName } else { "none" }
        Write-Output "TYPE: $($t.FullName) base=$base"
        foreach ($m in $t.Methods) {
            if ($m.Name -match "^Rpc_|HostUpdate") { Write-Output "  M: $(Sig $m)" }
        }
        Write-Output ""
    }
}

Write-Output "########## Q5c: UI tier-write path (panels/menus with Priority members) ##########"
foreach ($t in $allTypes) {
    if ($t.Name -match "TaskDataPanel|WorkstationMenu|StorageMenu|ResourceStorageMenu|StoragePanel") {
        foreach ($m in $t.Methods) {
            if ($m.Name -match "Priority" -and $m.Name -notmatch "^get_|^set_") { Write-Output "  $($t.FullName) :: $(Sig $m)" }
        }
    }
}
Write-Output ""
Write-Output "########## Q5d: ResourceStorage priority members ##########"
foreach ($t in ($allTypes | Where-Object { $_.Name -eq "ResourceStorage" })) {
    foreach ($f in $t.Fields) { if ($f.Name -match "riorit") { Write-Output "  F: $($f.Name) : $($f.FieldType.Name)" } }
    foreach ($m in $t.Methods) { if ($m.Name -match "riorit") { Write-Output "  M: $(Sig $m)" } }
}
