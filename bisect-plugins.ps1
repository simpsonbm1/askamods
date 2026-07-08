<#
.SYNOPSIS
  Enable/disable live BepInEx plugins for framerate (or crash) bisection, safely.

.DESCRIPTION
  Toggles each live plugin DLL between <name>.dll (loaded) and <name>.dll.off
  (skipped by BepInEx) -- the same convention sync-plugins.ps1 uses. Discovers
  EVERY plugin in the live folder, including external ones we didn't build
  (e.g. SummonReRoll), so nothing is missed during a bisect.

  State-safe: the first mutating action snapshots the current on/off layout to
  a save file, so -Restore always returns to exactly where you started
  (parked mods stay parked, whatever was on stays on). Nothing is deleted --
  only renamed .dll <-> .dll.off.

.PARAMETER PluginsDir
  ASKA's BepInEx\plugins. Defaults to $env:ASKA_PLUGINS, else the desktop path.

.EXAMPLE
  .\bisect-plugins.ps1 -Status                 # show what's on/off now
.EXAMPLE
  .\bisect-plugins.ps1 -DisableAll             # snapshot, then turn EVERYTHING off
.EXAMPLE
  .\bisect-plugins.ps1 -EnableOnly TreeRespawnMod,TorchFuelMod   # only these on
.EXAMPLE
  .\bisect-plugins.ps1 -Disable SummonReRoll   # turn one off, leave the rest
.EXAMPLE
  .\bisect-plugins.ps1 -Restore                # back to the saved starting layout
#>
[CmdletBinding(DefaultParameterSetName = 'Status')]
param(
  [Parameter(ParameterSetName = 'Status')]    [switch]$Status,
  [Parameter(ParameterSetName = 'DisableAll')][switch]$DisableAll,
  [Parameter(ParameterSetName = 'EnableAll')] [switch]$EnableAll,
  [Parameter(ParameterSetName = 'Restore')]   [switch]$Restore,
  [Parameter(ParameterSetName = 'EnableOnly')][string[]]$EnableOnly,
  [Parameter(ParameterSetName = 'Enable')]    [string[]]$Enable,
  [Parameter(ParameterSetName = 'Disable')]   [string[]]$Disable,
  [string]$PluginsDir = $(if ($env:ASKA_PLUGINS) { $env:ASKA_PLUGINS } else { 'D:\SteamLibrary\steamapps\common\ASKA\BepInEx\plugins' }),
  [string]$SaveFile   = (Join-Path $env:TEMP 'askamods-bisect-state.json')
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path $PluginsDir)) {
  Write-Host "ERROR: plugins folder not found: $PluginsDir" -ForegroundColor Red
  Write-Host "Pass -PluginsDir <path> or set `$env:ASKA_PLUGINS."
  exit 1
}

# Discover every plugin DLL (loaded or .off), keyed by mod name.
function Get-Plugins {
  Get-ChildItem -Path $PluginsDir -Recurse -File |
    Where-Object { $_.Name -like '*.dll' -or $_.Name -like '*.dll.off' } |
    ForEach-Object {
      $enabled = $_.Name -like '*.dll'
      $name = $_.Name -replace '\.off$', '' -replace '\.dll$', ''
      [PSCustomObject]@{ Name = $name; Enabled = $enabled; Path = $_.FullName; Dir = $_.DirectoryName }
    } | Sort-Object Name
}

function Set-Enabled([object]$p, [bool]$want) {
  if ($p.Enabled -eq $want) { return $false }
  $on  = Join-Path $p.Dir "$($p.Name).dll"
  $off = Join-Path $p.Dir "$($p.Name).dll.off"
  if ($want) { Rename-Item -LiteralPath $off -NewName (Split-Path $on -Leaf) }
  else       { Rename-Item -LiteralPath $on  -NewName (Split-Path $off -Leaf) }
  return $true
}

function Save-StateIfNeeded {
  if (Test-Path $SaveFile) { return }
  $snap = @{}
  foreach ($p in (Get-Plugins)) { $snap[$p.Name] = $p.Enabled }
  ($snap | ConvertTo-Json) | Set-Content -Path $SaveFile -Encoding utf8
  Write-Host "Saved starting layout -> $SaveFile" -ForegroundColor DarkGray
}

function Show-Status {
  $plugins = Get-Plugins
  $plugins | ForEach-Object {
    [PSCustomObject]@{ Mod = $_.Name; State = $(if ($_.Enabled) { 'ON' } else { 'off' }) }
  } | Format-Table -AutoSize | Out-String -Width 120 | Write-Host
  $on = ($plugins | Where-Object Enabled).Count
  Write-Host ("{0} ON / {1} total. Saved layout: {2}" -f $on, $plugins.Count, $(if (Test-Path $SaveFile) { 'yes (-Restore to revert)' } else { 'none yet' }))
}

switch ($PSCmdlet.ParameterSetName) {

  'Status' { Show-Status; break }

  'DisableAll' {
    Save-StateIfNeeded
    $n = 0; foreach ($p in (Get-Plugins)) { if (Set-Enabled $p $false) { $n++ } }
    Write-Host "Disabled all plugins ($n changed). BepInEx stays enabled (doorstop)." -ForegroundColor Yellow
    Show-Status
  }

  'EnableAll' {
    Save-StateIfNeeded
    $n = 0; foreach ($p in (Get-Plugins)) { if (Set-Enabled $p $true) { $n++ } }
    Write-Host "Enabled all plugins ($n changed). NOTE: this also un-parks CookingStationFix/SeedHarvester -- use -Restore to return to your real layout." -ForegroundColor Yellow
    Show-Status
  }

  'EnableOnly' {
    Save-StateIfNeeded
    $want = $EnableOnly
    foreach ($p in (Get-Plugins)) { [void](Set-Enabled $p ($want -contains $p.Name)) }
    Write-Host ("Enabled ONLY: {0}" -f ($want -join ', ')) -ForegroundColor Cyan
    Show-Status
  }

  'Enable' {
    Save-StateIfNeeded
    foreach ($p in (Get-Plugins)) { if ($Enable -contains $p.Name) { [void](Set-Enabled $p $true) } }
    Write-Host ("Enabled: {0}" -f ($Enable -join ', ')) -ForegroundColor Cyan
    Show-Status
  }

  'Disable' {
    Save-StateIfNeeded
    foreach ($p in (Get-Plugins)) { if ($Disable -contains $p.Name) { [void](Set-Enabled $p $false) } }
    Write-Host ("Disabled: {0}" -f ($Disable -join ', ')) -ForegroundColor Cyan
    Show-Status
  }

  'Restore' {
    if (-not (Test-Path $SaveFile)) { Write-Host "No saved layout at $SaveFile -- nothing to restore." -ForegroundColor Red; exit 1 }
    $snap = Get-Content $SaveFile -Raw | ConvertFrom-Json
    $n = 0
    foreach ($p in (Get-Plugins)) {
      $prop = $snap.PSObject.Properties[$p.Name]
      if ($prop) { if (Set-Enabled $p ([bool]$prop.Value)) { $n++ } }
    }
    Write-Host "Restored saved starting layout ($n changed)." -ForegroundColor Green
    Remove-Item $SaveFile -Force
    Show-Status
  }
}
