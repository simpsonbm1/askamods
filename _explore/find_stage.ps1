$asmPath = "D:\SteamLibrary\steamapps\common\ASKA\BepInEx\interop\Assembly-CSharp.dll"
Add-Type -Path "D:\SteamLibrary\steamapps\common\ASKA\BepInEx\core\Mono.Cecil.dll"
$asm = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($asmPath)
foreach ($t in $asm.MainModule.GetTypes()) {
    if ($t.Name -match "PlacementToolStage" -or $t.Name -match "DynamicPlacementTool") {
        Write-Host "TYPE: $($t.FullName) (enum=$($t.IsEnum))"
        if ($t.IsEnum) {
            foreach ($f in $t.Fields) { if ($f.HasConstant) { Write-Host "    $($f.Name) = $($f.Constant)" } }
        }
    }
}
