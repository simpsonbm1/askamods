$asmPath = "D:\SteamLibrary\steamapps\common\ASKA\BepInEx\interop\Assembly-CSharp.dll"
Add-Type -Path "D:\SteamLibrary\steamapps\common\ASKA\BepInEx\core\Mono.Cecil.dll"
$asm = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($asmPath)

$frags = @("NetworkDynamicDimensionBuildingState", "NetworkDynamicDimensionBuildingState_256", "NetworkDynamicDimensionBuildingState_512", "DynamicDimensionBuilding")
foreach ($t in $asm.MainModule.GetTypes()) {
    $match = $false
    foreach ($f in $frags) { if ($t.Name -eq $f) { $match = $true; break } }
    if (-not $match) { continue }
    Write-Host "==================== $($t.FullName) ===================="
    foreach ($m in $t.Methods) {
        if ($m.Name -like "get_*" -or $m.Name -like "set_*" -or $m.Name -like "add_*" -or $m.Name -like "remove_*" -or $m.Name -eq ".cctor") { continue }
        $ps = ($m.Parameters | ForEach-Object { "$($_.ParameterType.FullName) $($_.Name)" }) -join ", "
        $mods = @()
        if ($m.IsVirtual) { $mods += "virtual" }
        if ($m.IsAbstract) { $mods += "abstract" }
        if ($m.IsStatic) { $mods += "static" }
        Write-Host "    [$($mods -join ',')] $($m.ReturnType.FullName) $($m.Name)($ps)"
    }
    Write-Host "  -- Fields (raw, incl. non-public) --"
    foreach ($f in $t.Fields) {
        if ($f.Name -like "NativeFieldInfoPtr*" -or $f.Name -like "NativeMethodInfoPtr*") { continue }
        Write-Host "    $($f.Attributes) $($f.FieldType.FullName) $($f.Name)"
    }
    Write-Host ""
}
