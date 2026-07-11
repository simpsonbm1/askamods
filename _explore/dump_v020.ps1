$base = "D:\SteamLibrary\steamapps\common\ASKA\BepInEx"
$dirs = @("$base\interop", "$base\core", "$base\unity-libs")
[void][System.Reflection.Assembly]::LoadFrom("d:\Claude Projects\askamods\_explore\bin\Debug\net10.0\Mono.Cecil.dll")
$resolver = New-Object Mono.Cecil.DefaultAssemblyResolver
foreach ($d in $dirs) { $resolver.AddSearchDirectory($d) }
$rp = New-Object Mono.Cecil.ReaderParameters
$rp.AssemblyResolver = $resolver
$asms = @()
foreach ($f in Get-ChildItem "$base\interop\*.dll") {
    try { $asms += [Mono.Cecil.AssemblyDefinition]::ReadAssembly($f.FullName, $rp) } catch {}
}
$all = New-Object System.Collections.Generic.List[object]
foreach ($a in $asms) { foreach ($t in $a.MainModule.Types) { $all.Add($t); foreach ($n in $t.NestedTypes) { $all.Add($n) } } }
$names = @("ItemEventContext","ItemCategoryInfo","ItemContainer","NetworkObject","PlayerRunner")
foreach ($tn in $names) {
    $t = $all | Where-Object { $_.Name -eq $tn } | Select-Object -First 1
    if ($null -eq $t) { Write-Output "`n### $tn : NOT FOUND"; continue }
    Write-Output "`n### $($t.FullName)  (base=$($t.BaseType.FullName)) IsValueType=$($t.IsValueType)"
    foreach ($c in $t.Methods | Where-Object { $_.IsConstructor }) {
        $ps = ($c.Parameters | ForEach-Object { "$($_.ParameterType.Name) $($_.Name)" }) -join ", "
        Write-Output "  CTOR($ps)"
    }
    foreach ($f in ($t.Fields | Where-Object { -not $_.Name.StartsWith("Native") -and -not $_.Name.Contains("k__") })) {
        Write-Output "  F $($f.Name) : $($f.FieldType.Name)"
    }
    foreach ($p in $t.Properties) { Write-Output "  P $($p.Name) : $($p.PropertyType.Name)" }
}
