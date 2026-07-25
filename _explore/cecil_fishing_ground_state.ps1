# Fishing-ground respawn (#0050) - step 0, static pre-work.
#
# The item's step 1 is a read-only IN-GAME probe: fish a ground dry, dump every entry in
# PopulationManager._fishingGrounds before and after, and see which field moved. That probe has to
# know WHICH fields to dump. This script names them, so the play session costs one run instead of
# several. It answers nothing about behaviour - only "what state does a fishing ground carry, and
# what calls can change it".
#
# ASCII only, explicit loops (PowerShell 5.1).
$base = "D:\SteamLibrary\steamapps\common\ASKA\BepInEx"
[void][System.Reflection.Assembly]::LoadFrom("d:\Claude Projects\askamods\_explore\bin\Debug\net10.0\Mono.Cecil.dll")

$allTypes = New-Object System.Collections.Generic.List[object]
foreach ($f in Get-ChildItem "$base\interop\*.dll") {
    try { $asm = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($f.FullName) } catch { continue }
    foreach ($t in $asm.MainModule.Types) {
        $allTypes.Add($t)
        foreach ($n in $t.NestedTypes) { $allTypes.Add($n) }
    }
}
$byName = @{}
foreach ($t in $allTypes) { if (-not $byName.ContainsKey($t.FullName)) { $byName[$t.FullName] = $t } }
Write-Host ("loaded {0} types" -f $allTypes.Count)

function Format-Sig($m) {
    $ps = @()
    foreach ($p in $m.Parameters) { $ps += ("{0} {1}" -f $p.ParameterType.Name, $p.Name) }
    return ("{0}({1}) : {2}" -f $m.Name, [string]::Join(", ", $ps), $m.ReturnType.Name)
}

Write-Host ""
Write-Host "=========== 1. Anything named FishingGround (find the real type name) ==========="
foreach ($t in ($allTypes | Sort-Object FullName)) {
    if ($t.FullName -match "<") { continue }
    if ($t.FullName -notmatch "Fishing|FishGround") { continue }
    $bt = ""
    if ($t.BaseType) { $bt = $t.BaseType.FullName }
    Write-Host ("  {0}   base={1}" -f $t.FullName, $bt)
}

Write-Host ""
Write-Host "=========== 2. Every member mentioning fishing, anywhere ==========="
Write-Host "    (locates _fishingGrounds and the call surface, whatever type they actually live on)"
foreach ($t in ($allTypes | Sort-Object FullName)) {
    if ($t.FullName -match "<") { continue }
    foreach ($fl in $t.Fields) {
        if ($fl.Name -notmatch "ishing|ishGround") { continue }
        Write-Host ("  FIELD  {0}.{1} : {2}" -f $t.FullName, $fl.Name, $fl.FieldType.FullName)
    }
    foreach ($p in $t.Properties) {
        if ($p.Name -notmatch "ishing|ishGround") { continue }
        Write-Host ("  PROP   {0}.{1} : {2}" -f $t.FullName, $p.Name, $p.PropertyType.FullName)
    }
    foreach ($m in $t.Methods) {
        if ($m.Name -notmatch "ishing|ishGround") { continue }
        Write-Host ("  METHOD {0}.{1}" -f $t.FullName, (Format-Sig $m))
    }
}

Write-Host ""
Write-Host "=========== 3. FULL state surface of the fishing-ground type(s) ==========="
Write-Host "    THIS is the probe's dump list - every field/prop that could encode 'exhausted'."
$groundTypes = @()
foreach ($t in $allTypes) {
    if ($t.FullName -match "<") { continue }
    if ($t.FullName -match "Fishing" -and $t.FullName -notmatch "Manager|Quest|Interaction|Panel|UI\.") {
        $groundTypes += $t
    }
}
foreach ($t in ($groundTypes | Sort-Object FullName)) {
    $bt = ""
    if ($t.BaseType) { $bt = $t.BaseType.FullName }
    Write-Host ""
    Write-Host ("  ### {0}   base={1}" -f $t.FullName, $bt)
    Write-Host "      -- fields --"
    foreach ($fl in ($t.Fields | Sort-Object Name)) {
        $st = ""
        if ($fl.IsStatic) { $st = " [static]" }
        Write-Host ("        {0} : {1}{2}" -f $fl.Name, $fl.FieldType.Name, $st)
    }
    Write-Host "      -- properties --"
    foreach ($p in ($t.Properties | Sort-Object Name)) {
        Write-Host ("        {0} : {1}" -f $p.Name, $p.PropertyType.Name)
    }
    Write-Host "      -- methods --"
    foreach ($m in ($t.Methods | Sort-Object Name)) {
        if ($m.Name -match "^get_") { continue }
        if ($m.Name -match "^set_") { continue }
        if ($m.Name -match "^\.") { continue }
        Write-Host ("        {0}" -f (Format-Sig $m))
    }
}

Write-Host ""
Write-Host "=========== 4. PopulationManager surface (holds the registry; den work used it too) ==========="
foreach ($tn in @("SSSGame.PopulationManager","SSSGame.Network.NetworkPopulationManager")) {
    $t = $byName[$tn]
    if ($null -eq $t) { Write-Host ("  NO TYPE {0}" -f $tn); continue }
    Write-Host ""
    Write-Host ("  ### {0}" -f $tn)
    foreach ($fl in ($t.Fields | Sort-Object Name)) {
        Write-Host ("        F {0} : {1}" -f $fl.Name, $fl.FieldType.Name)
    }
    foreach ($m in ($t.Methods | Sort-Object Name)) {
        if ($m.Name -match "^get_") { continue }
        if ($m.Name -match "^set_") { continue }
        if ($m.Name -match "^\.") { continue }
        $mark = "   "
        if ($m.Name -match "^Rpc_") { $mark = ">> " }
        if ($m.Name -match "^Request") { $mark = ">> " }
        Write-Host ("      {0}M {1}" -f $mark, (Format-Sig $m))
    }
}
