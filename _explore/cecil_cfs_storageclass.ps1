# 2026-07-29 follow-up: ItemContainerType.GetStackSize(ItemStorageClass) turned up in the Q3 pass,
# so the game DOES have a per-item storage-class concept - the earlier filter (Size|Volume|Bulk|
# Weight) simply missed the word "StorageClass". Three things to confirm:
#   1. ItemStorageClass itself - enum values, or a ScriptableObject asset per class?
#   2. Does ItemInfo carry one? (its full dump came back with no members, which needs a re-read)
#   3. Which members anywhere take or return an ItemStorageClass.
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

# ---- 1: every type whose name contains StorageClass ----
Write-Output "=== 1: types matching StorageClass ==="
foreach ($t in $allTypes) {
    if ($t.FullName -match "StorageClass") {
        Write-Output "  TYPE: $($t.FullName)  ENUM=$($t.IsEnum)  BASE=$($t.BaseType.FullName)"
        if ($t.IsEnum) { foreach ($fl in $t.Fields) { if ($fl.HasConstant) { Write-Output "     E: $($fl.Name) = $($fl.Constant)" } } }
        else {
            foreach ($p in $t.Properties) { Write-Output "     P: $($p.PropertyType.FullName) $($p.Name)" }
            foreach ($fl in $t.Fields) { Write-Output "     F: $($fl.FieldType.FullName) $($fl.Name)" }
        }
    }
}

# ---- 2: ItemInfo - FIELDS as well as properties (the earlier dump showed neither) ----
Write-Output ""
Write-Output "=== 2: SandSailorStudio.Inventory.ItemInfo full member list ==="
$t = $byName["SandSailorStudio.Inventory.ItemInfo"]
if (-not $t) { Write-Output "  [NOT FOUND]" }
else {
    Write-Output "  BASE: $($t.BaseType.FullName)"
    Write-Output "  --- FIELDS ---"
    foreach ($fl in $t.Fields) { Write-Output "  F: $($fl.FieldType.FullName) $($fl.Name)" }
    Write-Output "  --- PROPERTIES ---"
    foreach ($p in $t.Properties) { Write-Output "  P: $($p.PropertyType.FullName) $($p.Name)" }
    Write-Output "  --- METHODS ---"
    foreach ($m in $t.Methods) { Write-Output "  M: $(Sig $m)" }
}

# ---- 3: who takes or returns an ItemStorageClass ----
Write-Output ""
Write-Output "=== 3: members mentioning ItemStorageClass anywhere ==="
$h = 0
foreach ($t in $allTypes) {
    foreach ($p in $t.Properties) { if ($p.PropertyType.FullName -match "StorageClass") { Write-Output "  $($t.FullName) :: P: $($p.PropertyType.FullName) $($p.Name)"; $h++ } }
    foreach ($fl in $t.Fields) { if ($fl.FieldType.FullName -match "StorageClass") { Write-Output "  $($t.FullName) :: F: $($fl.FieldType.FullName) $($fl.Name)"; $h++ } }
    foreach ($m in $t.Methods) { if ((Sig $m) -match "StorageClass") { Write-Output "  $($t.FullName) :: M: $(Sig $m)"; $h++ } }
}
if ($h -eq 0) { Write-Output "  NO HITS" }

# ---- 4: the CarpenterInteraction / AnvilInteraction family, for the transform discriminator ----
Write-Output ""
Write-Output "=== 4: every type deriving from CraftInteraction (direct or transitive) ==="
function Derives($t, $target) {
    $cur = $t; $guard = 0
    while ($cur -and $cur.BaseType -and $guard -lt 20) {
        if ($cur.BaseType.FullName -eq $target) { return $true }
        $cur = $byName[$cur.BaseType.FullName]; $guard++ }
    return $false
}
foreach ($t in $allTypes) {
    if (Derives $t "SSSGame.CraftInteraction") {
        $anvil = if (Derives $t "SSSGame.AnvilInteraction") { "  <<< derives from AnvilInteraction" } else { "" }
        Write-Output "  $($t.FullName)$anvil"
    }
}

Write-Output ""
Write-Output "DONE"
