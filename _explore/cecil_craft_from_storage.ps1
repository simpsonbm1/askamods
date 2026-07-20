# Craft-from-storage research (Nexus idea): find the availability check + craft-execute path
# for player crafting at CraftInteraction tables, and vet signatures for patchability.
$asmPath = "D:\SteamLibrary\steamapps\common\ASKA\BepInEx\interop\Assembly-CSharp.dll"
[void][System.Reflection.Assembly]::LoadFrom("d:\Claude Projects\askamods\_explore\bin\Debug\net10.0\Mono.Cecil.dll")
$asm = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($asmPath)
$mod = $asm.MainModule

$allTypes = New-Object System.Collections.Generic.List[object]
foreach ($t in $mod.Types) {
    $allTypes.Add($t)
    foreach ($n in $t.NestedTypes) { $allTypes.Add($n) }
}

function Sig($m) {
    $ps = ($m.Parameters | ForEach-Object { "$($_.ParameterType.Name) $($_.Name)" }) -join ", "
    $v = ""
    if ($m.IsVirtual) { $v = " [virt]" }
    "$($m.ReturnType.Name) $($m.Name)($ps)$v"
}

# Inventory-ish API call filter: what we consider "interesting" callees inside a method body
function InterestingCalls($m) {
    $out = @()
    if (-not $m.HasBody) { return $out }
    foreach ($ins in $m.Body.Instructions) {
        if ($ins.OpCode.Code -notin @([Mono.Cecil.Cil.Code]::Call, [Mono.Cecil.Cil.Code]::Callvirt, [Mono.Cecil.Cil.Code]::Newobj)) { continue }
        $mr = $ins.Operand
        if ($mr -isnot [Mono.Cecil.MethodReference]) { continue }
        $dt = $mr.DeclaringType.FullName
        if ($dt -match "Inventory|ItemManifest|ItemCollection|ItemContainer|Settlement|Storage|Blueprint|Manifest|CraftingStation|KnowledgeManager" -or
            $mr.Name -match "(?i)craft|ingredient|manifest|inventory|CanBuild|HasItems|Contains|Quantity|RemoveItem|AddItem|Transfer|Consume|Check") {
            $out += "$dt::$($mr.Name)"
        }
    }
    $out | Select-Object -Unique
}

Write-Output "=================================================================="
Write-Output "PASS 0: all types matching (?i)craft (name, base)"
Write-Output "=================================================================="
foreach ($t in $allTypes) {
    if ($t.Name -match "(?i)craft") {
        $b = ""
        if ($t.BaseType) { $b = $t.BaseType.Name }
        Write-Output "$($t.FullName)  : $b"
    }
}

Write-Output ""
Write-Output "=================================================================="
Write-Output "PASS 1: CraftInteraction full surface + base chain"
Write-Output "=================================================================="
$chainNames = @()
$ci = $allTypes | Where-Object { $_.FullName -eq "SSSGame.CraftInteraction" } | Select-Object -First 1
$cur = $ci
while ($cur -and $cur.FullName -notmatch "^UnityEngine") {
    $chainNames += $cur.FullName
    Write-Output ""
    Write-Output "---- TYPE: $($cur.FullName) (base: $(if ($cur.BaseType) { $cur.BaseType.FullName } else { '-' })) ----"
    foreach ($f in $cur.Fields) { Write-Output "  F: $($f.Name) : $($f.FieldType.Name)" }
    foreach ($p in $cur.Properties) { Write-Output "  P: $($p.Name) : $($p.PropertyType.Name)" }
    foreach ($m in $cur.Methods) {
        if ($m.Name -match "^get_|^set_|^add_|^remove_") { continue }
        Write-Output "  M: $(Sig $m)"
    }
    if ($cur.BaseType) {
        $bt = $cur.BaseType
        $cur = $allTypes | Where-Object { $_.FullName -eq $bt.FullName } | Select-Object -First 1
        if (-not $cur) { Write-Output ""; Write-Output "---- (base $($bt.FullName) not in Assembly-CSharp; chain stops) ----" }
    } else { $cur = $null }
}

Write-Output ""
Write-Output "=================================================================="
Write-Output "PASS 2: per-method inventory-API calls (CraftInteraction chain + Anvil/Carpenter)"
Write-Output "=================================================================="
$targetTypeNames = $chainNames + @("SSSGame.AnvilInteraction", "SSSGame.CarpenterInteraction")
foreach ($tn in $targetTypeNames) {
    $t = $allTypes | Where-Object { $_.FullName -eq $tn } | Select-Object -First 1
    if (-not $t) { continue }
    Write-Output ""
    Write-Output "---- $tn ----"
    foreach ($m in $t.Methods) {
        $calls = InterestingCalls $m
        if ($calls.Count -gt 0) {
            Write-Output "  $(Sig $m)"
            foreach ($c in $calls) { Write-Output "      -> $c" }
        }
    }
}

Write-Output ""
Write-Output "=================================================================="
Write-Output "PASS 3: types referencing CraftBlueprintInfo or CraftInteraction (UI candidates)"
Write-Output "=================================================================="
$refTypes = New-Object System.Collections.Generic.HashSet[string]
foreach ($t in $allTypes) {
    foreach ($m in $t.Methods) {
        if (-not $m.HasBody) { continue }
        foreach ($ins in $m.Body.Instructions) {
            $op = $ins.Operand
            $hit = $false
            if ($op -is [Mono.Cecil.MethodReference] -and $op.DeclaringType.FullName -match "CraftBlueprintInfo|CraftInteraction") { $hit = $true }
            if ($op -is [Mono.Cecil.TypeReference] -and $op.FullName -match "CraftBlueprintInfo|CraftInteraction") { $hit = $true }
            if ($op -is [Mono.Cecil.FieldReference] -and $op.FieldType.FullName -match "CraftBlueprintInfo|CraftInteraction") { $hit = $true }
            if ($hit) { [void]$refTypes.Add($t.FullName); break }
        }
        if ($refTypes.Contains($t.FullName)) { break }
    }
}
foreach ($tn in ($refTypes | Sort-Object)) { Write-Output "  $tn" }

Write-Output ""
Write-Output "=================================================================="
Write-Output "PASS 4: UI-candidate deep dump (menu/ui/panel/slot/button/widget names from pass 3)"
Write-Output "=================================================================="
foreach ($tn in ($refTypes | Sort-Object)) {
    if ($tn -notmatch "(?i)menu|ui|panel|slot|button|widget|hud|screen|window|thumb") { continue }
    $t = $allTypes | Where-Object { $_.FullName -eq $tn } | Select-Object -First 1
    if (-not $t) { continue }
    Write-Output ""
    Write-Output "---- $tn (base: $(if ($t.BaseType) { $t.BaseType.Name } else { '-' })) ----"
    foreach ($m in $t.Methods) {
        if ($m.Name -match "^get_|^set_|^add_|^remove_") { continue }
        $calls = InterestingCalls $m
        Write-Output "  M: $(Sig $m)"
        foreach ($c in $calls) { Write-Output "      -> $c" }
    }
}

Write-Output ""
Write-Output "=================================================================="
Write-Output "PASS 5: caller map for CraftInteraction-family + UI-candidate methods"
Write-Output "=================================================================="
# Build set of methods we care about (declared on target types), then one full scan for callers.
$watch = New-Object System.Collections.Generic.HashSet[string]
$watchTypes = $targetTypeNames + ($refTypes | Where-Object { $_ -match "(?i)menu|ui|panel|slot|craft" })
foreach ($tn in ($watchTypes | Select-Object -Unique)) {
    $t = $allTypes | Where-Object { $_.FullName -eq $tn } | Select-Object -First 1
    if (-not $t) { continue }
    foreach ($m in $t.Methods) {
        if ($m.Name -match "^get_|^set_|^add_|^remove_|^\.ctor|^\.cctor") { continue }
        [void]$watch.Add("$($t.FullName)::$($m.Name)")
    }
}
$callers = @{}
foreach ($t in $allTypes) {
    foreach ($m in $t.Methods) {
        if (-not $m.HasBody) { continue }
        foreach ($ins in $m.Body.Instructions) {
            if ($ins.OpCode.Code -notin @([Mono.Cecil.Cil.Code]::Call, [Mono.Cecil.Cil.Code]::Callvirt)) { continue }
            $mr = $ins.Operand
            if ($mr -isnot [Mono.Cecil.MethodReference]) { continue }
            $key = "$($mr.DeclaringType.FullName)::$($mr.Name)"
            if ($watch.Contains($key)) {
                if (-not $callers.ContainsKey($key)) { $callers[$key] = New-Object System.Collections.Generic.HashSet[string] }
                [void]$callers[$key].Add("$($t.FullName)::$($m.Name)")
            }
        }
    }
}
foreach ($key in ($callers.Keys | Sort-Object)) {
    Write-Output "  $key   <- callers: $($callers[$key].Count)"
    foreach ($c in ($callers[$key] | Sort-Object)) { Write-Output "        $c" }
}

Write-Output ""
Write-Output "DONE"
