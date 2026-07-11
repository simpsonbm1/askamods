$base = "D:\SteamLibrary\steamapps\common\ASKA\BepInEx"
$dirs = @("$base\interop", "$base\core", "$base\unity-libs")
[void][System.Reflection.Assembly]::LoadFrom("d:\Claude Projects\askamods\_explore\bin\Debug\net10.0\Mono.Cecil.dll")
$resolver = New-Object Mono.Cecil.DefaultAssemblyResolver
foreach ($d in $dirs) { $resolver.AddSearchDirectory($d) }
$rp = New-Object Mono.Cecil.ReaderParameters
$rp.AssemblyResolver = $resolver
foreach ($f in Get-ChildItem "$base\interop\Il2Cppmscorlib.dll") {
    $asm = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($f.FullName, $rp)
    $t = $asm.MainModule.Types | Where-Object { $_.FullName -eq "Il2CppSystem.Collections.Generic.IReadOnlyList`1" }
    foreach ($tt in $t) {
        Write-Output "### $($tt.FullName)"
        foreach ($p in $tt.Properties) { Write-Output "  P $($p.Name) : $($p.PropertyType.Name)" }
        foreach ($m in $tt.Methods) { Write-Output "  M $($m.Name)" }
    }
}
