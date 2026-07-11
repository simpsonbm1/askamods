$base = "D:\SteamLibrary\steamapps\common\ASKA\BepInEx"
$dirs = @("$base\interop", "$base\core", "$base\unity-libs")
[void][System.Reflection.Assembly]::LoadFrom("d:\Claude Projects\askamods\_explore\bin\Debug\net10.0\Mono.Cecil.dll")
$resolver = New-Object Mono.Cecil.DefaultAssemblyResolver
foreach ($d in $dirs) { $resolver.AddSearchDirectory($d) }
$rp = New-Object Mono.Cecil.ReaderParameters
$rp.AssemblyResolver = $resolver
$asms = @()
foreach ($f in Get-ChildItem "$base\unity-libs\*.dll") {
    try { $asms += [Mono.Cecil.AssemblyDefinition]::ReadAssembly($f.FullName, $rp) } catch {}
}
foreach ($f in Get-ChildItem "$base\interop\*.dll") {
    try { $asms += [Mono.Cecil.AssemblyDefinition]::ReadAssembly($f.FullName, $rp) } catch {}
}
$all = New-Object System.Collections.Generic.List[object]
foreach ($a in $asms) { foreach ($t in $a.MainModule.Types) { $all.Add($t) } }
$matches = $all | Where-Object { $_.FullName -eq "UnityEngine.GameObject" }
foreach ($t in $matches) {
    Write-Output "`n### $($t.FullName) from $($t.Module.Assembly.Name.Name)"
    foreach ($m in ($t.Methods | Where-Object { $_.Name -like "GetComponent*" })) {
        $ps = ($m.Parameters | ForEach-Object { "$($_.ParameterType.FullName) $($_.Name)" }) -join ", "
        Write-Output "  M $($m.Name)($ps) : $($m.ReturnType.Name)"
    }
}
