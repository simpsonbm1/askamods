# Map GameAssembly.dll stack RVAs from the reload-crash dump to method names via Cpp2IL dummy DLLs.
$dumpDir = "C:\Users\Ben\AppData\Local\Temp\claude\d--Claude-Projects-askamods\302d34da-0642-4905-bd57-deede562e1f7\scratchpad\cpp2il_out"
Add-Type -Path "D:\SteamLibrary\steamapps\common\ASKA\BepInEx\core\Mono.Cecil.dll"

# GameAssembly RVAs seen on the faulting thread's stack (Aska.exe.2708.dmp), nearest-RSP first
$targets = @(0x13e89d1, 0x6242f78, 0x6201a64, 0x617ec88, 0x61a8f88, 0x61a8f4c, 0x13e896f,
             0x623e3d, 0x1c0ddb0, 0x1c034e4, 0x5243d, 0x2b167d2, 0x2a739dd, 0x2a10ee2,
             0x2a9c2a2, 0xddd790, 0x5ddeae, 0xddd520, 0x3d615f, 0x5a9361, 0x5a8d79,
             0x5a92c9, 0x15aeb35, 0x1d6f975, 0x15a6c90)

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
    $lo = 0; $hi = $rvas.Count - 1; $best = -1
    while ($lo -le $hi) {
        $mid = [int](($lo + $hi) / 2)
        if ($rvas[$mid] -le $tgt) { $best = $mid; $lo = $mid + 1 } else { $hi = $mid - 1 }
    }
    if ($best -ge 0) {
        $hit = $sorted[$best]
        $next = if ($best + 1 -lt $sorted.Count) { $sorted[$best + 1] } else { $null }
        $into = $tgt - $hit.RVA
        $nextInfo = if ($next) { "next +0x{0:x} ({1})" -f ($next.RVA - $tgt), $next.Method } else { "last method" }
        Write-Host ("0x{0:x} -> {1} [{2}] (+0x{3:x} in; {4})" -f $tgt, $hit.Method, $hit.Asm, $into, $nextInfo)
    } else {
        Write-Host ("0x{0:x} -> BEFORE first method RVA (il2cpp runtime internals)" -f $tgt)
    }
}
