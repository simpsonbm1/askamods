# Craft-from-storage research pass 7 (PHASE 2 - villager half, cheap-lever-first probe).
#
# Question this pass exists to answer: does vanilla's "a villager crafter can only use its own
# station's storage" limit live in FETCH REACH (GetFetchDepth / GetPersonalFetchDepth) or in a
# storage-ACCESS whitelist? If either, widening it is a one-method patch and the villager half
# needs no custom mover. Surface only - see PASS Z for why no call-graph is possible here.
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
Write-Output "Loaded $($allTypes.Count) types."

function Sig($m) {
    $ps = ($m.Parameters | ForEach-Object { "$($_.ParameterType.Name) $($_.Name)" }) -join ", "
    $mods = @()
    if ($m.IsStatic) { $mods += "static" }
    if ($m.IsVirtual) { $mods += "virt" }
    if ($m.IsPublic) { $mods += "pub" } elseif ($m.IsFamily) { $mods += "prot" } else { $mods += "priv" }
    "$($m.ReturnType.Name) $($m.Name)($ps) [" + ($mods -join ",") + "]"
}
function DumpType($tn) {
    $t = $byName[$tn]
    if (-not $t) { Write-Output "  [NOT FOUND] $tn"; return }
    Write-Output ""
    Write-Output "---- $tn (base: $(if ($t.BaseType) { $t.BaseType.FullName } else { '-' })) ----"
    foreach ($p in $t.Properties) { Write-Output "  P: $($p.Name) : $($p.PropertyType.Name)" }
    foreach ($fld in $t.Fields) {
        if ($fld.Name -match "^NativeFieldInfoPtr|^NativeMethodInfoPtr|^NativeClassPtr") { continue }
        $fmod = if ($fld.IsStatic) { " [static]" } else { "" }
        Write-Output "  F: $($fld.Name) : $($fld.FieldType.Name)$fmod"
    }
    foreach ($m in $t.Methods) {
        if ($m.Name -match "^get_|^set_|^add_|^remove_") { continue }
        Write-Output "  M: $(Sig $m)"
    }
}

Write-Output ""
Write-Output "=================================================================="
Write-Output "PASS A: EVERY declaration of *FetchDepth* anywhere (the cheap lever)"
Write-Output "=================================================================="
foreach ($t in $allTypes) {
    foreach ($m in $t.Methods) {
        if ($m.Name -match "(?i)fetchdepth") { Write-Output "  $($t.FullName) :: $(Sig $m)" }
    }
    foreach ($fld in $t.Fields) {
        if ($fld.Name -match "(?i)fetchdepth|fetchrange|fetchradius") {
            Write-Output "  $($t.FullName) :: F: $($fld.Name) : $($fld.FieldType.Name)"
        }
    }
}

Write-Output ""
Write-Output "=================================================================="
Write-Output "PASS B: any member matching fetch/haul REACH vocabulary (depth/range/radius/dist)"
Write-Output "=================================================================="
foreach ($t in $allTypes) {
    if ($t.FullName -notmatch "^SSSGame|^SandSailorStudio") { continue }
    foreach ($m in $t.Methods) {
        if ($m.Name -match "(?i)(fetch|haul|supply|deliver).*(depth|range|radius|dist|scope|reach)" -or
            $m.Name -match "(?i)(depth|range|radius|scope|reach).*(fetch|haul|supply|storage)") {
            Write-Output "  $($t.FullName) :: $(Sig $m)"
        }
    }
    foreach ($fld in $t.Fields) {
        if ($fld.Name -match "(?i)(fetch|haul|supply).*(depth|range|radius|dist|scope|reach)") {
            Write-Output "  $($t.FullName) :: F: $($fld.Name) : $($fld.FieldType.Name)"
        }
    }
}

Write-Output ""
Write-Output "=================================================================="
Write-Output "PASS C: storage ACCESS / whitelist / permission surface (the other candidate)"
Write-Output "=================================================================="
foreach ($t in $allTypes) {
    if ($t.FullName -notmatch "^SSSGame|^SandSailorStudio") { continue }
    if ($t.Name -match "(?i)storageaccess|accesslevel|storagepermission|storagerule|accessrule") {
        Write-Output "  TYPE: $($t.FullName) (base $(if ($t.BaseType) { $t.BaseType.Name } else { '-' }))"
    }
    foreach ($m in $t.Methods) {
        if ($m.Name -match "(?i)^(can|is|has|allow|check).*(access|use|take|withdraw|draw|reach|share)" -and
            $t.FullName -match "(?i)storage|container|workstation|craft|settlement|structure|building|haul") {
            Write-Output "  $($t.FullName) :: $(Sig $m)"
        }
    }
}

Write-Output ""
Write-Output "=================================================================="
Write-Output "PASS D: CrafterFetchQuest full chain"
Write-Output "=================================================================="
$hits = @($allTypes | Where-Object { $_.Name -match "(?i)crafterfetch|fetchquest" })
foreach ($h in $hits) { DumpType $h.FullName }
if ($hits.Count -eq 0) { Write-Output "  (no *FetchQuest type found)" }

Write-Output ""
Write-Output "=================================================================="
Write-Output "PASS E: FSM_Fetch* / crafting-supply FSM states"
Write-Output "=================================================================="
$hits = @($allTypes | Where-Object { $_.Name -match "(?i)^FSM_.*(fetch|supply|craft)" })
foreach ($h in $hits) { Write-Output "  TYPE: $($h.FullName)" }
foreach ($h in @($hits | Where-Object { $_.Name -match "(?i)fetch|supply" })) { DumpType $h.FullName }

Write-Output ""
Write-Output "=================================================================="
Write-Output "PASS F: Workstation - the settlement-demand surface (Phase 2 lever)"
Write-Output "=================================================================="
$t = $byName["SSSGame.Workstation"]
if ($t) {
    foreach ($m in $t.Methods) {
        if ($m.Name -match "^get_|^set_|^add_|^remove_") { continue }
        if ($m.Name -match "(?i)settlement|needed|need|demand|request|supply|fetch|stock") {
            Write-Output "  $(Sig $m)"
        }
    }
}

Write-Output ""
Write-Output "=================================================================="
Write-Output "PASS G: who else declares GetItemsNeededFromSettlement-shaped members"
Write-Output "=================================================================="
foreach ($t in $allTypes) {
    foreach ($m in $t.Methods) {
        if ($m.Name -match "(?i)neededfromsettlement|itemsneeded|missingitems|requireditems") {
            Write-Output "  $($t.FullName) :: $(Sig $m)"
        }
    }
}

Write-Output ""
Write-Output "=================================================================="
Write-Output "PASS Z: EMPIRICAL - do interop method bodies carry real IL (call-graph possible?)"
Write-Output "=================================================================="
# If interop bodies are only native trampolines, no Cecil call-graph is possible and the ONLY
# way to answer 'who calls this' is Cpp2IL or a runtime probe. Measure, do not assume.
$probe = @("SSSGame.Workstation", "SSSGame.CraftInteraction")
foreach ($pn in $probe) {
    $t = $byName[$pn]
    if (-not $t) { continue }
    $withBody = 0; $totalInstr = 0; $n = 0; $calls = 0
    foreach ($m in $t.Methods) {
        $n++
        if ($m.HasBody) {
            $withBody++
            $totalInstr += $m.Body.Instructions.Count
            foreach ($i in $m.Body.Instructions) {
                if ($i.OpCode.Name -match "^call") {
                    $op = $i.Operand
                    if ($op -and $op.DeclaringType -and $op.DeclaringType.FullName -match "^SSSGame|^SandSailorStudio") { $calls++ }
                }
            }
        }
    }
    Write-Output "  $pn : $n methods, $withBody with body, $totalInstr instrs, $calls game-to-game calls"
}
Write-Output "  (game-to-game calls ~0 => bodies are native trampolines => Cecil call-graph IMPOSSIBLE)"

Write-Output ""
Write-Output "DONE"
