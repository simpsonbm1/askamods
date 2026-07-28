# Station-kind follow-up pass (2026-07-27). Same helpers as cecil_station_kind.ps1.
# Targets the auxiliary side surfaced by pass 1: CarpenterInteraction -> AnvilInteraction,
# DyeingInteraction -> CraftInteraction, plus blueprint types and StructureTemplate links.
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
function DerivesFrom($t, $target) {
    $cur = $t; $guard = 0
    while ($cur -and $cur.BaseType -and $guard -lt 20) {
        if ($cur.BaseType.FullName -eq $target) { return $true }
        $cur = $byName[$cur.BaseType.FullName]; $guard++ }
    return $false
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
    Write-Output "  ---- METHODS (non-accessor first pass: all) ----"
    foreach ($m in $t.Methods) { if ($m.Name -notmatch "^(get|set)_(Native|field_Compiler)") { Write-Output "  M: $(Sig $m)" } }
}

Write-Output "=================================================================="
Write-Output "A: TYPES DERIVING (transitively) FROM SSSGame.Interaction, name-filtered"
Write-Output "   to Craft|Anvil|Curing|Study|Dye|Loom|Tan|Forge|Smelt|Saw|Plank|Weav"
Write-Output "=================================================================="
$h = 0
foreach ($t in $allTypes) {
    if (DerivesFrom $t "SSSGame.Interaction") {
        if ($t.Name -match "(?i)Craft|Anvil|Curing|Study|Dye|Loom|Tan|Forge|Smelt|Saw|Plank|Weav") {
            Write-Output "  $($t.FullName)   base=$($t.BaseType.FullName)"; $h++ } } }
if ($h -eq 0) { Write-Output "  NO HITS" }

Write-Output ""
Write-Output "=================================================================="
Write-Output "B: EVERY type deriving from SSSGame.CraftInteraction / SSSGame.AnvilInteraction"
Write-Output "=================================================================="
foreach ($tgt in @("SSSGame.CraftInteraction","SSSGame.AnvilInteraction")) {
    Write-Output ""
    Write-Output "--- derived from $tgt ---"
    $h = 0
    foreach ($t in $allTypes) {
        if (DerivesFrom $t $tgt) { Write-Output "  $($t.FullName)   base=$($t.BaseType.FullName)"; $h++ } }
    if ($h -eq 0) { Write-Output "  NO HITS" }
}

foreach ($tn in @(
    "SSSGame.AnvilInteraction",
    "SSSGame.CarpenterInteraction",
    "SSSGame.CraftBlueprint",
    "SSSGame.CraftBlueprintInfo",
    "SSSGame.CraftingStation/StudyProject",
    "SSSGame.StructureTemplate")) { DumpType $tn }

Write-Output ""
Write-Output "=================================================================="
Write-Output "C: StructureTemplate members matching Workstation|Station|Job|Slot|Module|Addon"
Write-Output "=================================================================="
$t = $byName["SSSGame.StructureTemplate"]
$PAT = "(?i)Workstation|Station|Job|Slot|Module|Addon"
$h = 0
if ($t) {
    foreach ($p in $t.Properties) { if ($p.Name -match $PAT -or $p.PropertyType.FullName -match $PAT) { Write-Output "  P: $($p.PropertyType.FullName) $($p.Name)"; $h++ } }
    foreach ($m in $t.Methods) { if ((Sig $m) -match $PAT) { Write-Output "  M: $(Sig $m)"; $h++ } }
}
if ($h -eq 0) { Write-Output "  NO HITS" }

Write-Output ""
Write-Output "=================================================================="
Write-Output "D: every member ANYWHERE returning/typed SSSGame.CraftingStation"
Write-Output "   (who owns / hands out a CraftingStation reference)"
Write-Output "=================================================================="
$h = 0
foreach ($t in $allTypes) {
    foreach ($p in $t.Properties) { if ($p.PropertyType.FullName -eq "SSSGame.CraftingStation") { Write-Output "  $($t.FullName) :: P: SSSGame.CraftingStation $($p.Name)"; $h++ } }
    foreach ($m in $t.Methods) { if ($m.ReturnType.FullName -eq "SSSGame.CraftingStation" -and $m.Name -notmatch "^get_") { Write-Output "  $($t.FullName) :: M: $(Sig $m)"; $h++ } }
}
if ($h -eq 0) { Write-Output "  NO HITS" }

Write-Output ""
Write-Output "=================================================================="
Write-Output "E: SSSGame.AI.TaskDataComponents enum values"
Write-Output "=================================================================="
$t = $byName["SSSGame.AI.TaskDataComponents"]
if ($t) { foreach ($f in $t.Fields) { Write-Output "  F: $($f.FieldType.FullName) $($f.Name)" } } else { Write-Output "  NOT FOUND" }

Write-Output ""
Write-Output "DONE"
