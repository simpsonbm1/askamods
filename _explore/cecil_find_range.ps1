$asmPath = "D:\SteamLibrary\steamapps\common\ASKA\BepInEx\interop\Assembly-CSharp.dll"
Add-Type -Path "D:\SteamLibrary\steamapps\common\ASKA\BepInEx\core\Mono.Cecil.dll"
$asm = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($asmPath)

Write-Host "Looking for methods reading WorldObjectiveMarker.range..."
foreach ($t in $asm.MainModule.Types) {
    foreach ($m in $t.Methods) {
        if ($m.HasBody) {
            foreach ($inst in $m.Body.Instructions) {
                if ($inst.Operand -ne $null -and $inst.Operand.GetType().Name -eq "MethodReference") {
                    $methodRef = $inst.Operand
                    if ($methodRef.DeclaringType.Name -eq "WorldObjectiveMarker" -and $methodRef.Name -eq "get_range") {
                        Write-Host "Found in: $($t.Name).$($m.Name)"
                    }
                }
            }
        }
    }
}
Write-Host "Done."
