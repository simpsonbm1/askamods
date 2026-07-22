# Feature research: bear dens + spires are passive periodic spawners (NOT SSSGame.Den). Need:
# (1) PopulationSpawner API — how to force a spawn; (2) how these POIs are typed/reached;
# (3) any "Spire"/"Tower" type; (4) how a spawner names/identifies what it spawns.
$base = "D:\SteamLibrary\steamapps\common\ASKA\BepInEx"
[void][System.Reflection.Assembly]::LoadFrom("d:\Claude Projects\askamods\_explore\bin\Debug\net10.0\Mono.Cecil.dll")
$allTypes = New-Object System.Collections.Generic.List[object]
foreach ($f in Get-ChildItem "$base\interop\*.dll") {
    try { $asm = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($f.FullName) } catch { continue }
    foreach ($t in $asm.MainModule.Types) { $allTypes.Add($t); foreach ($n in $t.NestedTypes) { $allTypes.Add($n) } }
}
$byName = @{}
foreach ($t in $allTypes) { if (-not $byName.ContainsKey($t.FullName)) { $byName[$t.FullName] = $t } }

function Sig($m) {
    $ps = ($m.Parameters | ForEach-Object { "$($_.ParameterType.Name) $($_.Name)" }) -join ", "
    "$($m.ReturnType.Name) $($m.Name)($ps)"
}
function DumpType($tn) {
    $t = $byName[$tn]
    if (-not $t) { Write-Host "### NO TYPE $tn`n"; return }
    Write-Host "### $($t.FullName)  base=$($t.BaseType)"
    Write-Host "  -- properties --"
    foreach ($p in $t.Properties) { Write-Host "    $($p.Name) : $($p.PropertyType.Name)" }
    Write-Host "  -- methods (non-accessor) --"
    foreach ($m in $t.Methods) { if ($m.Name -notmatch "^(get_|set_|\.ctor|\.cctor)") { Write-Host "    $(Sig $m)" } }
    Write-Host ""
}

DumpType "SSSGame.PopulationSpawner"
DumpType "SSSGame.PopulationManager"

Write-Host "==================== TYPES MATCHING Spire / Tower / Nest ===================="
foreach ($t in $allTypes) { if ($t.FullName -match "Spire|Tower|Nest" -and $t.FullName -notmatch "<|MethodInfoStore") { Write-Host "  $($t.FullName)  base=$($t.BaseType)" } }

Write-Host ""
Write-Host "==================== TYPES with a PopulationSpawner field (who owns spawners besides Den?) ===================="
foreach ($t in $allTypes) {
    if ($t.FullName -match "<|MethodInfoStore") { continue }
    foreach ($f in $t.Fields) {
        if ($f.FieldType.FullName -match "PopulationSpawner" -and $f.Name -notmatch "^Native") {
            Write-Host "  $($t.FullName).$($f.Name) : $($f.FieldType.Name)"
        }
    }
}
