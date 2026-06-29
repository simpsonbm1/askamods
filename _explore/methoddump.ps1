param([string]$Type, [string]$Method)
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
$t = $all | Where-Object { $_.Name -eq $Type } | Select-Object -First 1
if ($null -eq $t) { Write-Output "TYPE NOT FOUND"; exit }
foreach ($m in $t.Methods) {
    if ($m.Name -eq $Method) {
        Write-Output "M $($m.Name) : $($m.ReturnType.FullName)"
        if ($m.ReturnType -is [Mono.Cecil.GenericInstanceType]) {
            foreach ($ga in $m.ReturnType.GenericArguments) { Write-Output "  generic arg: $($ga.FullName)" }
        }
    }
}
