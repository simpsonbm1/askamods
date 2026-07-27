# OuthouseComposter - can a probe attribute a removal to a NAMED VILLAGER + their WORKSTATION?
#
# The gate probes patch IsWhitelistedByStorage on quest-DATA types; Harmony gives us __instance =
# the QuestData. If QuestData -> QuestRunner -> Villager is reachable, and Villager exposes a name
# and a workstation/job, we can log "who asked" and correlate it with the tripwire's "what left".
#
# Flags the two known-fatal patch/access families so nothing is proposed blind.
# ASCII only, explicit loops - PowerShell 5.1 chokes on inline pipeline joins in assignments.
$base = "D:\SteamLibrary\steamapps\common\ASKA\BepInEx"
[void][System.Reflection.Assembly]::LoadFrom("d:\Claude Projects\askamods\_explore\bin\Debug\net10.0\Mono.Cecil.dll")

$allTypes = New-Object System.Collections.Generic.List[object]
foreach ($f in Get-ChildItem "$base\interop\*.dll") {
    try { $asm = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($f.FullName) } catch { continue }
    foreach ($t in $asm.MainModule.Types) {
        $allTypes.Add($t)
        foreach ($n in $t.NestedTypes) {
            $allTypes.Add($n)
            foreach ($n2 in $n.NestedTypes) { $allTypes.Add($n2) }
        }
    }
}
$byName = @{}
foreach ($t in $allTypes) { if (-not $byName.ContainsKey($t.FullName)) { $byName[$t.FullName] = $t } }
Write-Host ("loaded {0} types" -f $allTypes.Count)

function Sig($m) {
    $ps = @()
    foreach ($p in $m.Parameters) { $ps += ("{0} {1}" -f $p.ParameterType.FullName, $p.Name) }
    return ("{0}({1}) : {2}" -f $m.Name, [string]::Join(", ", $ps), $m.ReturnType.Name)
}

Write-Host ""
Write-Host "=========== 1. QuestData / WorkstationQuestData - route to runner/villager/station ==========="
foreach ($tn in @("SSSGame.AI.QuestData", "SSSGame.AI.WorkstationQuestData", "SSSGame.AI.QuestRunner")) {
    $t = $byName[$tn]
    if ($null -eq $t) { Write-Host ("  NO TYPE {0}" -f $tn); continue }
    $bt = "?"
    if ($t.BaseType) { $bt = $t.BaseType.FullName }
    Write-Host ("  ### {0}   base={1}" -f $tn, $bt)
    Write-Host "     -- fields --"
    foreach ($fl in ($t.Fields | Sort-Object Name)) {
        if ($fl.FieldType.FullName -notmatch "Villager|QuestRunner|Workstation|Structure|Character|Creature") { continue }
        Write-Host ("        {0} {1}" -f $fl.FieldType.FullName, $fl.Name)
    }
    Write-Host "     -- methods/props returning an actor-ish type --"
    foreach ($m in ($t.Methods | Sort-Object Name)) {
        if ($m.ReturnType.FullName -notmatch "Villager|QuestRunner|Workstation|Structure|Character|Creature") { continue }
        Write-Host ("        {0}" -f (Sig $m))
    }
}

Write-Host ""
Write-Host "=========== 2. Villager - name / job / workstation accessors ==========="
$t = $byName["SSSGame.Villager"]
if ($null -eq $t) { Write-Host "  NO TYPE SSSGame.Villager" }
else {
    $bt = "?"
    if ($t.BaseType) { $bt = $t.BaseType.FullName }
    Write-Host ("  ### SSSGame.Villager   base={0}" -f $bt)
    foreach ($m in ($t.Methods | Sort-Object Name)) {
        if ($m.Name -notmatch "Name|Job|Profession|Workstation|Station|Role|Occupation|Assign|Work") { continue }
        if ($m.Name -match "^(add_|remove_|set_)") { continue }
        Write-Host ("        {0}" -f (Sig $m))
    }
}

Write-Host ""
Write-Host "=========== 3. Workstation - display name for reporting ==========="
$t = $byName["SSSGame.Workstation"]
if ($null -eq $t) { Write-Host "  NO TYPE SSSGame.Workstation" }
else {
    foreach ($m in ($t.Methods | Sort-Object Name)) {
        if ($m.Name -notmatch "Name|Structure|Template") { continue }
        if ($m.Name -match "^(add_|remove_|set_)") { continue }
        Write-Host ("        {0}" -f (Sig $m))
    }
}

Write-Host ""
Write-Host "=========== 4. Which quest-data types expose a Workstation directly ==========="
$gate = @(
 "SSSGame.AI.CleanupInventoryQuest/CleanupInventoryQuestData",
 "SSSGame.AI.DressUpQuest/DressUpQuestData",
 "SSSGame.AI.FSM.FSM_Fetch/GatherData",
 "SSSGame.AI.GatherAndHarvestQuest/GatherAndHarvestData",
 "SSSGame.AI.HomeStockpileQuest/HomeStockpileQuestData",
 "SSSGame.AI.WorkstationQuest/WorkstationQuestData",
 "SSSGame.CookingQuest/CookingQuestData",
 "SSSGame.CookingStockpileQuest/CookingStockpileQuestData",
 "SSSGame.CrafterFetchQuest/CrafterFetchQuestData"
)
foreach ($tn in $gate) {
    $t = $byName[$tn]
    if ($null -eq $t) { Write-Host ("  NO TYPE {0}" -f $tn); continue }
    $bt = "?"
    if ($t.BaseType) { $bt = $t.BaseType.Name }
    $hits = @()
    foreach ($fl in $t.Fields) {
        if ($fl.FieldType.FullName -match "Villager|QuestRunner|Workstation") { $hits += ("field " + $fl.Name + ":" + $fl.FieldType.Name) }
    }
    foreach ($m in $t.Methods) {
        if ($m.ReturnType.FullName -match "Villager|QuestRunner|Workstation") { $hits += ("method " + $m.Name + "->" + $m.ReturnType.Name) }
    }
    if ($hits.Count -eq 0) { Write-Host ("  {0}  base={1}   (nothing direct - inherit from base)" -f $tn, $bt) }
    else { Write-Host ("  {0}  base={1}`n        {2}" -f $tn, $bt, [string]::Join("`n        ", $hits)) }
}
