# OuthouseComposter - is there a VANILLA, item-aware lever on ResourceStorage that makes forced-in
# food ungatherable while leaving Compost gatherable? No Harmony patch on a fragile hot method.
#
# Lead: SSSGame.ResourceStorage exposes onlyGatherNativeItems (settable), plus architecture.md
# mentions excludedResourcesList and _nativeItemsFilter. The outhouse's NATIVE accepted items are
# [Compost, Crawler Slime, Spoiled Food]; food/seeds are forced in past the acceptance gate and are
# NOT native. If "native items only" is real, it is exactly the goal.
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

function Sig($m) {
    $ps = @()
    foreach ($p in $m.Parameters) { $ps += ("{0} {1}" -f $p.ParameterType.FullName, $p.Name) }
    $fl = @()
    if ($m.IsVirtual) { $fl += "virtual" }
    if ($m.IsPublic) { $fl += "public" } else { $fl += "nonpublic" }
    return ("{0}({1}) : {2}  [{3}]" -f $m.Name, [string]::Join(", ", $ps), $m.ReturnType.Name, [string]::Join(" ", $fl))
}

Write-Host ""
Write-Host "=========== 1. ResourceStorage FIELDS (the settable vanilla levers) ==========="
$t = $byName["SSSGame.ResourceStorage"]
if ($null -eq $t) { Write-Host "  NO TYPE SSSGame.ResourceStorage" }
else {
    foreach ($fl in ($t.Fields | Sort-Object Name)) {
        Write-Host ("        {0} {1}   [{2}]" -f $fl.FieldType.FullName, $fl.Name, $(if ($fl.IsPublic) {"public"} else {"nonpublic"}))
    }
}

Write-Host ""
Write-Host "=========== 2. ResourceStorage methods mentioning native/exclude/gather/filter ==========="
if ($null -ne $t) {
    foreach ($m in ($t.Methods | Sort-Object Name)) {
        if ($m.Name -notmatch "[Nn]ative|[Ee]xclud|[Gg]ather|[Ff]ilter|Accept|Allot|Quota") { continue }
        Write-Host ("        {0}" -f (Sig $m))
    }
}

Write-Host ""
Write-Host "=========== 3. Anything named *NativeItems* / *ExcludedResources* anywhere ==========="
foreach ($t2 in ($allTypes | Sort-Object FullName)) {
    if ($t2.FullName -match "MethodInfoStore") { continue }
    foreach ($m in $t2.Methods) {
        if ($m.Name -notmatch "NativeItem|ExcludedResource|IsNative|NativeFilter") { continue }
        Write-Host ("  {0}.{1}" -f $t2.FullName, (Sig $m))
    }
    foreach ($fd in $t2.Fields) {
        if ($fd.Name -notmatch "nativeItem|NativeItem|excludedResource|ExcludedResource") { continue }
        Write-Host ("  {0} :: {1} {2}" -f $t2.FullName, $fd.FieldType.FullName, $fd.Name)
    }
}

Write-Host ""
Write-Host "=========== 4. Workstation base - accepted/allotted item surface ==========="
$w = $byName["SSSGame.Workstation"]
if ($null -eq $w) { Write-Host "  NO TYPE SSSGame.Workstation" }
else {
    foreach ($m in ($w.Methods | Sort-Object Name)) {
        if ($m.Name -notmatch "Accept|Allot|Native|Exclud|Gather|Storage|Item") { continue }
        if ($m.Name -match "^(add_|remove_)") { continue }
        Write-Host ("        {0}" -f (Sig $m))
    }
}

Write-Host ""
Write-Host "=========== 5. ItemContainerType - the outhouse's native accepted-type list ==========="
$ct = $byName["SandSailorStudio.Inventory.ItemContainerType"]
if ($null -eq $ct) { Write-Host "  NO TYPE SandSailorStudio.Inventory.ItemContainerType" }
else {
    foreach ($fd in ($ct.Fields | Sort-Object Name)) {
        Write-Host ("     field  {0} {1}" -f $fd.FieldType.FullName, $fd.Name)
    }
    foreach ($m in ($ct.Methods | Sort-Object Name)) {
        if ($m.Name -match "^\.") { continue }
        Write-Host ("     method {0}" -f (Sig $m))
    }
}
