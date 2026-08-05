$asmPath = "D:\SteamLibrary\steamapps\common\ASKA\BepInEx\interop\Assembly-CSharp.dll"
Add-Type -Path "D:\SteamLibrary\steamapps\common\ASKA\BepInEx\core\Mono.Cecil.dll"
$asm = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($asmPath)
$mod = $asm.MainModule
Write-Host "=== Properties/fields typed TerraformingMap ==="
foreach ($t in $mod.Types) {
  foreach ($p in $t.Properties) { if ($p.PropertyType.Name -eq "TerraformingMap") { Write-Host "  $($t.FullName).$($p.Name)  [prop]" } }
  foreach ($f in $t.Fields)     { if ($f.FieldType.Name -eq "TerraformingMap")    { Write-Host "  $($t.FullName).$($f.Name)  [field]" } }
  foreach ($m in $t.Methods) {
    if ($m.ReturnType.Name -eq "TerraformingMap") { Write-Host "  $($t.FullName).$($m.Name)() -> TerraformingMap  [method]" }
    foreach ($pp in $m.Parameters) { if ($pp.ParameterType.Name -eq "TerraformingMap") { Write-Host "  $($t.FullName).$($m.Name)(... $($pp.Name):TerraformingMap)  [param]" } }
  }
}
