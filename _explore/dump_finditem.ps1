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
$matches = $all | Where-Object { $_.FullName -eq "SandSailorStudio.Inventory.Item" }
foreach ($t in $matches) {
    Write-Output "`n### $($t.FullName)  (base=$($t.BaseType.Name))"
    foreach ($f in ($t.Fields | Where-Object { -not $_.Name.StartsWith("Native") -and -not $_.Name.Contains("k__") })) {
        Write-Output "  F $($f.Name) : $($f.FieldType.Name)"
    }
    foreach ($p in $t.Properties) { Write-Output "  P $($p.Name) : $($p.PropertyType.Name)" }
}
