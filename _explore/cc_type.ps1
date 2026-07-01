$asmPath = "D:\SteamLibrary\steamapps\common\ASKA\BepInEx\interop\Assembly-CSharp.dll"
Add-Type -Path "D:\SteamLibrary\steamapps\common\ASKA\BepInEx\core\Mono.Cecil.dll"
$asm = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($asmPath)
$mod = $asm.MainModule

function Dump-Type($name) {
    $t = $mod.GetType($name)
    if (-not $t) { Write-Host "NO TYPE $name"; return }
    Write-Host "TYPE: $($t.FullName)  base=$($t.BaseType.Name)"
    Write-Host "  -- Real fields (non-native-ptr) --"
    foreach ($f in $t.Fields) {
        if ($f.Name -notmatch "^Native") { Write-Host "    $($f.Name) : $($f.FieldType.FullName)" }
    }
    Write-Host "  -- Properties --"
    foreach ($p in $t.Properties) { Write-Host "    $($p.Name) : $($p.PropertyType.Name)" }
    Write-Host "  -- Methods --"
    foreach ($m in $t.Methods) {
        if ($m.Name -match "^get_|^set_") { continue }
        $ps = ($m.Parameters | ForEach-Object { $_.ParameterType.Name }) -join ", "
        Write-Host "    $($m.Name)($ps) : $($m.ReturnType.Name)"
    }
}

foreach ($n in $args) { Dump-Type $n; Write-Host "" }
