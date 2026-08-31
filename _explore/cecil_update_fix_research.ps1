# 2026-08-31 update: member shapes needed for the three published-mod fixes.
$ErrorActionPreference = 'Stop'
Add-Type -Path "D:\SteamLibrary\steamapps\common\ASKA\BepInEx\core\Mono.Cecil.dll"
$interopDir = "D:\SteamLibrary\steamapps\common\ASKA\BepInEx\interop"

function Dump-Type($asmFile, $typeName) {
    $a = [Mono.Cecil.AssemblyDefinition]::ReadAssembly((Join-Path $interopDir $asmFile))
    $stack = New-Object System.Collections.Stack
    foreach ($t in $a.MainModule.Types) { $stack.Push($t) }
    while ($stack.Count -gt 0) {
        $t = $stack.Pop()
        foreach ($n in $t.NestedTypes) { $stack.Push($n) }
        if ($t.Name -ne $typeName) { continue }
        Write-Host ""
        Write-Host "===== $($t.FullName)  [$asmFile] ====="
        Write-Host "-- properties --"
        foreach ($p in $t.Properties) { Write-Host ("  {0} {1}" -f $p.PropertyType.Name, $p.Name) }
        Write-Host "-- fields --"
        foreach ($f in $t.Fields) { Write-Host ("  {0} {1}" -f $f.FieldType.Name, $f.Name) }
        Write-Host "-- methods --"
        foreach ($m in $t.Methods) {
            $params = ($m.Parameters | ForEach-Object { "$($_.ParameterType.Name) $($_.Name)" }) -join ", "
            Write-Host ("  {0} {1}({2})" -f $m.ReturnType.Name, $m.Name, $params)
        }
    }
}

Dump-Type "SandSailorStudio.dll" "AvailabilityProcess"

# PopulationManager: only fishing-related members, it is huge
$a = [Mono.Cecil.AssemblyDefinition]::ReadAssembly((Join-Path $interopDir "Assembly-CSharp.dll"))
foreach ($t in $a.MainModule.Types) {
    if ($t.Name -eq "PopulationManager") {
        Write-Host ""
        Write-Host "===== $($t.FullName): members matching /[Ff]ish/ ====="
        foreach ($p in $t.Properties) { if ($p.Name -match "ish") { Write-Host ("  prop {0} {1}" -f $p.PropertyType.Name, $p.Name) } }
        foreach ($f in $t.Fields)     { if ($f.Name -match "ish") { Write-Host ("  fld  {0} {1}" -f $f.FieldType.Name, $f.Name) } }
        foreach ($m in $t.Methods) {
            if ($m.Name -match "ish") {
                $params = ($m.Parameters | ForEach-Object { "$($_.ParameterType.Name) $($_.Name)" }) -join ", "
                Write-Host ("  mth  {0} {1}({2})" -f $m.ReturnType.Name, $m.Name, $params)
            }
        }
    }
}
