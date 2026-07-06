# Parse a Windows minidump: exception stream + faulting-thread stack scan against module ranges.
param(
    [Parameter(Mandatory=$true)][string]$DumpPath,
    [int]$MaxHits = 120
)

$bytes = [System.IO.File]::ReadAllBytes($DumpPath)
Write-Host ("Dump: {0} ({1:N0} bytes)" -f $DumpPath, $bytes.Length)

function U32([byte[]]$b, [long]$o) { [BitConverter]::ToUInt32($b, $o) }
function U64([byte[]]$b, [long]$o) { [BitConverter]::ToUInt64($b, $o) }

if ([BitConverter]::ToUInt32($bytes, 0) -ne 0x504D444D) { throw "Not a minidump (bad magic)" }
$numStreams = U32 $bytes 8
$dirRva     = U32 $bytes 12
Write-Host "Streams: $numStreams"

$streams = @{}
for ($i = 0; $i -lt $numStreams; $i++) {
    $off = $dirRva + 12 * $i
    $type = [int](U32 $bytes $off)
    $size = U32 $bytes ($off + 4)
    $rva  = U32 $bytes ($off + 8)
    if (-not $streams.ContainsKey($type)) { $streams[$type] = @{ Size = $size; Rva = $rva } }
}

# ---- Modules (type 4) ----
$modules = New-Object System.Collections.Generic.List[object]
if ($streams.ContainsKey(4)) {
    $mo = [long]$streams[4].Rva
    $n = U32 $bytes $mo
    for ($i = 0; $i -lt $n; $i++) {
        $e = $mo + 4 + 108 * $i
        $base = U64 $bytes $e
        $size = U32 $bytes ($e + 8)
        $nameRva = U32 $bytes ($e + 20)
        $nameLen = U32 $bytes $nameRva
        $name = [System.Text.Encoding]::Unicode.GetString($bytes, $nameRva + 4, $nameLen)
        $modules.Add([PSCustomObject]@{ Base = $base; End = $base + $size; Name = [System.IO.Path]::GetFileName($name) })
    }
    Write-Host "Modules: $($modules.Count)"
}

function Resolve-Addr([uint64]$addr) {
    foreach ($m in $modules) {
        if ($addr -ge $m.Base -and $addr -lt $m.End) {
            return ("{0}+0x{1:x}" -f $m.Name, ($addr - $m.Base))
        }
    }
    return $null
}

# ---- Exception (type 6) ----
$excThreadId = 0
$rip = [uint64]0; $rsp = [uint64]0
if ($streams.ContainsKey(6)) {
    $eo = [long]$streams[6].Rva
    $excThreadId = U32 $bytes $eo
    $code = U32 $bytes ($eo + 8)
    $excAddr = U64 $bytes ($eo + 24)
    $ctxSize = U32 $bytes ($eo + 160)
    $ctxRva  = U32 $bytes ($eo + 164)
    $rsp = U64 $bytes ($ctxRva + 0x98)
    $rip = U64 $bytes ($ctxRva + 0xF8)
    Write-Host ""
    Write-Host ("EXCEPTION thread=0x{0:x} code=0x{1:x8}" -f $excThreadId, $code)
    Write-Host ("  ExceptionAddress = 0x{0:x}  -> {1}" -f $excAddr, (Resolve-Addr $excAddr))
    Write-Host ("  RIP = 0x{0:x}  -> {1}" -f $rip, (Resolve-Addr $rip))
    Write-Host ("  RSP = 0x{0:x}" -f $rsp)
    # exception parameters (for AV: [0]=read(0)/write(1)/dep(8), [1]=target address)
    $numParams = U32 $bytes ($eo + 32)
    if ($numParams -ge 2) {
        $p0 = U64 $bytes ($eo + 40)
        $p1 = U64 $bytes ($eo + 48)
        $kind = @{ 0 = 'READ'; 1 = 'WRITE'; 8 = 'EXECUTE' }[[int]$p0]
        Write-Host ("  AccessViolation: {0} of address 0x{1:x}" -f $kind, $p1)
    }
} else { throw "No exception stream" }

# ---- Threads (type 3): find faulting thread's stack ----
$stackStart = [uint64]0; $stackSize = 0; $stackRva = 0
if ($streams.ContainsKey(3)) {
    $to = [long]$streams[3].Rva
    $tn = U32 $bytes $to
    for ($i = 0; $i -lt $tn; $i++) {
        $e = $to + 4 + 48 * $i
        $tid = U32 $bytes $e
        if ($tid -eq $excThreadId) {
            $stackStart = U64 $bytes ($e + 24)
            $stackSize  = U32 $bytes ($e + 32)
            $stackRva   = U32 $bytes ($e + 36)
            break
        }
    }
}
if ($stackSize -eq 0) { throw "Faulting thread stack not found" }
Write-Host ""
Write-Host ("Stack memory: start=0x{0:x} size=0x{1:x}" -f $stackStart, $stackSize)

# ---- Scan stack from RSP upward for addresses inside modules ----
$fromOff = 0
if ($rsp -ge $stackStart -and $rsp -lt ($stackStart + $stackSize)) { $fromOff = [long]($rsp - $stackStart) }
Write-Host ("Scanning stack from RSP (offset 0x{0:x} into stack region):" -f $fromOff)
Write-Host ""
$hits = 0
for ($o = $fromOff; $o -le $stackSize - 8; $o += 8) {
    $val = U64 $bytes ($stackRva + $o)
    $res = Resolve-Addr $val
    if ($res) {
        Write-Host ("  rsp+0x{0:x6}: 0x{1:x} {2}" -f ($o - $fromOff), $val, $res)
        $hits++
        if ($hits -ge $MaxHits) { Write-Host "  ... (hit cap)"; break }
    }
}
Write-Host ""
Write-Host "Done. $hits module-range values on stack (candidate return addresses; nearest RSP = most recent)."
