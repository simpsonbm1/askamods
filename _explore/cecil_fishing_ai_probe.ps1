# Fishing AI probe pre-work (TaskUnlockerMod fishing report, 2026-10-08): prints the exact
# member types the in-game fishing diagnostic reads, so the diagnostic compiles against the
# real interop surface. Read-only.
# ASCII only, explicit loops (PowerShell 5.1).
$base = "D:\SteamLibrary\steamapps\common\ASKA\BepInEx"
[void][System.Reflection.Assembly]::LoadFrom("d:\Claude Projects\askamods\_explore\bin\Debug\net10.0\Mono.Cecil.dll")
$want = @(
    "SSSGame.FishingStation",
    "SSSGame.RowingInteraction",
    "SSSGame.AI.FSM.FSM_QuestAction",
    "SSSGame.AI.GatherAndHarvestQuest/GatherAndHarvestData",
    "SSSGame.AI.TaskDispatcher",
    "SSSGame.Villager"
)
foreach ($f in Get-ChildItem "$base\interop\*.dll") {
    try { $asm = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($f.FullName) } catch { continue }
    $types = New-Object System.Collections.Generic.List[object]
    foreach ($t in $asm.MainModule.Types) { $types.Add($t); foreach ($n in $t.NestedTypes) { $types.Add($n) } }
    foreach ($t in $types) {
        if ($want -notcontains $t.FullName) { continue }
        $bt = ""; if ($t.BaseType) { $bt = $t.BaseType.FullName }
        Write-Host ("### {0}  base={1}  asm={2}" -f $t.FullName, $bt, $f.Name)
        foreach ($p in $t.Properties) {
            if ($t.FullName -eq "SSSGame.Villager" -and $p.Name -notmatch "(?i)quest|fsm|task|station|work") { continue }
            Write-Host ("   PROP {0} : {1}" -f $p.Name, $p.PropertyType.FullName)
        }
        foreach ($m in $t.Methods) {
            if ($m.Name -match "^(get_|set_)") { continue }
            if ($t.FullName -eq "SSSGame.Villager" -and $m.Name -notmatch "(?i)quest|fsm|task|station|work") { continue }
            $ps = @(); foreach ($p in $m.Parameters) { $ps += ("{0} {1}" -f $p.ParameterType.FullName, $p.Name) }
            Write-Host ("   METHOD {0}({1}) : {2}" -f $m.Name, [string]::Join(", ", $ps), $m.ReturnType.Name)
        }
    }
}
