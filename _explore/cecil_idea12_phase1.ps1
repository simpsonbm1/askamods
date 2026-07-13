# Idea-12 Phase 1 probe: NetworkWorkstation reachability + priority RPC plumbing.
# Questions:
#  1. NetworkWorkstation<T> hierarchy + fields — is there a link field to the Workstation (or vice versa)?
#  2. Concrete subclasses (NetworkCraftingStation etc.) — base types, nested NetworkTask structs.
#  3. Workstation fields/properties/methods that mention Network (how the game maps station -> network comp).
#  4. TaskDataPanel + WorkstationMenu — how the UI resolves the RPC target for priority changes.
#  5. All call sites of Rpc_ChangeTaskPriority / Rpc_ChangeTaskQuantity across Assembly-CSharp.
#  6. WorkstationTaskData.SetPriority + onDataChanged surface.
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

Write-Output "===== 1/2. NetworkWorkstation family ====="
foreach ($t in ($allTypes | Where-Object { $_.Name -match "^NetworkWorkstation" })) {
    $base = if ($t.BaseType) { $t.BaseType.FullName } else { "none" }
    Write-Output "TYPE: $($t.FullName) base=$base"
    foreach ($f in $t.Fields) { if ($f.Name -notmatch "^Native") { Write-Output "  F: $($f.Name) : $($f.FieldType.FullName)" } }
    foreach ($p in $t.Properties) { Write-Output "  P: $($p.Name) : $($p.PropertyType.FullName)" }
    foreach ($m in $t.Methods) {
        if ($m.Name -match "^get_|^set_") { continue }
        if ($m.Name -match "Rpc_|Task|Workstation|Awake|Spawned|Init") { Write-Output "  M: $(Sig $m)" }
    }
    Write-Output ""
}
Write-Output "--- concrete Network*Station subclasses (name + base only) ---"
foreach ($t in ($allTypes | Where-Object { $_.Name -match "^Network.*(Station|Storage|Pen|Buildstation|Marketplace)" -and $_.Name -notmatch "^NetworkWorkstation" })) {
    $base = if ($t.BaseType) { $t.BaseType.FullName } else { "none" }
    Write-Output "  $($t.FullName)  base=$base"
}
Write-Output ""

Write-Output "===== 3. Workstation members mentioning Network ====="
foreach ($t in ($allTypes | Where-Object { $_.Name -eq "Workstation" -and $_.Namespace -eq "SSSGame" })) {
    foreach ($f in $t.Fields) { if ($f.FieldType.FullName -match "Network" -or $f.Name -match "[Nn]etwork") { Write-Output "  F: $($f.Name) : $($f.FieldType.FullName)" } }
    foreach ($p in $t.Properties) { if ($p.PropertyType.FullName -match "Network" -or $p.Name -match "[Nn]etwork") { Write-Output "  P: $($p.Name) : $($p.PropertyType.FullName)" } }
    foreach ($m in $t.Methods) { if ($m.Name -match "[Nn]etwork|Rpc|Priority|HostUpdate") { Write-Output "  M: $(Sig $m)" } }
}
Write-Output ""

Write-Output "===== CraftingStation/CookingStation members mentioning Network/Rpc/Priority ====="
foreach ($tn in @("CraftingStation", "CookingStation")) {
    foreach ($t in ($allTypes | Where-Object { $_.Name -eq $tn })) {
        Write-Output "TYPE: $($t.FullName)"
        foreach ($f in $t.Fields) { if ($f.FieldType.FullName -match "Network" -or $f.Name -match "[Nn]etwork") { Write-Output "  F: $($f.Name) : $($f.FieldType.FullName)" } }
        foreach ($p in $t.Properties) { if ($p.PropertyType.FullName -match "Network" -or $p.Name -match "[Nn]etwork") { Write-Output "  P: $($p.Name) : $($p.PropertyType.FullName)" } }
        foreach ($m in $t.Methods) { if ($m.Name -match "[Nn]etwork|Rpc|Priority") { Write-Output "  M: $(Sig $m)" } }
        Write-Output ""
    }
}

Write-Output "===== 4. TaskDataPanel / WorkstationMenu ====="
DumpType "TaskDataPanel" $null
DumpType "WorkstationMenu" "Priority|Task|Workstation|Show|Init"
Write-Output ""

Write-Output "===== 5. Call sites of Rpc_ChangeTaskPriority / Rpc_ChangeTaskQuantity / SetPriority ====="
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
                    if ($op.Name -match "^(Rpc_ChangeTaskPriority|Rpc_ChangeTaskQuantity|SetPriority)$") {
                        Write-Output "  $($cur.FullName).$($meth.Name)  ->  $($op.DeclaringType.Name).$($op.Name)"
                    }
                }
            }
        }
    }
}
Write-Output ""

Write-Output "===== 6. WorkstationTaskData priority surface ====="
DumpType "WorkstationTaskData" "Priority|Quantity|Data|Pin"
