$asmPath = "D:\SteamLibrary\steamapps\common\ASKA\BepInEx\interop\Assembly-CSharp.dll"
Add-Type -Path "D:\SteamLibrary\steamapps\common\ASKA\BepInEx\core\Mono.Cecil.dll"
$asm = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($asmPath)
$t = $asm.MainModule.GetType("SSSGame.OutpostStructure")
$m = $t.Methods | Where-Object { $_.Name -eq "get_OutpostRange" }
if ($m) { Write-Host "Found method: $($m.Name)" }
