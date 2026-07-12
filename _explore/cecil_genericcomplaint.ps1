# GenericMessageComplaint + Complaint surface: find the message-key member + named format keys
$asmPath = "D:\SteamLibrary\steamapps\common\ASKA\BepInEx\interop\Assembly-CSharp.dll"
[void][System.Reflection.Assembly]::LoadFrom("d:\Claude Projects\askamods\_explore\bin\Debug\net10.0\Mono.Cecil.dll")
$asm = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($asmPath)
$mod = $asm.MainModule

$allTypes = New-Object System.Collections.Generic.List[object]
foreach ($t in $mod.Types) {
    $allTypes.Add($t)
    foreach ($n in $t.NestedTypes) { $allTypes.Add($n) }
}

foreach ($tn in @("GenericMessageComplaint", "Complaint", "FailedObjectiveComplaint")) {
    foreach ($t in ($allTypes | Where-Object { $_.Name -eq $tn })) {
        Write-Output "TYPE: $($t.FullName) base=$($t.BaseType.FullName)"
        foreach ($p in $t.Properties) { Write-Output "  P: $($p.Name) : $($p.PropertyType.FullName)" }
        foreach ($m in $t.Methods) {
            if ($m.Name -match "^get_|^set_|^add_|^remove_") { continue }
            $ps = ($m.Parameters | ForEach-Object { "$($_.ParameterType.Name) $($_.Name)" }) -join ", "
            Write-Output "  M: $($m.ReturnType.Name) $($m.Name)($ps)"
        }
        Write-Output ""
    }
}

Write-Output "===== Complaint-related static string keys mentioning safe/defen/protect ====="
foreach ($t in $allTypes) {
    foreach ($p in $t.Properties) {
        if ($p.Name -match "(?i)safe|defen|protect|danger" -and $p.PropertyType.Name -eq "String") {
            Write-Output "$($t.FullName) :: $($p.Name)"
        }
    }
}
