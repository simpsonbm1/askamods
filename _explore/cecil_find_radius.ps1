$asmPath = "D:\SteamLibrary\steamapps\common\ASKA\BepInEx\interop\Assembly-CSharp.dll"
Add-Type -Path "D:\SteamLibrary\steamapps\common\ASKA\BepInEx\core\Mono.Cecil.dll"
$asm = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($asmPath)

foreach ($t in $asm.MainModule.Types) {
    if ($t.Name -match "Marker" -or $t.Name -match "Map" -or $t.Name -match "Icon") {
        foreach ($f in $t.Fields) {
            if ($f.Name -match "radius" -or $f.Name -match "range") {
                Write-Host "FIELD: $($t.Name).$($f.Name)"
            }
        }
        foreach ($p in $t.Properties) {
            if ($p.Name -match "radius" -or $p.Name -match "range") {
                Write-Host "PROP : $($t.Name).$($p.Name)"
            }
        }
    }
}
