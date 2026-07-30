Add-Type -Path "D:\SteamLibrary\steamapps\common\ASKA\BepInEx\core\Mono.Cecil.dll"
$asm = [Mono.Cecil.AssemblyDefinition]::ReadAssembly("D:\SteamLibrary\steamapps\common\ASKA\BepInEx\interop\Assembly-CSharp.dll")
$all = @($asm.MainModule.Types) + @($asm.MainModule.Types | ForEach-Object { $_.NestedTypes })

function Dump($name) {
    $t = $all | Where-Object { $_.FullName -eq $name }
    if (-not $t) { Write-Host "!! $name NOT FOUND`n"; return }
    Write-Host "=== $name : $($t.BaseType.FullName) ==="
    foreach ($p in $t.Properties) { Write-Host "  P $($p.PropertyType.Name) $($p.Name)" }
    foreach ($m in $t.Methods | Where-Object { $_.Name -notmatch "^get_|^set_|^\.c|^Method_" }) {
        $ps = ($m.Parameters | ForEach-Object { "$($_.ParameterType.Name) $($_.Name)" }) -join ", "
        Write-Host ("  M {0} {1}({2})" -f $m.ReturnType.Name, $m.Name, $ps)
    }
    Write-Host ""
}

Dump "SSSGame.UI.TaskDataPanel"
Dump "SSSGame.UI.FarmCropTaskPanel"
Dump "SSSGame.UI.FarmCropCellTabPage"
Dump "SSSGame.UI.VillagerPanel"

Write-Host "=== Types named *TaskDataPanel* or *VillagerPanel* ==="
foreach ($t in $all | Where-Object { $_.Name -match "TaskDataPanel|VillagerPanel|VillagerInChargePanel|WorkerPanel" }) {
    Write-Host ("  {0} : {1}" -f $t.FullName, $t.BaseType.Name)
}

Write-Host "`n=== Every method taking (Villager, WorkstationTaskData) or similar pairing ==="
foreach ($t in $all) {
    foreach ($m in $t.Methods) {
        $pn = ($m.Parameters | ForEach-Object { $_.ParameterType.Name })
        if (($pn -contains "Villager") -and ($pn -join ",") -match "TaskData") {
            Write-Host ("  {0}.{1}({2})" -f $t.FullName, $m.Name, ($pn -join ", "))
        }
    }
}
