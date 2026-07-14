[void][System.Reflection.Assembly]::LoadFrom("d:\Claude Projects\askamods\_explore\bin\Debug\net10.0\Mono.Cecil.dll")
$asm = [Mono.Cecil.AssemblyDefinition]::ReadAssembly("D:\SteamLibrary\steamapps\common\ASKA\BepInEx\interop\SandSailorStudio.dll")
foreach ($t in $asm.MainModule.Types) {
    if ($t.Name -eq "ItemInfoQuantity") {
        Write-Output "TYPE: $($t.FullName)"
        foreach ($p in $t.Properties) {
            $getAcc = if ($p.GetMethod) { $p.GetMethod.IsPublic } else { $null }
            $setAcc = if ($p.SetMethod) { $p.SetMethod.IsPublic } else { "no-setter" }
            Write-Output "  P: $($p.Name) : $($p.PropertyType.FullName) getPublic=$getAcc setPublic=$setAcc"
        }
        foreach ($f in $t.Fields) { Write-Output "  F: $($f.Name) : $($f.FieldType.FullName) public=$($f.IsPublic)" }
    }
}
$asm2 = [Mono.Cecil.AssemblyDefinition]::ReadAssembly("D:\SteamLibrary\steamapps\common\ASKA\BepInEx\interop\Assembly-CSharp.dll")
foreach ($t in $asm2.MainModule.Types) {
    if ($t.Name -match "^NetworkWorkstation") {
        Write-Output "TYPE: $($t.FullName) base=$($t.BaseType.FullName)"
        foreach ($m in $t.Methods) {
            if ($m.Name -match "HostUpdateTasks") { Write-Output "  M: $($m.ReturnType.Name) $($m.Name)() virtual=$($m.IsVirtual) abstract=$($m.IsAbstract)" }
        }
    }
}
