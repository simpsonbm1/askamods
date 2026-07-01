# Map GameAssembly.dll fault offsets (RVAs from WER) to method names using Cpp2IL dummy DLLs.
$dumpDir = "C:\Users\Ben\AppData\Local\Temp\claude\d--Claude-Projects-askamods\302d34da-0642-4905-bd57-deede562e1f7\scratchpad\cpp2il_out"
Add-Type -Path "D:\SteamLibrary\steamapps\common\ASKA\BepInEx\core\Mono.Cecil.dll"

# Crash offsets from Windows Error Reporting (Application Error events)
$targets = @(0xf975f2, 0xf98fff, 0x1236e2a, 0x107000a, 0x5b1929, 0x101ffff)

$all = New-Object System.Collections.Generic.List[object]
foreach ($dll in Get-ChildItem "$dumpDir\*.dll") {
    try { $asm = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($dll.FullName) } catch { continue }
    foreach ($t in $asm.MainModule.GetTypes()) {
        foreach ($m in $t.Methods) {
            foreach ($ca in $m.CustomAttributes) {
                if ($ca.AttributeType.Name -ne "AddressAttribute") { continue }
                foreach ($f in $ca.Fields) {
                    if ($f.Name -eq "RVA") {
                        $rva = [Convert]::ToInt64(($f.Argument.Value -replace "0x",""), 16)
                        $all.Add([PSCustomObject]@{ RVA = $rva; Method = "$($t.FullName).$($m.Name)"; Asm = $dll.Name })
                    }
                }
            }
        }
    }
    $asm.Dispose()
}
Write-Host "Indexed $($all.Count) method RVAs"
$sorted = $all | Sort-Object RVA
$rvas = @($sorted | ForEach-Object { $_.RVA })

foreach ($tgt in $targets) {
    # binary search: last method whose start RVA <= target
    $lo = 0; $hi = $rvas.Count - 1; $best = -1
    while ($lo -le $hi) {
        $mid = [int](($lo + $hi) / 2)
        if ($rvas[$mid] -le $tgt) { $best = $mid; $lo = $mid + 1 } else { $hi = $mid - 1 }
    }
    if ($best -ge 0) {
        $hit = $sorted[$best]
        $next = if ($best + 1 -lt $sorted.Count) { $sorted[$best + 1] } else { $null }
        $into = $tgt - $hit.RVA
        $nextInfo = if ($next) { "next method starts +0x{0:x} ({1})" -f ($next.RVA - $tgt), $next.Method } else { "last method" }
        Write-Host ("0x{0:x} -> {1} [{2}] (+0x{3:x} into method; {4})" -f $tgt, $hit.Method, $hit.Asm, $into, $nextInfo)
    } else {
        Write-Host ("0x{0:x} -> BEFORE first method RVA" -f $tgt)
    }
}
