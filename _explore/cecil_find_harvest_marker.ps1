$asmPath = "D:\SteamLibrary\steamapps\common\ASKA\BepInEx\interop\Assembly-CSharp.dll"
Add-Type -Path "D:\SteamLibrary\steamapps\common\ASKA\BepInEx\core\Mono.Cecil.dll"
$asm = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($asmPath)

Write-Host "Looking for methods taking HarvestMarker as parameter or returning it..."
foreach ($t in $asm.MainModule.Types) {
    foreach ($m in $t.Methods) {
        $hasMatch = $false
        if ($m.ReturnType.Name -eq "HarvestMarker") { $hasMatch = $true }
        foreach ($p in $m.Parameters) {
            if ($p.ParameterType.Name -eq "HarvestMarker") { $hasMatch = $true }
        }
        if ($hasMatch -and $t.Name -ne "HarvestMarker" -and $t.Name -ne "HarvestMarkerAreaType" -and $t.Name -ne "SerializedHarvestMarker") {
            Write-Host "Found in: $($t.Name).$($m.Name)"
        }
    }
}
Write-Host "Done."
