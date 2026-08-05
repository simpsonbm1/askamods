$asmPath = "D:\SteamLibrary\steamapps\common\ASKA\BepInEx\interop\Assembly-CSharp.dll"
Add-Type -Path "D:\SteamLibrary\steamapps\common\ASKA\BepInEx\core\Mono.Cecil.dll"
$asm = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($asmPath)
$mod = $asm.MainModule
Write-Host "=== members typed TerrainDataHandler ==="
foreach ($t in $mod.Types) {
  foreach ($p in $t.Properties) { if ($p.PropertyType.Name -eq "TerrainDataHandler") { Write-Host "  prop $($t.FullName).$($p.Name)" } }
  foreach ($f in $t.Fields)     { if ($f.FieldType.Name -eq "TerrainDataHandler")    { Write-Host "  field $($t.FullName).$($f.Name)" } }
  foreach ($m in $t.Methods) { if ($m.ReturnType.Name -eq "TerrainDataHandler") { Write-Host "  method $($t.FullName).$($m.Name)()" } }
}
Write-Host ""
Write-Host "=== WorldDataManager static/instance accessors (Instance-like) ==="
$t = $mod.GetType("SSSGame.WorldDataManager")
foreach ($f in $t.Fields) { Write-Host "  field $($f.Name) : $($f.FieldType.Name) static=$($f.IsStatic)" }
Write-Host ""
Write-Host "=== StreamingTerrainVS ==="
$t2 = $mod.GetType("SSSGame.StreamingTerrainVS")
foreach ($p in $t2.Properties) { Write-Host "  prop $($p.Name) : $($p.PropertyType.Name)" }
foreach ($m in $t2.Methods) { if ($m.Name -notmatch "^get_|^set_") { $ps = ($m.Parameters | ForEach-Object { $_.ParameterType.Name }) -join ", "; Write-Host "  $($m.Name)($ps) : $($m.ReturnType.Name)" } }
