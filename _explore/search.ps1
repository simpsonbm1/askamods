param(
    [string[]]$TypeKeywords = @(),
    [string[]]$MemberKeywords = @(),
    [switch]$Members
)

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

function IsNoise($fn) {
    return ($fn.Contains("MethodInfoStoreGeneric") -or $fn.Contains("Il2Cpp") -or $fn.Contains("/MethodInfoStore") -or $fn.Contains("NativeMethodInfoPtr"))
}

# Type-name search (game types only)
foreach ($kw in $TypeKeywords) {
    $hits = $allTypes | Where-Object {
        -not (IsNoise $_.FullName) -and
        $_.Name.IndexOf($kw, [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -and
        ($_.Namespace.StartsWith("SSSGame") -or $_.Namespace.StartsWith("SandSailor") -or $_.Namespace -eq "")
    } | ForEach-Object { $_.FullName } | Sort-Object -Unique
    Write-Output "`n[type ~ '$kw']"
    foreach ($h in $hits) { Write-Output "  $h" }
}

# Member-name search across SSSGame/SandSailor types
if ($Members) {
    foreach ($kw in $MemberKeywords) {
        Write-Output "`n[member ~ '$kw']"
        foreach ($t in ($allTypes | Where-Object { $_.Namespace -ne $null -and ($_.Namespace.StartsWith("SSSGame") -or $_.Namespace.StartsWith("SandSailor")) -and -not (IsNoise $_.FullName) })) {
            foreach ($m in ($t.Methods | Where-Object { -not $_.IsGetter -and -not $_.IsSetter })) {
                if ($m.Name.IndexOf($kw, [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
                    $ps = ($m.Parameters | ForEach-Object { $_.ParameterType.Name }) -join ","
                    Write-Output "  M $($t.Name).$($m.Name)($ps)"
                }
            }
            foreach ($f in ($t.Fields | Where-Object { -not $_.Name.StartsWith("Native") })) {
                if ($f.Name.IndexOf($kw, [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
                    Write-Output "  F $($t.Name).$($f.Name) : $($f.FieldType.Name)"
                }
            }
            foreach ($p in $t.Properties) {
                if ($p.Name.IndexOf($kw, [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
                    Write-Output "  P $($t.Name).$($p.Name) : $($p.PropertyType.Name)"
                }
            }
        }
    }
}
