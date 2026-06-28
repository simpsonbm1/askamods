[void][System.Reflection.Assembly]::LoadFrom('d:\Claude Projects\askamods\_explore\bin\Debug\net10.0\Mono.Cecil.dll');
$rp = New-Object Mono.Cecil.ReaderParameters;
$resolver = New-Object Mono.Cecil.DefaultAssemblyResolver;
$dirs = @('D:\SteamLibrary\steamapps\common\ASKA\BepInEx\interop', 'D:\SteamLibrary\steamapps\common\ASKA\BepInEx\core', 'D:\SteamLibrary\steamapps\common\ASKA\BepInEx\unity-libs');
foreach ($d in $dirs) { $resolver.AddSearchDirectory($d) };
$rp.AssemblyResolver = $resolver;
$asm = [Mono.Cecil.AssemblyDefinition]::ReadAssembly('D:\SteamLibrary\steamapps\common\ASKA\BepInEx\interop\SandSailorStudio.dll', $rp);
foreach ($t in $asm.MainModule.Types) {
    if ($t.Name -eq 'LootSpawner') {
        foreach ($nt in $t.NestedTypes) {
            if ($nt.Name -eq 'LootData') {
                foreach ($p in $nt.Properties) { Write-Output "$($p.PropertyType.FullName) $($p.Name)" }
            }
        }
    }
}
