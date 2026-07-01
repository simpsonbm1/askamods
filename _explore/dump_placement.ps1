$asmPath = "D:\SteamLibrary\steamapps\common\ASKA\BepInEx\interop\Assembly-CSharp.dll"
Add-Type -Path "D:\SteamLibrary\steamapps\common\ASKA\BepInEx\core\Mono.Cecil.dll"
$asm = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($asmPath)

$types = @(
    "SSSGame.DynamicDimensionsPlacementTool",
    "SSSGame.DynamicDimensionTemplate",
    "SSSGame.TerraformingGrid"
)

foreach ($tn in $types) {
    $t = $asm.MainModule.GetType($tn)
    if (-not $t) { Write-Host "NOT FOUND: $tn"; continue }
    Write-Host "==================== $($t.FullName) ===================="
    Write-Host "  -- Fields --"
    foreach ($f in $t.Fields) { Write-Host "    $($f.Name) : $($f.FieldType.Name)" }
    Write-Host "  -- Properties --"
    foreach ($p in $t.Properties) { Write-Host "    $($p.Name) : $($p.PropertyType.Name)" }
    Write-Host "  -- Methods --"
    foreach ($m in $t.Methods) {
        $ps = ($m.Parameters | ForEach-Object { "$($_.ParameterType.Name) $($_.Name)" }) -join ", "
        Write-Host "    $($m.ReturnType.Name) $($m.Name)($ps)"
    }
    Write-Host ""
}
