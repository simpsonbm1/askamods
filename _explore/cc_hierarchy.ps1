$asmPath = "D:\SteamLibrary\steamapps\common\ASKA\BepInEx\interop\Assembly-CSharp.dll"
Add-Type -Path "D:\SteamLibrary\steamapps\common\ASKA\BepInEx\core\Mono.Cecil.dll"
$asm = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($asmPath)
$mod = $asm.MainModule

Write-Host "### Subclasses of WorldObjectiveMarker ###"
foreach ($t in $mod.Types) {
    if ($t.BaseType -and $t.BaseType.Name -eq "WorldObjectiveMarker") {
        Write-Host "  $($t.FullName)"
    }
}

Write-Host ""
Write-Host "### Types whose name contains ObjectiveMarker / HarvestMarker / MapMarker ###"
foreach ($t in $mod.Types) {
    if ($t.Name -match "ObjectiveMarker|HarvestMarker|MapMarker|RangeRing|MarkerWidget|MarkerObject") {
        Write-Host "  $($t.FullName)  : base=$($t.BaseType.Name)"
    }
}
