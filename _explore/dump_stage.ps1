$asmPath = "D:\SteamLibrary\steamapps\common\ASKA\BepInEx\interop\Assembly-CSharp.dll"
Add-Type -Path "D:\SteamLibrary\steamapps\common\ASKA\BepInEx\core\Mono.Cecil.dll"
$asm = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($asmPath)
foreach ($tn in @("SSSGame.DynamicPlacementToolStage")) {
    $t = $asm.MainModule.GetType($tn)
    if (-not $t) { Write-Host "NOT FOUND: $tn"; continue }
    Write-Host "ENUM: $($t.FullName)"
    foreach ($f in $t.Fields) {
        if ($f.HasConstant) { Write-Host "    $($f.Name) = $($f.Constant)" }
    }
}
