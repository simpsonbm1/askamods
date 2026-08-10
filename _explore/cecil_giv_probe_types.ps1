$base = "D:\SteamLibrary\steamapps\common\ASKA\BepInEx"
[void][System.Reflection.Assembly]::LoadFrom("d:\Claude Projects\askamods\_explore\bin\Debug\net10.0\Mono.Cecil.dll")
$all = New-Object System.Collections.Generic.List[object]
foreach ($f in Get-ChildItem "$base\interop\*.dll") {
  try { $asm = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($f.FullName) } catch { continue }
  foreach ($t in $asm.MainModule.Types) { $all.Add($t) }
}
foreach ($n in @("SSSGame.Monster","SSSGame.Creature","SSSGame.Character","SSSGame.DynamicItemObject","SSSGame.WorldItemObject")) {
  $t = $all | Where-Object { $_.FullName -eq $n } | Select-Object -First 1
  if ($t) { Write-Host "FOUND $n  base=$($t.BaseType)" } else { Write-Host "MISSING $n" }
}
