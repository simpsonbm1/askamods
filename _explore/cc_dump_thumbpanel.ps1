Add-Type -Path "D:\SteamLibrary\steamapps\common\ASKA\BepInEx\core\Mono.Cecil.dll"
$interop = "D:\SteamLibrary\steamapps\common\ASKA\BepInEx\interop"
$asms = @("Assembly-CSharp.dll","SandSailorStudio.dll") | ForEach-Object { [Mono.Cecil.AssemblyDefinition]::ReadAssembly((Join-Path $interop $_)) }

function Find-Type($simpleOrFull) {
    foreach ($a in $asms) {
        $t = $a.MainModule.GetType($simpleOrFull)
        if ($t) { return $t }
        foreach ($tt in $a.MainModule.GetTypes()) { if ($tt.Name -eq $simpleOrFull) { return $tt } }
    }
    return $null
}

function Dump-Methods($t, $label) {
    Write-Host "==== $label : $($t.FullName)  base=$($t.BaseType) ===="
    foreach ($m in ($t.Methods | Sort-Object Name)) {
        if ($m.Name -match "^get_|^set_|^add_|^remove_") { continue }
        $ps = ($m.Parameters | ForEach-Object { "$($_.ParameterType.Name) $($_.Name)" }) -join ", "
        Write-Host ("    {0}({1}) : {2}" -f $m.Name, $ps, $m.ReturnType.Name)
    }
}

$t = Find-Type "ItemThumbnailPanel"
if (-not $t) { Write-Host "NO ItemThumbnailPanel"; exit }
Dump-Methods $t "ItemThumbnailPanel"

# Walk base chain for inherited click/pointer handlers
$b = $t.BaseType
while ($b -and $b.FullName -notmatch "MonoBehaviour|Object|Component") {
    $bt = Find-Type $b.Name
    if ($bt) { Dump-Methods $bt ("BASE " + $b.Name) } else { break }
    $b = $bt.BaseType
}
