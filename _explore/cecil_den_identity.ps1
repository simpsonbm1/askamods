# DenRespawn locale bug: rec.TypeName (den.GetName(), localized) gates AutoRules + looksLikeDen.
# Need a locale-INVARIANT den identity available at capture time (Den is loaded). Candidates:
# den.gameObject.name, den.dataSheet.name, a template/type enum.
$base = "D:\SteamLibrary\steamapps\common\ASKA\BepInEx"
[void][System.Reflection.Assembly]::LoadFrom("d:\Claude Projects\askamods\_explore\bin\Debug\net10.0\Mono.Cecil.dll")
$byName = @{}
foreach ($f in Get-ChildItem "$base\interop\*.dll") {
    try { $asm = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($f.FullName) } catch { continue }
    foreach ($t in $asm.MainModule.Types) { if (-not $byName.ContainsKey($t.FullName)) { $byName[$t.FullName] = $t }
        foreach ($n in $t.NestedTypes) { if (-not $byName.ContainsKey($n.FullName)) { $byName[$n.FullName] = $n } } }
}
foreach ($tn in @("SSSGame.Den")) {
    $t = $byName[$tn]
    if (-not $t) { Write-Host "### NO TYPE $tn"; continue }
    Write-Host "### $($t.FullName)  base=$($t.BaseType)"
    Write-Host "  -- properties --"
    foreach ($p in $t.Properties) { Write-Host "    $($p.Name) : $($p.PropertyType.Name)" }
    Write-Host "  -- name/type/sheet methods --"
    foreach ($m in $t.Methods) { if ($m.Name -match "Name|name|Type|type|Sheet|sheet|Data|data|Template") { $ps=($m.Parameters|%{$_.ParameterType.Name}) -join ","; Write-Host "    $($m.ReturnType.Name) $($m.Name)($ps)" } }
}
