$asmPath = "D:\SteamLibrary\steamapps\common\ASKA\BepInEx\interop\Assembly-CSharp.dll"
Add-Type -Path "D:\SteamLibrary\steamapps\common\ASKA\BepInEx\core\Mono.Cecil.dll"
$asm = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($asmPath)
$t = $asm.MainModule.GetType("SSSGame.ObjectiveIcon")
if ($t) {
    Write-Host "TYPE: $($t.FullName)"
    $m = $t.Methods | Where-Object { $_.Name -eq "Refresh" }
    if ($m) {
        Write-Host "Method: $($m.Name)"
        foreach ($inst in $m.Body.Instructions) {
            Write-Host "  $($inst.OpCode) $($inst.Operand)"
        }
    }
}
