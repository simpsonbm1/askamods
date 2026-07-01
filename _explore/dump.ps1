Add-Type -Path 'D:\SteamLibrary\steamapps\common\ASKA\BepInEx\interop\SandSailorStudio.dll'
$t1 = [SSSGame.WorldObjectiveMarker]
Write-Host "WorldObjectiveMarker Fields:"
$t1.GetFields() | Select-Object Name
Write-Host "WorldObjectiveMarker Props:"
$t1.GetProperties() | Select-Object Name
Write-Host "WorldObjectiveMarker Methods:"
$t1.GetMethods() | Select-Object Name

$t2 = [SSSGame.CompassObjectiveMarker]
if ($null -ne $t2) {
    Write-Host "CompassObjectiveMarker Fields:"
    $t2.GetFields() | Select-Object Name
    Write-Host "CompassObjectiveMarker Props:"
    $t2.GetProperties() | Select-Object Name
    Write-Host "CompassObjectiveMarker Methods:"
    $t2.GetMethods() | Select-Object Name
}
