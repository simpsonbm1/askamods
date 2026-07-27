# OuthouseComposter #theft-report - is GetItemManifest() a PATCHABLE choke point?
#
# IResourceStorageSite.GetItemManifest() : ItemManifest is the AI-side "what does this site hold"
# view - zero params, and the same IResourceStorageSite the quest whitelist gates take. Patching it
# for the outhouse would make non-compost contents invisible to EVERY quest, enumerated or not.
#
# BLOCKER: Harmony patches a concrete method, not an interface slot. The previous sweep found ZERO
# types listing IResourceStorageSite in .Interfaces (Il2CppInterop may not model it that way), so we
# do not yet know WHICH class to patch. This finds every concrete declarer of GetItemManifest.
#
# ASCII only, explicit loops - PowerShell 5.1 chokes on inline pipeline joins in assignments.
$base = "D:\SteamLibrary\steamapps\common\ASKA\BepInEx"
[void][System.Reflection.Assembly]::LoadFrom("d:\Claude Projects\askamods\_explore\bin\Debug\net10.0\Mono.Cecil.dll")

$allTypes = New-Object System.Collections.Generic.List[object]
foreach ($f in Get-ChildItem "$base\interop\*.dll") {
    try { $asm = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($f.FullName) } catch { continue }
    foreach ($t in $asm.MainModule.Types) {
        $allTypes.Add($t)
        foreach ($n in $t.NestedTypes) {
            $allTypes.Add($n)
            foreach ($n2 in $n.NestedTypes) { $allTypes.Add($n2) }
        }
    }
}
$byName = @{}
foreach ($t in $allTypes) { if (-not $byName.ContainsKey($t.FullName)) { $byName[$t.FullName] = $t } }
Write-Host ("loaded {0} types" -f $allTypes.Count)

function Format-Sig($m) {
    $ps = @()
    foreach ($p in $m.Parameters) { $ps += ("{0} {1}" -f $p.ParameterType.FullName, $p.Name) }
    $flags = @()
    if ($m.IsVirtual) { $flags += "virtual" }
    if ($m.IsAbstract) { $flags += "abstract" }
    if ($m.IsStatic) { $flags += "static" }
    if ($m.IsPublic) { $flags += "public" } else { $flags += "nonpublic" }
    return ("{0}({1}) : {2}   [{3}]" -f $m.Name, [string]::Join(", ", $ps), $m.ReturnType.Name, [string]::Join(" ", $flags))
}

Write-Host ""
Write-Host "=========== 1. EVERY concrete declarer of GetItemManifest ==========="
Write-Host "   (these are the Harmony-patchable targets; an interface slot is NOT patchable)"
$n = 0
foreach ($t in ($allTypes | Sort-Object FullName)) {
    if ($t.FullName -match "MethodInfoStore") { continue }
    foreach ($m in $t.Methods) {
        if ($m.Name -notmatch "GetItemManifest") { continue }
        $kind = "class"
        if ($t.IsInterface) { $kind = "INTERFACE" }
        $bt = "?"
        if ($t.BaseType) { $bt = $t.BaseType.Name }
        Write-Host ("  [{0}] {1}   base={2}" -f $kind, $t.FullName, $bt)
        Write-Host ("        {0}" -f (Format-Sig $m))
        $n++
    }
}
Write-Host ("  {0} declarer(s)" -f $n)

Write-Host ""
Write-Host "=========== 2. How interface implementation is modelled at all ==========="
Write-Host "   (sanity check: does ANY type list IResourceStorageSite / IWhitelistingSite?)"
foreach ($iface in @("IResourceStorageSite", "IWhitelistingSite", "IItemFilter")) {
    $c = 0
    foreach ($t in $allTypes) {
        foreach ($i in $t.Interfaces) { if ($i.InterfaceType.Name -eq $iface) { $c++ } }
    }
    Write-Host ("  {0}: {1} type(s) declare it in .Interfaces" -f $iface, $c)
}

Write-Host ""
Write-Host "=========== 3. IItemFilter surface (can we tell WHAT a query asked for?) ==========="
Write-Host "   (needed to keep Compost visible while hiding everything else)"
foreach ($t in ($allTypes | Sort-Object FullName)) {
    if ($t.FullName -match "MethodInfoStore") { continue }
    if ($t.Name -ne "IItemFilter") { continue }
    Write-Host ("  ### {0}" -f $t.FullName)
    foreach ($m in ($t.Methods | Sort-Object Name)) { Write-Host ("        {0}" -f (Format-Sig $m)) }
}
Write-Host "  --- concrete IItemFilter-shaped types (name match) ---"
foreach ($t in ($allTypes | Sort-Object FullName)) {
    if ($t.FullName -match "MethodInfoStore") { continue }
    if ($t.FullName -match "<") { continue }
    if ($t.Name -notmatch "ItemFilter|ItemManifest") { continue }
    $bt = "?"
    if ($t.BaseType) { $bt = $t.BaseType.Name }
    Write-Host ("    {0}   base={1}" -f $t.FullName, $bt)
}

Write-Host ""
Write-Host "=========== 4. ItemManifest surface (can we build/filter one safely?) ==========="
$t = $byName["SandSailorStudio.Inventory.ItemManifest"]
if ($null -eq $t) { Write-Host "  NO TYPE SandSailorStudio.Inventory.ItemManifest" }
else {
    foreach ($m in ($t.Methods | Sort-Object Name)) {
        if ($m.Name -match "^(add_|remove_|set_)") { continue }
        Write-Host ("        {0}" -f (Format-Sig $m))
    }
}
