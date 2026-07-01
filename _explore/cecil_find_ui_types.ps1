$asmPath = "D:\SteamLibrary\steamapps\common\ASKA\BepInEx\interop\Assembly-CSharp.dll"
Add-Type -Path "D:\SteamLibrary\steamapps\common\ASKA\BepInEx\core\Mono.Cecil.dll"
$asm = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($asmPath)
foreach ($t in $asm.MainModule.Types) {
    if ($t.Namespace -eq "SSSGame.UI" -or $t.Namespace -eq "SSSGame") {
        if ($t.Name -match "Icon" -or $t.Name -match "Marker") {
            Write-Host $t.FullName
        }
    }
}
