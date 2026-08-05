$asmPath = "D:\SteamLibrary\steamapps\common\ASKA\BepInEx\interop\Assembly-CSharp.dll"
Add-Type -Path "D:\SteamLibrary\steamapps\common\ASKA\BepInEx\core\Mono.Cecil.dll"
$asm = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($asmPath)
$mod = $asm.MainModule
foreach ($t in $mod.Types) {
  if ($t.FullName -match "Terrain|Heightmap|HeightMap|Terraform|Deform|Voxel|Chunk") {
    Write-Host $t.FullName
  }
}
