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

Write-Output "---- exact FullName search for QuestData (non-nested) ----"
foreach ($t in $allTypes) {
    if ($t.Name -eq "QuestData") { Write-Output "  FOUND: $($t.FullName)  base=$($t.BaseType.FullName)" }
}

Write-Output ""
Write-Output "---- GetQuestData return type full name (on FSM_QuestAction) ----"
$t = $byName["SSSGame.AI.FSM.FSM_QuestAction"]
foreach ($m in $t.Methods) {
    if ($m.Name -eq "GetQuestData") { Write-Output "  $($m.ReturnType.FullName) GetQuestData($($m.Parameters | ForEach-Object { $_.ParameterType.FullName }))" }
}

Write-Output ""
Write-Output "---- WorkstationQuestData base chain ----"
$t = $byName["SSSGame.AI.WorkstationQuest/WorkstationQuestData"]
$cur = $t
while ($cur -ne $null) {
    Write-Output "  $($cur.FullName)"
    if ($cur.BaseType -eq $null) { break }
    $cur = $byName[$cur.BaseType.FullName]
    if ($cur -eq $null) { Write-Output "  (base not in byName map: stop)"; break }
}

Write-Output ""
Write-Output "---- CrafterFetchQuestData base chain ----"
$t = $byName["SSSGame.CrafterFetchQuest/CrafterFetchQuestData"]
$cur = $t
while ($cur -ne $null) {
    Write-Output "  $($cur.FullName)"
    if ($cur.BaseType -eq $null) { break }
    $cur = $byName[$cur.BaseType.FullName]
    if ($cur -eq $null) { Write-Output "  (base not in byName map: stop)"; break }
}

Write-Output ""
Write-Output "---- WorkstationQuestData GetVillager + declaring type check ----"
$t = $byName["SSSGame.AI.WorkstationQuest/WorkstationQuestData"]
foreach ($m in $t.Methods) {
    if ($m.Name -match "GetVillager|ctor") { Write-Output "  $($m.ReturnType.Name) $($m.Name)(...) [$(if($m.IsVirtual){'virt '})$(if($m.IsPublic){'pub'})]" }
}

Write-Output ""
Write-Output "---- CrafterFetchQuestData ctor list (need (IntPtr) for rewrap) ----"
$t = $byName["SSSGame.CrafterFetchQuest/CrafterFetchQuestData"]
foreach ($m in $t.Methods) {
    if ($m.Name -eq ".ctor") { Write-Output "  .ctor($($m.Parameters | ForEach-Object { $_.ParameterType.FullName }))" }
}

Write-Output "DONE"
