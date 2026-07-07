param([string[]]$Names, [string]$Filter = "")
$base = "D:\SteamLibrary\steamapps\common\ASKA\BepInEx"
[void][System.Reflection.Assembly]::LoadFrom("d:\Claude Projects\askamods\_explore\bin\Debug\net10.0\Mono.Cecil.dll")
$resolver = New-Object Mono.Cecil.DefaultAssemblyResolver
foreach ($d in @("$base\interop","$base\core","$base\unity-libs")) { $resolver.AddSearchDirectory($d) }
$rp = New-Object Mono.Cecil.ReaderParameters; $rp.AssemblyResolver = $resolver
$all = New-Object System.Collections.Generic.List[object]
foreach ($f in Get-ChildItem "$base\interop\*.dll") {
    try { $a = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($f.FullName, $rp)
        foreach ($t in $a.MainModule.Types) { $all.Add($t); foreach ($n in $t.NestedTypes){$all.Add($n)} } } catch {}
}
function Dump($t) {
    Write-Output "==== $($t.FullName)  : base=$($t.BaseType.Name) ===="
    foreach ($f in $t.Fields) { if ($f.Name.StartsWith("Native")){continue}
        if ($Filter -eq "" -or $f.Name -match $Filter) { Write-Output "  F $($f.Name) : $($f.FieldType.Name)" } }
    foreach ($p in $t.Properties) { if ($Filter -eq "" -or $p.Name -match $Filter) { Write-Output "  P $($p.Name) : $($p.PropertyType.Name)" } }
    foreach ($m in $t.Methods) { if ($m.IsGetter -or $m.IsSetter){continue}
        if ($Filter -eq "" -or $m.Name -match $Filter) {
            $ps = ($m.Parameters | ForEach-Object { "$($_.ParameterType.Name) $($_.Name)" }) -join ", "
            Write-Output "  M $($m.Name)($ps) : $($m.ReturnType.Name)" } }
}
foreach ($n in $Names) {
    $hits = $all | Where-Object { $_.Name -eq $n }
    if (-not $hits) { Write-Output "NO TYPE $n (trying contains)"; $hits = $all | Where-Object { $_.Name -like "*$n*" -and ($_.Namespace -like "SSSGame*" -or $_.Namespace -like "SandSailor*") } }
    foreach ($h in $hits) { Dump $h; Write-Output "" }
}
