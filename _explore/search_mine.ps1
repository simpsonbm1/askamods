[void][System.Reflection.Assembly]::LoadFrom("d:\Claude Projects\askamods\_explore\bin\Debug\net10.0\Mono.Cecil.dll")
$base = "D:\SteamLibrary\steamapps\common\ASKA\BepInEx"
$dirs = @("$base\interop", "$base\core", "$base\unity-libs")
$resolver = New-Object Mono.Cecil.DefaultAssemblyResolver
foreach ($d in $dirs) { $resolver.AddSearchDirectory($d) }
$rp = New-Object Mono.Cecil.ReaderParameters
$rp.AssemblyResolver = $resolver
$asms = @()
foreach ($f in Get-ChildItem "$base\interop\*.dll") {
    try { $asms += [Mono.Cecil.AssemblyDefinition]::ReadAssembly($f.FullName, $rp) } catch {}
}
$allTypes = New-Object System.Collections.Generic.List[object]
foreach ($a in $asms) {
    foreach ($t in $a.MainModule.Types) {
        $allTypes.Add($t)
        foreach ($n in $t.NestedTypes) { $allTypes.Add($n) }
    }
}
$pattern = '(?i)(Mine|Cave|Hallway|Tunnel|Explod|Sack|CaveIn|Collapse|Detonat|Demolish|Rubble|Blockade|Obstruct)'
$allTypes | Where-Object { $_.FullName -match $pattern } | ForEach-Object { $_.FullName } | Sort-Object
