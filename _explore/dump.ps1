param([string[]]$Types)

$base = "D:\SteamLibrary\steamapps\common\ASKA\BepInEx"
$dirs = @("$base\interop", "$base\core", "$base\unity-libs")
[void][System.Reflection.Assembly]::LoadFrom("d:\Claude Projects\askamods\_explore\bin\Debug\net10.0\Mono.Cecil.dll")

$resolver = New-Object Mono.Cecil.DefaultAssemblyResolver
foreach ($d in $dirs) { $resolver.AddSearchDirectory($d) }
$rp = New-Object Mono.Cecil.ReaderParameters
$rp.AssemblyResolver = $resolver

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

function BaseChain($t) {
    $bc = @(); $bt = $t.BaseType; $g = 0
    while ($bt -ne $null -and $g -lt 20) { $bc += $bt.Name; try { $bt = $bt.Resolve().BaseType } catch { $bt = $null }; $g++ }
    return ($bc -join " -> ")
}

function Dump($fullName) {
    $t = $allTypes | Where-Object { $_.FullName -eq $fullName } | Select-Object -First 1
    if ($t -eq $null) { Write-Output "`n##### [NOT FOUND] $fullName"; return }
    Write-Output "`n##### $($t.FullName)"
    Write-Output "  : $(BaseChain $t)"
    foreach ($i in $t.Interfaces) { Write-Output "  IFACE $($i.InterfaceType.Name)" }
    foreach ($f in ($t.Fields | Where-Object { -not $_.Name.StartsWith("Native") } | Sort-Object Name)) {
        $st = ""; if ($f.IsStatic) { $st = "static " }
        Write-Output "  F $st$($f.FieldType.Name) $($f.Name)"
    }
    foreach ($p in ($t.Properties | Sort-Object Name)) {
        Write-Output "  P $($p.PropertyType.Name) $($p.Name)"
    }
    foreach ($m in ($t.Methods | Where-Object { -not $_.IsConstructor -and -not $_.IsGetter -and -not $_.IsSetter } | Sort-Object Name)) {
        $st = ""; if ($m.IsStatic) { $st = "static " }
        $ps = ($m.Parameters | ForEach-Object { "$($_.ParameterType.Name) $($_.Name)" }) -join ", "
        Write-Output "  M $st$($m.ReturnType.Name) $($m.Name)($ps)"
    }
}

foreach ($n in $Types) { Dump $n }
