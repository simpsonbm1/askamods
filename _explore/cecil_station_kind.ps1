# Station-kind discriminator evidence pass (2026-07-27).
# Adapted from cecil_cfs_v09.ps1 (same assembly-index + Sig/BaseChain/DumpType helpers).
# Question: how does the game distinguish a workshop's MAIN crafting table from an
# attached AUXILIARY transform station? Evidence only - no conclusions.
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

$KEY = "(?i)Kind|Role|Category|Definition|Config|IsMain|Primary|Parent|Child|Attach|Recipe|Blueprint|Craftable|Transform|Refine|Process|Convert|Aux"

function Sig($m) {
    $ps = ($m.Parameters | ForEach-Object {
        $pt = $_.ParameterType
        $s = "$($pt.FullName) $($_.Name)"
        if ($pt.IsByReference) { $s += " <<BY-REF>>" }
        $s
    }) -join ", "
    $mods = @()
    if ($m.IsStatic) { $mods += "static" }
    if ($m.IsVirtual) { $mods += "virt" }
    if ($m.IsPublic) { $mods += "pub" } elseif ($m.IsFamily) { $mods += "prot" } else { $mods += "priv" }
    "$($m.ReturnType.FullName) $($m.Name)($ps) [" + ($mods -join ",") + "]"
}

function BaseChain($t) {
    $chain = @(); $cur = $t; $guard = 0
    while ($cur -and $cur.BaseType -and $guard -lt 20) {
        $bn = $cur.BaseType.FullName; $chain += $bn; $cur = $byName[$bn]; $guard++
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

# ---------------------------------------------------------------- Q1: hierarchy
Write-Output "=================================================================="
Write-Output "Q1a: TYPES DERIVING (transitively) FROM SSSGame.CraftingStation"
Write-Output "=================================================================="
function DerivesFrom($t, $target) {
    $cur = $t; $guard = 0
    while ($cur -and $cur.BaseType -and $guard -lt 20) {
        if ($cur.BaseType.FullName -eq $target) { return $true }
        $cur = $byName[$cur.BaseType.FullName]; $guard++
    }
    return $false
}
$n = 0
foreach ($t in $allTypes) {
    if (DerivesFrom $t "SSSGame.CraftingStation") {
        Write-Output "  $($t.FullName)   base=$($t.BaseType.FullName)   [$($t.Module.Assembly.Name.Name)]"
        $n++
    }
}
if ($n -eq 0) { Write-Output "  NO HITS - nothing derives from SSSGame.CraftingStation" }

Write-Output ""
Write-Output "=================================================================="
Write-Output "Q1b: TYPES DERIVING (transitively) FROM SSSGame.Workstation"
Write-Output "=================================================================="
$n = 0
foreach ($t in $allTypes) {
    if (DerivesFrom $t "SSSGame.Workstation") {
        Write-Output "  $($t.FullName)   base=$($t.BaseType.FullName)   [$($t.Module.Assembly.Name.Name)]"
        $n++
    }
}
if ($n -eq 0) { Write-Output "  NO HITS - nothing derives from SSSGame.Workstation" }

Write-Output ""
Write-Output "=================================================================="
Write-Output "Q1c: TYPE-NAME KEYWORD SWEEP (SSSGame/SandSailor namespaces only)"
Write-Output "=================================================================="
foreach ($kw in @("Transform","Refine","Process","Convert","Auxiliary","Aux","Carpenter","Metalwork","Leatherwork","Weaver","Dyer","Dyeing","Bench","Table","Workbench")) {
    Write-Output ""
    Write-Output "--- keyword: $kw ---"
    $h = 0
    foreach ($t in $allTypes) {
        if ($t.FullName -notmatch "^(SSSGame|SandSailorStudio)") { continue }
        if ($t.Name -match "(?i)$kw") {
            $bt = if ($t.BaseType) { $t.BaseType.FullName } else { "(no base)" }
            Write-Output "  $($t.FullName)   base=$bt   [$($t.Module.Assembly.Name.Name)]"
            $h++
        }
    }
    if ($h -eq 0) { Write-Output "  NO HITS" }
}

# ---------------------------------------------------------------- Q2/Q3: member sweeps
Write-Output ""
Write-Output "=================================================================="
Write-Output "Q2: KIND/ROLE/TYPE/CATEGORY-SHAPED MEMBERS ON CraftingStation + Workstation"
Write-Output "=================================================================="
$KINDPAT = "(?i)Kind|Type|Role|Category|Info$|Definition|Config|Data|IsMain|Main|Primary|Parent|Child|Attach|Sub|Slot"
foreach ($tn in @("SSSGame.CraftingStation","SSSGame.Workstation")) {
    $t = $byName[$tn]
    Write-Output ""
    Write-Output "--- $tn ---"
    $h = 0
    foreach ($p in $t.Properties) { if ($p.Name -match $KINDPAT) { Write-Output "  P: $($p.PropertyType.FullName) $($p.Name)"; $h++ } }
    foreach ($m in $t.Methods) { if ($m.Name -match $KINDPAT -and $m.Name -notmatch "^(get|set)__") { Write-Output "  M: $(Sig $m)"; $h++ } }
    foreach ($f in $t.Fields) { if ($f.Name -match $KINDPAT) { Write-Output "  F: $($f.FieldType.FullName) $($f.Name)"; $h++ } }
    if ($h -eq 0) { Write-Output "  NO HITS" }
}

Write-Output ""
Write-Output "=================================================================="
Write-Output "Q3: RECIPE/BLUEPRINT-SHAPED MEMBERS ON CraftingStation + Workstation"
Write-Output "=================================================================="
$BPPAT = "(?i)Recipe|Blueprint|Bp|Craftable|Project|Knows"
foreach ($tn in @("SSSGame.CraftingStation","SSSGame.Workstation")) {
    $t = $byName[$tn]
    Write-Output ""
    Write-Output "--- $tn ---"
    $h = 0
    foreach ($p in $t.Properties) { if ($p.Name -match $BPPAT -or $p.PropertyType.FullName -match $BPPAT) { Write-Output "  P: $($p.PropertyType.FullName) $($p.Name)"; $h++ } }
    foreach ($m in $t.Methods) { if ((Sig $m) -match $BPPAT) { Write-Output "  M: $(Sig $m)"; $h++ } }
    foreach ($f in $t.Fields) { if ($f.Name -match $BPPAT -or $f.FieldType.FullName -match $BPPAT) { Write-Output "  F: $($f.FieldType.FullName) $($f.Name)"; $h++ } }
    if ($h -eq 0) { Write-Output "  NO HITS" }
}

# ---------------------------------------------------------------- Q4 + full dumps
foreach ($tn in @(
    "SSSGame.CraftingStationType",
    "SSSGame.CraftingStation/CraftingProject",
    "SSSGame.CraftInteraction",
    "SSSGame.Structure")) { DumpType $tn }

Write-Output ""
Write-Output "=================================================================="
Write-Output "Q4: STRUCTURE-SIDE LINK MEMBERS (Structure + Workstation)"
Write-Output "=================================================================="
$LINKPAT = "(?i)Workstation|Station|Attach|Slot|Module|Addon|Component|Parent|Children|Child"
foreach ($tn in @("SSSGame.Structure","SSSGame.Workstation")) {
    $t = $byName[$tn]
    Write-Output ""
    Write-Output "--- $tn ---"
    $h = 0
    foreach ($p in $t.Properties) { if ($p.Name -match $LINKPAT -or $p.PropertyType.FullName -match $LINKPAT) { Write-Output "  P: $($p.PropertyType.FullName) $($p.Name)"; $h++ } }
    foreach ($m in $t.Methods) { if ((Sig $m) -match $LINKPAT) { Write-Output "  M: $(Sig $m)"; $h++ } }
    foreach ($f in $t.Fields) { if ($f.Name -match $LINKPAT -or $f.FieldType.FullName -match $LINKPAT) { Write-Output "  F: $($f.FieldType.FullName) $($f.Name)"; $h++ } }
    if ($h -eq 0) { Write-Output "  NO HITS" }
}

Write-Output ""
Write-Output "=================================================================="
Write-Output "PASS X: every member ANYWHERE whose TYPE is SSSGame.CraftingStationType"
Write-Output "=================================================================="
$h = 0
foreach ($t in $allTypes) {
    foreach ($p in $t.Properties) { if ($p.PropertyType.FullName -match "CraftingStationType") { Write-Output "  $($t.FullName) :: P: $($p.PropertyType.FullName) $($p.Name)"; $h++ } }
    foreach ($f in $t.Fields) { if ($f.FieldType.FullName -match "CraftingStationType") { Write-Output "  $($t.FullName) :: F: $($f.FieldType.FullName) $($f.Name)"; $h++ } }
    foreach ($m in $t.Methods) { if ((Sig $m) -match "CraftingStationType") { Write-Output "  $($t.FullName) :: M: $(Sig $m)"; $h++ } }
}
if ($h -eq 0) { Write-Output "  NO HITS" }

Write-Output ""
Write-Output "DONE"
