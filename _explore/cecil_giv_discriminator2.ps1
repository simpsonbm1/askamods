# Follow-up: is there a cheaper/more direct "is this a loose dropped item" flag than the
# source-prefab DynamicItemObject check? Look at ItemInfo, ItemObjectSpawnContext, and the two
# InventoryItemInstancesBufferBase subclasses.
$base = "D:\SteamLibrary\steamapps\common\ASKA\BepInEx"
[void][System.Reflection.Assembly]::LoadFrom("d:\Claude Projects\askamods\_explore\bin\Debug\net10.0\Mono.Cecil.dll")

$allTypes = New-Object System.Collections.Generic.List[object]
foreach ($f in Get-ChildItem "$base\interop\*.dll") {
    try { $asm = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($f.FullName) } catch { continue }
    foreach ($t in $asm.MainModule.Types) {
        $allTypes.Add($t)
        foreach ($n in $t.NestedTypes) { $allTypes.Add($n) }
    }
}
$byName = @{}
foreach ($t in $allTypes) { if (-not $byName.ContainsKey($t.FullName)) { $byName[$t.FullName] = $t } }

function Sig($m) {
    $ps = ($m.Parameters | ForEach-Object { "$($_.ParameterType.Name) $($_.Name)" }) -join ", "
    $mods = @()
    if ($m.IsStatic) { $mods += "static" }
    if ($m.IsVirtual) { $mods += "virt" }
    if ($m.IsPublic) { $mods += "pub" } elseif ($m.IsFamily) { $mods += "prot" } else { $mods += "priv" }
    "$($m.ReturnType.Name) $($m.Name)($ps) [" + ($mods -join ",") + "]"
}
function DumpType($tn) {
    $t = $byName[$tn]
    if (-not $t) { Write-Host "### NO TYPE $tn`n"; return }
    Write-Host "### TYPE $($t.FullName)  base=$($t.BaseType)"
    if ($t.IsEnum) { foreach ($f in $t.Fields) { if ($f.HasConstant) { Write-Host "    $($f.Name) = $($f.Constant)" } }; Write-Host ""; return }
    Write-Host "  -- properties --"
    foreach ($p in $t.Properties) { Write-Host "    $($p.Name) : $($p.PropertyType.Name)" }
    Write-Host "  -- methods --"
    foreach ($m in $t.Methods) { Write-Host "    $(Sig $m)" }
    Write-Host ""
}

DumpType "SSSGame.ItemObjectSpawnContext"
DumpType "SandSailorStudio.Inventory.ItemInfo"
DumpType "SSSGame.InventoryItemInstancesBufferBase"
DumpType "SSSGame.InventoryItemInstancesBuffer"
DumpType "SSSGame.VegetationItemInstancesBuffer"
DumpType "SSSGame.InventoryCellDataContainer"

Write-Host "==================== Types whose name contains WorldItem / ItemObject ===================="
foreach ($t in $allTypes) {
    if ($t.FullName -match "SSSGame\.[A-Za-z]*(WorldItem|ItemObject)[A-Za-z]*$" -and $t.FullName -notmatch "<") {
        Write-Host "  $($t.FullName)  base=$($t.BaseType)"
    }
}

Write-Host "`n==================== MonoBehaviours that look like world-placed item objects ===================="
foreach ($t in $allTypes) {
    if ($t.BaseType -and $t.BaseType.FullName -eq "SSSGame.WorldItemObject") { Write-Host "  SUBCLASS OF WorldItemObject: $($t.FullName)" }
}
foreach ($t in $allTypes) {
    if ($t.BaseType -and $t.BaseType.FullName -eq "SandSailorStudio.Inventory.ItemComponent") { Write-Host "  ItemComponent subclass: $($t.FullName)" }
}
