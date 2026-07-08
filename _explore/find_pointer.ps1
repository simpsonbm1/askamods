Add-Type -Path "D:\SteamLibrary\steamapps\common\ASKA\BepInEx\core\Mono.Cecil.dll"
$interop = "D:\SteamLibrary\steamapps\common\ASKA\BepInEx\interop"
Get-ChildItem $interop -Filter *.dll | ForEach-Object {
    try {
        $a = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($_.FullName)
        foreach ($t in $a.MainModule.GetTypes()) {
            if ($t.FullName -eq "UnityEngine.EventSystems.PointerEventData" -or $t.Name -eq "InputButton") {
                Write-Host "$($_.Name)  ->  $($t.FullName)"
            }
        }
    } catch {}
}
