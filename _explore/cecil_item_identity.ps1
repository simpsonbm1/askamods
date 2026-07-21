# Locale-safety research: what is language-INDEPENDENT identity for an item / item category?
# Motivating field report (Nexus, 2026-07-21): OuthouseComposter + FishFillet work in English only.
# Both gate on ItemInfo.Name / ItemCategoryInfo.Name substring matches.
# Question: which members are asset/id identity (locale-invariant) vs display strings (localized)?
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
    if (-not $t) { Write-Host "### NO TYPE $tn"; return }
    Write-Host "### TYPE $($t.FullName)  base=$($t.BaseType)"
    Write-Host "  -- fields --"
    foreach ($f in $t.Fields) {
        if ($f.Name -match "^(NativeFieldInfoPtr|NativeMethodInfoPtr|NativeClassPtr)") { continue }
        Write-Host "    $($f.Name) : $($f.FieldType.Name)"
    }
    Write-Host "  -- properties --"
    foreach ($p in $t.Properties) { Write-Host "    $($p.Name) : $($p.PropertyType.Name)" }
    Write-Host "  -- methods (name/key/id/loc related) --"
    foreach ($m in $t.Methods) {
        if ($m.Name -match "name|Name|key|Key|id|Id|ID|loc|Loc|Display|Title") { Write-Host "    $(Sig $m)" }
    }
    Write-Host ""
}

Write-Host "==================== ITEM IDENTITY TYPES ===================="
DumpType "SandSailorStudio.Inventory.ItemInfo"
DumpType "SandSailorStudio.Inventory.ItemCategoryInfo"

Write-Host "==================== LOCALIZATION MANAGER ===================="
foreach ($tn in @("SSSGame.LocalizationManager","SandSailorStudio.LocalizationManager","LocalizationManager")) {
    if ($byName.ContainsKey($tn)) { DumpType $tn }
}

Write-Host "==================== ANY TYPE NAMED *Localiz* ===================="
foreach ($t in $allTypes) {
    if ($t.FullName -match "Localiz" -and $t.FullName -notmatch "<") { Write-Host "  $($t.FullName)" }
}
