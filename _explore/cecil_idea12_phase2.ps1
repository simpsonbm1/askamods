# Idea-12 Phase 2 probe: ResourceStorage (warehouse) task/quota machinery + network path.
# Questions:
#  1. ResourceStorage type surface — task-data creation (FillResourceStorageTaskDatas,
#     CreateOrUpdateTasksFromStorageSupply, CanCreateStorageTaskForItemInfo), quota/priority members.
#  2. Which task-data subclass does ResourceStorage use? (all WorkstationTaskData descendants)
#  3. Is there a NetworkResourceStorage? Base type, Rpc/HostUpdateTasks surface.
#  4. StorageSupply surface (taskMaxQuota, defaultTaskPriority) + who reads taskMaxQuota.
#  5. Quota UI/RPC path: call sites of Rpc_ChangeTaskQuantity + WorkstationMenu/TaskDataPanel quantity methods.
#  6. Serialization keys (ldstr literals) in ResourceStorage/Workstation Serialize/Deserialize bodies —
#     does the task QUANTITY persist like priority does?
#  7. ItemInfoQuantity surface (is quantity writable?).
$ErrorActionPreference = "Stop"
[void][System.Reflection.Assembly]::LoadFrom("d:\Claude Projects\askamods\_explore\bin\Debug\net10.0\Mono.Cecil.dll")

$allTypes = New-Object System.Collections.Generic.List[object]
$modules = New-Object System.Collections.Generic.List[object]
foreach ($dll in @("Assembly-CSharp.dll", "SandSailorStudio.dll", "SandSailorStudio.Core.dll")) {
    $p = "D:\SteamLibrary\steamapps\common\ASKA\BepInEx\interop\$dll"
    if (-not (Test-Path $p)) { Write-Output "SKIP missing $dll"; continue }
    $asm = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($p)
    $modules.Add($asm.MainModule)
    foreach ($t in $asm.MainModule.Types) {
        $allTypes.Add($t)
        foreach ($n in $t.NestedTypes) { $allTypes.Add($n) }
    }
}

function Sig($m) {
    $ps = ($m.Parameters | ForEach-Object { "$($_.ParameterType.Name) $($_.Name)" }) -join ", "
    $flags = @()
    if ($m.IsVirtual) { $flags += "virtual" }
    if ($m.IsStatic) { $flags += "static" }
    $fs = if ($flags) { " [" + ($flags -join ",") + "]" } else { "" }
    "$($m.ReturnType.Name) $($m.Name)($ps)$fs"
}

function DumpType($tn, $methodFilter) {
    foreach ($t in ($allTypes | Where-Object { $_.Name -eq $tn })) {
        $base = if ($t.BaseType) { $t.BaseType.FullName } else { "none" }
        Write-Output "TYPE: $($t.FullName) base=$base"
        foreach ($f in $t.Fields) { if ($f.Name -notmatch "^Native") { Write-Output "  F: $($f.Name) : $($f.FieldType.FullName)" } }
        foreach ($p in $t.Properties) { Write-Output "  P: $($p.Name) : $($p.PropertyType.FullName)" }
        foreach ($m in $t.Methods) {
            if ($m.Name -match "^get_|^set_") { continue }
            if ($methodFilter -and ($m.Name -notmatch $methodFilter)) { continue }
            Write-Output "  M: $(Sig $m)"
        }
        Write-Output ""
    }
}

Write-Output "===== 1. ResourceStorage surface (task/quota/priority/supply members) ====="
DumpType "ResourceStorage" "Task|Quota|Priority|Supply|Gather|Storage|Item|Fill|Serialize|Deserialize"
Write-Output ""

Write-Output "===== 2. WorkstationTaskData descendants ====="
foreach ($t in $allTypes) {
    $b = $t.BaseType
    while ($b -ne $null) {
        if ($b.Name -eq "WorkstationTaskData") {
            Write-Output "  $($t.FullName)  base=$($t.BaseType.FullName)"
            break
        }
        $bt = $allTypes | Where-Object { $_.FullName -eq $b.FullName } | Select-Object -First 1
        if ($bt -eq $null) { break }
        $b = $bt.BaseType
    }
}
Write-Output ""

Write-Output "===== 3. NetworkResourceStorage / Network types serving storage ====="
foreach ($t in ($allTypes | Where-Object { $_.Name -match "^Network.*(Storage|Resource)" })) {
    $base = if ($t.BaseType) { $t.BaseType.FullName } else { "none" }
    Write-Output "TYPE: $($t.FullName) base=$base"
    foreach ($m in $t.Methods) {
        if ($m.Name -match "^get_|^set_") { continue }
        if ($m.Name -match "Rpc_|Task|HostUpdate|Spawned|Awake") { Write-Output "  M: $(Sig $m)" }
    }
    Write-Output ""
}
Write-Output ""

Write-Output "===== 4. StorageSupply surface + taskMaxQuota readers ====="
DumpType "StorageSupply" $null
Write-Output "--- readers of taskMaxQuota / defaultTaskPriority ---"
foreach ($m in $modules) {
    foreach ($t in $m.Types) {
        $stack = New-Object System.Collections.Generic.Stack[object]
        $stack.Push($t)
        while ($stack.Count -gt 0) {
            $cur = $stack.Pop()
            foreach ($n in $cur.NestedTypes) { $stack.Push($n) }
            foreach ($meth in $cur.Methods) {
                if (-not $meth.HasBody) { continue }
                foreach ($ins in $meth.Body.Instructions) {
                    $op = $ins.Operand
                    if ($op -eq $null) { continue }
                    if (($op -is [Mono.Cecil.FieldReference]) -and ($op.Name -match "taskMaxQuota|defaultTaskPriority")) {
                        Write-Output "  $($cur.FullName).$($meth.Name)  ->  $($op.DeclaringType.Name).$($op.Name)"
                    }
                    if (($op -is [Mono.Cecil.MethodReference]) -and ($op.Name -match "taskMaxQuota|defaultTaskPriority")) {
                        Write-Output "  $($cur.FullName).$($meth.Name)  ->  $($op.DeclaringType.Name).$($op.Name)()"
                    }
                }
            }
        }
    }
}
Write-Output ""

Write-Output "===== 5. Quota change path: Rpc_ChangeTaskQuantity call sites + UI quantity methods ====="
foreach ($m in $modules) {
    foreach ($t in $m.Types) {
        $stack = New-Object System.Collections.Generic.Stack[object]
        $stack.Push($t)
        while ($stack.Count -gt 0) {
            $cur = $stack.Pop()
            foreach ($n in $cur.NestedTypes) { $stack.Push($n) }
            foreach ($meth in $cur.Methods) {
                if (-not $meth.HasBody) { continue }
                foreach ($ins in $meth.Body.Instructions) {
                    if ($ins.OpCode.Name -notmatch "^call") { continue }
                    $op = $ins.Operand
                    if ($op -eq $null -or -not ($op -is [Mono.Cecil.MethodReference])) { continue }
                    if ($op.Name -match "^(Rpc_ChangeTaskQuantity|Rpc_ChangeAllTasksQuantities)$") {
                        Write-Output "  $($cur.FullName).$($meth.Name)  ->  $($op.DeclaringType.Name).$($op.Name)"
                    }
                }
            }
        }
    }
}
Write-Output "--- TaskDataPanel / WorkstationMenu quantity methods ---"
DumpType "TaskDataPanel" "Quantity|Quota"
DumpType "WorkstationMenu" "Quantity|Quota"
Write-Output ""

Write-Output "===== 6. Serialization keys (ldstr) in Workstation/ResourceStorage (De)Serialize bodies ====="
foreach ($tn in @("Workstation", "ResourceStorage")) {
    foreach ($t in ($allTypes | Where-Object { $_.Name -eq $tn })) {
        foreach ($meth in $t.Methods) {
            if ($meth.Name -notmatch "Serialize|Deserialize|Save|Load") { continue }
            if (-not $meth.HasBody) { continue }
            $strs = @()
            foreach ($ins in $meth.Body.Instructions) {
                if ($ins.OpCode.Name -eq "ldstr") { $strs += $ins.Operand }
            }
            if ($strs.Count -gt 0) {
                Write-Output "  $($t.FullName).$($meth.Name): $($strs -join ', ')"
            }
        }
    }
}
Write-Output "--- c_task* string constants anywhere in Workstation-family fields ---"
foreach ($t in ($allTypes | Where-Object { $_.Name -match "Workstation|ResourceStorage|TaskData" })) {
    foreach ($f in $t.Fields) {
        if ($f.HasConstant -and ($f.Constant -is [string]) -and ($f.Constant -match "task|Task")) {
            Write-Output "  $($t.FullName).$($f.Name) = '$($f.Constant)'"
        }
    }
}
Write-Output ""

Write-Output "===== 7. ItemInfoQuantity surface ====="
DumpType "ItemInfoQuantity" $null
