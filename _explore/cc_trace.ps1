$asmPath = "D:\SteamLibrary\steamapps\common\ASKA\BepInEx\interop\Assembly-CSharp.dll"
Add-Type -Path "D:\SteamLibrary\steamapps\common\ASKA\BepInEx\core\Mono.Cecil.dll"
$asm = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($asmPath)
$mod = $asm.MainModule

function Dump-Method($typeName, $methodName) {
    $t = $mod.GetType($typeName)
    if (-not $t) { Write-Host "NO TYPE $typeName"; return }
    $ms = $t.Methods | Where-Object { $_.Name -eq $methodName }
    if (-not $ms) { Write-Host "NO METHOD $typeName.$methodName"; return }
    foreach ($m in $ms) {
        Write-Host "=== $typeName.$($m.Name) ($($m.Parameters.Count) params) ==="
        if (-not $m.HasBody) { Write-Host "  <no body>"; continue }
        foreach ($inst in $m.Body.Instructions) {
            $op = $inst.Operand
            $opStr = ""
            if ($op -ne $null) {
                if ($op -is [Mono.Cecil.MethodReference]) { $opStr = $op.DeclaringType.Name + "::" + $op.Name }
                elseif ($op -is [Mono.Cecil.FieldReference]) { $opStr = $op.DeclaringType.Name + "::" + $op.Name }
                else { $opStr = $op.ToString() }
            }
            Write-Host ("  {0,-12} {1}" -f $inst.OpCode.Name, $opStr)
        }
    }
}

$args2 = $args
Dump-Method $args2[0] $args2[1]
