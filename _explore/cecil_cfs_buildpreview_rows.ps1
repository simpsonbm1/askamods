$asmPath = "D:\SteamLibrary\steamapps\common\ASKA\BepInEx\interop\Assembly-CSharp.dll"
Add-Type -Path "D:\SteamLibrary\steamapps\common\ASKA\BepInEx\core\Mono.Cecil.dll"
$asm = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($asmPath)
$mod = $asm.MainModule

function Show($name) {
    foreach ($t in $mod.GetTypes()) {
        if ($t.Name -eq $name) {
            Write-Host "### $($t.FullName)  base=$($t.BaseType)"
            foreach ($p in $t.Properties) { Write-Host "   PROP  $($p.PropertyType.Name) $($p.Name)" }
            foreach ($m in $t.Methods) { if (-not $m.IsGetter -and -not $m.IsSetter) { Write-Host "   METH  $($m.ReturnType.Name) $($m.Name)($(($m.Parameters | ForEach-Object { $_.ParameterType.Name }) -join ', '))" } }
            Write-Host ""
        }
    }
}
Show "BuildPreviewTabPage"
Show "ItemThumbnailPanel"
