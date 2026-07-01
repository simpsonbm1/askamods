$asmPath = "D:\SteamLibrary\steamapps\common\ASKA\BepInEx\interop\Assembly-CSharp.dll"
Add-Type -Path "D:\SteamLibrary\steamapps\common\ASKA\BepInEx\core\Mono.Cecil.dll"
$asm = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($asmPath)
foreach ($t in $asm.MainModule.Types) {
    if ($t.Name -match "HarvestMarkerState") {
        Write-Host "TYPE: $($t.FullName)"
        foreach ($f in $t.Fields) { Write-Host "    $($f.Name)" }
        foreach ($p in $t.Properties) { Write-Host "    $($p.Name)" }
    }
}
