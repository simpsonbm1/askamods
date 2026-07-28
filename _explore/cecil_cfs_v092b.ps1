# v0.9.2 pass B (2026-07-28): is the metalworker/forge a DISTINCT CraftingStation subclass or a
# CraftingStationType value? Either would make the "crafting tables only" gate a one-line check.
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
    if ($t.NestedTypes.Count) { Write-Output ("  NESTED   : " + (($t.NestedTypes | ForEach-Object { $_.FullName }) -join ", ")) }
    Write-Output "  ---- FIELDS (enum values land here) ----"
    foreach ($f in $t.Fields) { if ($f.Name -notmatch "^(NativeFieldInfoPtr|NativeMethodInfoPtr)") { Write-Output "  F: $($f.FieldType.FullName) $($f.Name)" } }
    Write-Output "  ---- PROPERTIES ----"
    foreach ($p in $t.Properties) { Write-Output "  P: $($p.PropertyType.FullName) $($p.Name)" }
    Write-Output "  ---- METHODS ----"
    foreach ($m in $t.Methods) { if ($m.Name -notmatch "^(get|set)_(Native|field_Compiler)") { Write-Output "  M: $(Sig $m)" } }
}

Write-Output "=================================================================="
Write-Output "A: EVERY type deriving (transitively) from SSSGame.CraftingStation"
Write-Output "=================================================================="
$h = 0
foreach ($t in $allTypes) {
    if (DerivesFrom $t "SSSGame.CraftingStation") { Write-Output "  $($t.FullName)   base=$($t.BaseType.FullName)"; $h++ } }
if ($h -eq 0) { Write-Output "  NO HITS" }

Write-Output ""
Write-Output "=================================================================="
Write-Output "B: EVERY type deriving (transitively) from SSSGame.Workstation"
Write-Output "=================================================================="
$h = 0
foreach ($t in $allTypes) {
    if (DerivesFrom $t "SSSGame.Workstation") { Write-Output "  $($t.FullName)   base=$($t.BaseType.FullName)"; $h++ } }
if ($h -eq 0) { Write-Output "  NO HITS" }

foreach ($tn in @(
    "SSSGame.CraftingStationType",
    "SSSGame.Bloomstation",
    "SSSGame.CraftingQuest/CraftingQuestData",
    "SSSGame.CrafterFetchQuest/CrafterFetchQuestData",
    "SSSGame.CraftInteraction",
    "SSSGame.AnvilInteraction")) { DumpType $tn }

Write-Output ""
Write-Output "=================================================================="
Write-Output "C: every member ANYWHERE typed SSSGame.CraftingStationType"
Write-Output "=================================================================="
$h = 0
foreach ($t in $allTypes) {
    foreach ($p in $t.Properties) { if ($p.PropertyType.FullName -match "CraftingStationType") { Write-Output "  $($t.FullName) :: P: $($p.PropertyType.FullName) $($p.Name)"; $h++ } }
    foreach ($m in $t.Methods) { if ((Sig $m) -match "CraftingStationType" -and $m.Name -notmatch "^get_") { Write-Output "  $($t.FullName) :: M: $(Sig $m)"; $h++ } }
}
if ($h -eq 0) { Write-Output "  NO HITS" }

Write-Output ""
Write-Output "=================================================================="
Write-Output "D: every member ANYWHERE typed SSSGame.Bloomstation"
Write-Output "=================================================================="
$h = 0
foreach ($t in $allTypes) {
    foreach ($p in $t.Properties) { if ($p.PropertyType.FullName -match "Bloomstation") { Write-Output "  $($t.FullName) :: P: $($p.PropertyType.FullName) $($p.Name)"; $h++ } }
    foreach ($m in $t.Methods) { if ((Sig $m) -match "Bloomstation" -and $m.Name -notmatch "^get_") { Write-Output "  $($t.FullName) :: M: $(Sig $m)"; $h++ } }
}
if ($h -eq 0) { Write-Output "  NO HITS" }

Write-Output ""
Write-Output "DONE"
