# WIDEN THE NET: enumerate every game type whose display name is LOCALIZED, so we can grep the
# mods for uses of those members. The tell for a localized entity is the Loc/GetKey/Localized/
# localizedName member cluster inherited from the localization plumbing.
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

Write-Host "=========== A. TYPES CARRYING THE LOCALIZATION MEMBER CLUSTER ==========="
Write-Host "(has GetKey/Loc AND a localizedName-ish member => its .Name/.Description is TRANSLATED)"
foreach ($t in $allTypes) {
    if ($t.FullName -match "<|MethodInfoStore|__c__") { continue }
    $props = @($t.Properties | ForEach-Object { $_.Name })
    $meths = @($t.Methods | ForEach-Object { $_.Name })
    $hasKey = ($meths -contains "GetKey") -or ($meths -contains "Loc")
    $hasLocName = ($props | Where-Object { $_ -match "^localized" }).Count -gt 0
    if ($hasKey -and $hasLocName) {
        $exposed = ($props | Where-Object { $_ -match "^(Name|Description|Lore|Title)$" }) -join ", "
        Write-Host ("  {0,-62} exposes: {1}" -f $t.FullName, $exposed)
    }
}

Write-Host ""
Write-Host "=========== B. ANY TYPE WITH A 'localizedName'-ISH MEMBER ==========="
foreach ($t in $allTypes) {
    if ($t.FullName -match "<|MethodInfoStore|__c__") { continue }
    $hits = @()
    foreach ($p in $t.Properties) { if ($p.Name -match "localizedName|localizedTitle|localisedName") { $hits += $p.Name } }
    if ($hits.Count -gt 0) { Write-Host ("  {0,-62} {1}" -f $t.FullName, ($hits -join ", ")) }
}

Write-Host ""
Write-Host "=========== C. DISPLAY-NAME ACCESSORS ON NON-ITEM TYPES (Structure/Creature/etc) ==========="
foreach ($t in $allTypes) {
    if ($t.FullName -match "<|MethodInfoStore|__c__") { continue }
    if ($t.FullName -notmatch "SSSGame|SandSailorStudio") { continue }
    foreach ($m in $t.Methods) {
        if ($m.Name -match "^(GetName|GetDisplayName|GetTitle)$") {
            Write-Host ("  {0}.{1}() : {2}" -f $t.FullName, $m.Name, $m.ReturnType.Name)
        }
    }
    foreach ($p in $t.Properties) {
        if ($p.Name -match "^(DefaultName|StructureName|DisplayName|Title)$") {
            Write-Host ("  {0}.{1} : {2}  [property]" -f $t.FullName, $p.Name, $p.PropertyType.Name)
        }
    }
}
