# Idea 12 Phase 0 static research, part B: FiltersTaskData, network task structs, station->NetworkWorkstation map
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

Write-Output "===== A. FiltersTaskData + CraftingStationTaskData full surface ====="
foreach ($tn in @("FiltersTaskData", "CraftingStationTaskData", "BuildStationTaskData")) {
    foreach ($t in ($allTypes | Where-Object { $_.Name -eq $tn })) {
        Write-Output "TYPE: $($t.FullName) base=$($t.BaseType.FullName)"
        foreach ($f in $t.Fields) { if ($f.Name -notmatch "^Native") { Write-Output "  F: $($f.Name) : $($f.FieldType.FullName)" } }
        foreach ($p in $t.Properties) { Write-Output "  P: $($p.Name) : $($p.PropertyType.FullName)" }
        foreach ($m in $t.Methods) { if ($m.Name -notmatch "^get_|^set_") { Write-Output "  M: $(Sig $m)" } }
        Write-Output ""
    }
}

Write-Output "===== B. Types deriving from NetworkWorkstation (concrete instantiations + TaskType arg) ====="
foreach ($t in $allTypes) {
    $b = $t.BaseType
    if ($b -and $b.Name -match "^NetworkWorkstation") {
        $ga = ""
        if ($b -is [Mono.Cecil.GenericInstanceType]) { $ga = ($b.GenericArguments | ForEach-Object { $_.FullName }) -join ", " }
        Write-Output "$($t.FullName)  :  $($b.Name)<$ga>"
    }
}

Write-Output ""
Write-Output "===== C. The network task structs themselves (fields = what Rpc_AddTask needs) ====="
$structNames = New-Object System.Collections.Generic.HashSet[string]
foreach ($t in $allTypes) {
    $b = $t.BaseType
    if ($b -and $b.Name -match "^NetworkWorkstation" -and $b -is [Mono.Cecil.GenericInstanceType]) {
        foreach ($g in $b.GenericArguments) { [void]$structNames.Add($g.FullName) }
    }
}
foreach ($sn in $structNames) {
    $t = $allTypes | Where-Object { $_.FullName -eq $sn } | Select-Object -First 1
    if ($t) {
        Write-Output "STRUCT: $($t.FullName) (isValueType=$($t.IsValueType)) base=$($t.BaseType.FullName)"
        foreach ($f in $t.Fields) { Write-Output "  F: $($f.Name) : $($f.FieldType.FullName)" }
        foreach ($p in $t.Properties) { Write-Output "  P: $($p.Name) : $($p.PropertyType.FullName)" }
        Write-Output ""
    }
}

Write-Output "===== D. IWorkstation interface surface (what WorkstationMenu drives) ====="
$t = $allTypes | Where-Object { $_.Name -eq "IWorkstation" } | Select-Object -First 1
if ($t) {
    Write-Output "TYPE: $($t.FullName)"
    foreach ($m in $t.Methods) { Write-Output "  M: $(Sig $m)" }
}

Write-Output ""
Write-Output "===== E. SettlementIssueTrackerWidget surface (no-production maps) ====="
$t = $allTypes | Where-Object { $_.Name -eq "SettlementIssueTrackerWidget" } | Select-Object -First 1
if ($t) {
    Write-Output "TYPE: $($t.FullName) base=$($t.BaseType.FullName)"
    foreach ($f in $t.Fields) { if ($f.Name -notmatch "^Native") { Write-Output "  F: $($f.Name) : $($f.FieldType.FullName)" } }
    foreach ($p in $t.Properties) { Write-Output "  P: $($p.Name) : $($p.PropertyType.FullName)" }
    foreach ($m in $t.Methods) { if ($m.Name -notmatch "^get_|^set_") { Write-Output "  M: $(Sig $m)" } }
}
