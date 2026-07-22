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
    foreach ($fld in $t.Fields) { Write-Host "    F $($fld.Name) : $($fld.FieldType.Name)" }
    foreach ($p in $t.Properties) { Write-Host "    prop $($p.Name) : $($p.PropertyType.Name)" }
    foreach ($m in $t.Methods) { if ($m.Name -notmatch "^(get_|set_|add_|remove_|\.c)") { Write-Host "    M $(Sig $m)" } }
    Write-Host ""
}
# resolve the element type of PopulationSpawner._populations
$ps = $byName["SSSGame.Combat.PopulationSpawner"]
$popsField = $ps.Fields | Where-Object { $_.Name -eq "_populations" }
if ($popsField) { Write-Host ">>> _populations field type = $($popsField.FieldType.FullName)`n" }
DumpType "SSSGame.Combat.SpawnPopulation"
DumpType "SSSGame.Combat.CreaturePopulationConfiguration"
DumpType "SSSGame.CreaturePopulationConfiguration"
DumpType "SSSGame.Combat.PopulationHandler"
