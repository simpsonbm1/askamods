$asmPath = "D:\SteamLibrary\steamapps\common\ASKA\BepInEx\interop\Assembly-CSharp.dll"
Add-Type -Path "D:\SteamLibrary\steamapps\common\ASKA\BepInEx\core\Mono.Cecil.dll"
$asm = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($asmPath)
$mod = $asm.MainModule
Write-Host "=== methods returning WorldTileData ==="
foreach ($t in $mod.Types) {
  foreach ($m in $t.Methods) {
    if ($m.ReturnType.Name -eq "WorldTileData") {
      $ps = ($m.Parameters | ForEach-Object { "$($_.ParameterType.Name) $($_.Name)" }) -join ", "
      Write-Host "  $($t.FullName).$($m.Name)($ps)"
    }
  }
}
Write-Host ""
Write-Host "=== WorldDataManager surface ==="
$t = $mod.GetType("SSSGame.WorldDataManager")
if ($t) {
  foreach ($p in $t.Properties) { Write-Host "  prop $($p.Name) : $($p.PropertyType.Name)" }
  foreach ($m in $t.Methods) {
    if ($m.Name -match "^get_|^set_") { continue }
    $ps = ($m.Parameters | ForEach-Object { "$($_.ParameterType.Name) $($_.Name)" }) -join ", "
    Write-Host "  $($m.Name)($ps) : $($m.ReturnType.Name)"
  }
} else { Write-Host "  (no SSSGame.WorldDataManager)" }
