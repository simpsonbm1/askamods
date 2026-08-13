# Locate the ASKA PERSONAL (bench-free) crafting menu UI class and its craftability gate.
# CraftFromStorageMod currently hooks SSSGame.UI.CraftMenu : ContextMenu, which in-game evidence
# (2026-08-13) shows is the CRAFTING TABLE menu only. This enumerates the sibling UI types so the
# personal menu can be named.
$base = "D:\SteamLibrary\steamapps\common\ASKA\BepInEx"
Add-Type -Path "$base\core\Mono.Cecil.dll"

$mods = @()
foreach ($f in Get-ChildItem "$base\interop\*.dll") {
    try { $mods += [Mono.Cecil.AssemblyDefinition]::ReadAssembly($f.FullName).MainModule } catch {}
}
Write-Host "Loaded $($mods.Count) interop module(s)."
Write-Host ""

Write-Host "===== UI types whose name matches Craft|Create|Handcraft|Build|Recipe|Blueprint ====="
foreach ($m in $mods) {
    foreach ($t in $m.Types) {
        if ($t.Namespace -notlike "SSSGame*" -and $t.Namespace -notlike "SandSailorStudio*") { continue }
        if ($t.Name -notmatch "Craft|Create|Handcraft|Recipe|Blueprint") { continue }
        Write-Host ("{0,-58} base={1,-28} ns={2}" -f $t.FullName, $t.BaseType.Name, $t.Namespace)
    }
}
Write-Host ""

Write-Host "===== Every type deriving from ContextMenu or TabPage or Menu ====="
foreach ($m in $mods) {
    foreach ($t in $m.Types) {
        if (-not $t.BaseType) { continue }
        if ($t.BaseType.Name -notmatch "^(ContextMenu|TabPage|Menu|MenuPage|UIPanel)$") { continue }
        Write-Host ("{0,-58} base={1}" -f $t.FullName, $t.BaseType.Name)
    }
}
