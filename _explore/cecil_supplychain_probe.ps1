# SupplyChainMod Phase 0 probe: Complaint hierarchy, ItemCollection, ItemManifest, Blueprint surfaces
$asmPath = "D:\SteamLibrary\steamapps\common\ASKA\BepInEx\interop\Assembly-CSharp.dll"
[void][System.Reflection.Assembly]::LoadFrom("d:\Claude Projects\askamods\_explore\bin\Debug\net10.0\Mono.Cecil.dll")
$asm = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($asmPath)
$mod = $asm.MainModule

$allTypes = New-Object System.Collections.Generic.List[object]
foreach ($t in $mod.Types) {
    $allTypes.Add($t)
    foreach ($n in $t.NestedTypes) { $allTypes.Add($n) }
}

$inv = [Mono.Cecil.AssemblyDefinition]::ReadAssembly("D:\SteamLibrary\steamapps\common\ASKA\BepInEx\interop\SandSailorStudio.dll")
foreach ($t in $inv.MainModule.Types) {
    $allTypes.Add($t)
    foreach ($n in $t.NestedTypes) { $allTypes.Add($n) }
}

function Sig($m) {
    $ps = ($m.Parameters | ForEach-Object { "$($_.ParameterType.Name) $($_.Name)" }) -join ", "
    $flags = @()
    if ($m.IsVirtual) { $flags += "virtual" }
    if ($m.IsStatic) { $flags += "static" }
    $fs = if ($flags) { " [" + ($flags -join ",") + "]" } else { "" }
    "$($m.ReturnType.Name) $($m.Name)($ps)$fs"
}

function DumpType($tn) {
    foreach ($t in ($allTypes | Where-Object { $_.Name -eq $tn })) {
        $base = if ($t.BaseType) { $t.BaseType.FullName } else { "none" }
        Write-Output "TYPE: $($t.FullName) base=$base"
        foreach ($f in $t.Fields) { if ($f.Name -notmatch "^Native") { Write-Output "  F: $($f.Name) : $($f.FieldType.Name)" } }
        foreach ($p in $t.Properties) { Write-Output "  P: $($p.Name) : $($p.PropertyType.Name)" }
        foreach ($m in $t.Methods) { if ($m.Name -notmatch "^get_|^set_") { Write-Output "  M: $(Sig $m)" } }
        Write-Output ""
    }
}

Write-Output "===== Complaint hierarchy ====="
foreach ($tn in @("Complaint","ItemComplaint","ItemManifestComplaint","ItemCategoryComplaint","ComplexCategoryComplaint","LoadoutsComplaint","StructureComplaint","HarvestMarkerComplaint","GenericMessageComplaint")) {
    DumpType $tn
}

Write-Output "===== ItemCollection / ItemManifest / ItemInfoQuantity ====="
foreach ($tn in @("ItemCollection","ItemManifest","ItemInfoQuantity")) {
    DumpType $tn
}

Write-Output "===== CraftingStation blueprint methods ====="
foreach ($t in ($allTypes | Where-Object { $_.Name -eq "CraftingStation" })) {
    foreach ($m in $t.Methods) {
        if ($m.Name -match "Blueprint|Recipe|Manifest") { Write-Output "  $(Sig $m)" }
    }
}

Write-Output ""
Write-Output "===== Blueprint / BlueprintInfo base surfaces ====="
foreach ($tn in @("Blueprint","BlueprintInfo")) {
    DumpType $tn
}

Write-Output "===== ItemManifest static factory methods ====="
foreach ($t in ($allTypes | Where-Object { $_.Name -eq "ItemManifest" })) {
    foreach ($m in $t.Methods) {
        if ($m.IsStatic) { Write-Output "  $(Sig $m)" }
    }
}
