# v0.9.2 pass (2026-07-28): can a crafter fetch quest name WHICH interaction it supplies?
# Chain under test: CrafterFetchQuest -> (native CrafterSpecificFetchQuest) -> craftingProject
#                   -> taskData : SSSGame.AI.CraftingStationTaskData -> ??? -> CraftInteraction vs AnvilInteraction
# Same helpers as cecil_station_kind2.ps1.
$base = "D:\SteamLibrary\steamapps\common\ASKA\BepInEx"
[void][System.Reflection.Assembly]::LoadFrom("d:\Claude Projects\askamods\_explore\bin\Debug\net10.0\Mono.Cecil.dll")

$allTypes = New-Object System.Collections.Generic.List[object]
function AddRec($t) { $allTypes.Add($t); foreach ($n in $t.NestedTypes) { AddRec $n } }
foreach ($f in Get-ChildItem "$base\interop\*.dll") {
    try { $asm = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($f.FullName) } catch { continue }
    foreach ($t in $asm.MainModule.Types) { AddRec $t }
}
$byName = @{}
foreach ($t in $allTypes) { if (-not $byName.ContainsKey($t.FullName)) { $byName[$t.FullName] = $t } }

function Sig($m) {
    $ps = ($m.Parameters | ForEach-Object {
        $pt = $_.ParameterType; $s = "$($pt.FullName) $($_.Name)"
        if ($pt.IsByReference) { $s += " <<BY-REF>>" }; $s }) -join ", "
    $mods = @()
    if ($m.IsStatic) { $mods += "static" }
    if ($m.IsVirtual) { $mods += "virt" }
    if ($m.IsPublic) { $mods += "pub" } elseif ($m.IsFamily) { $mods += "prot" } else { $mods += "priv" }
    "$($m.ReturnType.FullName) $($m.Name)($ps) [" + ($mods -join ",") + "]"
}
function BaseChain($t) {
    $chain = @(); $cur = $t; $guard = 0
    while ($cur -and $cur.BaseType -and $guard -lt 20) {
        $bn = $cur.BaseType.FullName; $chain += $bn; $cur = $byName[$bn]; $guard++ }
    if ($chain.Count -eq 0) { return "(none)" }
    return ($chain -join "  ->  ")
}
function DumpType($tn) {
    Write-Output ""
    Write-Output "=================================================================="
    Write-Output "TYPE: $tn"
    Write-Output "=================================================================="
    $t = $byName[$tn]
    if (-not $t) { Write-Output "  [NOT FOUND]"; return }
    Write-Output "  ASSEMBLY : $($t.Module.Assembly.Name.Name)"
    Write-Output "  BASECHAIN: $(BaseChain $t)"
    Write-Output ("  INTERFACES: " + (($t.Interfaces | ForEach-Object { $_.InterfaceType.FullName }) -join ", "))
    if ($t.NestedTypes.Count) { Write-Output ("  NESTED   : " + (($t.NestedTypes | ForEach-Object { $_.FullName }) -join ", ")) }
    Write-Output "  ---- PROPERTIES ----"
    foreach ($p in $t.Properties) { Write-Output "  P: $($p.PropertyType.FullName) $($p.Name)" }
    Write-Output "  ---- METHODS ----"
    foreach ($m in $t.Methods) { if ($m.Name -notmatch "^(get|set)_(Native|field_Compiler)") { Write-Output "  M: $(Sig $m)" } }
}

# ---- A: the task-data type the CraftingProject points at, plus its whole base chain ----
foreach ($tn in @(
    "SSSGame.AI.CraftingStationTaskData",
    "SSSGame.AI.WorkstationTaskData",
    "SSSGame.AI.TaskData",
    "SSSGame.CraftingQuest",
    "SSSGame.CrafterSpecificFetchQuest",
    "SSSGame.CrafterFetchQuest",
    "SSSGame.AI.FSM.FSM_FetchCraftingSupplies")) { DumpType $tn }

# ---- B: every member ANYWHERE typed CraftingStationTaskData (who hands one out) ----
Write-Output ""
Write-Output "=================================================================="
Write-Output "B: members typed SSSGame.AI.CraftingStationTaskData"
Write-Output "=================================================================="
$h = 0
foreach ($t in $allTypes) {
    foreach ($p in $t.Properties) { if ($p.PropertyType.FullName -match "CraftingStationTaskData") { Write-Output "  $($t.FullName) :: P: $($p.PropertyType.FullName) $($p.Name)"; $h++ } }
    foreach ($m in $t.Methods) { if ((Sig $m) -match "CraftingStationTaskData" -and $m.Name -notmatch "^get_") { Write-Output "  $($t.FullName) :: M: $(Sig $m)"; $h++ } }
}
if ($h -eq 0) { Write-Output "  NO HITS" }

# ---- C: does ANY type link a task-data / project / quest to an Interaction? ----
Write-Output ""
Write-Output "=================================================================="
Write-Output "C: members typed CraftInteraction / AnvilInteraction anywhere OUTSIDE"
Write-Output "   the interaction classes themselves (who else holds one)"
Write-Output "=================================================================="
$h = 0
foreach ($t in $allTypes) {
    if ($t.FullName -match "Interaction$") { continue }
    foreach ($p in $t.Properties) {
        if ($p.PropertyType.FullName -match "(CraftInteraction|AnvilInteraction|CarpenterInteraction|DyeingInteraction)") {
            Write-Output "  $($t.FullName) :: P: $($p.PropertyType.FullName) $($p.Name)"; $h++ } }
    foreach ($m in $t.Methods) {
        if ($m.Name -match "^get_") { continue }
        if ((Sig $m) -match "(CraftInteraction|AnvilInteraction|CarpenterInteraction|DyeingInteraction)") {
            Write-Output "  $($t.FullName) :: M: $(Sig $m)"; $h++ } }
}
if ($h -eq 0) { Write-Output "  NO HITS" }

# ---- D: CraftingStation members mentioning project/table/anvil/interaction/kind/type ----
Write-Output ""
Write-Output "=================================================================="
Write-Output "D: SSSGame.CraftingStation members matching Project|Table|Anvil|Interaction|Kind|Type|Station"
Write-Output "=================================================================="
$t = $byName["SSSGame.CraftingStation"]
$PAT = "(?i)Project|Table|Anvil|Interaction|Kind|Type|Station"
$h = 0
if ($t) {
    foreach ($p in $t.Properties) { if ($p.Name -match $PAT -or $p.PropertyType.FullName -match $PAT) { Write-Output "  P: $($p.PropertyType.FullName) $($p.Name)"; $h++ } }
    foreach ($m in $t.Methods) { if ($m.Name -notmatch "^(get|set)_" -and (Sig $m) -match $PAT) { Write-Output "  M: $(Sig $m)"; $h++ } }
} else { Write-Output "  [CraftingStation NOT FOUND]" }
if ($h -eq 0) { Write-Output "  NO HITS" }

Write-Output ""
Write-Output "DONE"
