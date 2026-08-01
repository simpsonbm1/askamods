Add-Type -Path "D:\SteamLibrary\steamapps\common\ASKA\BepInEx\core\Mono.Cecil.dll"
$interop = "D:\SteamLibrary\steamapps\common\ASKA\BepInEx\interop"
$names = @("Assembly-CSharp.dll","SandSailorStudio.dll","Assembly-CSharp-firstpass.dll")
$asms = $names | ForEach-Object { [Mono.Cecil.AssemblyDefinition]::ReadAssembly((Join-Path $interop $_)) }

function Find-Type($simple) {
    foreach ($a in $asms) {
        foreach ($tt in $a.MainModule.GetTypes()) { if ($tt.Name -eq $simple) { return $tt } }
    }
    return $null
}

Write-Host "==== 1. ItemThumbnailPanel FIELDS ===="
$t = Find-Type "ItemThumbnailPanel"
foreach ($f in ($t.Fields | Sort-Object Name)) {
    Write-Host ("    {0} : {1}" -f $f.Name, $f.FieldType.Name)
}

Write-Host ""
Write-Host "==== 2. CALLERS of _OnHighlighted ===="
foreach ($a in $asms) {
  foreach ($ty in $a.MainModule.GetTypes()) {
    foreach ($m in $ty.Methods) {
      if (-not $m.HasBody) { continue }
      foreach ($i in $m.Body.Instructions) {
        $op = $i.Operand
        if ($op -and $op.ToString() -match "_OnHighlighted") {
          Write-Host ("    {0}::{1}  ->  {2}" -f $ty.FullName, $m.Name, $op.ToString())
        }
      }
    }
  }
}

Write-Host ""
Write-Host "==== 3. TYPES declaring OnPointerEnter ===="
foreach ($a in $asms) {
  foreach ($ty in $a.MainModule.GetTypes()) {
    foreach ($m in $ty.Methods) {
      if ($m.Name -eq "OnPointerEnter") {
        Write-Host ("    {0}   (base={1})" -f $ty.FullName, $ty.BaseType)
      }
    }
  }
}
