# CONTROL for the "Il2CppInterop models no interface implementations" claim.
# Three interfaces returned zero implementors. Is that specific to them, or global?
$base = "D:\SteamLibrary\steamapps\common\ASKA\BepInEx"
[void][System.Reflection.Assembly]::LoadFrom("d:\Claude Projects\askamods\_explore\bin\Debug\net10.0\Mono.Cecil.dll")
$total = 0; $withIface = 0
$ifaceCounts = @{}
foreach ($f in Get-ChildItem "$base\interop\*.dll") {
    try { $asm = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($f.FullName) } catch { continue }
    foreach ($t in $asm.MainModule.Types) {
        $total++
        if ($t.Interfaces.Count -gt 0) {
            $withIface++
            foreach ($i in $t.Interfaces) {
                $k = $i.InterfaceType.Name
                if (-not $ifaceCounts.ContainsKey($k)) { $ifaceCounts[$k] = 0 }
                $ifaceCounts[$k] = $ifaceCounts[$k] + 1
            }
        }
        foreach ($n in $t.NestedTypes) {
            $total++
            if ($n.Interfaces.Count -gt 0) { $withIface++ }
        }
    }
}
Write-Host ("types scanned: {0}" -f $total)
Write-Host ("types declaring >=1 interface: {0}" -f $withIface)
Write-Host "top interfaces by implementor count:"
foreach ($k in ($ifaceCounts.GetEnumerator() | Sort-Object -Property Value -Descending | Select-Object -First 15)) {
    Write-Host ("   {0}  x{1}" -f $k.Key, $k.Value)
}
