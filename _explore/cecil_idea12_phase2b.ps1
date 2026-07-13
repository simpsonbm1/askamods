# Idea-12 Phase 2 probe (b): ResourceStorageTaskData surface + who constructs/reads it.
$ErrorActionPreference = "Stop"
[void][System.Reflection.Assembly]::LoadFrom("d:\Claude Projects\askamods\_explore\bin\Debug\net10.0\Mono.Cecil.dll")

$allTypes = New-Object System.Collections.Generic.List[object]
foreach ($dll in @("Assembly-CSharp.dll", "SandSailorStudio.dll")) {
    $p = "D:\SteamLibrary\steamapps\common\ASKA\BepInEx\interop\$dll"
    if (-not (Test-Path $p)) { Write-Output "SKIP missing $dll"; continue }
    $asm = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($p)
    foreach ($t in $asm.MainModule.Types) {
        $allTypes.Add($t)
        foreach ($n in $t.NestedTypes) { $allTypes.Add($n) }
    }
}

function Sig($m) {
    $ps = ($m.Parameters | ForEach-Object { "$($_.ParameterType.Name) $($_.Name)" }) -join ", "
    $flags = @()
    if ($m.IsVirtual) { $flags += "virtual" }
    if ($m.IsStatic) { $flags += "static" }
    $fs = if ($flags) { " [" + ($flags -join ",") + "]" } else { "" }
    "$($m.ReturnType.Name) $($m.Name)($ps)$fs"
}

foreach ($tn in @("ResourceStorageTaskData", "WorkstationTaskData")) {
    foreach ($t in ($allTypes | Where-Object { $_.Name -eq $tn })) {
        $base = if ($t.BaseType) { $t.BaseType.FullName } else { "none" }
        Write-Output "TYPE: $($t.FullName) base=$base"
        foreach ($f in $t.Fields) { if ($f.Name -notmatch "^Native") { Write-Output "  F: $($f.Name) : $($f.FieldType.FullName)" } }
        foreach ($p in $t.Properties) { Write-Output "  P: $($p.Name) : $($p.PropertyType.FullName)" }
        foreach ($m in $t.Methods) {
            if ($m.Name -match "^get_|^set_") { continue }
            Write-Output "  M: $(Sig $m)"
        }
        Write-Output ""
    }
}

# NetworkResourceStorage / NetworkWorkstation concrete types serving ResourceStorage
Write-Output "===== Network types with 'ResourceStorage' or 'Storage' in the name ====="
foreach ($t in ($allTypes | Where-Object { $_.Name -match "Network" -and $_.Name -match "Storage" })) {
    $base = if ($t.BaseType) { $t.BaseType.FullName } else { "none" }
    Write-Output "TYPE: $($t.FullName) base=$base"
    foreach ($m in $t.Methods) {
        if ($m.Name -match "^get_|^set_") { continue }
        if ($m.Name -match "Rpc_|Task|HostUpdate") { Write-Output "  M: $(Sig $m)" }
    }
    Write-Output ""
}
