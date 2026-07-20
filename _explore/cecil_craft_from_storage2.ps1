# Craft-from-storage research pass 2: display/UI/session surfaces + family tree + override map
$asmPath = "D:\SteamLibrary\steamapps\common\ASKA\BepInEx\interop\Assembly-CSharp.dll"
[void][System.Reflection.Assembly]::LoadFrom("d:\Claude Projects\askamods\_explore\bin\Debug\net10.0\Mono.Cecil.dll")
$asm = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($asmPath)
$mod = $asm.MainModule

$allTypes = New-Object System.Collections.Generic.List[object]
foreach ($t in $mod.Types) {
    $allTypes.Add($t)
    foreach ($n in $t.NestedTypes) { $allTypes.Add($n) }
}
$byName = @{}
foreach ($t in $allTypes) { $byName[$t.FullName] = $t }

function Sig($m) {
    $ps = ($m.Parameters | ForEach-Object { "$($_.ParameterType.Name) $($_.Name)" }) -join ", "
    $v = ""
    if ($m.IsVirtual) { $v = " [virt]" }
    "$($m.ReturnType.Name) $($m.Name)($ps)$v"
}
function DumpType($tn, $withFields) {
    $t = $byName[$tn]
    if (-not $t) { Write-Output "  [NOT FOUND] $tn"; return }
    Write-Output ""
    Write-Output "---- $tn (base: $(if ($t.BaseType) { $t.BaseType.FullName } else { '-' })) ----"
    if ($withFields) {
        foreach ($p in $t.Properties) { Write-Output "  P: $($p.Name) : $($p.PropertyType.Name)" }
    }
    foreach ($m in $t.Methods) {
        if ($m.Name -match "^get_|^set_|^add_|^remove_") { continue }
        Write-Output "  M: $(Sig $m)"
    }
}

Write-Output "=================================================================="
Write-Output "PASS A: all types transitively deriving from CraftInteraction / Interaction subtree of interest"
Write-Output "=================================================================="
function BaseChain($t) {
    $names = @()
    $cur = $t
    while ($cur -and $cur.BaseType) {
        $names += $cur.BaseType.FullName
        $cur = $byName[$cur.BaseType.FullName]
    }
    $names
}
foreach ($t in $allTypes) {
    $chain = BaseChain $t
    if ($chain -contains "SSSGame.CraftInteraction") {
        Write-Output "  $($t.FullName)  (chain: $($chain -join ' <- '))"
    }
}
Write-Output ""
Write-Output "-- Interaction-derived types with 'Craft|Anvil|Carpenter|Dye|Forg|Workshop' in name --"
foreach ($t in $allTypes) {
    if ($t.Name -notmatch "(?i)craft|anvil|carpenter|dye|forg|workshop") { continue }
    $chain = BaseChain $t
    if ($chain -contains "SSSGame.Interaction") {
        Write-Output "  $($t.FullName)  : $($t.BaseType.Name)"
    }
}

Write-Output ""
Write-Output "=================================================================="
Write-Output "PASS B: override map - who redeclares the family methods"
Write-Output "=================================================================="
$familyMethods = @("CheckOwnedRequirements", "_CheckOwnedBlueprintManifest", "BeginCraftingSequence", "_OnCraftingSuccess", "CheckAgent", "Use", "_TryInteract")
foreach ($fm in $familyMethods) {
    Write-Output ""
    Write-Output "-- $fm --"
    foreach ($t in $allTypes) {
        foreach ($m in $t.Methods) {
            if ($m.Name -eq $fm) { Write-Output "  $($t.FullName) :: $(Sig $m)" }
        }
    }
}

Write-Output ""
Write-Output "=================================================================="
Write-Output "PASS C: display / UI / session / config surfaces"
Write-Output "=================================================================="
DumpType "SSSGame.CraftInteractionDisplay" $true
DumpType "SSSGame.CraftInteractionDisplay/__CraftRoutine_d__10" $true
DumpType "SSSGame.AnvilInteractionDisplay" $true
DumpType "SSSGame.UI.CraftMenu" $true
DumpType "SSSGame.UI.CraftMenu/CraftMenuParameters" $true
DumpType "SSSGame.PlayerCraftInteractionConfig" $true
DumpType "SSSGame.PlayerCraftInteractionConfig/PlayerCraftInteractionSession" $true
DumpType "SSSGame.VillagerCraftInteractionConfig" $true
DumpType "SSSGame.VillagerCraftInteractionConfig/VillagerCraftSession" $true
DumpType "SSSGame.CraftInteraction/CraftingEvents" $true
DumpType "SSSGame.ICraftingSession" $true
DumpType "SSSGame.ICraftInteraction" $true
DumpType "SSSGame.CraftingStation/CraftingProject" $true
DumpType "SSSGame.InteractionSession" $true
DumpType "SSSGame.InventoryComponent" $true

Write-Output ""
Write-Output "=================================================================="
Write-Output "PASS D: global scan - sibling availability/consumption method names (any type)"
Write-Output "=================================================================="
foreach ($t in $allTypes) {
    foreach ($m in $t.Methods) {
        if ($m.Name -match "(?i)^CheckOwned|^CanCraft|^HasIngredients|^HasRequired|^ConsumeManifest|^RemoveManifest|^ConsumeItems|^ConsumeBlueprint|^PayCost|^SpendItems") {
            Write-Output "  $($t.FullName) :: $(Sig $m)"
        }
    }
}

Write-Output ""
Write-Output "DONE"
