# CraftFromStorage #0052 (multiplayer client) - step 1 discovery.
#
# QUESTION: is there an entry point a NON-HOST may call that moves items into/out of a settlement
# container? CraftTransfer's four write points currently call ItemCollection.AddItems / RemoveItems /
# RemoveItem directly behind an IsHostOrSolo() gate.
#
# The decisive fact is NOT the method name. ASKA is a Fusion game, and Fusion RPCs carry
# [Rpc(RpcSources, RpcTargets)] whose ctor args say who is ALLOWED to invoke them. Cecil reads
# custom-attribute ctor args fine even though the method bodies are native trampolines. So
# "can a client call this?" is statically answerable.
#
# ASCII only, explicit loops - PowerShell 5.1 chokes on inline pipeline joins in assignments.
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
Write-Host ("loaded {0} types" -f $allTypes.Count)

function Resolve-EnumName($typeRef, $value) {
    if ($null -eq $typeRef) { return [string]$value }
    $td = $byName[$typeRef.FullName]
    if ($null -eq $td) { return [string]$value }
    if (-not $td.IsEnum) { return [string]$value }
    $iv = [int]$value
    $names = @()
    foreach ($fld in $td.Fields) {
        if (-not $fld.HasConstant) { continue }
        if ($fld.Name -eq "value__") { continue }
        $c = [int]$fld.Constant
        if ($c -eq $iv) { return $fld.Name }
        if ($c -ne 0) { if (($iv -band $c) -eq $c) { $names += $fld.Name } }
    }
    if ($names.Count -gt 0) { return [string]::Join("|", $names) }
    return [string]$value
}

function Format-RpcAttr($m) {
    foreach ($a in $m.CustomAttributes) {
        if ($a.AttributeType.Name -notmatch "Rpc") { continue }
        $parts = @()
        foreach ($ca in $a.ConstructorArguments) { $parts += (Resolve-EnumName $ca.Type $ca.Value) }
        foreach ($na in $a.Properties) { $parts += ("{0}={1}" -f $na.Name, $na.Argument.Value) }
        return ("[{0}({1})]" -f $a.AttributeType.Name, [string]::Join(", ", $parts))
    }
    return $null
}

function Format-Sig($m) {
    $ps = @()
    foreach ($p in $m.Parameters) { $ps += ("{0} {1}" -f $p.ParameterType.Name, $p.Name) }
    return ("{0}({1}) : {2}" -f $m.Name, [string]::Join(", ", $ps), $m.ReturnType.Name)
}

function Get-ParamText($m) {
    $ps = @()
    foreach ($p in $m.Parameters) { $ps += $p.ParameterType.FullName }
    return [string]::Join(",", $ps)
}

Write-Host ""
Write-Host "=========== 1. Which Rpc attribute types exist at all ==========="
$attrNames = @{}
foreach ($t in $allTypes) {
    foreach ($m in $t.Methods) {
        foreach ($a in $m.CustomAttributes) {
            if ($a.AttributeType.Name -match "Rpc") {
                $k = $a.AttributeType.FullName
                if (-not $attrNames.ContainsKey($k)) { $attrNames[$k] = 0 }
                $attrNames[$k] = $attrNames[$k] + 1
            }
        }
    }
}
if ($attrNames.Count -eq 0) {
    Write-Host "  NONE FOUND - attributes may be stripped from the interop assemblies."
}
foreach ($k in ($attrNames.Keys | Sort-Object)) {
    Write-Host ("  {0}   x{1}" -f $k, $attrNames[$k])
}

Write-Host ""
Write-Host "=========== 2. Rpc-attributed methods mentioning item/container/storage ==========="
$subject = "Item|Container|Storage|Inventory|Collection|Chest|Stock|Warehouse"
$hits = 0
foreach ($t in $allTypes) {
    if ($t.FullName -match "<") { continue }
    if ($t.FullName -match "MethodInfoStore") { continue }
    foreach ($m in $t.Methods) {
        $rpc = Format-RpcAttr $m
        if ($null -eq $rpc) { continue }
        $paramText = Get-ParamText $m
        $match = $false
        if ($t.FullName -match $subject) { $match = $true }
        if ($m.Name -match $subject) { $match = $true }
        if ($paramText -match $subject) { $match = $true }
        if (-not $match) { continue }
        $hits++
        Write-Host ("  {0}" -f $t.FullName)
        Write-Host ("      {0}  {1}" -f $rpc, (Format-Sig $m))
    }
}
Write-Host ("  {0} matching Rpc methods" -f $hits)

Write-Host ""
Write-Host "=========== 3. Method surface of the types CFS writes to ==========="
$targets = @(
  "SandSailorStudio.Inventory.ItemCollection",
  "SandSailorStudio.Inventory.Container",
  "SSSGame.Container",
  "SSSGame.StorageManager",
  "SSSGame.NetworkContainer",
  "SSSGame.ContainerInteraction"
)
foreach ($tn in $targets) {
    $t = $byName[$tn]
    if ($null -eq $t) { Write-Host ("  NO TYPE {0}" -f $tn); continue }
    $bn = "?"
    if ($t.BaseType) { $bn = $t.BaseType.Name }
    Write-Host ("  ### {0}   base={1}" -f $tn, $bn)
    foreach ($m in ($t.Methods | Sort-Object Name)) {
        if ($m.Name -match "^get_") { continue }
        if ($m.Name -match "^set_") { continue }
        if ($m.Name -match "^add_") { continue }
        if ($m.Name -match "^remove_") { continue }
        if ($m.Name -match "^\.") { continue }
        $rpc = Format-RpcAttr $m
        $tag = ""
        if ($rpc) { $tag = "  <-- " + $rpc }
        Write-Host ("      {0}{1}" -f (Format-Sig $m), $tag)
    }
}

Write-Host ""
Write-Host "=========== 4. Types named like a networked container ==========="
foreach ($t in ($allTypes | Sort-Object FullName)) {
    if ($t.FullName -match "<") { continue }
    if ($t.FullName -match "MethodInfoStore") { continue }
    if ($t.FullName -notmatch "Container|Storage|Chest|Warehouse") { continue }
    $bt = ""
    if ($t.BaseType) { $bt = $t.BaseType.Name }
    Write-Host ("  {0}   base={1}" -f $t.FullName, $bt)
}

Write-Host ""
Write-Host "=========== 5. Precedent: every Request* method (TaskUnlocker found its door here) ==========="
foreach ($t in $allTypes) {
    if ($t.FullName -match "<") { continue }
    if ($t.FullName -match "MethodInfoStore") { continue }
    foreach ($m in $t.Methods) {
        if ($m.Name -notmatch "^Request") { continue }
        $rpc = Format-RpcAttr $m
        $tag = "(no rpc attr)"
        if ($rpc) { $tag = $rpc }
        Write-Host ("  {0}.{1}   {2}" -f $t.FullName, (Format-Sig $m), $tag)
    }
}
