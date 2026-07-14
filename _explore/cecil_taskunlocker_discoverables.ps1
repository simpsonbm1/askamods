# TaskUnlockerMod v1.3.0 research: which ItemInfo types implement SSSGame.IDiscoverableItem
# (the journal/item-discovery gate behind tavern/harbor task visibility), and what the
# NetworkBlueprintConditionsDatabase codegen constants look like from the interop side.
$base = "D:\SteamLibrary\steamapps\common\ASKA\BepInEx"
[void][System.Reflection.Assembly]::LoadFrom("d:\Claude Projects\askamods\_explore\bin\Debug\net10.0\Mono.Cecil.dll")

$resolver = New-Object Mono.Cecil.DefaultAssemblyResolver
foreach ($d in @("$base\interop", "$base\core", "$base\unity-libs")) { $resolver.AddSearchDirectory($d) }
$rp = New-Object Mono.Cecil.ReaderParameters
$rp.AssemblyResolver = $resolver

$asm = [Mono.Cecil.AssemblyDefinition]::ReadAssembly("$base\interop\Assembly-CSharp.dll", $rp)
$all = New-Object System.Collections.Generic.List[object]
foreach ($t in $asm.MainModule.Types) { $all.Add($t); foreach ($n in $t.NestedTypes) { $all.Add($n) } }

# 1) Every type whose interface list (own or inherited) contains IDiscoverableItem
Write-Output "=== Types implementing SSSGame.IDiscoverableItem (own declaration) ==="
$direct = @{}
foreach ($t in $all) {
    foreach ($i in $t.Interfaces) {
        if ($i.InterfaceType.FullName -eq "SSSGame.IDiscoverableItem") { $direct[$t.FullName] = $t }
    }
}
foreach ($k in ($direct.Keys | Sort-Object)) { Write-Output "  $k (base: $($direct[$k].BaseType.FullName))" }

# 2) Full hierarchy under each direct implementor (derived types inherit the interface)
Write-Output "`n=== Derived types of each implementor ==="
function BaseChain($t) {
    $names = @()
    $b = $t.BaseType
    while ($b -ne $null) {
        $names += $b.FullName
        try { $b = ($b.Resolve()).BaseType } catch { break }
    }
    return $names
}
foreach ($t in $all) {
    if ($direct.ContainsKey($t.FullName)) { continue }
    $chain = BaseChain $t
    foreach ($c in $chain) {
        if ($direct.ContainsKey($c)) { Write-Output "  $($t.FullName)  : derives from $c"; break }
    }
}

# 3) ItemInfo hierarchy overview: all types deriving from the base item-info type
Write-Output "`n=== All types deriving from SandSailorStudio.Inventory.ItemInfo ==="
foreach ($t in $all) {
    $chain = BaseChain $t
    if ($chain -contains "SandSailorStudio.Inventory.ItemInfo") {
        $ifaces = ($t.Interfaces | ForEach-Object { $_.InterfaceType.Name }) -join ","
        Write-Output "  $($t.FullName) : $($t.BaseType.Name)  ifaces=[$ifaces]"
    }
}

# 4) NetworkBlueprintConditionsDatabase: full member dump (looking for capacity constants)
Write-Output "`n=== SSSGame.Network.NetworkBlueprintConditionsDatabase members ==="
$ndb = $all | Where-Object { $_.FullName -eq "SSSGame.Network.NetworkBlueprintConditionsDatabase" } | Select-Object -First 1
if ($ndb) {
    foreach ($f in $ndb.Fields | Where-Object { -not $_.Name.Contains("NativeField") -and -not $_.Name.Contains("NativeMethod") }) {
        $init = ""
        if ($f.HasConstant) { $init = " = $($f.Constant)" }
        Write-Output "  F $($f.FieldType.Name) $($f.Name)$init (static=$($f.IsStatic))"
    }
    foreach ($p in $ndb.Properties) { Write-Output "  P $($p.PropertyType.Name) $($p.Name)" }
    foreach ($m in $ndb.Methods | Where-Object { -not $_.IsGetter -and -not $_.IsSetter -and -not $_.Name.StartsWith(".") }) {
        $ps = ($m.Parameters | ForEach-Object { "$($_.ParameterType.Name) $($_.Name)" }) -join ", "
        Write-Output "  M $($m.ReturnType.Name) $($m.Name)($ps)"
    }
}

# 5) BlueprintConditionsDatabase (non-network side): members
Write-Output "`n=== SSSGame.BlueprintConditionsDatabase members ==="
$bdb = $all | Where-Object { $_.FullName -eq "SSSGame.BlueprintConditionsDatabase" } | Select-Object -First 1
if ($bdb) {
    foreach ($p in $bdb.Properties) { Write-Output "  P $($p.PropertyType.Name) $($p.Name)" }
    foreach ($m in $bdb.Methods | Where-Object { -not $_.IsGetter -and -not $_.IsSetter -and -not $_.Name.StartsWith(".") }) {
        $ps = ($m.Parameters | ForEach-Object { "$($_.ParameterType.Name) $($_.Name)" }) -join ", "
        Write-Output "  M $($m.ReturnType.Name) $($m.Name)($ps)"
    }
}

# 6) Who calls the discovery gate for tasks: CheckUndiscoveredTasks / TaskDataComponents.CheckTaskDiscovery
Write-Output "`n=== TaskDataComponents enum values ==="
$tdc = $all | Where-Object { $_.FullName -like "*TaskDataComponents" } | Select-Object -First 1
if ($tdc) { foreach ($f in $tdc.Fields | Where-Object { $_.HasConstant }) { Write-Output "  $($f.Name) = $($f.Constant)" } }
