$asmPath = "D:\SteamLibrary\steamapps\common\ASKA\BepInEx\interop\Assembly-CSharp.dll"
Add-Type -Path "D:\SteamLibrary\steamapps\common\ASKA\BepInEx\core\Mono.Cecil.dll"
$asm = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($asmPath)
$mod = $asm.MainModule

Write-Host "### BlueprintInfo: base chain ###"
foreach ($t in $mod.Types) {
    if ($t.Name -eq "BlueprintInfo") {
        $cur = $t
        while ($cur) {
            Write-Host "  $($cur.FullName)"
            if (-not $cur.BaseType) { break }
            try { $cur = $cur.BaseType.Resolve() } catch { break }
        }
    }
}

Write-Host ""
Write-Host "### Types deriving (directly) from BlueprintInfo ###"
foreach ($t in $mod.Types) {
    if ($t.BaseType -and $t.BaseType.Name -eq "BlueprintInfo") {
        Write-Host "  $($t.FullName)"
    }
}

Write-Host ""
Write-Host "### Types deriving from ItemInfo ###"
foreach ($t in $mod.Types) {
    if ($t.BaseType -and $t.BaseType.Name -eq "ItemInfo") {
        Write-Host "  $($t.FullName)"
    }
}

Write-Host ""
Write-Host "### Info/blueprint-ish types mentioning Structure/Building/Construction ###"
foreach ($t in $mod.Types) {
    if ($t.Name -match "Structure.*Info|Building.*Info|Construction.*Info|.*Blueprint.*") {
        $b = if ($t.BaseType) { $t.BaseType.Name } else { "-" }
        Write-Host "  $($t.FullName)  : base=$b"
    }
}

Write-Host ""
Write-Host "### BlueprintInfo members (fields/props that discriminate a building) ###"
foreach ($t in $mod.Types) {
    if ($t.Name -eq "BlueprintInfo") {
        foreach ($f in $t.Fields) { Write-Host "  FIELD $($f.FieldType.Name) $($f.Name)" }
        foreach ($p in $t.Properties) { Write-Host "  PROP  $($p.PropertyType.Name) $($p.Name)" }
        foreach ($m in $t.Methods) { Write-Host "  METH  $($m.ReturnType.Name) $($m.Name)($(($m.Parameters | ForEach-Object { $_.ParameterType.Name }) -join ', '))" }
    }
}
