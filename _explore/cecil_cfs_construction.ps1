# 2026-08-08: does CraftFromStorageMod's reach extend to BUILDING CONSTRUCTION?
#
# Nexus report (hill2hell, 2026-08-08) says builders instantly completed plotted structures while
# the mod's villager half was on. The mod patches no construction type BY NAME, but two paths could
# still reach construction:
#   Q1 Does any construction/building type DERIVE from SSSGame.CraftInteraction? A patch on
#      CraftInteraction.CheckOwnedRequirements fires for every subclass that does not override it.
#   Q2 Do builders use FSM_FetchCraftingSupplies (the station stocker's hook), or their own FSM?
# Harness copied from cecil_cfs_station_unit.ps1.
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

function BaseChain($t) {
    $chain = @(); $cur = $t; $guard = 0
    while ($cur -and $cur.BaseType -and $guard -lt 20) {
        $bn = $cur.BaseType.FullName; $chain += $bn; $cur = $byName[$bn]; $guard++ }
    if ($chain.Count -eq 0) { return "(none)" }
    return ($chain -join "  ->  ")
}

Write-Output "=== Q1a: every type whose base chain reaches SSSGame.CraftInteraction ==="
foreach ($t in $allTypes) {
    $chain = BaseChain $t
    if ($chain -match "SSSGame\.CraftInteraction") {
        Write-Output ("  " + $t.FullName + "   ::   " + $chain)
    }
}

Write-Output ""
Write-Output "=== Q1b: does any type declare its own CheckOwnedRequirements? ==="
foreach ($t in $allTypes) {
    foreach ($m in $t.Methods) {
        if ($m.Name -eq "CheckOwnedRequirements") { Write-Output ("  " + $t.FullName + "." + $m.Name) }
    }
}

Write-Output ""
Write-Output "=== Q1c: construction/building-named types and their base chains ==="
foreach ($t in $allTypes) {
    if ($t.FullName -match "Constructi|BuildSite|BuildingSite|ConstructionSite|Blueprint.*Structure|StructureBuild") {
        Write-Output ("  " + $t.FullName + "   ::   " + (BaseChain $t))
    }
}

Write-Output ""
Write-Output "=== Q2: every FSM_Fetch* / FSM_*Build* state action type ==="
foreach ($t in $allTypes) {
    if ($t.Name -match "^FSM_.*(Fetch|Build|Construct)") {
        Write-Output ("  " + $t.FullName + "   ::   " + (BaseChain $t))
    }
}
