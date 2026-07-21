# Craft-from-storage research pass 6 (Phase 1 implementation prep): pin down the exact API
# details Phase 0 left ambiguous - Blueprint -> BlueprintInfo acquisition path, ItemContainer's
# own Add/Remove surface, ItemInfoQuantity fields, and IsStatic for the ItemManifest/ItemCollection
# methods the Phase 1 design leans on (static vs instance changes call syntax).
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
    $modStr = if ($mods.Count -gt 0) { " [" + ($mods -join ",") + "]" } else { "" }
    "$($m.ReturnType.Name) $($m.Name)($ps)$modStr"
}
function DumpType($tn) {
    $t = $byName[$tn]
    if (-not $t) { Write-Output "  [NOT FOUND] $tn"; return }
    Write-Output ""
    Write-Output "---- $tn (base: $(if ($t.BaseType) { $t.BaseType.FullName } else { '-' })) ----"
    foreach ($p in $t.Properties) { Write-Output "  P: $($p.Name) : $($p.PropertyType.Name)" }
    foreach ($fld in $t.Fields) {
        if ($fld.Name -match "^NativeFieldInfoPtr|^NativeMethodInfoPtr") { continue }
        $fmods = @(); if ($fld.IsStatic) { $fmods += "static" }
        $fmodStr = if ($fmods.Count -gt 0) { " [" + ($fmods -join ",") + "]" } else { "" }
        Write-Output "  F: $($fld.Name) : $($fld.FieldType.Name)$fmodStr"
    }
    foreach ($m in $t.Methods) {
        if ($m.Name -match "^get_|^set_|^add_|^remove_") { continue }
        Write-Output "  M: $(Sig $m)"
    }
}
function DumpChain($tn) {
    $t = $byName[$tn]
    while ($t) {
        DumpType $t.FullName
        if ($t.BaseType -and $t.BaseType.FullName -notmatch "^UnityEngine|^Il2Cpp") {
            $t = $byName[$t.BaseType.FullName]
        } else {
            if ($t.BaseType) { Write-Output ""; Write-Output "(chain stops at $($t.BaseType.FullName))" }
            $t = $null
        }
    }
}

Write-Output "=================================================================="
Write-Output "PASS L: Blueprint / CraftBlueprint chain (properties+methods+IsStatic)"
Write-Output "=================================================================="
DumpChain "SSSGame.CraftBlueprint"

Write-Output ""
Write-Output "=================================================================="
Write-Output "PASS M: BlueprintInfo / CraftBlueprintInfo chain"
Write-Output "=================================================================="
DumpChain "SSSGame.CraftBlueprintInfo"

Write-Output ""
Write-Output "=================================================================="
Write-Output "PASS N: Item / ItemInfo chain (just the .info-relevant bits)"
Write-Output "=================================================================="
DumpType "SandSailorStudio.Inventory.Item"
DumpType "SandSailorStudio.Inventory.ItemInfo"

Write-Output ""
Write-Output "=================================================================="
Write-Output "PASS O: ItemContainer full surface (own Add/Remove? owning collection?)"
Write-Output "=================================================================="
DumpType "SandSailorStudio.Inventory.ItemContainer"

Write-Output ""
Write-Output "=================================================================="
Write-Output "PASS P: ItemContainerComponent full surface"
Write-Output "=================================================================="
DumpType "SandSailorStudio.Inventory.ItemContainerComponent"

Write-Output ""
Write-Output "=================================================================="
Write-Output "PASS Q: ItemInfoQuantity full surface"
Write-Output "=================================================================="
DumpType "SandSailorStudio.Inventory.ItemInfoQuantity"

Write-Output ""
Write-Output "=================================================================="
Write-Output "PASS R: AddItemRequest full surface"
Write-Output "=================================================================="
DumpType "SandSailorStudio.Inventory.AddItemRequest"

Write-Output ""
Write-Output "=================================================================="
Write-Output "PASS S: ItemManifest - IsStatic for the methods Phase 1 needs"
Write-Output "=================================================================="
$t = $byName["SandSailorStudio.Inventory.ItemManifest"]
if ($t) {
    foreach ($m in $t.Methods) {
        if ($m.Name -match "^CreateFrom|^Transfer$|^Contains$|^GetQuantity$|^GetItems$|^GetItemInfoCount$|^GetTotalQuantity$|\.ctor") {
            Write-Output "  M: $(Sig $m)"
        }
    }
}

Write-Output ""
Write-Output "=================================================================="
Write-Output "PASS T: ItemCollection - IsStatic for the methods Phase 1 needs"
Write-Output "=================================================================="
$t = $byName["SandSailorStudio.Inventory.ItemCollection"]
if ($t) {
    foreach ($m in $t.Methods) {
        if ($m.Name -match "^AddItems$|^RemoveItems$|^ContainsItemManifest$|^RemoveOwnedItemManifest$|^GetItemQuantity$|^RequestAddItems$|^SubmitRequest$|^GetContainers$|^GetItemInfos$|^GetTotalItemsQuantity$") {
            Write-Output "  M: $(Sig $m)"
        }
    }
}

Write-Output ""
Write-Output "=================================================================="
Write-Output "PASS U: Workstation - GetInventory / structure-owning-collection candidates"
Write-Output "=================================================================="
$t = $byName["SSSGame.Workstation"]
if ($t) {
    foreach ($m in $t.Methods) {
        if ($m.Name -match "^get_|^set_|^add_|^remove_") { continue }
        if ($m.Name -match "(?i)inventory|container|collection") { Write-Output "  M: $(Sig $m)" }
    }
}

Write-Output ""
Write-Output "DONE"
