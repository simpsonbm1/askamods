Add-Type -Path "D:\SteamLibrary\steamapps\common\ASKA\BepInEx\core\Mono.Cecil.dll"
$asm = [Mono.Cecil.AssemblyDefinition]::ReadAssembly("D:\SteamLibrary\steamapps\common\ASKA\BepInEx\interop\Assembly-CSharp.dll")
$all = @($asm.MainModule.Types) + @($asm.MainModule.Types | ForEach-Object { $_.NestedTypes })

Write-Host "=== UI types whose name mentions Workstation / Task / Villager ==="
foreach ($t in $all | Where-Object { $_.Namespace -match "UI" -and $_.Name -match "Workstation|TaskRow|TaskPanel|TaskWidget|VillagerAssign|VillagerRow|WorkerRow|Assign" }) {
    Write-Host ("  {0} : {1}" -f $t.FullName, $t.BaseType.Name)
}

Write-Host "`n=== SSSGame.UI.WorkstationMenu members ==="
$t = $all | Where-Object { $_.FullName -eq "SSSGame.UI.WorkstationMenu" }
if ($t) {
    Write-Host "  base=$($t.BaseType.FullName)"
    foreach ($f in $t.Fields | Where-Object { $_.Name -notmatch "^Native" }) { Write-Host "    F $($f.FieldType.Name) $($f.Name)" }
    foreach ($m in $t.Methods | Where-Object { $_.Name -notmatch "^get_|^set_|^\.c" }) {
        $ps = ($m.Parameters | ForEach-Object { $_.ParameterType.Name }) -join ", "
        Write-Host ("    M {0} {1}({2})" -f $m.ReturnType.Name, $m.Name, $ps)
    }
} else { Write-Host "  !! not found" }

Write-Host "`n=== Any UI type mentioning VillagersInCharge in a member name ==="
foreach ($t in $all) {
    foreach ($m in $t.Methods) {
        if ($m.Name -match "VillagerInCharge|VillagersInCharge|ToggleVillager|SetVillagerOnTask") {
            $ps = ($m.Parameters | ForEach-Object { $_.ParameterType.Name }) -join ", "
            Write-Host ("  {0}.{1}({2})" -f $t.FullName, $m.Name, $ps)
        }
    }
}

Write-Host "`n=== Menus / tab pages referencing Farm or Forestry ==="
foreach ($t in $all | Where-Object { $_.Namespace -match "UI" }) {
    if ($t.Name -match "Farm|Forest|Crop|Seed") { Write-Host ("  {0} : {1}" -f $t.FullName, $t.BaseType.Name) }
}

Write-Host "`n=== ForestryStation members (does it add its own task UI?) ==="
$t = $all | Where-Object { $_.FullName -eq "SSSGame.ForestryStation" }
if ($t) {
    foreach ($p in $t.Properties) { Write-Host "  P $($p.PropertyType.Name) $($p.Name)" }
    foreach ($m in $t.Methods | Where-Object { $_.Name -notmatch "^get_|^set_|^\.c|^Method_" }) {
        $ps = ($m.Parameters | ForEach-Object { $_.ParameterType.Name }) -join ", "
        Write-Host ("  M {0} {1}({2})" -f $m.ReturnType.Name, $m.Name, $ps)
    }
}
