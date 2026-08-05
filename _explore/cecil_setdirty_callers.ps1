$asmPath = "D:\SteamLibrary\steamapps\common\ASKA\BepInEx\interop\Assembly-CSharp.dll"
Add-Type -Path "D:\SteamLibrary\steamapps\common\ASKA\BepInEx\core\Mono.Cecil.dll"
$rp = New-Object Mono.Cecil.ReaderParameters
$rp.ReadingMode = [Mono.Cecil.ReadingMode]::Immediate
$asm = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($asmPath, $rp)
$mod = $asm.MainModule
foreach ($t in $mod.Types) {
  foreach ($m in $t.Methods) {
    if (-not $m.HasBody) { continue }
    foreach ($i in $m.Body.Instructions) {
      $op = $i.Operand
      if ($op -and $op.ToString() -match "SetHeightmapDirty|IsHeightmapModified|GetTerraformingData") {
        Write-Host "  $($t.FullName).$($m.Name)  ->  $($op.ToString())"
      }
    }
  }
}
