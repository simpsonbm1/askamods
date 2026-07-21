# Craft requirement-UI research pass 2: dump the concrete panel types that render a blueprint's
# ingredient rows, to find where the "have/need" number is sourced.
# Pure ASCII only (powershell 5.1 reads BOM-less UTF-8 as ANSI).
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
    $v = ""; if ($m.IsVirtual) { $v = " [virt]" }
    "$($m.ReturnType.Name) $($m.Name)($ps)$v"
}
function DumpType($tn) {
    $t = $byName[$tn]
    if (-not $t) { Write-Output "  [NOT FOUND] $tn"; return }
    Write-Output ""
    Write-Output "---- $tn (base: $(if ($t.BaseType) { $t.BaseType.FullName } else { '-' })) ----"
    foreach ($fl in $t.Fields) {
        if ($fl.Name -match "^NativeMethodInfoPtr|^NativeFieldInfoPtr|^__|k__Backing") { continue }
        Write-Output "  F: $($fl.Name) : $($fl.FieldType.Name)"
    }
    foreach ($p in $t.Properties) { Write-Output "  P: $($p.Name) : $($p.PropertyType.Name)" }
    foreach ($m in $t.Methods) {
        if ($m.Name -match "^get_|^set_|^add_|^remove_|^\.c") { continue }
        Write-Output "  M: $(Sig $m)"
    }
}

foreach ($tn in @(
    "SSSGame.UI.ItemDetailsPanel",
    "SSSGame.UI.ItemManifestDisplayer",
    "SSSGame.UI.ItemThumbnailPanel",
    "SSSGame.UI.IconTextPairComponent"
)) { DumpType $tn }

Write-Output ""
Write-Output "=================================================================="
Write-Output "Any type named *ItemDetails* / *ManifestDisplay* / *ItemPanel* anywhere"
Write-Output "=================================================================="
foreach ($t in $allTypes) {
    if ($t.Name -match "ItemDetails|ManifestDisplay|ItemPanel|ItemThumbnail|IconTextPair|ManifestContainer") {
        Write-Output "  TYPE: $($t.FullName)  : $(if ($t.BaseType) { $t.BaseType.Name } else { '-' })"
    }
}

Write-Output ""
Write-Output "=================================================================="
Write-Output "ItemManifest full surface (what a manifest can report per item)"
Write-Output "=================================================================="
DumpType "SandSailorStudio.Inventory.ItemManifest"
