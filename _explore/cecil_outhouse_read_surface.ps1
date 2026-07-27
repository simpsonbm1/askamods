# OuthouseComposter #theft-report - find the SOURCE-SIDE choke point.
#
# QUESTION: the goal is "non-compost outhouse contents are INVISIBLE to villagers" - one place the
# container under-reports - rather than denying N separate quest-side whitelist gates (which can
# never be proven exhaustive: interop method bodies are native trampolines, so "who calls this" is
# not statically answerable in this game).
#
# We already patch the WRITE side safely in production (ItemContainer.CanStoreItemType/Check/
# HasSpace/GetStackSize, all taking ItemInfo). This looks for the READ side: whatever answers
# "do you have any of X / how many / give me one".
#
# Flags the two known-fatal patch families so a candidate is never proposed blind:
#   - by-ref primitive params (trampoline NRE)
#   - inventory-family params: Item / ItemCollection / ItemEventContext (native CLR crash)
# NOTE: ItemInfo is NOT in the fatal family - that is why our four write-side patches are safe.
#
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

function Has-ByRefPrimitive($m) {
    foreach ($p in $m.Parameters) {
        if ($p.ParameterType.IsByReference) {
            $en = $p.ParameterType.GetElementType().FullName
            if ($en -match "^System\.(Single|Int32|Boolean|Int64|Double|Byte|UInt32)$") { return $true }
        }
    }
    return $false
}

function Has-InventoryFamilyParam($m) {
    foreach ($p in $m.Parameters) {
        $n = $p.ParameterType.FullName
        if ($n -match "Inventory\.Item$") { return $true }
        if ($n -match "ItemCollection") { return $true }
        if ($n -match "ItemEventContext") { return $true }
    }
    return $false
}

function Format-Sig($m) {
    $ps = @()
    foreach ($p in $m.Parameters) { $ps += ("{0} {1}" -f $p.ParameterType.FullName, $p.Name) }
    $flags = @()
    if ($m.IsVirtual) { $flags += "virtual" }
    if ($m.IsStatic) { $flags += "static" }
    if ($m.IsPublic) { $flags += "public" } else { $flags += "nonpublic" }
    $warn = ""
    if (Has-ByRefPrimitive $m) { $warn = "   <-- BYREF-PRIM: FATAL, DO NOT PATCH" }
    if (Has-InventoryFamilyParam $m) { $warn = $warn + "   <-- INVENTORY-FAMILY: FATAL, DO NOT PATCH" }
    if ($warn -eq "") { $warn = "   [PATCH-SAFE signature]" }
    return ("{0}({1}) : {2}   [{3}]{4}" -f $m.Name, [string]::Join(", ", $ps), $m.ReturnType.Name, [string]::Join(" ", $flags), $warn)
}

Write-Host ""
Write-Host "=========== 1. ItemContainer / ItemCollection READ surface ==========="
Write-Host "   (Has/Count/Get/Find/Contains/Available/Take/Remove - what a query would call)"
$targets = @(
  "SandSailorStudio.Inventory.ItemContainer",
  "SandSailorStudio.Inventory.ItemCollection",
  "SandSailorStudio.Inventory.ItemContainerComponent"
)
foreach ($tn in $targets) {
    $t = $byName[$tn]
    if ($null -eq $t) { Write-Host ("  NO TYPE {0}" -f $tn); continue }
    $bt = "?"
    if ($t.BaseType) { $bt = $t.BaseType.FullName }
    Write-Host ("  ### {0}   base={1}" -f $tn, $bt)
    foreach ($m in ($t.Methods | Sort-Object Name)) {
        if ($m.Name -match "^(add_|remove_|\.)") { continue }
        if ($m.Name -notmatch "Has|Count|Get|Find|Contains|Available|Take|Query|Amount|Quantity|Stack|Check|Search|Peek") { continue }
        Write-Host ("        {0}" -f (Format-Sig $m))
    }
}

Write-Host ""
Write-Host "=========== 2. IResourceStorageSite + ResourceOutlet full surface ==========="
Write-Host "   (architecture.md: the outlet is how AI quests reach a building's storage + whitelist)"
foreach ($tn in @("SSSGame.IResourceStorageSite", "SSSGame.ResourceOutlet")) {
    $t = $byName[$tn]
    if ($null -eq $t) { Write-Host ("  NO TYPE {0}" -f $tn); continue }
    $bt = "?"
    if ($t.BaseType) { $bt = $t.BaseType.FullName }
    Write-Host ("  ### {0}   base={1}" -f $tn, $bt)
    foreach ($m in ($t.Methods | Sort-Object Name)) {
        if ($m.Name -match "^(add_|remove_|\.)") { continue }
        Write-Host ("        {0}" -f (Format-Sig $m))
    }
}

Write-Host ""
Write-Host "=========== 3. Every implementor of IResourceStorageSite ==========="
Write-Host "   (which concrete types can BE the outhouse's storage site)"
foreach ($t in ($allTypes | Sort-Object FullName)) {
    if ($t.FullName -match "<") { continue }
    if ($t.FullName -match "MethodInfoStore") { continue }
    $impl = $false
    foreach ($i in $t.Interfaces) { if ($i.InterfaceType.Name -eq "IResourceStorageSite") { $impl = $true } }
    if (-not $impl) { continue }
    $bt = "?"
    if ($t.BaseType) { $bt = $t.BaseType.Name }
    Write-Host ("  {0}   base={1}" -f $t.FullName, $bt)
}

Write-Host ""
Write-Host "=========== 4. StorageInteraction surface (the outhouse HAS one) ==========="
$t = $byName["SSSGame.StorageInteraction"]
if ($null -eq $t) { Write-Host "  NO TYPE SSSGame.StorageInteraction" }
else {
    $bt = "?"
    if ($t.BaseType) { $bt = $t.BaseType.FullName }
    Write-Host ("  ### SSSGame.StorageInteraction   base={0}" -f $bt)
    $ifs = @()
    foreach ($i in $t.Interfaces) { $ifs += $i.InterfaceType.Name }
    Write-Host ("      implements: {0}" -f [string]::Join(", ", $ifs))
    foreach ($m in ($t.Methods | Sort-Object Name)) {
        if ($m.Name -match "^(add_|remove_|\.)") { continue }
        Write-Host ("        {0}" -f (Format-Sig $m))
    }
}

Write-Host ""
Write-Host "=========== 5. Settlement/Homestead store-search entry points ==========="
Write-Host "   (architecture.md marks FindItemStorage UNPATCHABLE - confirm, and look for siblings)"
foreach ($tn in @("SSSGame.Settlement", "SSSGame.Homestead")) {
    $t = $byName[$tn]
    if ($null -eq $t) { Write-Host ("  NO TYPE {0}" -f $tn); continue }
    Write-Host ("  ### {0}" -f $tn)
    foreach ($m in ($t.Methods | Sort-Object Name)) {
        if ($m.Name -notmatch "Storage|Store|FindItem|Outlet|Supply") { continue }
        if ($m.Name -match "^(add_|remove_|\.)") { continue }
        Write-Host ("        {0}" -f (Format-Sig $m))
    }
}
