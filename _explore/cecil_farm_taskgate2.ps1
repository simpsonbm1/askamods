Add-Type -Path "D:\SteamLibrary\steamapps\common\ASKA\BepInEx\core\Mono.Cecil.dll"
$asm = [Mono.Cecil.AssemblyDefinition]::ReadAssembly("D:\SteamLibrary\steamapps\common\ASKA\BepInEx\interop\Assembly-CSharp.dll")
$all = @($asm.MainModule.Types) + @($asm.MainModule.Types | ForEach-Object { $_.NestedTypes })

Write-Host "=== ALL direct + indirect subclasses of SSSGame.Workstation ==="
$ws = @{}
$ws["SSSGame.Workstation"] = $true
$changed = $true
while ($changed) {
    $changed = $false
    foreach ($t in $all) {
        if ($t.BaseType -and $ws.ContainsKey($t.BaseType.FullName) -and -not $ws.ContainsKey($t.FullName)) {
            $ws[$t.FullName] = $true; $changed = $true
        }
    }
}
foreach ($n in ($ws.Keys | Sort-Object)) {
    $t = $all | Where-Object { $_.FullName -eq $n }
    $ov = ($t.Methods | Where-Object { $_.Name -eq "_CanAddVillagerToTaskData" }).Count
    $ifaces = ($t.Interfaces | ForEach-Object { $_.InterfaceType.Name }) -join ","
    Write-Host ("  {0}   base={1}  ownCanAdd={2}  ifaces={3}" -f $n, $t.BaseType.Name, $ov, $ifaces)
}

Write-Host "`n=== Implementers of IWhitelistingSite ==="
foreach ($t in $all) {
    if ($t.Interfaces | Where-Object { $_.InterfaceType.Name -match "Whitelist" }) {
        Write-Host ("  {0} : {1}" -f $t.FullName, $t.BaseType.FullName)
    }
}

Write-Host "`n=== IWhitelistingSite members ==="
$iw = $all | Where-Object { $_.Name -match "^IWhitelist" }
foreach ($t in $iw) {
    Write-Host "  --- $($t.FullName) ---"
    foreach ($m in $t.Methods) {
        $ps = ($m.Parameters | ForEach-Object { "$($_.ParameterType.Name) $($_.Name)" }) -join ", "
        Write-Host ("    {0} {1}({2})" -f $m.ReturnType.Name, $m.Name, $ps)
    }
}

Write-Host "`n=== FarmingStationTaskData members ==="
$t = $all | Where-Object { $_.FullName -eq "SSSGame.AI.FarmingStationTaskData" }
foreach ($p in $t.Properties) { Write-Host "  P $($p.PropertyType.Name) $($p.Name)" }
foreach ($m in $t.Methods | Where-Object { $_.Name -notmatch "^get_|^set_" }) {
    $ps = ($m.Parameters | ForEach-Object { "$($_.ParameterType.Name) $($_.Name)" }) -join ", "
    Write-Host ("  M {0} {1}({2})" -f $m.ReturnType.Name, $m.Name, $ps)
}

Write-Host "`n=== WorkstationTaskData members (for VillagersInCharge shape) ==="
$t = $all | Where-Object { $_.FullName -eq "SSSGame.AI.WorkstationTaskData" }
foreach ($p in $t.Properties) { Write-Host "  P $($p.PropertyType.FullName) $($p.Name)" }
foreach ($m in $t.Methods | Where-Object { $_.Name -notmatch "^get_|^set_" }) {
    $ps = ($m.Parameters | ForEach-Object { "$($_.ParameterType.Name) $($_.Name)" }) -join ", "
    Write-Host ("  M {0} {1}({2})" -f $m.ReturnType.Name, $m.Name, $ps)
}

Write-Host "`n=== UI panels that might show per-villager task toggles ==="
foreach ($n in @("SSSGame.UI.FarmCropTaskPanel","SSSGame.UI.FarmCropCellTabPage","SSSGame.UI.FarmCropPainterPanel")) {
    $t = $all | Where-Object { $_.FullName -eq $n }
    if (-not $t) { Write-Host "  !! $n missing"; continue }
    Write-Host "  --- $n : $($t.BaseType.FullName) ---"
    foreach ($f in $t.Fields | Where-Object { $_.Name -notmatch "^Native" }) { Write-Host "    F $($f.FieldType.Name) $($f.Name)" }
    foreach ($m in $t.Methods | Where-Object { $_.Name -notmatch "^get_|^set_|^\.c" }) {
        $ps = ($m.Parameters | ForEach-Object { $_.ParameterType.Name }) -join ", "
        Write-Host ("    M {0} {1}({2})" -f $m.ReturnType.Name, $m.Name, $ps)
    }
}

Write-Host "`n=== Types with 'Whitelist' in the name (UI included) ==="
foreach ($t in $all | Where-Object { $_.Name -match "Whitelist" }) {
    Write-Host ("  {0} : {1}" -f $t.FullName, $t.BaseType.FullName)
}
