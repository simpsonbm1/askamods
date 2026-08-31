# Member-reference sweep (2026-08-31 game update).
# For every committed mod DLL, verify each type/method/field reference into the regenerated
# interop assemblies still resolves. Catches runtime-called members that a clean plugin load
# never touches (the get_Lifespan / RequestMarkFishinGround class of break).

$ErrorActionPreference = 'Stop'
Add-Type -Path "D:\SteamLibrary\steamapps\common\ASKA\BepInEx\core\Mono.Cecil.dll"
$interopDir = "D:\SteamLibrary\steamapps\common\ASKA\BepInEx\interop"
$repo = "D:\Claude Projects\askamods"

# cache: interop assembly name -> (dotted fullname -> TypeDefinition), or $null if no such DLL
$asmCache = @{}
function Get-InteropIndex([string]$asmName) {
    if ($script:asmCache.ContainsKey($asmName)) { return $script:asmCache[$asmName] }
    $path = Join-Path $interopDir ($asmName + ".dll")
    if (-not (Test-Path $path)) { $script:asmCache[$asmName] = $null; return $null }
    $idx = @{}
    $a = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($path)
    $stack = New-Object System.Collections.Stack
    foreach ($t in $a.MainModule.Types) { $stack.Push($t) }
    while ($stack.Count -gt 0) {
        $t = $stack.Pop()
        $idx[$t.FullName] = $t   # Cecil FullName uses / for nesting; refs use the same form
        foreach ($n in $t.NestedTypes) { $stack.Push($n) }
    }
    $script:asmCache[$asmName] = $idx
    return $idx
}

function Get-ScopeName($scope) {
    if ($scope -is [Mono.Cecil.AssemblyNameReference]) { return $scope.Name }
    if ($scope -is [Mono.Cecil.ModuleDefinition]) { return $scope.Assembly.Name.Name }
    return $scope.ToString()
}

$modDlls = Get-ChildItem -Path $repo -Directory | ForEach-Object {
    $dll = Join-Path $_.FullName ($_.Name + ".dll")
    if (Test-Path $dll) { $dll }
}

$totalMissing = 0
foreach ($dll in $modDlls) {
    $modName = [System.IO.Path]::GetFileNameWithoutExtension($dll)
    $m = [Mono.Cecil.ModuleDefinition]::ReadModule($dll)
    $misses = @()

    foreach ($tr in $m.GetTypeReferences()) {
        $scopeName = Get-ScopeName $tr.Scope
        $idx = Get-InteropIndex $scopeName
        if ($null -eq $idx) { continue }               # not an interop-dir assembly
        if (-not $idx.ContainsKey($tr.FullName)) {
            $misses += "TYPE   $($tr.FullName)  [$scopeName]"
        }
    }

    foreach ($mr in $m.GetMemberReferences()) {
        $dt = $mr.DeclaringType
        if ($dt -is [Mono.Cecil.GenericInstanceType]) { $dt = $dt.ElementType }
        if ($dt -is [Mono.Cecil.ArrayType]) { continue }
        $scopeName = Get-ScopeName $dt.Scope
        $idx = Get-InteropIndex $scopeName
        if ($null -eq $idx) { continue }
        if (-not $idx.ContainsKey($dt.FullName)) { continue }  # already reported as TYPE miss
        $t = $idx[$dt.FullName]
        $target = $mr
        if ($target -is [Mono.Cecil.MethodReference]) {
            if ($target -is [Mono.Cecil.GenericInstanceMethod]) { $target = $target.ElementMethod }
            $name = $target.Name
            $pc = $target.Parameters.Count
            $hit = @($t.Methods | Where-Object { $_.Name -eq $name -and $_.Parameters.Count -eq $pc })
            if ($hit.Count -eq 0) {
                $anyName = @($t.Methods | Where-Object { $_.Name -eq $name })
                if ($anyName.Count -gt 0) {
                    $sigs = ($anyName | ForEach-Object { "($(($_.Parameters | ForEach-Object { $_.ParameterType.Name }) -join ','))" }) -join " "
                    $misses += "SIG    $($dt.FullName)::$name  ref has $pc param(s); game now has $sigs"
                } else {
                    $misses += "METHOD $($dt.FullName)::$name($pc params)"
                }
            }
        } elseif ($target -is [Mono.Cecil.FieldReference]) {
            $hit = @($t.Fields | Where-Object { $_.Name -eq $target.Name })
            if ($hit.Count -eq 0) { $misses += "FIELD  $($dt.FullName)::$($target.Name)" }
        }
    }

    $misses = $misses | Sort-Object -Unique
    if ($misses.Count -eq 0) {
        Write-Host ("CLEAN   {0}" -f $modName)
    } else {
        Write-Host ("BROKEN  {0}  ({1} unresolved reference(s)):" -f $modName, $misses.Count)
        $misses | ForEach-Object { Write-Host ("        {0}" -f $_) }
        $totalMissing += $misses.Count
    }
    $m.Dispose()
}
Write-Host ""
Write-Host ("Sweep done. {0} unresolved reference(s) across all mods." -f $totalMissing)
