[void][System.Reflection.Assembly]::LoadFrom('d:\Claude Projects\askamods\_explore\bin\Debug\net10.0\Mono.Cecil.dll');
$rp = New-Object Mono.Cecil.ReaderParameters;
$resolver = New-Object Mono.Cecil.DefaultAssemblyResolver;
$dirs = @('D:\SteamLibrary\steamapps\common\ASKA\BepInEx\interop', 'D:\SteamLibrary\steamapps\common\ASKA\BepInEx\core', 'D:\SteamLibrary\steamapps\common\ASKA\BepInEx\unity-libs');
foreach ($d in $dirs) { $resolver.AddSearchDirectory($d) };
$rp.AssemblyResolver = $resolver;
$asm = [Mono.Cecil.AssemblyDefinition]::ReadAssembly('D:\SteamLibrary\steamapps\common\ASKA\BepInEx\interop\Assembly-CSharp.dll', $rp);
foreach ($t in $asm.MainModule.Types) {
    if ($t.Name -eq 'HarvestSpawner') {
        foreach ($nt in $t.NestedTypes) {
            if ($nt.Name -eq 'Loot') {
                Write-Output "FIELDS:";
                foreach ($f in $nt.Fields) { Write-Output "$($f.FieldType.Name) $($f.Name)" }
                Write-Output "METHODS:";
                foreach ($m in $nt.Methods) { Write-Output "$($m.ReturnType.Name) $($m.Name)" }
            }
        }
    }
}
