$asmPath = "D:\SteamLibrary\steamapps\common\ASKA\BepInEx\interop\Assembly-CSharp.dll"
Add-Type -Path "D:\SteamLibrary\steamapps\common\ASKA\BepInEx\core\Mono.Cecil.dll"
$asm = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($asmPath)
$mod = $asm.MainModule

function Show-Type($name) {
    foreach ($t in $mod.GetTypes()) {
        if ($t.Name -eq $name) {
            Write-Host "### $($t.FullName)  base=$($t.BaseType)"
            foreach ($f in $t.Fields) { Write-Host "   FIELD $($f.FieldType.Name) $($f.Name)" }
            foreach ($p in $t.Properties) { Write-Host "   PROP  $($p.PropertyType.Name) $($p.Name)" }
            foreach ($m in $t.Methods) { if (-not $m.IsGetter -and -not $m.IsSetter) { Write-Host "   METH  $($m.ReturnType.Name) $($m.Name)($(($m.Parameters | ForEach-Object { $_.ParameterType.Name }) -join ', '))" } }
            Write-Host ""
        }
    }
}

Show-Type "BlueprintInfo"
Show-Type "StructureTemplate"

Write-Host "### Full subtree under BlueprintInfo (transitive) ###"
$all = $mod.GetTypes()
function Descends($t, $target) {
    $cur = $t
    $guard = 0
    while ($cur -and $guard -lt 20) {
        $guard++
        if (-not $cur.BaseType) { return $false }
        if ($cur.BaseType.Name -eq $target) { return $true }
        try { $cur = $cur.BaseType.Resolve() } catch { return $false }
    }
    return $false
}
foreach ($t in $all) {
    if (Descends $t "BlueprintInfo") { Write-Host "  $($t.FullName)   (base=$($t.BaseType.Name))" }
}

Write-Host ""
Write-Host "### Types deriving from StructureTemplate ###"
foreach ($t in $all) {
    if ($t.BaseType -and $t.BaseType.Name -eq "StructureTemplate") { Write-Host "  $($t.FullName)" }
}
