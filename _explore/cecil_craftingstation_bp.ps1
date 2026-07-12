# CraftingStation blueprint/recipe surface + CraftInteraction + knowledge manager (BOM anomaly hunt)
$asmPath = "D:\SteamLibrary\steamapps\common\ASKA\BepInEx\interop\Assembly-CSharp.dll"
[void][System.Reflection.Assembly]::LoadFrom("d:\Claude Projects\askamods\_explore\bin\Debug\net10.0\Mono.Cecil.dll")
$asm = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($asmPath)
$mod = $asm.MainModule

$allTypes = New-Object System.Collections.Generic.List[object]
foreach ($t in $mod.Types) {
    $allTypes.Add($t)
    foreach ($n in $t.NestedTypes) { $allTypes.Add($n) }
}

function Sig($m) {
    $ps = ($m.Parameters | ForEach-Object { "$($_.ParameterType.Name) $($_.Name)" }) -join ", "
    "$($m.ReturnType.Name) $($m.Name)($ps)"
}

Write-Output "===== CraftingStation members matching blueprint|craft|recipe|know|unlock ====="
$t = $allTypes | Where-Object { $_.FullName -eq "SSSGame.CraftingStation" } | Select-Object -First 1
if ($t) {
    foreach ($p in $t.Properties) {
        if ($p.Name -match "(?i)blueprint|craft|recipe|know|unlock|interaction") { Write-Output "  P: $($p.Name) : $($p.PropertyType.FullName)" }
    }
    foreach ($m in $t.Methods) {
        if ($m.Name -match "^get_|^set_") { continue }
        if ($m.Name -match "(?i)blueprint|craft|recipe|know|unlock") { Write-Output "  M: $(Sig $m)" }
    }
}

Write-Output ""
Write-Output "===== CraftInteraction surface ====="
$t = $allTypes | Where-Object { $_.FullName -eq "SSSGame.CraftInteraction" } | Select-Object -First 1
if ($t) {
    Write-Output "TYPE: $($t.FullName) base=$($t.BaseType.FullName)"
    foreach ($p in $t.Properties) { Write-Output "  P: $($p.Name) : $($p.PropertyType.FullName)" }
    foreach ($m in $t.Methods) {
        if ($m.Name -match "^get_|^set_|^add_|^remove_") { continue }
        Write-Output "  M: $(Sig $m)"
    }
}

Write-Output ""
Write-Output "===== NetworkKnowledgeManager members matching blueprint|know|unlock ====="
$t = $allTypes | Where-Object { $_.Name -eq "NetworkKnowledgeManager" } | Select-Object -First 1
if ($t) {
    Write-Output "TYPE: $($t.FullName) base=$($t.BaseType.FullName)"
    foreach ($p in $t.Properties) {
        if ($p.Name -match "(?i)blueprint|know|unlock|instance") { Write-Output "  P: $($p.Name) : $($p.PropertyType.FullName)" }
    }
    foreach ($m in $t.Methods) {
        if ($m.Name -match "^get_|^set_|^add_|^remove_") { continue }
        if ($m.Name -match "(?i)blueprint|know|unlock") { Write-Output "  M: $(Sig $m)" }
    }
}

Write-Output ""
Write-Output "===== Who else has KnowsBlueprintForItem or GetBlueprint-ish methods (any type) ====="
foreach ($t in $allTypes) {
    foreach ($m in $t.Methods) {
        if ($m.Name -match "(?i)^KnowsBlueprint|^GetBlueprintFor|^FindBlueprint|^TryGetBlueprint|^GetCraftBlueprint") {
            Write-Output "$($t.FullName) :: $(Sig $m)"
        }
    }
}
