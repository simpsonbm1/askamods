$base = "D:\SteamLibrary\steamapps\common\ASKA\BepInEx"
$dirs = @("$base\interop", "$base\core", "$base\unity-libs")
[void][System.Reflection.Assembly]::LoadFrom("d:\Claude Projects\askamods\_explore\bin\Debug\net10.0\Mono.Cecil.dll")
$resolver = New-Object Mono.Cecil.DefaultAssemblyResolver
foreach ($d in $dirs) { $resolver.AddSearchDirectory($d) }
$rp = New-Object Mono.Cecil.ReaderParameters
$rp.AssemblyResolver = $resolver
$asms = @()
foreach ($f in Get-ChildItem "$base\interop\Il2Cppmscorlib.dll") {
    try { $asms += [Mono.Cecil.AssemblyDefinition]::ReadAssembly($f.FullName, $rp) } catch {}
}
$all = New-Object System.Collections.Generic.List[object]
foreach ($a in $asms) { foreach ($t in $a.MainModule.Types) { $all.Add($t); foreach ($n in $t.NestedTypes) { $all.Add($n) } } }
$matches = $all | Where-Object { $_.FullName -eq "Il2CppSystem.Type" }
foreach ($t in $matches) {
    Write-Output "`n### $($t.FullName)"
    foreach ($m in ($t.Methods | Where-Object { $_.Name -like "op_*" -or $_.Name -like "*Implicit*" -or $_.Name -like "*Explicit*" })) {
        $ps = ($m.Parameters | ForEach-Object { "$($_.ParameterType.FullName) $($_.Name)" }) -join ", "
        Write-Output "  M $($m.Name)($ps) : $($m.ReturnType.FullName)"
    }
}
