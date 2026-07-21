# Craft requirement-UI research (idea-17 v0.4 feature): find where the per-ingredient
# "have/need" counts in the crafting menu are computed and rendered, so settlement stock can be
# folded into them. Discovery-first: assume NO type names, search by shape.
# Pure ASCII only (powershell 5.1 reads BOM-less UTF-8 as ANSI - an em dash becomes a delimiter).
$base = "D:\SteamLibrary\steamapps\common\ASKA\BepInEx"
[void][System.Reflection.Assembly]::LoadFrom("d:\Claude Projects\askamods\_explore\bin\Debug\net10.0\Mono.Cecil.dll")

$allTypes = New-Object System.Collections.Generic.List[object]
foreach ($f in Get-ChildItem "$base\interop\*.dll") {
    try { $asm = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($f.FullName) } catch { continue }
    foreach ($t in $asm.MainModule.Types) {
        $allTypes.Add($t)
        foreach ($n in $t.NestedTypes) { $allTypes.Add($n) }
    }
}
$byName = @{}
foreach ($t in $allTypes) { if (-not $byName.ContainsKey($t.FullName)) { $byName[$t.FullName] = $t } }
Write-Output "types scanned: $($allTypes.Count)"

function Sig($m) {
    $ps = ($m.Parameters | ForEach-Object { "$($_.ParameterType.Name) $($_.Name)" }) -join ", "
    $v = ""; if ($m.IsVirtual) { $v = " [virt]" }
    "$($m.ReturnType.Name) $($m.Name)($ps)$v"
}
function DumpType($tn) {
    $t = $byName[$tn]
    if (-not $t) { Write-Output "  [NOT FOUND] $tn"; return }
    Write-Output ""
    Write-Output "---- $tn (base: $(if ($t.BaseType) { $t.BaseType.FullName } else { '-' })) ----"
    foreach ($fl in $t.Fields) {
        if ($fl.Name -match "^NativeMethodInfoPtr|^NativeFieldInfoPtr|^__|k__Backing") { continue }
        Write-Output "  F: $($fl.Name) : $($fl.FieldType.Name)"
    }
    foreach ($p in $t.Properties) { Write-Output "  P: $($p.Name) : $($p.PropertyType.Name)" }
    foreach ($m in $t.Methods) {
        if ($m.Name -match "^get_|^set_|^add_|^remove_|^\.c") { continue }
        Write-Output "  M: $(Sig $m)"
    }
}

Write-Output ""
Write-Output "=================================================================="
Write-Output "PASS 1: every UI type whose name smells like a recipe/ingredient row"
Write-Output "=================================================================="
foreach ($t in $allTypes) {
    if ($t.FullName -notmatch "UI\.") { continue }
    if ($t.Name -match "Craft|Ingredient|Requirement|Recipe|Blueprint|Part|Cost|Amount|Quantity|Slot|Row|Entry") {
        Write-Output "  TYPE: $($t.FullName)  : $(if ($t.BaseType) { $t.BaseType.Name } else { '-' })"
    }
}

Write-Output ""
Write-Output "=================================================================="
Write-Output "PASS 2: ANY type (namespace-agnostic) with a method named like a count setter"
Write-Output "=================================================================="
foreach ($t in $allTypes) {
    foreach ($m in $t.Methods) {
        if ($m.Name -match "^(Set|Update|Refresh|Fill|Display)(Amount|Quantity|Count|Requirement|Ingredient|Parts|Cost|Stock|Owned)") {
            Write-Output "  $($t.FullName) :: $(Sig $m)"
        }
    }
}

Write-Output ""
Write-Output "=================================================================="
Write-Output "PASS 3: methods anywhere taking a Blueprint/BlueprintInfo param, in UI-ish types"
Write-Output "=================================================================="
foreach ($t in $allTypes) {
    if ($t.FullName -notmatch "UI\.|Display|Menu|Panel|Widget|Tooltip|Popup") { continue }
    foreach ($m in $t.Methods) {
        $hit = $false
        foreach ($p in $m.Parameters) { if ($p.ParameterType.Name -match "^Blueprint|BlueprintInfo|ItemManifest") { $hit = $true } }
        if ($hit) { Write-Output "  $($t.FullName) :: $(Sig $m)" }
    }
}

Write-Output ""
Write-Output "=================================================================="
Write-Output "PASS 4: CraftMenu + the tab page we already patch, in full"
Write-Output "=================================================================="
DumpType "SSSGame.UI.CraftMenu"
DumpType "SSSGame.UI.CreateItemsTabPage"

Write-Output ""
Write-Output "=================================================================="
Write-Output "PASS 5: who owns GetRequirementOccurenceCount / FillRequirementsManifest"
Write-Output "=================================================================="
foreach ($t in $allTypes) {
    foreach ($m in $t.Methods) {
        if ($m.Name -match "GetRequirementOccurenceCount|FillRequirementsManifest|FillPartsManifest|GetMissing") {
            Write-Output "  $($t.FullName) :: $(Sig $m)"
        }
    }
}
