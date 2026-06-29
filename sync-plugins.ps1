<#
.SYNOPSIS
  Push this repo's committed mod DLLs into ASKA's live BepInEx\plugins folder.

.DESCRIPTION
  For each <ModName>\<ModName>.dll tracked in this repo, copy it into
  <PluginsDir>\<ModName>\ -- but only when the live copy actually differs
  (SHA256 comparison), so identical DLLs are left untouched.

  State-aware: if a mod is parked live as <ModName>.dll.off, the update is
  written to the .off file so the mod stays DISABLED (we never silently
  re-enable a parked mod). A mod with no live folder is installed enabled.

  Direction is repo -> live only. The build already writes the repo-side DLL;
  this just gets the live game folder up to the committed versions -- run it
  after a `git pull` on the other machine.

.PARAMETER PluginsDir
  Path to ASKA's BepInEx\plugins. Defaults to $env:ASKA_PLUGINS if set,
  otherwise the known desktop install. Override on a machine whose install
  path differs.

.PARAMETER DryRun
  Report what would change without copying anything.

.PARAMETER NoBackup
  Don't back up overwritten live DLLs (backups go to
  %TEMP%\askamods-sync-backups\<timestamp>\, outside the plugins folder).

.EXAMPLE
  .\sync-plugins.ps1 -DryRun      # preview
.EXAMPLE
  .\sync-plugins.ps1              # sync
#>
[CmdletBinding()]
param(
  [string]$PluginsDir = $(if ($env:ASKA_PLUGINS) { $env:ASKA_PLUGINS } else { 'D:\SteamLibrary\steamapps\common\ASKA\BepInEx\plugins' }),
  [switch]$DryRun,
  [switch]$NoBackup
)

$ErrorActionPreference = 'Stop'
$repo = $PSScriptRoot

# Mods intentionally parked (blocked spikes, not meant to load). On a machine that
# doesn't have them yet, install them DISABLED (.dll.off) so a known-blocked mod
# never loads. If a live enabled/disabled state already exists, that wins (below).
$ParkedByDefault = @('CookingStationFixMod', 'SeedHarvesterMod')

if (-not (Test-Path $PluginsDir)) {
  Write-Host "ERROR: plugins folder not found:" -ForegroundColor Red
  Write-Host "  $PluginsDir"
  Write-Host "Pass -PluginsDir <path> or set `$env:ASKA_PLUGINS to your ASKA BepInEx\plugins folder."
  exit 1
}

# Discover mods: a top-level <ModName> folder that contains <ModName>.dll.
$mods = Get-ChildItem -Directory $repo |
  Where-Object { $_.Name -match 'Mod$' -and (Test-Path (Join-Path $_.FullName "$($_.Name).dll")) } |
  Select-Object -ExpandProperty Name | Sort-Object

if (-not $mods) { Write-Host "No <ModName>\<ModName>.dll found under $repo" -ForegroundColor Red; exit 1 }

$backupRoot = Join-Path $env:TEMP ("askamods-sync-backups\" + (Get-Date -Format 'yyyyMMdd-HHmmss'))
$results = New-Object System.Collections.Generic.List[object]
$changed = 0
$backedUp = 0

foreach ($m in $mods) {
  $srcDll  = Join-Path $repo "$m\$m.dll"
  $liveDir = Join-Path $PluginsDir $m
  $liveOn  = Join-Path $liveDir "$m.dll"
  $liveOff = Join-Path $liveDir "$m.dll.off"

  # Pick the destination, preserving the live enabled/disabled state.
  # An existing live file (enabled or disabled) always wins; only a brand-new
  # install consults $ParkedByDefault to decide whether to land enabled or parked.
  if     (Test-Path $liveOn)              { $dst = $liveOn;  $state = 'enabled' }
  elseif (Test-Path $liveOff)             { $dst = $liveOff; $state = 'disabled' }
  elseif ($ParkedByDefault -contains $m)  { $dst = $liveOff; $state = 'new(parked)' }
  else                                    { $dst = $liveOn;  $state = 'new' }

  $srcHash = (Get-FileHash $srcDll -Algorithm SHA256).Hash
  $srcVer  = (Get-Item $srcDll).VersionInfo.FileVersion
  if (Test-Path $dst) {
    $dstHash = (Get-FileHash $dst -Algorithm SHA256).Hash
    $dstVer  = (Get-Item $dst).VersionInfo.FileVersion
  } else { $dstHash = $null; $dstVer = '-' }

  if ($srcHash -eq $dstHash) {
    $action = 'same'
  } else {
    $action = if ($null -eq $dstHash) { 'INSTALL' } else { 'UPDATE' }
    if (-not $DryRun) {
      New-Item -ItemType Directory -Force -Path $liveDir | Out-Null
      if ($dstHash -and -not $NoBackup) {
        New-Item -ItemType Directory -Force -Path $backupRoot | Out-Null
        Copy-Item $dst (Join-Path $backupRoot (Split-Path $dst -Leaf)) -Force
        $backedUp++
      }
      Copy-Item $srcDll $dst -Force
    }
    $changed++
  }

  $results.Add([PSCustomObject]@{
    Mod      = $m
    Action   = $action
    State    = $state
    LiveVer  = $dstVer
    RepoVer  = $srcVer
    DestFile = (Split-Path $dst -Leaf)
  })
}

$results | Format-Table -AutoSize | Out-String -Width 120 | Write-Host

if ($DryRun) {
  Write-Host ("DRY RUN -- nothing copied. {0} mod(s) would change." -f $changed) -ForegroundColor Yellow
} else {
  Write-Host ("{0} mod(s) updated." -f $changed) -ForegroundColor Green
  if ($backedUp -gt 0) { Write-Host ("Backed up {0} replaced DLL(s) to: {1}" -f $backedUp, $backupRoot) }
  if ($changed -gt 0) {
    Write-Host ""
    Write-Host "Reminder: Smart App Control may block a freshly-copied unsigned DLL on first" -ForegroundColor Yellow
    Write-Host "load (FileLoadException 0x800711C7). After launching ASKA, confirm each mod's" -ForegroundColor Yellow
    Write-Host "loaded version in BepInEx\LogOutput.log. If one is blocked, bump that mod's" -ForegroundColor Yellow
    Write-Host "PLUGIN_VERSION + csproj <Version>, rebuild, and re-run this script." -ForegroundColor Yellow
  }
}
