# Craft-from-storage research pass 8 (PHASE 2a spike prep): nail down the exact members the
# read-only villager-fetch spike needs, so its spec contains no guesses.
#
# The spike must let the LOG ALONE answer: "did the crafter go straight to the station, or take a
# supply tour first?" That needs the state-transition trace (Fetch vs Use vs Return), a way to
# name WHICH villager, and a way to name WHICH storage site was probed.
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

function Sig($m) {
    $ps = ($m.Parameters | ForEach-Object { "$($_.ParameterType.Name) $($_.Name)" }) -join ", "
    $mods = @()
    if ($m.IsStatic) { $mods += "static" }
    if ($m.IsVirtual) { $mods += "virt" }
    if ($m.IsPublic) { $mods += "pub" } elseif ($m.IsFamily) { $mods += "prot" } else { $mods += "priv" }
    "$($m.ReturnType.Name) $($m.Name)($ps) [" + ($mods -join ",") + "]"
}
function DumpType($tn) {
    $t = $byName[$tn]
    if (-not $t) { Write-Output "  [NOT FOUND] $tn"; return }
    Write-Output ""
    Write-Output "---- $tn (base: $(if ($t.BaseType) { $t.BaseType.FullName } else { '-' })) ----"
    foreach ($p in $t.Properties) { Write-Output "  P: $($p.Name) : $($p.PropertyType.Name)" }
    foreach ($fld in $t.Fields) {
        if ($fld.Name -match "^NativeFieldInfoPtr|^NativeMethodInfoPtr|^NativeClassPtr") { continue }
        $fmod = if ($fld.IsStatic) { " [static]" } else { "" }
        Write-Output "  F: $($fld.Name) : $($fld.FieldType.Name)$fmod"
    }
    foreach ($m in $t.Methods) {
        if ($m.Name -match "^get_|^set_|^add_|^remove_") { continue }
        Write-Output "  M: $(Sig $m)"
    }
}

Write-Output "=================================================================="
Write-Output "PASS AA: the three state-transition markers the spike traces"
Write-Output "=================================================================="
DumpType "SSSGame.AI.FSM.FSM_UseCraftingStation"
DumpType "SSSGame.AI.FSM.FSM_ReturnCraftingSupplies"
DumpType "SSSGame.AI.FSM.FSM_FetchSupplies"

Write-Output ""
Write-Output "=================================================================="
Write-Output "PASS BB: FSM_QuestAction base + vStateAction (what __instance gives us)"
Write-Output "=================================================================="
DumpType "SSSGame.AI.FSM.FSM_QuestAction"
DumpType "SSSGame.AI.FSM.vStateAction"

Write-Output ""
Write-Output "=================================================================="
Write-Output "PASS CC: IFSMBehaviourController - the OnStateEnter param (villager route?)"
Write-Output "=================================================================="
DumpType "SSSGame.AI.FSM.IFSMBehaviourController"
foreach ($t in $allTypes) {
    if ($t.Name -match "(?i)^IFSMBehaviourController$|^vFSMBehaviour$") {
        Write-Output "  (found as $($t.FullName))"
    }
}
DumpType "SSSGame.AI.FSM.vFSMBehaviour"

Write-Output ""
Write-Output "=================================================================="
Write-Output "PASS DD: ANY member returning a Villager on FSM / quest / questdata types"
Write-Output "=================================================================="
foreach ($t in $allTypes) {
    if ($t.FullName -notmatch "(?i)FSM|Quest|Crafting|Workstation") { continue }
    foreach ($m in $t.Methods) {
        if ($m.ReturnType.Name -eq "Villager" -and $m.Name -notmatch "^set_") {
            Write-Output "  $($t.FullName) :: $(Sig $m)"
        }
    }
    foreach ($p in $t.Properties) {
        if ($p.PropertyType.Name -eq "Villager") { Write-Output "  $($t.FullName) :: P: $($p.Name) : Villager" }
    }
}

Write-Output ""
Write-Output "=================================================================="
Write-Output "PASS EE: IResourceStorageSite - how to NAME a probed storage in the log"
Write-Output "=================================================================="
DumpType "SSSGame.IResourceStorageSite"
foreach ($t in $allTypes) {
    if ($t.Name -match "(?i)^IResourceStorageSite$") { Write-Output "  (found as $($t.FullName))" }
}

Write-Output ""
Write-Output "=================================================================="
Write-Output "PASS FF: CraftingStation - project/quest creation surface (the test trigger)"
Write-Output "=================================================================="
$t = $byName["SSSGame.CraftingStation"]
if ($t) {
    Write-Output "  (base: $($t.BaseType.FullName))"
    foreach ($p in $t.Properties) { Write-Output "  P: $($p.Name) : $($p.PropertyType.Name)" }
    foreach ($m in $t.Methods) {
        if ($m.Name -match "^get_|^set_|^add_|^remove_") { continue }
        Write-Output "  M: $(Sig $m)"
    }
}

Write-Output ""
Write-Output "=================================================================="
Write-Output "PASS GG: CraftingProject / CraftingQuest surface"
Write-Output "=================================================================="
DumpType "SSSGame.CraftingProject"
DumpType "SSSGame.CraftingQuest"

Write-Output ""
Write-Output "DONE"
