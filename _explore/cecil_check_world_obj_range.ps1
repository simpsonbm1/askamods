$asmPath = "D:\SteamLibrary\steamapps\common\ASKA\BepInEx\interop\Assembly-CSharp.dll"
Add-Type -Path "D:\SteamLibrary\steamapps\common\ASKA\BepInEx\core\Mono.Cecil.dll"
$asm = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($asmPath)
$t = $asm.MainModule.GetType("SSSGame.WorldObjectiveMarker")
if ($t) {
    $m = $t.Methods | Where-Object { $_.Name -eq "get_range" }
    if ($m) { Write-Host "Found get_range!" }
}
