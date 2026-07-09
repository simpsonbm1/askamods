param([string]$MethodName = "OnPointerClick")
$base = "D:\SteamLibrary\steamapps\common\ASKA\BepInEx"
$dirs = @("$base\interop", "$base\core", "$base\unity-libs")
[void][System.Reflection.Assembly]::LoadFrom("d:\Claude Projects\askamods\_explore\bin\Debug\net10.0\Mono.Cecil.dll")
$resolver = New-Object Mono.Cecil.DefaultAssemblyResolver
foreach ($d in $dirs) { $resolver.AddSearchDirectory($d) }
$rp = New-Object Mono.Cecil.ReaderParameters
$rp.AssemblyResolver = $resolver
$asm = [Mono.Cecil.AssemblyDefinition]::ReadAssembly("$base\interop\Assembly-CSharp.dll", $rp)
foreach ($t in $asm.MainModule.Types) {
    $types = @($t) + @($t.NestedTypes)
    foreach ($ty in $types) {
        foreach ($m in $ty.Methods) {
            if ($m.Name -eq $MethodName) {
                Write-Output "$($ty.FullName) (base=$($ty.BaseType.Name))"
            }
        }
    }
}
