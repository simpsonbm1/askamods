# v0.9.2 pass C (2026-07-28): the blueprint -> interaction link that decides crafting table vs anvil.
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
    Write-Output "  BASECHAIN: $(BaseChain $t)"
    Write-Output "  ---- PROPERTIES ----"
    foreach ($p in $t.Properties) { Write-Output "  P: $($p.PropertyType.FullName) $($p.Name)" }
    Write-Output "  ---- METHODS ----"
    foreach ($m in $t.Methods) { if ($m.Name -notmatch "^(get|set)_(Native|field_Compiler)") { Write-Output "  M: $(Sig $m)" } }
}

foreach ($tn in @(
    "SSSGame.CraftBlueprintInfo",
    "SandSailorStudio.Inventory.BlueprintInfo",
    "SandSailorStudio.Inventory.Blueprint",
    "SSSGame.AnvilBlueprintInfo",
    "SSSGame.CarpenterBlueprintInfo")) { DumpType $tn }

Write-Output ""
Write-Output "=================================================================="
Write-Output "A: EVERY type deriving (transitively) from SandSailorStudio.Inventory.BlueprintInfo"
Write-Output "=================================================================="
$h = 0
foreach ($t in $allTypes) {
    if (DerivesFrom $t "SandSailorStudio.Inventory.BlueprintInfo") { Write-Output "  $($t.FullName)   base=$($t.BaseType.FullName)"; $h++ } }
if ($h -eq 0) { Write-Output "  NO HITS" }

Write-Output ""
Write-Output "=================================================================="
Write-Output "B: EVERY type deriving (transitively) from SandSailorStudio.Inventory.Blueprint"
Write-Output "=================================================================="
$h = 0
foreach ($t in $allTypes) {
    if (DerivesFrom $t "SandSailorStudio.Inventory.Blueprint") { Write-Output "  $($t.FullName)   base=$($t.BaseType.FullName)"; $h++ } }
if ($h -eq 0) { Write-Output "  NO HITS" }

Write-Output ""
Write-Output "=================================================================="
Write-Output "C: members typed CraftBlueprintInfo anywhere"
Write-Output "=================================================================="
$h = 0
foreach ($t in $allTypes) {
    foreach ($p in $t.Properties) { if ($p.PropertyType.FullName -match "CraftBlueprintInfo") { Write-Output "  $($t.FullName) :: P: $($p.PropertyType.FullName) $($p.Name)"; $h++ } }
    foreach ($m in $t.Methods) { if ((Sig $m) -match "CraftBlueprintInfo" -and $m.Name -notmatch "^get_") { Write-Output "  $($t.FullName) :: M: $(Sig $m)"; $h++ } }
}
if ($h -eq 0) { Write-Output "  NO HITS" }

Write-Output ""
Write-Output "DONE"
