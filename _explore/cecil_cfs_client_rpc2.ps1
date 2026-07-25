# CraftFromStorage #0052 step 1, pass 2.
#
# Pass 1 established: Il2CppInterop STRIPS custom attributes, so [Rpc(RpcSources...)] is not
# readable and "who may call this" cannot be decided from attributes. Fall back to the TaskUnlocker
# method: enumerate the NAMES. Pass 1 also corrected my type guesses - the real networked storage
# types are SSSGame.Network.NetworkItemStorage and friends, and the game's own player-moves-items
# path is SSSGame.StorageInteraction / PlayerStorageInteractionSession.
#
# ASCII only, explicit loops (PowerShell 5.1).
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
Write-Host ("loaded {0} types" -f $allTypes.Count)

function Format-Sig($m) {
    $ps = @()
    foreach ($p in $m.Parameters) { $ps += ("{0} {1}" -f $p.ParameterType.Name, $p.Name) }
    return ("{0}({1}) : {2}" -f $m.Name, [string]::Join(", ", $ps), $m.ReturnType.Name)
}
function Get-ParamText($m) {
    $ps = @()
    foreach ($p in $m.Parameters) { $ps += $p.ParameterType.FullName }
    return [string]::Join(",", $ps)
}
function Is-GameType($t) {
    if ($t.FullName -match "^SSSGame") { return $true }
    if ($t.FullName -match "^SandSailorStudio") { return $true }
    return $false
}

Write-Host ""
Write-Host "=========== A. EVERY Rpc_* method on a game type (the whole callable RPC surface) ==========="
Write-Host "    filtered to ones whose name or parameters mention item/storage/container/inventory"
$subject = "Item|Container|Storage|Inventory|Stock|Chest|Resource"
$n = 0
foreach ($t in ($allTypes | Sort-Object FullName)) {
    if (-not (Is-GameType $t)) { continue }
    if ($t.FullName -match "<") { continue }
    foreach ($m in $t.Methods) {
        if ($m.Name -notmatch "^Rpc_") { continue }
        $pt = Get-ParamText $m
        $hit = $false
        if ($m.Name -match $subject) { $hit = $true }
        if ($pt -match $subject) { $hit = $true }
        if ($t.FullName -match $subject) { $hit = $true }
        if (-not $hit) { continue }
        $n++
        Write-Host ("  {0}" -f $t.FullName)
        Write-Host ("      {0}" -f (Format-Sig $m))
    }
}
Write-Host ("  {0} item/storage-related Rpc_ methods" -f $n)

Write-Host ""
Write-Host "=========== B. Full surface of the real networked-storage + interaction types ==========="
$targets = @(
  "SSSGame.Network.NetworkItemStorage",
  "SSSGame.Network.NetworkItemCountItemStorage",
  "SSSGame.Network.NetworkSpecificNoAttributeItemStorage",
  "SSSGame.Network.NetworkComponent",
  "SSSGame.StorageInteraction",
  "SSSGame.StorageInteractionSession",
  "SSSGame.PlayerStorageInteractionSession",
  "SandSailorStudio.Inventory.MoveItemToContainerOperation",
  "SandSailorStudio.Inventory.ItemContainer"
)
foreach ($tn in $targets) {
    $t = $byName[$tn]
    if ($null -eq $t) { Write-Host ("  NO TYPE {0}" -f $tn); continue }
    $bn = "?"
    if ($t.BaseType) { $bn = $t.BaseType.FullName }
    Write-Host ""
    Write-Host ("  ### {0}   base={1}" -f $tn, $bn)
    foreach ($m in ($t.Methods | Sort-Object Name)) {
        if ($m.Name -match "^get_") { continue }
        if ($m.Name -match "^set_") { continue }
        if ($m.Name -match "^add_") { continue }
        if ($m.Name -match "^remove_") { continue }
        if ($m.Name -match "^\.") { continue }
        $mark = "   "
        if ($m.Name -match "^Rpc_") { $mark = ">> " }
        if ($m.Name -match "^Request") { $mark = ">> " }
        Write-Host ("     {0}{1}" -f $mark, (Format-Sig $m))
    }
}

Write-Host ""
Write-Host "=========== C. NetworkWorldDataManager - full surface (has RequestAddInventoryItemInstance) ==========="
$t = $byName["SSSGame.Network.NetworkWorldDataManager"]
if ($null -eq $t) { Write-Host "  NO TYPE" } else {
    foreach ($m in ($t.Methods | Sort-Object Name)) {
        if ($m.Name -match "^get_") { continue }
        if ($m.Name -match "^set_") { continue }
        if ($m.Name -match "^\.") { continue }
        $mark = "   "
        if ($m.Name -match "^Rpc_") { $mark = ">> " }
        if ($m.Name -match "^Request") { $mark = ">> " }
        Write-Host ("     {0}{1}" -f $mark, (Format-Sig $m))
    }
}

Write-Host ""
Write-Host "=========== D. Authority helpers: what does the game itself test before a storage write? ==========="
Write-Host "    (members named HasAuthority/HasStateAuthority/IsServer/IsHost on game types)"
$seen = @{}
foreach ($t in ($allTypes | Sort-Object FullName)) {
    if (-not (Is-GameType $t)) { continue }
    if ($t.FullName -match "<") { continue }
    foreach ($p in $t.Properties) {
        if ($p.Name -notmatch "Authority|IsServer|IsHost|IsClient|IsMaster") { continue }
        $k = $t.FullName + "." + $p.Name
        if ($seen.ContainsKey($k)) { continue }
        $seen[$k] = 1
        Write-Host ("  {0}.{1} : {2}" -f $t.FullName, $p.Name, $p.PropertyType.Name)
    }
}
