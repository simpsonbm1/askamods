# Like cc_type.ps1 but searches EVERY interop assembly for the named type(s).
$base = "D:\SteamLibrary\steamapps\common\ASKA\BepInEx"
Add-Type -Path "$base\core\Mono.Cecil.dll"

$mods = @()
foreach ($f in Get-ChildItem "$base\interop\*.dll") {
    try { $mods += [Mono.Cecil.AssemblyDefinition]::ReadAssembly($f.FullName).MainModule } catch {}
}

function Dump-Type($name) {
    $t = $null
    foreach ($m in $mods) { $t = $m.GetType($name); if ($t) { break } }
    if (-not $t) { Write-Host "NO TYPE $name"; return }
    Write-Host "TYPE: $($t.FullName)  base=$($t.BaseType.Name)  asm=$($t.Module.Assembly.Name.Name)"
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
