$base = "D:\SteamLibrary\steamapps\common\ASKA\BepInEx"
$dirs = @("$base\interop", "$base\core", "$base\unity-libs")
[void][System.Reflection.Assembly]::LoadFrom("d:\Claude Projects\askamods\_explore\bin\Debug\net10.0\Mono.Cecil.dll")
$resolver = New-Object Mono.Cecil.DefaultAssemblyResolver
foreach ($d in $dirs) { $resolver.AddSearchDirectory($d) }
$rp = New-Object Mono.Cecil.ReaderParameters
$rp.AssemblyResolver = $resolver
foreach ($f in Get-ChildItem "$base\interop\*.dll") {
    try {
        $asm = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($f.FullName, $rp)
        $t = $asm.MainModule.Types | Where-Object { $_.FullName -eq "SandSailorStudio.Inventory.ItemContainer" }
        if ($t) {
            Write-Output "Found in $($f.Name)"
            foreach ($m in ($t.Methods | Where-Object { $_.Name -eq "GetItems" })) {
                Write-Output "M GetItems() : $($m.ReturnType.FullName)"
            }
        }
    } catch {}
}
