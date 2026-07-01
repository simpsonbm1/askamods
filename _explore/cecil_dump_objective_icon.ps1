$asmPath = "D:\SteamLibrary\steamapps\common\ASKA\BepInEx\interop\Assembly-CSharp.dll"
Add-Type -Path "D:\SteamLibrary\steamapps\common\ASKA\BepInEx\core\Mono.Cecil.dll"
$asm = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($asmPath)
$t = $asm.MainModule.GetType("SSSGame.ObjectiveIcon")
if ($t) {
    Write-Host "TYPE: $($t.FullName)"
    Write-Host "  Fields:"
    foreach ($f in $t.Fields) { Write-Host "    $($f.Name) : $($f.FieldType.Name)" }
    Write-Host "  Methods:"
    foreach ($m in $t.Methods) { Write-Host "    $($m.Name)" }
}
