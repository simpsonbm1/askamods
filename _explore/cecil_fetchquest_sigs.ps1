# Crash triage: CraftFromStorageMod v0.8.0 added patches on CrafterFetchQuest.GetPriority and
# CrafterSpecificFetchQuest.GetPriority. Check those signatures for the documented hazards:
# by-ref primitives, inventory-family parameter types, and which assembly each param type lives in.
$base = "D:\SteamLibrary\steamapps\common\ASKA\BepInEx"
[void][System.Reflection.Assembly]::LoadFrom("d:\Claude Projects\askamods\_explore\bin\Debug\net10.0\Mono.Cecil.dll")

$byName = @{}
foreach ($f in Get-ChildItem "$base\interop\*.dll") {
    try { $asm = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($f.FullName) } catch { continue }
    foreach ($t in $asm.MainModule.Types) {
        if (-not $byName.ContainsKey($t.FullName)) { $byName[$t.FullName] = @{ T = $t; Asm = $f.Name } }
        foreach ($n in $t.NestedTypes) { if (-not $byName.ContainsKey($n.FullName)) { $byName[$n.FullName] = @{ T = $n; Asm = $f.Name } } }
    }
}

function Where-Does-Type-Live($typeRef) {
    $n = $typeRef.FullName -replace '&$','' -replace '\[\]$',''
    if ($byName.ContainsKey($n)) { return $byName[$n].Asm }
    $scope = $typeRef.Scope
    if ($scope) { return "scope:$($scope.Name)" }
    return "?"
}

foreach ($tn in @(
    "SSSGame.CrafterFetchQuest",
    "SSSGame.CrafterSpecificFetchQuest",
    "SSSGame.CrafterFetchQuest/CrafterFetchQuestData")) {
    $e = $byName[$tn]
    if (-not $e) { Write-Host "### NO TYPE $tn`n"; continue }
    $t = $e.T
    Write-Host "### $($t.FullName)   [in $($e.Asm)]  base=$($t.BaseType)"
    foreach ($m in $t.Methods) {
        if ($m.Name -notmatch "GetPriority|IsWhitelistedByStorage") { continue }
        Write-Host "  $($m.ReturnType.Name) $($m.Name)  virtual=$($m.IsVirtual)"
        foreach ($p in $m.Parameters) {
            $flag = ""
            if ($p.ParameterType.IsByReference) { $flag = "  <<< BY-REF" }
            if ($p.ParameterType.FullName -match "Item|ItemCollection|ItemEventContext") { $flag += "  <<< INVENTORY-FAMILY" }
            Write-Host ("      param {0} : {1}   [{2}]{3}" -f $p.Name, $p.ParameterType.FullName, (Where-Does-Type-Live $p.ParameterType), $flag)
        }
    }
    Write-Host ""
}
