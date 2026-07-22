$base = "D:\SteamLibrary\steamapps\common\ASKA\BepInEx"
[void][System.Reflection.Assembly]::LoadFrom("d:\Claude Projects\askamods\_explore\bin\Debug\net10.0\Mono.Cecil.dll")
$byName = @{}
$allTypes = @()
foreach ($f in Get-ChildItem "$base\interop\*.dll") {
    try { $asm = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($f.FullName) } catch { continue }
    foreach ($t in $asm.MainModule.Types) {
        if (-not $byName.ContainsKey($t.FullName)) { $byName[$t.FullName] = $t; $allTypes += $t }
        foreach ($n in $t.NestedTypes) { if (-not $byName.ContainsKey($n.FullName)) { $byName[$n.FullName] = $n; $allTypes += $n } } }
}
function Sig($m) { $ps=($m.Parameters|%{"$($_.ParameterType.Name) $($_.Name)"}) -join ", "; "$($m.ReturnType.Name) $($m.Name)($ps)" }
function DumpType($tn) {
    $t = $byName[$tn]; if (-not $t) { Write-Host "### NO TYPE $tn`n"; return }
    Write-Host "### $($t.FullName)  base=$($t.BaseType)"
    foreach ($fld in $t.Fields) { if ($fld.Name -notmatch '^NativeF|^NativeM') { Write-Host "    F $($fld.Name) : $($fld.FieldType.Name)" } }
    foreach ($p in $t.Properties) { Write-Host "    prop $($p.Name) : $($p.PropertyType.Name)" }
    foreach ($m in $t.Methods) { if ($m.Name -notmatch "^(get_|set_|add_|remove_|\.c)") { Write-Host "    M $(Sig $m)" } }
    Write-Host ""
}
# 1. Find any type whose name mentions SpawnPopulation
Write-Host ">>> Types matching *SpawnPopulation* / *PopulationInfo*:"
$allTypes | Where-Object { $_.Name -match "SpawnPopulation|PopulationInfo|SpawnPopulationConfiguration" } | ForEach-Object { Write-Host "    $($_.FullName)  base=$($_.BaseType)" }
Write-Host ""
# 2. The generic arg of PopulationSpawner._populations
$ps = $byName["SSSGame.Combat.PopulationSpawner"]
$popsProp = $ps.Properties | Where-Object { $_.Name -eq "_populations" }
if ($popsProp) { Write-Host ">>> _populations prop type = $($popsProp.PropertyType.FullName)`n" }
DumpType "SSSGame.Combat.SpawnPopulation"
DumpType "SSSGame.SpawnPopulation"
DumpType "SSSGame.PopulationInfo"
DumpType "SSSGame.Combat.PopulationInfo"
