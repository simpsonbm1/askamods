# CraftFromStorageMod v0.9 evidence pass: member surface of the five types the next change targets.
# Adapted from cecil_craft_from_storage8.ps1 (same assembly-index + Sig helpers), extended with:
#   - full parameter type FullNames + explicit BY-REF / INVENTORY-FAMILY hazard flags
#   - base-type chain walk
#   - keyword call-out pass (Manifest|Fetch|Needed|Suppl|Inventory|Container|Collection)
$base = "D:\SteamLibrary\steamapps\common\ASKA\BepInEx"
[void][System.Reflection.Assembly]::LoadFrom("d:\Claude Projects\askamods\_explore\bin\Debug\net10.0\Mono.Cecil.dll")

$allTypes = New-Object System.Collections.Generic.List[object]
function AddRec($t) {
    $allTypes.Add($t)
    foreach ($n in $t.NestedTypes) { AddRec $n }
}
foreach ($f in Get-ChildItem "$base\interop\*.dll") {
    try { $asm = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($f.FullName) } catch { continue }
    foreach ($t in $asm.MainModule.Types) { AddRec $t }
}
$byName = @{}
foreach ($t in $allTypes) {
    if (-not $byName.ContainsKey($t.FullName)) { $byName[$t.FullName] = $t }
}

$KEY = "(?i)Manifest|Fetch|Needed|Suppl|Inventory|Container|Collection"

function Sig($m) {
    $ps = ($m.Parameters | ForEach-Object {
        $pt = $_.ParameterType
        $s = "$($pt.FullName) $($_.Name)"
        if ($pt.IsByReference) { $s += " <<BY-REF>>" }
        if ($pt.FullName -match "(?i)ItemCollection|ItemEventContext|ItemManifest|\.Item(&|\[\]|$)") { $s += " <<INV-FAMILY>>" }
        $s
    }) -join ", "
    $mods = @()
    if ($m.IsStatic) { $mods += "static" }
    if ($m.IsVirtual) { $mods += "virt" }
    if ($m.IsPublic) { $mods += "pub" } elseif ($m.IsFamily) { $mods += "prot" } else { $mods += "priv" }
    "$($m.ReturnType.FullName) $($m.Name)($ps) [" + ($mods -join ",") + "]"
}

function BaseChain($t) {
    $chain = @()
    $cur = $t
    $guard = 0
    while ($cur -and $cur.BaseType -and $guard -lt 20) {
        $bn = $cur.BaseType.FullName
        $chain += $bn
        $cur = $byName[$bn]
        $guard++
    }
    if ($chain.Count -eq 0) { return "(none)" }
    return ($chain -join "  ->  ")
}

function DumpType($tn) {
    Write-Output ""
    Write-Output "=================================================================="
    Write-Output "TYPE: $tn"
    Write-Output "=================================================================="
    $t = $byName[$tn]
    if (-not $t) {
        Write-Output "  [NOT FOUND under that exact name]"
        $leaf = ($tn -split '[./+]')[-1]
        Write-Output "  -- similar names present in interop --"
        foreach ($x in $allTypes) {
            if ($x.Name -match [regex]::Escape($leaf) -or $leaf -match [regex]::Escape($x.Name)) {
                Write-Output "     $($x.FullName)   [$($x.Module.Assembly.Name.Name)]"
            }
        }
        return
    }
    Write-Output "  ASSEMBLY : $($t.Module.Assembly.Name.Name)"
    Write-Output "  BASECHAIN: $(BaseChain $t)"
    Write-Output ("  INTERFACES: " + (($t.Interfaces | ForEach-Object { $_.InterfaceType.FullName }) -join ", "))
    if ($t.NestedTypes.Count) {
        Write-Output ("  NESTED   : " + (($t.NestedTypes | ForEach-Object { $_.FullName }) -join ", "))
    }

    Write-Output "  ---- PROPERTIES ----"
    foreach ($p in $t.Properties) {
        $mark = if ($p.Name -match $KEY -or $p.PropertyType.FullName -match $KEY) { "   *** KEYWORD" } else { "" }
        Write-Output "  P: $($p.PropertyType.FullName) $($p.Name)$mark"
    }
    Write-Output "  ---- FIELDS ----"
    foreach ($fld in $t.Fields) {
        if ($fld.Name -match "^NativeFieldInfoPtr|^NativeMethodInfoPtr|^NativeClassPtr") { continue }
        $fmod = ""
        if ($fld.IsStatic) { $fmod += " [static]" }
        if ($fld.IsPublic) { $fmod += " [pub]" } elseif ($fld.IsFamily) { $fmod += " [prot]" } else { $fmod += " [priv]" }
        $mark = if ($fld.Name -match $KEY -or $fld.FieldType.FullName -match $KEY) { "   *** KEYWORD" } else { "" }
        Write-Output "  F: $($fld.FieldType.FullName) $($fld.Name)$fmod$mark"
    }
    Write-Output "  ---- METHODS (incl. accessors) ----"
    foreach ($m in $t.Methods) {
        $sg = Sig $m
        $mark = if ($sg -match $KEY) { "   *** KEYWORD" } else { "" }
        Write-Output "  M: $sg$mark"
    }
}

foreach ($tn in @(
    "SSSGame.AI.FSM.FSM_FetchCraftingSupplies",
    "SSSGame.AI.WorkstationQuestData",
    "SSSGame.WorkstationQuestData",
    "SSSGame.AI.WorkstationQuest",
    "SSSGame.AI.WorkstationQuest/WorkstationQuestData",
    "SSSGame.AI.QuestData",
    "SSSGame.AI.Quest",
    "SSSGame.CrafterFetchQuest",
    "SSSGame.CrafterFetchQuest/CrafterFetchQuestData",
    "SSSGame.CraftingStation",
    "SSSGame.Workstation")) { DumpType $tn }

Write-Output ""
Write-Output "=================================================================="
Write-Output "PASS 2: every type whose NAME contains QuestData"
Write-Output "=================================================================="
foreach ($t in $allTypes) {
    if ($t.Name -match "(?i)QuestData") { Write-Output "  $($t.FullName)   [$($t.Module.Assembly.Name.Name)]  base=$($t.BaseType)" }
}

Write-Output ""
Write-Output "=================================================================="
Write-Output "PASS 3: every member ANYWHERE returning or naming ItemManifest"
Write-Output "=================================================================="
foreach ($t in $allTypes) {
    foreach ($m in $t.Methods) {
        if ($m.ReturnType.FullName -match "ItemManifest" -or $m.Name -match "(?i)Manifest") {
            Write-Output "  $($t.FullName) :: M: $(Sig $m)"
        }
    }
    foreach ($p in $t.Properties) {
        if ($p.PropertyType.FullName -match "ItemManifest" -or $p.Name -match "(?i)Manifest") {
            Write-Output "  $($t.FullName) :: P: $($p.PropertyType.FullName) $($p.Name)"
        }
    }
    foreach ($fld in $t.Fields) {
        if ($fld.FieldType.FullName -match "ItemManifest" -or $fld.Name -match "(?i)Manifest") {
            Write-Output "  $($t.FullName) :: F: $($fld.FieldType.FullName) $($fld.Name)"
        }
    }
}

Write-Output ""
Write-Output "=================================================================="
Write-Output "PASS 4: the five expected names - do they exist anywhere?"
Write-Output "=================================================================="
foreach ($n in @("GetNeededSuppliesManifest","GetMinimumFetchManifest","_minimumFetchManifest","FetchRequirementManifest","GetItemsNeededFromSettlement")) {
    $hits = 0
    foreach ($t in $allTypes) {
        foreach ($m in $t.Methods) { if ($m.Name -eq $n) { Write-Output "  [$n] $($t.FullName) :: M: $(Sig $m)"; $hits++ } }
        foreach ($p in $t.Properties) { if ($p.Name -eq $n) { Write-Output "  [$n] $($t.FullName) :: P: $($p.PropertyType.FullName)"; $hits++ } }
        foreach ($fld in $t.Fields) { if ($fld.Name -eq $n) { Write-Output "  [$n] $($t.FullName) :: F: $($fld.FieldType.FullName)"; $hits++ } }
    }
    if ($hits -eq 0) { Write-Output "  [$n] ABSENT everywhere in interop" }
}

Write-Output ""
Write-Output "DONE"
