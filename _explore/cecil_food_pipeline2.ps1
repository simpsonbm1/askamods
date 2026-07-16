# Food-pipeline probe 2: BlueprintInfo manifest surface (barbecue raw->cooked edge),
# ItemTableConfig (resolve Table '?' reqs), ResourceStorage task/row API (gather lever),
# dining/eat configs (population-eating magnitude).
$asmPath = "D:\SteamLibrary\steamapps\common\ASKA\BepInEx\interop\Assembly-CSharp.dll"
[void][System.Reflection.Assembly]::LoadFrom("d:\Claude Projects\askamods\_explore\bin\Debug\net10.0\Mono.Cecil.dll")
$asm = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($asmPath)
$mod = $asm.MainModule
$allTypes = New-Object System.Collections.Generic.List[object]
foreach ($t in $mod.Types) {
    $allTypes.Add($t)
    foreach ($n in $t.NestedTypes) { $allTypes.Add($n) }
}
$inv = [Mono.Cecil.AssemblyDefinition]::ReadAssembly("D:\SteamLibrary\steamapps\common\ASKA\BepInEx\interop\SandSailorStudio.dll")
foreach ($t in $inv.MainModule.Types) {
    $allTypes.Add($t)
    foreach ($n in $t.NestedTypes) { $allTypes.Add($n) }
}
function Sig($m) {
    $ps = ($m.Parameters | ForEach-Object { "$($_.ParameterType.Name) $($_.Name)" }) -join ", "
    "$($m.ReturnType.Name) $($m.Name)($ps)"
}
function DumpType($tn) {
    foreach ($t in ($allTypes | Where-Object { $_.Name -eq $tn })) {
        $base = if ($t.BaseType) { $t.BaseType.Name } else { "none" }
        Write-Output "TYPE: $($t.FullName) base=$base"
        foreach ($f in $t.Fields) { if ($f.Name -notmatch "^Native") { Write-Output "  F: $($f.Name) : $($f.FieldType.Name)" } }
        foreach ($p in $t.Properties) { Write-Output "  P: $($p.Name) : $($p.PropertyType.Name)" }
        foreach ($m in $t.Methods) { if ($m.Name -notmatch "^get_|^set_|^\.") { Write-Output "  M: $(Sig $m)" } }
        Write-Output ""
    }
}
Write-Output "===== BlueprintInfo (manifest surface) ====="
DumpType "BlueprintInfo"
Write-Output "===== ItemTableConfig ====="
DumpType "ItemTableConfig"
Write-Output "===== ResourceStorage (task/quota row API only) ====="
foreach ($t in ($allTypes | Where-Object { $_.Name -eq "ResourceStorage" })) {
    foreach ($p in $t.Properties) { Write-Output "  P: $($p.Name) : $($p.PropertyType.Name)" }
    foreach ($m in $t.Methods) { if ($m.Name -notmatch "^get_|^set_|^\." -and $m.Name -match "Task|Quota|Filter|Storage|Manifest|Gather") { Write-Output "  M: $(Sig $m)" } }
}
Write-Output ""
Write-Output "===== HuntingStation + HuntingData + HunterFiltersTaskData ====="
DumpType "HuntingStation"
DumpType "HuntingData"
DumpType "HunterFiltersTaskData"
Write-Output "===== VillagerDiningInteractionConfig (eat restore magnitude?) ====="
DumpType "VillagerDiningInteractionConfig"
