# Idea 12 Phase 0 static research: add-task path, tier RPC, task-data surface, complaint capture point
$asmPath = "D:\SteamLibrary\steamapps\common\ASKA\BepInEx\interop\Assembly-CSharp.dll"
[void][System.Reflection.Assembly]::LoadFrom("d:\Claude Projects\askamods\_explore\bin\Debug\net10.0\Mono.Cecil.dll")
$asm = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($asmPath)
$mod = $asm.MainModule

$allTypes = New-Object System.Collections.Generic.List[object]
foreach ($t in $mod.Types) {
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

Write-Output "===== 1. Enum WorkstationTaskPriority values ====="
$e = $allTypes | Where-Object { $_.Name -eq "WorkstationTaskPriority" }
foreach ($t in $e) {
    Write-Output "TYPE: $($t.FullName) (isEnum=$($t.IsEnum))"
    foreach ($f in $t.Fields) { if ($f.HasConstant) { Write-Output "  $($f.Name) = $($f.Constant)" } }
}

Write-Output ""
Write-Output "===== 2. Methods anywhere named *Task* with Add/Create/Remove/Set (task lifecycle) ====="
foreach ($t in $allTypes) {
    foreach ($m in $t.Methods) {
        if ($m.Name -match "^(Add|Create|Remove|Delete|Set).*Task|Task.*(Add|Create|Remove|Delete)|^Rpc_.*Task") {
            Write-Output "$($t.FullName) :: $(Sig $m)"
        }
    }
}

Write-Output ""
Write-Output "===== 3. Full method surface: WorkstationTaskData ====="
$t = $allTypes | Where-Object { $_.Name -eq "WorkstationTaskData" } | Select-Object -First 1
if ($t) {
    Write-Output "TYPE: $($t.FullName) base=$($t.BaseType.FullName)"
    foreach ($f in $t.Fields) { if ($f.Name -notmatch "^Native") { Write-Output "  F: $($f.Name) : $($f.FieldType.Name)" } }
    foreach ($p in $t.Properties) { Write-Output "  P: $($p.Name) : $($p.PropertyType.Name)" }
    foreach ($m in $t.Methods) { if ($m.Name -notmatch "^get_|^set_") { Write-Output "  M: $(Sig $m)" } }
}

Write-Output ""
Write-Output "===== 4. NetworkWorkstation generic: all Rpc_* and task methods ====="
foreach ($t in $allTypes) {
    if ($t.Name -match "^NetworkWorkstation") {
        Write-Output "TYPE: $($t.FullName) base=$($t.BaseType.FullName)"
        foreach ($m in $t.Methods) {
            if ($m.Name -match "Rpc_|Task") { Write-Output "  $(Sig $m)" }
        }
    }
}

Write-Output ""
Write-Output "===== 5. WorkstationMenu + TaskDataPanel task-related methods ====="
foreach ($tn in @("WorkstationMenu", "TaskDataPanel")) {
    foreach ($t in ($allTypes | Where-Object { $_.Name -eq $tn })) {
        Write-Output "TYPE: $($t.FullName)"
        foreach ($m in $t.Methods) {
            if ($m.Name -match "Task|Priority|Quota|Quantity|Add|Create|Pin") { Write-Output "  $(Sig $m)" }
        }
    }
}

Write-Output ""
Write-Output "===== 6. Types referencing WorkstationTaskPriority in fields/props/method sigs ====="
foreach ($t in $allTypes) {
    $hits = @()
    foreach ($f in $t.Fields) { if ($f.FieldType.Name -eq "WorkstationTaskPriority") { $hits += "F:$($f.Name)" } }
    foreach ($p in $t.Properties) { if ($p.PropertyType.Name -eq "WorkstationTaskPriority") { $hits += "P:$($p.Name)" } }
    foreach ($m in $t.Methods) {
        if ($m.ReturnType.Name -eq "WorkstationTaskPriority") { $hits += "M:$($m.Name)->ret" }
        foreach ($pa in $m.Parameters) { if ($pa.ParameterType.Name -eq "WorkstationTaskPriority") { $hits += "M:$($m.Name)($($pa.Name))" } }
    }
    if ($hits.Count -gt 0) { Write-Output "$($t.FullName): $($hits -join '; ')" }
}

Write-Output ""
Write-Output "===== 7. VillagerSocial complaint methods (virtual flags = inline risk signal) ====="
$t = $allTypes | Where-Object { $_.Name -eq "VillagerSocial" } | Select-Object -First 1
if ($t) {
    foreach ($m in $t.Methods) {
        if ($m.Name -match "Complain") { Write-Output "  $(Sig $m)" }
    }
}

Write-Output ""
Write-Output "===== 8. CraftBlueprint vs CraftBlueprintInfo member surface ====="
foreach ($tn in @("CraftBlueprint", "CraftBlueprintInfo")) {
    foreach ($t in ($allTypes | Where-Object { $_.Name -eq $tn })) {
        Write-Output "TYPE: $($t.FullName) base=$($t.BaseType.FullName)"
        foreach ($f in $t.Fields) { if ($f.Name -notmatch "^Native") { Write-Output "  F: $($f.Name) : $($f.FieldType.Name)" } }
        foreach ($p in $t.Properties) { Write-Output "  P: $($p.Name) : $($p.PropertyType.Name)" }
        foreach ($m in $t.Methods) { if ($m.Name -notmatch "^get_|^set_") { Write-Output "  M: $(Sig $m)" } }
    }
}

Write-Output ""
Write-Output "===== 9. Workstation base: taskDatas surface (AddToTaskDatas etc.) ====="
$t = $allTypes | Where-Object { $_.FullName -eq "SSSGame.Workstation" } | Select-Object -First 1
if ($t) {
    foreach ($f in $t.Fields) { if ($f.Name -match "task" -and $f.Name -notmatch "^Native") { Write-Output "  F: $($f.Name) : $($f.FieldType.Name)" } }
    foreach ($p in $t.Properties) { if ($p.Name -match "Task") { Write-Output "  P: $($p.Name) : $($p.PropertyType.Name)" } }
    foreach ($m in $t.Methods) { if ($m.Name -match "Task") { Write-Output "  M: $(Sig $m)" } }
}
