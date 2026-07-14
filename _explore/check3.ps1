[void][System.Reflection.Assembly]::LoadFrom("d:\Claude Projects\askamods\_explore\bin\Debug\net10.0\Mono.Cecil.dll")
foreach ($dll in @("Assembly-CSharp.dll","SandSailorStudio.dll")) {
$asm = [Mono.Cecil.AssemblyDefinition]::ReadAssembly("D:\SteamLibrary\steamapps\common\ASKA\BepInEx\interop\$dll")
foreach ($t in $asm.MainModule.Types) {
    foreach ($m in $t.Methods) {
        if ($m.Name -match "Rpc_ChangeTaskQuantity" -or $m.Name -match "ChangeTaskQuantity") {
            Write-Output "$($t.FullName)  M: $($m.Name)"
        }
    }
}
}
