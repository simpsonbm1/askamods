Add-Type -Path "D:\SteamLibrary\steamapps\common\ASKA\BepInEx\core\Mono.Cecil.dll"
$asm = [Mono.Cecil.AssemblyDefinition]::ReadAssembly("D:\SteamLibrary\steamapps\common\ASKA\BepInEx\interop\Assembly-CSharp.dll")
$all = @($asm.MainModule.Types) + @($asm.MainModule.Types | ForEach-Object { $_.NestedTypes })

function Dump-Methods($typeName, $pattern) {
    $t = $all | Where-Object { $_.FullName -eq $typeName }
    if (-not $t) { Write-Host "!! $typeName NOT FOUND"; return }
    Write-Host "=== $($t.FullName) : $($t.BaseType.FullName) ==="
    foreach ($m in $t.Methods | Where-Object { $_.Name -match $pattern }) {
        $ps = ($m.Parameters | ForEach-Object { "$($_.ParameterType.Name) $($_.Name)" }) -join ", "
        $mods = @()
        if ($m.IsVirtual) { $mods += "virtual" }
        if ($m.IsStatic) { $mods += "static" }
        Write-Host ("  [{0}] {1} {2}({3})" -f ($mods -join " "), $m.ReturnType.Name, $m.Name, $ps)
    }
}

$pat = "TaskData|TaskAgent|CanAdd|Serialize|Deserialize|VillagersInCharge"
Dump-Methods "SSSGame.Workstation" $pat
Dump-Methods "SSSGame.Buildstation" $pat

# every type in the Workstation hierarchy that overrides _CanAddVillagerToTaskData or AddToTaskDatas
Write-Host "`n=== Overrides of _CanAddVillagerToTaskData / AddToTaskDatas across all types ==="
foreach ($t in $all) {
    foreach ($m in $t.Methods | Where-Object { $_.Name -in @("_CanAddVillagerToTaskData", "AddToTaskDatas", "RemoveFromTaskDatas") }) {
        $ps = ($m.Parameters | ForEach-Object { $_.ParameterType.Name }) -join ", "
        Write-Host ("  {0}.{1}({2}) virtual={3} newslot={4}" -f $t.FullName, $m.Name, $ps, $m.IsVirtual, $m.IsNewSlot)
    }
}

# WorkstationTaskData: fields + props for a loggable identity
Write-Host "`n=== SSSGame.AI.WorkstationTaskData members ==="
$wtd = $all | Where-Object { $_.FullName -eq "SSSGame.AI.WorkstationTaskData" }
if ($wtd) {
    Write-Host "  base: $($wtd.BaseType.FullName)"
    foreach ($f in $wtd.Fields) { Write-Host "  F $($f.FieldType.Name) $($f.Name)" }
    foreach ($p in $wtd.Properties) { Write-Host "  P $($p.PropertyType.Name) $($p.Name)" }
    foreach ($m in $wtd.Methods | Where-Object { $_.Name -match "Name|ToString|Get" }) {
        $ps = ($m.Parameters | ForEach-Object { $_.ParameterType.Name }) -join ", "
        Write-Host "  M $($m.ReturnType.Name) $($m.Name)($ps)"
    }
} else { Write-Host "!! WorkstationTaskData NOT FOUND" }
