$asmPath = "D:\SteamLibrary\steamapps\common\ASKA\BepInEx\interop\Assembly-CSharp.dll"
Add-Type -Path "D:\SteamLibrary\steamapps\common\ASKA\BepInEx\core\Mono.Cecil.dll"
$asm = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($asmPath)

# Find by name fragment across all namespaces
$frags = @("NetworkDynamicDimensionBuildingState_256", "NetworkDynamicDimensionBuildingState_512")
foreach ($t in $asm.MainModule.GetTypes()) {
    $match = $false
    foreach ($f in $frags) { if ($t.Name -eq $f) { $match = $true; break } }
    if (-not $match) { continue }
    Write-Host "==================== $($t.FullName) (base: $($t.BaseType.Name)) ===================="
    Write-Host "  -- Properties --"
    foreach ($p in $t.Properties) { Write-Host "    $($p.Name) : $($p.PropertyType.Name)" }
    Write-Host "  -- Methods (non-getter/setter) --"
    foreach ($m in $t.Methods) {
        if ($m.Name -like "get_*" -or $m.Name -like "set_*" -or $m.Name -like "add_*" -or $m.Name -like "remove_*") { continue }
        $ps = ($m.Parameters | ForEach-Object { "$($_.ParameterType.Name) $($_.Name)" }) -join ", "
        Write-Host "    $($m.ReturnType.Name) $($m.Name)($ps)"
    }
    Write-Host ""
}
