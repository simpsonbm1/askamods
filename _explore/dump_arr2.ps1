$base = "D:\SteamLibrary\steamapps\common\ASKA\BepInEx"
$dirs = @("$base\interop", "$base\core", "$base\unity-libs")
[void][System.Reflection.Assembly]::LoadFrom("d:\Claude Projects\askamods\_explore\bin\Debug\net10.0\Mono.Cecil.dll")
$resolver = New-Object Mono.Cecil.DefaultAssemblyResolver
foreach ($d in $dirs) { $resolver.AddSearchDirectory($d) }
$rp = New-Object Mono.Cecil.ReaderParameters
$rp.AssemblyResolver = $resolver
foreach ($dllpath in Get-ChildItem "$base\core\*.dll") {
    try {
        $a = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($dllpath.FullName, $rp)
        foreach ($t in $a.MainModule.Types) {
            if ($t.Name -like "Il2CppArrayBase*" -or $t.Name -like "Il2CppReferenceArray*") {
                Write-Output "FOUND in $($dllpath.Name): $($t.FullName) base=$($t.BaseType.Name)"
                foreach ($p in $t.Properties) { Write-Output "  P $($p.Name) : $($p.PropertyType.Name)" }
                foreach ($m in ($t.Methods | Where-Object {-not $_.IsGetter -and -not $_.IsSetter -and -not $_.IsConstructor})) {
                    Write-Output "  M $($m.Name)() : $($m.ReturnType.Name)"
                }
            }
        }
    } catch {}
}
