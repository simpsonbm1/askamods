# Index a Cpp2IL dummydll dump (attributeinjector) for Ghidra work: every method's RVA -> full
# name as CSV, plus field offsets for the named types. Reusable for any native-code read.
# Usage: powershell -File cpp2il_index.ps1 -DumpDir <cpp2il_out> -OutDir <dir> -Types "A.B","C.D/E"
# ASCII only, explicit loops (PowerShell 5.1).
param(
    [Parameter(Mandatory = $true)][string]$DumpDir,
    [Parameter(Mandatory = $true)][string]$OutDir,
    [string[]]$Types = @()
)
# powershell -File passes "A,B" as one string; split it.
$Types = @($Types | ForEach-Object { $_ -split "," } | ForEach-Object { $_.Trim() } | Where-Object { $_ })
Add-Type -Path "D:\SteamLibrary\steamapps\common\ASKA\BepInEx\core\Mono.Cecil.dll"

function Get-AttrField($ca, $name) {
    foreach ($f in $ca.Fields) { if ($f.Name -eq $name) { return [string]$f.Argument.Value } }
    return $null
}

$rows = New-Object System.Collections.Generic.List[string]
$fieldLines = New-Object System.Collections.Generic.List[string]
foreach ($dll in Get-ChildItem "$DumpDir\*.dll") {
    try { $asm = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($dll.FullName) } catch { continue }
    foreach ($t in $asm.MainModule.GetTypes()) {
        foreach ($m in $t.Methods) {
            foreach ($ca in $m.CustomAttributes) {
                if ($ca.AttributeType.Name -ne "AddressAttribute") { continue }
                $rva = Get-AttrField $ca "RVA"
                if (-not $rva) { continue }
                $ps = @()
                foreach ($p in $m.Parameters) { $ps += $p.ParameterType.Name }
                $rows.Add(("{0},{1}::{2}({3})" -f $rva, $t.FullName, $m.Name, [string]::Join(";", $ps)))
            }
        }
        if ($Types -contains $t.FullName) {
            $bt = ""; if ($t.BaseType) { $bt = $t.BaseType.FullName }
            $fieldLines.Add(("### {0}  base={1}" -f $t.FullName, $bt))
            foreach ($f in $t.Fields) {
                $off = $null
                foreach ($ca in $f.CustomAttributes) {
                    if ($ca.AttributeType.Name -eq "FieldOffsetAttribute") { $off = Get-AttrField $ca "Offset" }
                }
                $st = ""; if ($f.IsStatic) { $st = " [static]" }
                $fieldLines.Add(("   {0}  {1} : {2}{3}" -f $off, $f.Name, $f.FieldType.FullName, $st))
            }
        }
    }
    $asm.Dispose()
}
[System.IO.File]::WriteAllLines((Join-Path $OutDir "rva_index.csv"), $rows)
[System.IO.File]::WriteAllLines((Join-Path $OutDir "field_offsets.txt"), $fieldLines)
Write-Host ("indexed {0} methods; {1} field lines" -f $rows.Count, $fieldLines.Count)
