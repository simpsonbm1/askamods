$asmPath = "D:\SteamLibrary\steamapps\common\ASKA\BepInEx\interop\Assembly-CSharp.dll"
Add-Type -Path "D:\SteamLibrary\steamapps\common\ASKA\BepInEx\core\Mono.Cecil.dll"
$asm = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($asmPath)
$mod = $asm.MainModule
foreach ($n in @("SSSGame.StreamingTerrainVS","SSSGame.TerrainChunk")) {
  $t = $mod.GetType($n)
  $b = $t.BaseType
  Write-Host "$n base chain:"
  while ($b) { Write-Host "   -> $($b.FullName)"; try { $bd = $b.Resolve() } catch { $bd = $null }; if (-not $bd) { break }; $b = $bd.BaseType }
  $m = $t.Methods | Where-Object { $_.Name -eq "Awake" }
  foreach ($mm in $m) { Write-Host "   Awake public=$($mm.IsPublic) private=$($mm.IsPrivate) virtual=$($mm.IsVirtual)" }
  Write-Host ""
}
