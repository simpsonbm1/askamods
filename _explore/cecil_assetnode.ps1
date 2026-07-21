# Follow-up: does AssetNode (base of ItemInfo/ItemCategoryInfo) expose a locale-INVARIANT
# asset name? And what does ItemStorageClass look like (architecture.md says it is an asset name)?
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

function DumpFull($tn) {
    $t = $byName[$tn]
    if (-not $t) { Write-Host "### NO TYPE $tn`n"; return }
    Write-Host "### TYPE $($t.FullName)  base=$($t.BaseType)"
    Write-Host "  -- properties --"
    foreach ($p in $t.Properties) { Write-Host "    $($p.Name) : $($p.PropertyType.Name)" }
    Write-Host "  -- methods --"
    foreach ($m in $t.Methods) {
        if ($m.Name -match "^(get_|set_)") { continue }
        $ps = ($m.Parameters | ForEach-Object { "$($_.ParameterType.Name) $($_.Name)" }) -join ", "
        Write-Host "    $($m.ReturnType.Name) $($m.Name)($ps)"
    }
    Write-Host ""
}

DumpFull "SandSailorStudio.Assets.AssetNode"
DumpFull "SandSailorStudio.Inventory.ItemStorageClass"
DumpFull "SSSGame.ILocalizedEntity"
DumpFull "SandSailorStudio.Localization.LocalizationManager"

Write-Host "==================== ItemCategoryInfo ancestry / who else derives AssetNode ===================="
foreach ($t in $allTypes) {
    if ($t.BaseType -and $t.BaseType.FullName -match "AssetNode") { Write-Host "  $($t.FullName)" }
}
