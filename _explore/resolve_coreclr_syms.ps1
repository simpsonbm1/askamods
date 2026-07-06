# Resolve coreclr.dll offsets to symbol names: parse PE debug dir -> download PDB from msdl -> dbghelp SymFromAddr.
$ErrorActionPreference = 'Stop'
$dll = "D:\SteamLibrary\steamapps\common\ASKA\dotnet\coreclr.dll"
$workDir = "C:\Users\Ben\AppData\Local\Temp\claude\d--Claude-Projects-askamods\1d2a05c9-3f36-4baa-ab70-8ee91c8dec02\scratchpad\syms"
if (-not (Test-Path $workDir)) { New-Item -ItemType Directory -Force $workDir | Out-Null }

$offsets = @(0x1d1fdd, 0x25c713, 0x1d1ffc, 0x40ca68, 0x155b15, 0x11f66a, 0xf7212, 0x1385e2, 0x14a813, 0x10b8c5, 0x15890f)

# ---- 1. Parse PE debug directory for RSDS GUID+age ----
$b = [System.IO.File]::ReadAllBytes($dll)
$peOff = [BitConverter]::ToInt32($b, 0x3C)
$numSections = [BitConverter]::ToUInt16($b, $peOff + 6)
$optSize = [BitConverter]::ToUInt16($b, $peOff + 20)
$optOff = $peOff + 24
$magic = [BitConverter]::ToUInt16($b, $optOff)
if ($magic -ne 0x20B) { throw "not PE32+" }
# Debug directory = data directory index 6; PE32+ data dirs start at optOff+112
$dbgRva = [BitConverter]::ToUInt32($b, $optOff + 112 + 6*8)
$dbgSize = [BitConverter]::ToUInt32($b, $optOff + 112 + 6*8 + 4)
# Section table to convert RVA->file offset
$secOff = $optOff + $optSize
function RvaToOff([uint32]$rva) {
    for ($i = 0; $i -lt $numSections; $i++) {
        $s = $secOff + 40*$i
        $va = [BitConverter]::ToUInt32($b, $s + 12)
        $vsz = [BitConverter]::ToUInt32($b, $s + 8)
        $raw = [BitConverter]::ToUInt32($b, $s + 20)
        if ($rva -ge $va -and $rva -lt ($va + $vsz)) { return $raw + ($rva - $va) }
    }
    throw "rva not mapped"
}
$dbgOff = RvaToOff $dbgRva
$guid = $null; $age = 0; $pdbName = $null
for ($i = 0; $i -lt ($dbgSize / 28); $i++) {
    $e = $dbgOff + 28*$i
    $type = [BitConverter]::ToUInt32($b, $e + 12)
    if ($type -ne 2) { continue } # IMAGE_DEBUG_TYPE_CODEVIEW
    $dataOff = [BitConverter]::ToUInt32($b, $e + 24)
    $sig = [System.Text.Encoding]::ASCII.GetString($b, $dataOff, 4)
    if ($sig -ne 'RSDS') { continue }
    $guidBytes = New-Object byte[] 16
    [Array]::Copy($b, $dataOff + 4, $guidBytes, 0, 16)
    $guid = New-Object System.Guid (,$guidBytes)
    $age = [BitConverter]::ToUInt32($b, $dataOff + 20)
    $end = $dataOff + 24
    while ($b[$end] -ne 0) { $end++ }
    $pdbName = [System.Text.Encoding]::ASCII.GetString($b, $dataOff + 24, $end - ($dataOff + 24))
    break
}
if (-not $guid) { throw "no RSDS record" }
$idStr = ($guid.ToString('N').ToUpper()) + $age
Write-Host "PDB: $pdbName  ID: $idStr"

# ---- 2. Download PDB ----
$pdbLeaf = [System.IO.Path]::GetFileName($pdbName)
$pdbPath = Join-Path $workDir $pdbLeaf
if (-not (Test-Path $pdbPath)) {
    $url = "https://msdl.microsoft.com/download/symbols/$pdbLeaf/$idStr/$pdbLeaf"
    Write-Host "Downloading $url"
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    Invoke-WebRequest -Uri $url -OutFile $pdbPath -UseBasicParsing
}
Write-Host ("PDB size: {0:N0} bytes" -f (Get-Item $pdbPath).Length)

# ---- 3. dbghelp SymFromAddr ----
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class DbgHelp {
    [DllImport("dbghelp.dll", SetLastError=true)]
    public static extern bool SymInitialize(IntPtr hProcess, string UserSearchPath, bool fInvadeProcess);
    [DllImport("dbghelp.dll", SetLastError=true)]
    public static extern uint SymSetOptions(uint SymOptions);
    [DllImport("dbghelp.dll", SetLastError=true)]
    public static extern ulong SymLoadModuleEx(IntPtr hProcess, IntPtr hFile, string ImageName, string ModuleName, ulong BaseOfDll, uint DllSize, IntPtr Data, uint Flags);
    [DllImport("dbghelp.dll", SetLastError=true, CharSet=CharSet.Ansi)]
    public static extern bool SymFromAddr(IntPtr hProcess, ulong Address, out ulong Displacement, IntPtr Symbol);
}
'@
$hProc = [IntPtr]::new(4242)
[DbgHelp]::SymSetOptions(0x00000002) | Out-Null  # UNDNAME (no deferred loads - load PDB immediately)
if (-not [DbgHelp]::SymInitialize($hProc, $workDir, $false)) { throw "SymInitialize failed" }
$base = [uint64]0x10000000
$imgSize = [uint32](Get-Item $dll).Length
$mod = [DbgHelp]::SymLoadModuleEx($hProc, [IntPtr]::Zero, $dll, "coreclr", $base, $imgSize, [IntPtr]::Zero, 0)
if ($mod -eq 0) { throw "SymLoadModuleEx failed: $([Runtime.InteropServices.Marshal]::GetLastWin32Error())" }

# SYMBOL_INFO: SizeOfStruct(4)... fixed part 88 bytes, MaxNameLen at offset 80, Name at 84
$maxName = 512
$bufSize = 88 + $maxName
$buf = [Runtime.InteropServices.Marshal]::AllocHGlobal($bufSize)
foreach ($off in $offsets) {
    for ($z = 0; $z -lt $bufSize; $z++) { [Runtime.InteropServices.Marshal]::WriteByte($buf, $z, 0) }
    [Runtime.InteropServices.Marshal]::WriteInt32($buf, 0, 88)        # SizeOfStruct
    [Runtime.InteropServices.Marshal]::WriteInt32($buf, 80, $maxName) # MaxNameLen
    $disp = [uint64]0
    if ([DbgHelp]::SymFromAddr($hProc, $base + $off, [ref]$disp, $buf)) {
        $name = [Runtime.InteropServices.Marshal]::PtrToStringAnsi([IntPtr]::new($buf.ToInt64() + 84))
        Write-Host ("coreclr+0x{0:x} -> {1}+0x{2:x}" -f $off, $name, $disp)
    } else {
        Write-Host ("coreclr+0x{0:x} -> <no symbol> (err {1})" -f $off, [Runtime.InteropServices.Marshal]::GetLastWin32Error())
    }
}
[Runtime.InteropServices.Marshal]::FreeHGlobal($buf)
