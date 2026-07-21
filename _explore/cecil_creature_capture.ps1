$base = "D:\SteamLibrary\steamapps\common\ASKA\BepInEx"
[void][System.Reflection.Assembly]::LoadFrom("d:\Claude Projects\askamods\_explore\bin\Debug\net10.0\Mono.Cecil.dll")
$allTypes = New-Object System.Collections.Generic.List[object]
foreach ($f in Get-ChildItem "$base\interop\*.dll") {
    try { $asm = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($f.FullName) } catch { continue }
    foreach ($t in $asm.MainModule.Types) { $allTypes.Add($t); foreach ($n in $t.NestedTypes) { $allTypes.Add($n) } }
}
$byName = @{}
foreach ($t in $allTypes) { if (-not $byName.ContainsKey($t.FullName)) { $byName[$t.FullName] = $t } }

$c = $byName["SSSGame.Creature"]
Write-Host "### SSSGame.Creature  base=$($c.BaseType)"
Write-Host "  -- zero-arg lifecycle candidates --"
foreach ($m in $c.Methods) {
    if ($m.Name -match "^(Awake|Start|Spawned|OnEnable|Init|Initialize)$") {
        $ps = ($m.Parameters | ForEach-Object { $_.ParameterType.Name }) -join ","
        Write-Host ("    {0}({1})  virt={2}" -f $m.Name, $ps, $m.IsVirtual)
    }
}
Write-Host "  -- identity members --"
foreach ($p in $c.Properties) { if ($p.Name -match "dataSheet|Faction|Name") { Write-Host "    $($p.Name) : $($p.PropertyType.Name)" } }

Write-Host ""
Write-Host "### CreatureDataSheet base (is it a ScriptableObject => has .name?)"
$d = $byName["SSSGame.CreatureDataSheet"]
Write-Host "  base=$($d.BaseType)"

Write-Host ""
Write-Host "### Structure identity members"
$s = $byName["SSSGame.Structure"]
Write-Host "  base=$($s.BaseType)"
foreach ($p in $s.Properties) { if ($p.Name -match "Name|template|Template") { Write-Host "    $($p.Name) : $($p.PropertyType.Name)" } }

Write-Host ""
Write-Host "### StructureTemplate base + name members"
$st = $byName["SSSGame.StructureTemplate"]
if ($st) {
    Write-Host "  base=$($st.BaseType)"
    foreach ($p in $st.Properties) { if ($p.Name -match "Name|name|id") { Write-Host "    $($p.Name) : $($p.PropertyType.Name)" } }
}
