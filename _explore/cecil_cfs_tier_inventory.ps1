# 2026-07-30: what station types belong in each tier of the user's final three-level model?
#
#   default  - PLAYER crafts from storage
#   toggle 1 - VILLAGERS use CRAFTING TABLES from storage (workshops AND the armorsmith)
#   toggle 2 - villagers also TRANSFORM from storage: any workshop add-on, BLOOMERIES, COAL MAKERS
#   deliberately excluded for now: cooking and cheesemaking
#
# Already confirmed (Cecil 2026-07-29/30): exactly three types derive from SSSGame.CraftInteraction -
# AnvilInteraction, CarpenterInteraction (via Anvil) and DyeingInteraction. SSSGame.CraftingStation
# holds _craftingTables / _anvils / _studyInteractions as separate typed lists, CraftInteraction has a
# craftStationHost back-reference, and CraftingQuest/CraftingQuestData carries a CraftInteraction _ci
# naming the exact unit a job targets.
#
# The open question: a bloomery and a coal maker are NOT in that three-type list, so they are probably
# a different family entirely and may not be reachable from the stocker's hook at all (it postfixes
# FSM_FetchCraftingSupplies, a CRAFTING supply fetch). This probe establishes what they are before any
# code is written.
#   Q1 What types exist for bloomery / coal / charcoal / kiln / smelt / furnace, and what do they derive from?
#   Q2 Same for cooking and cheesemaking, so the deliberate exclusion is a named set rather than a guess.
#   Q3 Do these types expose an inventory/supply surface the mod could stock (GetInventory, manifests)?
#   Q4 Which FSM states fetch or deliver supplies, so we know whether one hook covers all tiers.
# Harness copied from cecil_cfs_v092.ps1.
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
    $ps = ($m.Parameters | ForEach-Object { "$($_.ParameterType.FullName) $($_.Name)" }) -join ", "
    $mods = @(); if ($m.IsStatic) { $mods += "static" }; if ($m.IsVirtual) { $mods += "virt" }
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
$NOISE = "^(Il2Cpp|UnityEngine|Fusion|Invector|Pathfinding|Cinemachine|TMPro|System|Mono|Microsoft)"

# ---- Q1/Q2: the candidate station types, by name ----
Write-Output "=================================================================="
Write-Output "Q1/Q2: types matching the tier-2 and excluded-set keywords"
Write-Output "=================================================================="
$PAT = "(?i)Bloomery|Bloom|Coal|Charcoal|Kiln|Smelt|Furnace|Forge|Cook|Cheese|Dairy|Cauldron|Oven|Tanner|Curing"
foreach ($t in $allTypes) {
    if ($t.FullName -match $PAT -and $t.FullName -notmatch $NOISE) {
        Write-Output "  $($t.FullName)"
        Write-Output "      BASE: $(BaseChain $t)"
    }
}

# ---- Q1b: EVERY Interaction subclass in the game, so nothing in a tier is missed ----
Write-Output ""
Write-Output "=================================================================="
Write-Output "Q1b: every type deriving from SSSGame.Interaction (the full station roster)"
Write-Output "=================================================================="
function Derives($t, $target) {
    $cur = $t; $guard = 0
    while ($cur -and $cur.BaseType -and $guard -lt 20) {
        if ($cur.BaseType.FullName -eq $target) { return $true }
        $cur = $byName[$cur.BaseType.FullName]; $guard++ }
    return $false
}
foreach ($t in $allTypes) {
    if ($t.FullName -match $NOISE) { continue }
    if (Derives $t "SSSGame.Interaction") {
        $tag = ""
        if (Derives $t "SSSGame.CraftInteraction") { $tag += "  [CraftInteraction family]" }
        if (Derives $t "SSSGame.AnvilInteraction") { $tag += "  [AnvilInteraction family]" }
        Write-Output "  $($t.FullName)$tag"
    }
}

# ---- Q3: do the tier-2 candidates expose a stockable inventory? ----
Write-Output ""
Write-Output "=================================================================="
Write-Output "Q3: members matching Inventory|Manifest|Supply|Needed|Fuel|Input|Add on"
Write-Output "    the tier-2 candidate types found above"
Write-Output "=================================================================="
$PAT3 = "(?i)Inventory|Manifest|Supply|Needed|Fuel|Input|Container|Collection"
foreach ($t in $allTypes) {
    if ($t.FullName -notmatch "(?i)Bloomery|Coal|Charcoal|Kiln|Smelt|Furnace" -or $t.FullName -match $NOISE) { continue }
    $hits = @()
    foreach ($p in $t.Properties) { if ($p.Name -match $PAT3 -or $p.PropertyType.FullName -match $PAT3) { $hits += "  P: $($p.PropertyType.FullName) $($p.Name)" } }
    foreach ($m in $t.Methods) { if ($m.Name -notmatch "^(get|set)_" -and (Sig $m) -match $PAT3) { $hits += "  M: $(Sig $m)" } }
    if ($hits.Count) {
        Write-Output ""
        Write-Output "  --- $($t.FullName) ---"
        $hits | ForEach-Object { Write-Output $_ }
    }
}

# ---- Q3b: what lists does CraftingStation hold, in full? ----
Write-Output ""
Write-Output "=================================================================="
Write-Output "Q3b: SSSGame.CraftingStation - EVERY List/collection-typed member"
Write-Output "=================================================================="
$t = $byName["SSSGame.CraftingStation"]
if (-not $t) { Write-Output "  [NOT FOUND]" }
else {
    foreach ($p in $t.Properties) { if ($p.PropertyType.FullName -match "(List|Dictionary|HashSet|Array)") { Write-Output "  P: $($p.PropertyType.FullName) $($p.Name)" } }
}

# ---- Q4: which FSM states fetch or deliver supplies? ----
Write-Output ""
Write-Output "=================================================================="
Write-Output "Q4: FSM state types matching Fetch|Deliver|Supply|Haul|Refuel|Load|Feed|Stock"
Write-Output "=================================================================="
foreach ($t in $allTypes) {
    if ($t.FullName -match "(?i)FSM_" -and $t.FullName -match "(?i)Fetch|Deliver|Supply|Haul|Refuel|Load|Feed|Stock|Return") {
        Write-Output "  $($t.FullName)"
    }
}

Write-Output ""
Write-Output "DONE"
