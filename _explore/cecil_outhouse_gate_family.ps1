# OuthouseComposter #theft-report - enumerate the FULL whitelist/storage-selection gate family.
#
# QUESTION: we ship two gates (ResourceStorage.CanCreateStorageTaskForItemInfo, and
# SurvivalObjectiveQuest/SatisfyObjectiveQuestData.IsWhitelistedBy*). A Nexus reporter still loses
# outhouse contents. Which OTHER types declare the same gate shape, so we can cover the family in
# ONE build instead of iterating (we get exactly one shot with this reporter)?
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

function Format-Sig($m) {
    $ps = @()
    foreach ($p in $m.Parameters) { $ps += ("{0} {1}" -f $p.ParameterType.FullName, $p.Name) }
    $flags = @()
    if ($m.IsVirtual) { $flags += "virtual" }
    if ($m.IsAbstract) { $flags += "abstract" }
    if ($m.IsStatic) { $flags += "static" }
    if ($m.IsPublic) { $flags += "public" } else { $flags += "nonpublic" }
    return ("{0}({1}) : {2}   [{3}]" -f $m.Name, [string]::Join(", ", $ps), $m.ReturnType.Name, [string]::Join(" ", $flags))
}

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

Write-Host ""
Write-Host "=========== 1. EVERY type declaring an IsWhitelisted* method ==========="
Write-Host "   (this is the gate family we must cover; ours are SatisfyObjectiveQuestData only)"
foreach ($t in ($allTypes | Sort-Object FullName)) {
    if ($t.FullName -match "<") { continue }
    if ($t.FullName -match "MethodInfoStore") { continue }
    $hit = $false
    foreach ($m in $t.Methods) { if ($m.Name -match "^IsWhitelisted") { $hit = $true } }
    if (-not $hit) { continue }
    $bt = "?"
    if ($t.BaseType) { $bt = $t.BaseType.Name }
    Write-Host ("  ### {0}   base={1}" -f $t.FullName, $bt)
    foreach ($m in ($t.Methods | Sort-Object Name)) {
        if ($m.Name -notmatch "^IsWhitelisted") { continue }
        $warn = ""
        if (Has-ByRefPrimitive $m) { $warn = "   <-- BYREF-PRIMITIVE: DO NOT PATCH" }
        if (Has-InventoryFamilyParam $m) { $warn = $warn + "   <-- INVENTORY-FAMILY PARAM: DO NOT PATCH" }
        Write-Host ("        {0}{1}" -f (Format-Sig $m), $warn)
    }
}

Write-Host ""
Write-Host "=========== 2. IWhitelistingSite interface + all implementors ==========="
foreach ($t in ($allTypes | Sort-Object FullName)) {
    if ($t.FullName -match "<") { continue }
    if ($t.FullName -match "MethodInfoStore") { continue }
    if ($t.Name -eq "IWhitelistingSite") {
        Write-Host ("  ### INTERFACE {0}" -f $t.FullName)
        foreach ($m in ($t.Methods | Sort-Object Name)) { Write-Host ("        {0}" -f (Format-Sig $m)) }
        continue
    }
    $impl = $false
    foreach ($i in $t.Interfaces) { if ($i.InterfaceType.Name -eq "IWhitelistingSite") { $impl = $true } }
    if (-not $impl) { continue }
    $bt = "?"
    if ($t.BaseType) { $bt = $t.BaseType.Name }
    Write-Host ("  IMPLEMENTOR {0}   base={1}" -f $t.FullName, $bt)
    foreach ($m in ($t.Methods | Sort-Object Name)) {
        if ($m.Name -match "Whitelist") { Write-Host ("        {0}" -f (Format-Sig $m)) }
    }
}

Write-Host ""
Write-Host "=========== 3. Full method surface: the fetch/stockpile quest families ==========="
$targets = @(
  "SSSGame.AI.CrafterFetchQuest",
  "SSSGame.AI.CrafterSpecificFetchQuest",
  "SSSGame.AI.CookingStockpileQuest",
  "SSSGame.AI.DiningStationSupplyQuest",
  "SSSGame.AI.BuildAndSupplyQuest",
  "SSSGame.AI.SupplyPatrolQuest"
)
foreach ($tn in $targets) {
    $t = $byName[$tn]
    if ($null -eq $t) { Write-Host ("  NO TYPE {0}" -f $tn); continue }
    $bt = "?"
    if ($t.BaseType) { $bt = $t.BaseType.FullName }
    Write-Host ("  ### {0}   base={1}" -f $tn, $bt)
    foreach ($m in ($t.Methods | Sort-Object Name)) {
        if ($m.Name -match "^(get_|set_|add_|remove_|\.)") { continue }
        $warn = ""
        if (Has-ByRefPrimitive $m) { $warn = "   <-- BYREF-PRIMITIVE: DO NOT PATCH" }
        if (Has-InventoryFamilyParam $m) { $warn = $warn + "   <-- INVENTORY-FAMILY PARAM: DO NOT PATCH" }
        Write-Host ("        {0}{1}" -f (Format-Sig $m), $warn)
    }
    foreach ($n in $t.NestedTypes) {
        Write-Host ("     --- nested {0}" -f $n.FullName)
        foreach ($m in ($n.Methods | Sort-Object Name)) {
            if ($m.Name -match "^(get_|set_|add_|remove_|\.)") { continue }
            $warn = ""
            if (Has-ByRefPrimitive $m) { $warn = "   <-- BYREF-PRIMITIVE: DO NOT PATCH" }
            if (Has-InventoryFamilyParam $m) { $warn = $warn + "   <-- INVENTORY-FAMILY PARAM: DO NOT PATCH" }
            Write-Host ("        {0}{1}" -f (Format-Sig $m), $warn)
        }
    }
}

Write-Host ""
Write-Host "=========== 4. ResourceStorage whitelist + storage-task surface ==========="
$t = $byName["SSSGame.ResourceStorage"]
if ($null -eq $t) { Write-Host "  NO TYPE SSSGame.ResourceStorage" }
else {
    foreach ($m in ($t.Methods | Sort-Object Name)) {
        if ($m.Name -notmatch "Whitelist|CanCreate|StorageTask|Gather") { continue }
        $warn = ""
        if (Has-ByRefPrimitive $m) { $warn = "   <-- BYREF-PRIMITIVE: DO NOT PATCH" }
        if (Has-InventoryFamilyParam $m) { $warn = $warn + "   <-- INVENTORY-FAMILY PARAM: DO NOT PATCH" }
        Write-Host ("        {0}{1}" -f (Format-Sig $m), $warn)
    }
}

Write-Host ""
Write-Host "=========== 5. Derived overrides of the gate methods we patch/plan to patch ==========="
$gateNames = @("CanCreateStorageTaskForItemInfo", "IsWhitelistedByStorage", "IsWhitelistedByAny", "IsWhitelisted")
foreach ($gn in $gateNames) {
    Write-Host ("  --- {0} declared on:" -f $gn)
    foreach ($t2 in ($allTypes | Sort-Object FullName)) {
        if ($t2.FullName -match "<") { continue }
        if ($t2.FullName -match "MethodInfoStore") { continue }
        foreach ($m in $t2.Methods) {
            if ($m.Name -ne $gn) { continue }
            Write-Host ("        {0}.{1}" -f $t2.FullName, (Format-Sig $m))
        }
    }
}
