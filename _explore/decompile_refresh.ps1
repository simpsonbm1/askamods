$asmPath = "D:\SteamLibrary\steamapps\common\ASKA\BepInEx\interop\Assembly-CSharp.dll"
Add-Type -Path "D:\SteamLibrary\steamapps\common\ASKA\BepInEx\core\Mono.Cecil.dll"
$asm = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($asmPath)
$t = $asm.MainModule.GetType("SSSGame.WorldObjectiveMarker")
$m = $t.Methods | Where-Object { $_.Name -eq "Refresh" }
Write-Host "IL for WorldObjectiveMarker.Refresh:"
foreach ($inst in $m.Body.Instructions) {
    Write-Host "  $($inst.OpCode.Name) $($inst.Operand)"
}

$t2 = $asm.MainModule.GetType("SSSGame.CompassObjectiveMarker")
$m2 = $t2.Methods | Where-Object { $_.Name -eq "SetRange" -or $_.Name -eq "SetRadius" -or $_.Name -eq "UpdateRange" }
if ($m2) {
    Write-Host "Found method on CompassObjectiveMarker:"
    foreach ($m in $m2) { Write-Host $m.Name }
}
