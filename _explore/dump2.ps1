[void][System.Reflection.Assembly]::LoadFrom('d:\Claude Projects\askamods\_explore\bin\Debug\net10.0\Mono.Cecil.dll');
$resolver = New-Object Mono.Cecil.DefaultAssemblyResolver;
$dirs = @('D:\SteamLibrary\steamapps\common\ASKA\BepInEx\interop', 'D:\SteamLibrary\steamapps\common\ASKA\BepInEx\core', 'D:\SteamLibrary\steamapps\common\ASKA\BepInEx\unity-libs');
foreach ($d in $dirs) { $resolver.AddSearchDirectory($d) };
$rp = New-Object Mono.Cecil.ReaderParameters;
$rp.AssemblyResolver = $resolver;
$asm = [Mono.Cecil.AssemblyDefinition]::ReadAssembly('D:\SteamLibrary\steamapps\common\ASKA\BepInEx\interop\Assembly-CSharp.dll', $rp);
$allTypes = New-Object System.Collections.Generic.List[object];
foreach ($t in $asm.MainModule.Types) {
    $allTypes.Add($t);
    foreach ($nt in $t.NestedTypes) { $allTypes.Add($nt); }
}

$types = @('HarvestSpawner', 'LootSpawner', 'HarvestInteraction', 'LootSpawnData', 'LootData', 'LootTable', 'ResourceItem', 'ResourcePieces', 'LootSpawnDrop', 'ItemSpawner', 'ItemDrop');
foreach ($tname in $types) {
    $t = $allTypes | Where-Object { $_.Name -eq $tname };
    if ($t) {
        Write-Output "`n--- $($t.FullName) ---";
        Write-Output "FIELDS:";
        foreach ($f in $t.Fields) { Write-Output "  $($f.FieldType.Name) $($f.Name)" };
        Write-Output "METHODS:";
        foreach ($m in $t.Methods) { 
            $ps = ($m.Parameters | ForEach-Object { $_.ParameterType.Name }) -join ", ";
            Write-Output "  $($m.ReturnType.Name) $($m.Name)($ps)" 
        };
    }
}
