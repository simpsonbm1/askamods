Add-Type -Path "D:\SteamLibrary\steamapps\common\ASKA\BepInEx\core\Mono.Cecil.dll"
$asm = [Mono.Cecil.AssemblyDefinition]::ReadAssembly("D:\SteamLibrary\steamapps\common\ASKA\BepInEx\interop\Assembly-CSharp.dll")
$all = @($asm.MainModule.Types) + @($asm.MainModule.Types | ForEach-Object { $_.NestedTypes })

Write-Host "=== Types whose name contains Farm ==="
foreach ($t in $all | Where-Object { $_.Name -match "Farm" }) {
    Write-Host ("  {0} : {1}" -f $t.FullName, $t.BaseType.FullName)
}

Write-Host "`n=== Ancestry chain for candidate station types ==="
function Chain($name) {
    $t = $all | Where-Object { $_.FullName -eq $name }
    if (-not $t) { Write-Host "  !! $name NOT FOUND"; return }
    $chain = @()
    $cur = $t
    while ($cur) {
        $chain += $cur.FullName
        if (-not $cur.BaseType) { break }
        $next = $all | Where-Object { $_.FullName -eq $cur.BaseType.FullName }
        if (-not $next) { $chain += "(" + $cur.BaseType.FullName + ")"; break }
        $cur = $next
    }
    Write-Host ("  " + ($chain -join "  ->  "))
}
Chain "SSSGame.FarmingStation"
Chain "SSSGame.AnimalPen"
Chain "SSSGame.CraftingStation"

Write-Host "`n=== FarmingStation members ==="
$fs = $all | Where-Object { $_.FullName -eq "SSSGame.FarmingStation" }
if ($fs) {
    foreach ($f in $fs.Fields | Where-Object { $_.Name -notmatch "^Native" }) { Write-Host "  F $($f.FieldType.Name) $($f.Name)" }
    foreach ($p in $fs.Properties) { Write-Host "  P $($p.PropertyType.Name) $($p.Name)" }
    foreach ($m in $fs.Methods | Where-Object { $_.Name -notmatch "^get_|^set_" }) {
        $ps = ($m.Parameters | ForEach-Object { "$($_.ParameterType.Name) $($_.Name)" }) -join ", "
        $v = if ($m.IsVirtual) { "virtual " } else { "" }
        Write-Host ("  M {0}{1} {2}({3})" -f $v, $m.ReturnType.Name, $m.Name, $ps)
    }
}

Write-Host "`n=== Who READS VillagersInCharge ==="
foreach ($t in $all) {
    foreach ($m in $t.Methods) {
        if (-not $m.HasBody) { continue }
        foreach ($i in $m.Body.Instructions) {
            $op = $i.Operand
            if ($op -and $op.ToString() -match "VillagersInCharge") {
                Write-Host ("  {0}.{1}  ->  {2}" -f $t.FullName, $m.Name, $op.ToString())
                break
            }
        }
    }
}
