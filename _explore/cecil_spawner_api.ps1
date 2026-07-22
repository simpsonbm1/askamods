$base = "D:\SteamLibrary\steamapps\common\ASKA\BepInEx"
[void][System.Reflection.Assembly]::LoadFrom("d:\Claude Projects\askamods\_explore\bin\Debug\net10.0\Mono.Cecil.dll")
$byName = @{}
foreach ($f in Get-ChildItem "$base\interop\*.dll") {
    try { $asm = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($f.FullName) } catch { continue }
    foreach ($t in $asm.MainModule.Types) { if (-not $byName.ContainsKey($t.FullName)) { $byName[$t.FullName] = $t }
        foreach ($n in $t.NestedTypes) { if (-not $byName.ContainsKey($n.FullName)) { $byName[$n.FullName] = $n } } }
}
function Sig($m) { $ps=($m.Parameters|%{"$($_.ParameterType.Name) $($_.Name)"}) -join ", "; "$($m.ReturnType.Name) $($m.Name)($ps)" }
function DumpType($tn) {
    $t = $byName[$tn]; if (-not $t) { Write-Host "### NO TYPE $tn`n"; return }
    Write-Host "### $($t.FullName)  base=$($t.BaseType)"
    foreach ($p in $t.Properties) { Write-Host "    prop $($p.Name) : $($p.PropertyType.Name)" }
    foreach ($m in $t.Methods) { if ($m.Name -notmatch "^(get_|set_|\.c)") { Write-Host "    M $(Sig $m)" } }
    Write-Host ""
}
DumpType "SSSGame.Combat.PopulationSpawner"
DumpType "SSSGame.CreatureSpawner"
DumpType "SSSGame.ExplorationTower"
