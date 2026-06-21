param(
    [Parameter(Mandatory=$true)][string]$Type,
    [string[]]$Methods = @()
)

$base = "D:\SteamLibrary\steamapps\common\ASKA\BepInEx"
$dirs = @("$base\interop", "$base\core", "$base\unity-libs")
[void][System.Reflection.Assembly]::LoadFrom("d:\Claude Projects\askamods\_explore\bin\Debug\net10.0\Mono.Cecil.dll")

$resolver = New-Object Mono.Cecil.DefaultAssemblyResolver
foreach ($d in $dirs) { $resolver.AddSearchDirectory($d) }
$rp = New-Object Mono.Cecil.ReaderParameters
$rp.AssemblyResolver = $resolver
$rp.ReadingMode = [Mono.Cecil.ReadingMode]::Immediate

$asms = @()
foreach ($f in Get-ChildItem "$base\interop\*.dll") {
    try { $asms += [Mono.Cecil.AssemblyDefinition]::ReadAssembly($f.FullName, $rp) } catch {}
}

$allTypes = New-Object System.Collections.Generic.List[object]
foreach ($a in $asms) {
    foreach ($t in $a.MainModule.Types) {
        $allTypes.Add($t)
        foreach ($n in $t.NestedTypes) { $allTypes.Add($n) }
    }
}

$t = $allTypes | Where-Object { $_.FullName -eq $Type } | Select-Object -First 1
if ($t -eq $null) { Write-Output "[NOT FOUND] $Type"; return }

foreach ($m in $t.Methods) {
    if ($Methods.Count -gt 0 -and ($Methods -notcontains $m.Name)) { continue }
    if (-not $m.HasBody) { Write-Output "`n### $($m.Name) (no body)"; continue }
    $ps = ($m.Parameters | ForEach-Object { "$($_.ParameterType.Name) $($_.Name)" }) -join ", "
    Write-Output "`n### $($m.ReturnType.Name) $($m.Name)($ps)"
    foreach ($i in $m.Body.Instructions) {
        $op = $i.Operand
        $opStr = ""
        if ($op -ne $null) {
            if ($op -is [Mono.Cecil.MethodReference]) { $opStr = $op.FullName }
            elseif ($op -is [Mono.Cecil.FieldReference]) { $opStr = $op.FullName }
            elseif ($op -is [Mono.Cecil.MemberReference]) { $opStr = $op.FullName }
            else { $opStr = $op.ToString() }
        }
        Write-Output ("  {0,-9} {1}" -f $i.OpCode.Name, $opStr)
    }
}
