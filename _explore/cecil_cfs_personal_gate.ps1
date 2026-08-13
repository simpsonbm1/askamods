# Find the craftability gate used by the PERSONAL crafting menu (SSSGame.UI.CreateItemsTabPage
# hosted under SSSGame.UI.PlayerMenu). CraftFromStorageMod's existing gate is
# CraftInteraction.CheckOwnedRequirements(Blueprint, IInteractionAgent), which is a station method.
$base = "D:\SteamLibrary\steamapps\common\ASKA\BepInEx"
Add-Type -Path "$base\core\Mono.Cecil.dll"

$mods = @()
foreach ($f in Get-ChildItem "$base\interop\*.dll") {
    try { $mods += [Mono.Cecil.AssemblyDefinition]::ReadAssembly($f.FullName).MainModule } catch {}
}

Write-Host "===== Methods named like a craftability check ====="
foreach ($m in $mods) {
    foreach ($t in $m.Types) {
        if ($t.Namespace -notlike "SSSGame*" -and $t.Namespace -notlike "SandSailorStudio*") { continue }
        foreach ($mm in $t.Methods) {
            if ($mm.Name -notmatch "CheckOwned|CanCraft|IsCraftable|HasRequirement|CheckRequirement|MissingComponent|CanBeCrafted|CheckAvailab") { continue }
            $ps = ($mm.Parameters | ForEach-Object { "$($_.ParameterType.Name) $($_.Name)" }) -join ", "
            Write-Host ("{0}.{1}({2}) : {3}" -f $t.FullName, $mm.Name, $ps, $mm.ReturnType.Name)
        }
    }
}
Write-Host ""

Write-Host "===== Blueprint / BlueprintInfo full member dump ====="
foreach ($n in @("SandSailorStudio.Inventory.Blueprint","SandSailorStudio.Inventory.BlueprintInfo")) {
    $t = $null
    foreach ($m in $mods) { $t = $m.GetType($n); if ($t) { break } }
    if (-not $t) { Write-Host "NO TYPE $n"; continue }
    Write-Host "TYPE: $($t.FullName) base=$($t.BaseType.Name)"
    foreach ($p in $t.Properties) { Write-Host "    P: $($p.Name) : $($p.PropertyType.Name)" }
    foreach ($mm in $t.Methods) {
        if ($mm.Name -match "^get_|^set_") { continue }
        $ps = ($mm.Parameters | ForEach-Object { "$($_.ParameterType.Name) $($_.Name)" }) -join ", "
        Write-Host "    M: $($mm.Name)($ps) : $($mm.ReturnType.Name)"
    }
    Write-Host ""
}
