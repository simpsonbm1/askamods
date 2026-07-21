# Probe spec: confirm the exact registries the locale-audit probe will enumerate, so the mod
# contains no guesses. Need: (1) all ItemInfo, (2) all creature datasheets, (3) live structures.
$base = "D:\SteamLibrary\steamapps\common\ASKA\BepInEx"
[void][System.Reflection.Assembly]::LoadFrom("d:\Claude Projects\askamods\_explore\bin\Debug\net10.0\Mono.Cecil.dll")

$allTypes = New-Object System.Collections.Generic.List[object]
foreach ($f in Get-ChildItem "$base\interop\*.dll") {
    try { $asm = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($f.FullName) } catch { continue }
    foreach ($t in $asm.MainModule.Types) {
        $allTypes.Add($t); foreach ($n in $t.NestedTypes) { $allTypes.Add($n) }
    }
}
$byName = @{}
foreach ($t in $allTypes) { if (-not $byName.ContainsKey($t.FullName)) { $byName[$t.FullName] = $t } }

Write-Host "=========== 1. ItemInfo static registry ==========="
$t = $byName["SandSailorStudio.Inventory.ItemInfo"]
foreach ($p in $t.Properties) {
    if ($p.Name -match "^s_|List|Database|All") {
        $g = $p.GetMethod
        Write-Host ("  {0} : {1}   static={2}" -f $p.Name, $p.PropertyType.FullName, $(if($g){$g.IsStatic}else{"?"}))
    }
}
foreach ($m in $t.Methods) { if ($m.Name -match "GetAll|GetItem|FromId|ById") { Write-Host "  METHOD $($m.Name) static=$($m.IsStatic) ret=$($m.ReturnType.Name)" } }

Write-Host ""
Write-Host "=========== 2. Item database manager ==========="
foreach ($tn in @("SSSGame.ItemDatabaseManager","SandSailorStudio.Inventory.ItemDatabase","SandSailorStudio.Inventory.BaseItemsConfig")) {
    $d = $byName[$tn]
    if (-not $d) { Write-Host "  NO TYPE $tn"; continue }
    Write-Host "  ### $tn"
    foreach ($p in $d.Properties) { Write-Host ("      {0} : {1}" -f $p.Name, $p.PropertyType.Name) }
    foreach ($m in $d.Methods) { if ($m.Name -notmatch "^(get_|set_|\.ctor|\.cctor)") { Write-Host "      M {0}({1}) : {2}" -f $m.Name, (($m.Parameters | ForEach-Object { $_.ParameterType.Name }) -join ","), $m.ReturnType.Name } }
}

Write-Host ""
Write-Host "=========== 3. CreatureDataSheet + any registry ==========="
$c = $byName["SSSGame.CreatureDataSheet"]
if ($c) { foreach ($p in $c.Properties) { Write-Host ("  CreatureDataSheet.{0} : {1}" -f $p.Name, $p.PropertyType.Name) } }
Write-Host "  --- types holding a CreatureDataSheet collection ---"
foreach ($t2 in $allTypes) {
    if ($t2.FullName -match "<|MethodInfoStore") { continue }
    foreach ($p in $t2.Properties) {
        if ($p.PropertyType.FullName -match "CreatureDataSheet" -and $p.PropertyType.FullName -match "List|Array") {
            Write-Host ("    {0}.{1} : {2}" -f $t2.FullName, $p.Name, $p.PropertyType.Name)
        }
    }
}

Write-Host ""
Write-Host "=========== 4. Structure enumeration (no FindObjectsByType!) ==========="
foreach ($tn in @("SSSGame.Settlement","SSSGame.SettlementManager")) {
    $s = $byName[$tn]
    if (-not $s) { Write-Host "  NO TYPE $tn"; continue }
    Write-Host "  ### $tn"
    foreach ($p in $s.Properties) { if ($p.PropertyType.FullName -match "Structure|List") { Write-Host ("      {0} : {1}" -f $p.Name, $p.PropertyType.FullName) } }
    foreach ($m in $s.Methods) { if ($m.Name -match "Structure|Building") { Write-Host ("      M {0} : {1}" -f $m.Name, $m.ReturnType.Name) } }
}
