# Craft-from-storage research pass 5 (idea-17 Phase 0 follow-up): find WHICH ItemCollection the
# craft gate reads / consumption drains, and the EquipPoint structural-blacklist surface.
# Pure ASCII only (powershell 5.1 reads BOM-less UTF-8 as ANSI - an em dash becomes a string delimiter).
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
    $v = ""
    if ($m.IsVirtual) { $v = " [virt]" }
    "$($m.ReturnType.Name) $($m.Name)($ps)$v"
}
function DumpType($tn) {
    $t = $byName[$tn]
    if (-not $t) { Write-Output "  [NOT FOUND] $tn"; return }
    Write-Output ""
    Write-Output "---- $tn (base: $(if ($t.BaseType) { $t.BaseType.FullName } else { '-' })) ----"
    foreach ($p in $t.Properties) { Write-Output "  P: $($p.Name) : $($p.PropertyType.Name)" }
    foreach ($m in $t.Methods) {
        if ($m.Name -match "^get_|^set_|^add_|^remove_") { continue }
        Write-Output "  M: $(Sig $m)"
    }
}

Write-Output "=================================================================="
Write-Output "PASS R: agent-side inventory access (the useAgentInventory=True path)"
Write-Output "=================================================================="
DumpType "SSSGame.IInteractionAgent"
foreach ($t in $allTypes) {
    if ($t.Name -match "InteractionAgent$") { Write-Output "  TYPE: $($t.FullName)  : $(if ($t.BaseType) { $t.BaseType.Name } else { '-' })" }
}

Write-Output ""
Write-Output "=================================================================="
Write-Output "PASS S: PlayerInteractionAgent / CraftingAgent / ICraftingAgent surface"
Write-Output "=================================================================="
DumpType "SSSGame.PlayerInteractionAgent"
DumpType "SSSGame.CraftingAgent"
DumpType "SSSGame.ICraftingAgent"

Write-Output ""
Write-Output "=================================================================="
Write-Output "PASS T: CraftingStation (: Workstation) + Workstation inventory surface"
Write-Output "=================================================================="
DumpType "SSSGame.CraftingStation"
DumpType "SSSGame.CraftingStation/CraftingProject"
$t = $byName["SSSGame.Workstation"]
if ($t) {
    Write-Output ""
    Write-Output "-- SSSGame.Workstation: inventory/container members --"
    foreach ($p in $t.Properties) { if ($p.Name -match "(?i)invent|contain|item|storage") { Write-Output "  P: $($p.Name) : $($p.PropertyType.Name)" } }
    foreach ($m in $t.Methods) { if ($m.Name -match "(?i)invent|contain|item|storage") { Write-Output "  M: $(Sig $m)" } }
}

Write-Output ""
Write-Output "=================================================================="
Write-Output "PASS U: CraftInteractionDisplay + _CraftRoutine state machine"
Write-Output "=================================================================="
DumpType "SSSGame.CraftInteractionDisplay"
DumpType "SSSGame.CraftInteractionDisplay/__CraftRoutine_d__10"

Write-Output ""
Write-Output "=================================================================="
Write-Output "PASS V: InventoryComponent + ItemContainerComponent surface"
Write-Output "=================================================================="
DumpType "SandSailorStudio.Inventory.InventoryComponent"
DumpType "SandSailorStudio.Inventory.ItemContainerComponent"

Write-Output ""
Write-Output "=================================================================="
Write-Output "PASS W: EquipPoint / EquipmentManager (structural blacklist rule)"
Write-Output "=================================================================="
DumpType "SandSailorStudio.Inventory.EquipPoint"
DumpType "SandSailorStudio.Inventory.EquipmentManager"
foreach ($t in $allTypes) {
    if ($t.Name -match "^Equip") { Write-Output "  TYPE: $($t.FullName)  : $(if ($t.BaseType) { $t.BaseType.Name } else { '-' })" }
}

Write-Output ""
Write-Output "=================================================================="
Write-Output "PASS X: every method anywhere that takes an ItemManifest (consumption candidates)"
Write-Output "=================================================================="
foreach ($t in $allTypes) {
    foreach ($m in $t.Methods) {
        if ($m.Name -match "^get_|^set_|^add_|^remove_") { continue }
        $hit = $false
        foreach ($p in $m.Parameters) { if ($p.ParameterType.Name -eq "ItemManifest") { $hit = $true } }
        if ($hit) { Write-Output "  $($t.FullName) :: $(Sig $m)" }
    }
}

Write-Output ""
Write-Output "=================================================================="
Write-Output "PASS Y: PlayerCraftInteractionSession / VillagerCraftSession surface"
Write-Output "=================================================================="
DumpType "SSSGame.PlayerCraftInteractionConfig/PlayerCraftInteractionSession"
DumpType "SSSGame.VillagerCraftInteractionConfig/VillagerCraftSession"
DumpType "SSSGame.UI.CraftMenu"

Write-Output ""
Write-Output "DONE"
