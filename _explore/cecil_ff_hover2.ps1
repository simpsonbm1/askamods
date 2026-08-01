Add-Type -Path "D:\SteamLibrary\steamapps\common\ASKA\BepInEx\core\Mono.Cecil.dll"
$interop = "D:\SteamLibrary\steamapps\common\ASKA\BepInEx\interop"
$names = @("Assembly-CSharp.dll","SandSailorStudio.dll")
$asms = $names | ForEach-Object { [Mono.Cecil.AssemblyDefinition]::ReadAssembly((Join-Path $interop $_)) }

function Find-Type($simple) {
    foreach ($a in $asms) {
        foreach ($tt in $a.MainModule.GetTypes()) { if ($tt.Name -eq $simple) { return $tt } }
    }
    return $null
}

foreach ($n in @("ItemHighlightBehaviour")) {
    $t = Find-Type $n
    Write-Host "==== $($t.FullName)  base=$($t.BaseType) ===="
    Write-Host "-- methods --"
    foreach ($m in ($t.Methods | Sort-Object Name)) {
        $ps = ($m.Parameters | ForEach-Object { "$($_.ParameterType.Name) $($_.Name)" }) -join ", "
        Write-Host ("    {0}({1}) : {2}   [public={3} virtual={4}]" -f $m.Name, $ps, $m.ReturnType.Name, $m.IsPublic, $m.IsVirtual)
    }
    Write-Host "-- non-native fields --"
    foreach ($f in ($t.Fields | Sort-Object Name)) {
        if ($f.Name -match "^Native") { continue }
        Write-Host ("    {0} : {1}" -f $f.Name, $f.FieldType.FullName)
    }
    Write-Host "-- properties --"
    foreach ($p in ($t.Properties | Sort-Object Name)) {
        Write-Host ("    {0} : {1}" -f $p.Name, $p.PropertyType.FullName)
    }
}

Write-Host ""
Write-Host "==== ItemThumbnailPanel._OnHighlighted accessibility ===="
$tp = Find-Type "ItemThumbnailPanel"
foreach ($m in $tp.Methods) {
    if ($m.Name -in @("_OnHighlighted","OnSelect","OnDeselect","OnPointerClick")) {
        $ps = ($m.Parameters | ForEach-Object { $_.ParameterType.Name }) -join ", "
        Write-Host ("    {0}({1})  public={2} private={3} static={4}" -f $m.Name, $ps, $m.IsPublic, $m.IsPrivate, $m.IsStatic)
    }
}
