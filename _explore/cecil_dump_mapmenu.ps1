$asmPath = "D:\SteamLibrary\steamapps\common\ASKA\BepInEx\interop\Assembly-CSharp.dll"
Add-Type -Path "D:\SteamLibrary\steamapps\common\ASKA\BepInEx\core\Mono.Cecil.dll"
$asm = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($asmPath)
$t = $asm.MainModule.GetType("SSSGame.UI.MapMenu")
if ($t) {
    Write-Host "TYPE: $($t.FullName)"
    Write-Host "  Methods:"
    foreach ($m in $t.Methods) { 
        if ($m.Name -match "Hover" -or $m.Name -match "Pointer" -or $m.Name -match "Icon") {
            Write-Host "    $($m.Name)"
        }
    }
}
