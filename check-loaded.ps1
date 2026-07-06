<#
.SYNOPSIS
  Verifies which mod versions the game actually loaded, in one command.

.DESCRIPTION
  For every mod in this repo (top-level folder with <Mod>/<Mod>.csproj), reports:
    - repo version   (csproj <Version> — ground truth, kept equal to PLUGIN_VERSION by the
                      pre-commit hook)
    - live DLL       (version of ASKA\BepInEx\plugins\<Mod>\<Mod>.dll, or .dll.off = parked)
    - loaded         (what BepInEx logged at the LAST game launch — LogOutput.log is per-launch)
  and a verdict. Run after every deploy + game launch instead of ad-hoc Select-String.

  NOTE: "loaded" reflects the most recent launch only. If you deployed after the game last ran,
  expect LOG STALE until the next launch.

.PARAMETER AskaPath
  Override the ASKA install path (default: D:\SteamLibrary\steamapps\common\ASKA, or $env:ASKA_PATH).
#>
param([string]$AskaPath = $(if ($env:ASKA_PATH) { $env:ASKA_PATH } else { 'D:\SteamLibrary\steamapps\common\ASKA' }))
$ErrorActionPreference = 'Stop'

$repo = $PSScriptRoot
$pluginsDir = Join-Path $AskaPath 'BepInEx\plugins'
$logPath = Join-Path $AskaPath 'BepInEx\LogOutput.log'
$log = if (Test-Path $logPath) { Get-Content $logPath -Raw } else { '' }
if (-not $log) { Write-Host "WARNING: no LogOutput.log at $logPath - 'loaded' column will be empty." -ForegroundColor Yellow }

$rows = foreach ($csproj in Get-ChildItem $repo -Directory | ForEach-Object { Join-Path $_.FullName "$($_.Name).csproj" } | Where-Object { Test-Path $_ }) {
    $mod = Split-Path (Split-Path $csproj -Parent) -Leaf
    $repoVer = ([regex]::Match((Get-Content $csproj -Raw), '<Version>([\d.]+)</Version>')).Groups[1].Value

    # PLUGIN_NAME can differ from the folder name (e.g. "Jotun Blood Yield Mod") - it's what
    # BepInEx prints in the Loading line.
    $pluginName = $mod
    foreach ($cs in (Get-ChildItem (Join-Path $repo $mod) -Filter *.cs -File)) {
        $m = [regex]::Match((Get-Content $cs.FullName -Raw), 'PLUGIN_NAME\s*=\s*"([^"]+)"')
        if ($m.Success) { $pluginName = $m.Groups[1].Value; break }
    }

    $liveDll = Join-Path $pluginsDir "$mod\$mod.dll"
    $liveVer = ''
    $parked = $false
    if (Test-Path $liveDll) {
        # FileVersionInfo reads the PE header WITHOUT loading the assembly - GetAssemblyName
        # throws 0x800711C7 on any DLL Smart App Control has blocked (confirmed 2026-07-05).
        $fv = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($liveDll).FileVersion
        if ($fv) { $liveVer = ($fv -split '\.')[0..2] -join '.' }
    } elseif (Test-Path "$liveDll.off") {
        $parked = $true
    }

    $loadedVer = ''
    $m = [regex]::Match($log, [regex]::Escape("Loading [$pluginName ") + '([\d.]+)\]')
    if ($m.Success) { $loadedVer = $m.Groups[1].Value }

    $verdict =
        if ($parked)                    { 'PARKED (.dll.off)' }
        elseif (-not $liveVer)          { 'NOT DEPLOYED' }
        elseif ($liveVer -ne $repoVer)  { "LIVE STALE (repo $repoVer)" }
        elseif (-not $loadedVer)        { 'NOT IN LAST LAUNCH LOG' }
        elseif ($loadedVer -ne $repoVer){ "LOG STALE (launch predates deploy?)" }
        else                            { 'OK' }

    [pscustomobject]@{ Mod = $mod; Repo = $repoVer; Live = $(if ($parked) {'off'} else {$liveVer}); Loaded = $loadedVer; Verdict = $verdict }
}

$rows | Format-Table -AutoSize
$bad = @($rows | Where-Object { $_.Verdict -notin 'OK', 'PARKED (.dll.off)' })
if ($bad.Count -gt 0) { Write-Host "$($bad.Count) mod(s) need attention." -ForegroundColor Yellow } else { Write-Host "All live mods consistent." -ForegroundColor Green }
