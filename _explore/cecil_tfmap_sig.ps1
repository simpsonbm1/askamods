$asmPath = "D:\SteamLibrary\steamapps\common\ASKA\BepInEx\interop\Assembly-CSharp.dll"
Add-Type -Path "D:\SteamLibrary\steamapps\common\ASKA\BepInEx\core\Mono.Cecil.dll"
$asm = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($asmPath)
$mod = $asm.MainModule
foreach ($n in @("SSSGame.TerraformingMap","SSSGame.TerrainType")) {
  $t = $mod.GetType($n)
  Write-Host "TYPE $n"
  foreach ($f in $t.Fields) { if ($f.Name -notmatch "^Native") { Write-Host "  field $($f.Name) : $($f.FieldType.Name) static=$($f.IsStatic)" } }
  foreach ($m in $t.Methods) {
    $ps = ($m.Parameters | ForEach-Object { "$($_.ParameterType.FullName) $($_.Name)" }) -join ", "
    Write-Host "  $($m.Name)($ps) : $($m.ReturnType.Name) static=$($m.IsStatic) public=$($m.IsPublic)"
  }
  Write-Host ""
}
