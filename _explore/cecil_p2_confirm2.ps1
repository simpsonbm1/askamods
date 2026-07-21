$base = "D:\SteamLibrary\steamapps\common\ASKA\BepInEx"
[void][System.Reflection.Assembly]::LoadFrom("d:\Claude Projects\askamods\_explore\bin\Debug\net10.0\Mono.Cecil.dll")

$allTypes = New-Object System.Collections.Generic.List[object]
foreach ($f in Get-ChildItem "$base\interop\*.dll") {
    try { $asm = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($f.FullName) } catch { continue }
    foreach ($t in $asm.MainModule.Types) {
        $allTypes.Add($t)
        foreach ($n in $t.NestedTypes) { $allTypes.Add($n) }
    }
}
$byName = @{}
foreach ($t in $allTypes) { if (-not $byName.ContainsKey($t.FullName)) { $byName[$t.FullName] = $t } }

Write-Output "---- Villager base chain + key members ----"
$t = $byName["SSSGame.Villager"]
if (-not $t) { foreach ($tt in $allTypes) { if ($tt.Name -eq "Villager") { $t = $tt } } }
$cur = $t
while ($cur -ne $null) {
    Write-Output "  $($cur.FullName)"
    if ($cur.BaseType -eq $null) { break }
    $cur = $byName[$cur.BaseType.FullName]
    if ($cur -eq $null) { Write-Output "  (base not in byName map: stop)"; break }
}
Write-Output "-- Villager members matching gameObject/transform/GetName --"
foreach ($p in $t.Properties) { if ($p.Name -match "(?i)gameObject|transform") { Write-Output "  P: $($p.Name) : $($p.PropertyType.FullName)" } }
foreach ($m in $t.Methods) { if ($m.Name -match "(?i)GetName") { Write-Output "  M: $($m.ReturnType.Name) $($m.Name)()" } }

Write-Output ""
Write-Output "---- IFSMBehaviourController.gameObject full type ----"
$t2 = $byName["SSSGame.AI.FSM.IFSMBehaviourController"]
foreach ($p in $t2.Properties) { if ($p.Name -eq "gameObject") { Write-Output "  P: gameObject : $($p.PropertyType.FullName)" } }

Write-Output "DONE"
