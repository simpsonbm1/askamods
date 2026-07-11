$base = "D:\SteamLibrary\steamapps\common\ASKA\BepInEx"
$dirs = @("$base\interop", "$base\core", "$base\unity-libs")
[void][System.Reflection.Assembly]::LoadFrom("d:\Claude Projects\askamods\_explore\bin\Debug\net10.0\Mono.Cecil.dll")
$resolver = New-Object Mono.Cecil.DefaultAssemblyResolver
foreach ($d in $dirs) { $resolver.AddSearchDirectory($d) }
$rp = New-Object Mono.Cecil.ReaderParameters
$rp.AssemblyResolver = $resolver
foreach ($f in Get-ChildItem "$base\interop\Fusion*.dll") {
    try {
        $asm = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($f.FullName, $rp)
        $matches = $asm.MainModule.Types | Where-Object { $_.Name -eq "NetworkBehaviour" }
        foreach ($t in $matches) {
            Write-Output "Found in $($f.Name): $($t.FullName) base=$($t.BaseType.FullName) IsAbstract=$($t.IsAbstract)"
        }
    } catch {}
}
