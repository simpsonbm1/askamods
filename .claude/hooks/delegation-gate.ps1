<#
.SYNOPSIS
  Delegation gate. Refuses a MAIN-SESSION source-code edit or build, forcing the one-off-vs-delegate
  decision, then lets the retry through. Delegated work (any subagent) passes silently.

.DESCRIPTION
  The defect this exists for: docs/agent-delegation.md says the main thread orchestrates
  (research/design/spec) and mod-implementer writes the code, with a single exception — a one-off
  genuinely cheaper inline than delegating. That rule is prose; it was ignored repeatedly across a
  session even while being pointed at, because prose executes only when the model remembers it.

  Mechanism, not instruction (process-hygiene prime rule). The discriminator is empirical: a
  PreToolUse payload carries `agent_id` ONLY inside a subagent (fire-verified 2026-07-21 — a
  general-purpose subagent's Bash call had agent_id/agent_type; every main-session call omitted
  them). So a code action with no agent_id IS the main thread doing it inline.

  Scope: gates Write/Edit/MultiEdit to .cs/.csproj and Bash/PowerShell `dotnet build`/`msbuild`.
  Docs (.md), config, hooks, reads, and non-build shell are untouched — the main thread legitimately
  does those. Per-session, per-action marker so the deliberate retry proceeds (identical call passes;
  a different file/build gets its own gate). Fails OPEN on any error — never wedge the session.

  What it CANNOT do: force the judgment. It delivers the prompt at the decision point; choosing
  one-off vs delegate is still judgment (process-hygiene: a judgment call is prompted, not guaranteed).

.NOTES
  Modes:
    (default)  PreToolUse hook — reads hook JSON on stdin. Exit 2 = refuse.
    -SelfTest  Built-in test suite (synthesized events). Exit 1 on any failure.
#>
[CmdletBinding()]
param([switch]$SelfTest)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Test-GatedAction {
    # Returns @{ Gate=$bool; Kind='code'|'log'; Key=$string; What=$string } for an event object.
    param($ev)
    $tool = ''
    if ($ev.PSObject.Properties.Name -contains 'tool_name') { $tool = [string]$ev.tool_name }

    # Salient string inputs (Read.file_path / Grep.path / Bash.command / Write.file_path).
    $fp = ''; $cmd = ''; $gpath = ''
    if ($ev.PSObject.Properties.Name -contains 'tool_input' -and $ev.tool_input) {
        $ti = $ev.tool_input
        if ($ti.PSObject.Properties.Name -contains 'file_path') { $fp = [string]$ti.file_path }
        if ($ti.PSObject.Properties.Name -contains 'command')   { $cmd = [string]$ti.command }
        if ($ti.PSObject.Properties.Name -contains 'path')      { $gpath = [string]$ti.path }
    }

    # --- BepInEx log review -> log-analyst. Reading/grepping/shell-touching the BepInEx logs is
    #     triage (loaded versions, exceptions, fire markers), which is log-analyst's job. ---
    if (($tool -in @('Read', 'Grep', 'Bash', 'PowerShell')) -and
        ("$fp $cmd $gpath" -match '(?i)(LogOutput\.log|ErrorLog\.log)')) {
        return @{ Gate = $true; Kind = 'log'; Key = 'log-review'; What = 'review the BepInEx log' }
    }

    # --- Source edit / build -> mod-implementer ---
    if ($tool -in @('Write', 'Edit', 'MultiEdit')) {
        if ($fp -match '\.(cs|csproj)$') { return @{ Gate = $true; Kind = 'code'; Key = "edit:$fp"; What = "edit $fp" } }
    }
    elseif ($tool -in @('Bash', 'PowerShell')) {
        # Match a build only at a COMMAND boundary (start, or after && ; | or a newline), so a
        # git-commit message or any prose that merely mentions "dotnet build" does not trip it
        # (fire-verified false positive 2026-07-21: a commit whose message described this gate).
        if ($cmd -match '(?i)(^|&&|;|\||[\r\n])\s*(cd\s+[^\r\n&;|]+&&\s*)?(dotnet\s+build|msbuild)\b') {
            return @{ Gate = $true; Kind = 'code'; Key = "build:$cmd"; What = 'a build (dotnet build)' }
        }
    }
    return @{ Gate = $false; Kind = ''; Key = ''; What = '' }
}

function Write-Gate {
    param([string]$What, [string]$Kind)
    try {
        [Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
        [Console]::InputEncoding  = [System.Text.UTF8Encoding]::new($false)
    } catch { }
    $e = { param($s) [Console]::Error.WriteLine($s) }
    & $e "DELEGATION GATE - the MAIN session is about to $What."
    & $e "Repeat the call to proceed. First, one question:"
    & $e ""
    & $e "    Is this the RARE one-off genuinely cheaper inline than the delegation round-trip?"
    & $e ""
    & $e "  YES -> tiny, self-contained, the handoff would cost more than the task. Repeat, proceed."
    if ($Kind -eq 'log') {
        & $e "  NO  -> STOP. Hand log-analyst the BepInEx log: it reports loaded versions, exceptions,"
        & $e "         and fire-verification markers (docs/agent-delegation.md). You DIAGNOSE from its"
        & $e "         report - gathering the evidence is delegated, the reasoning is yours."
    } else {
        & $e "  NO  -> STOP. You are the orchestrator (docs/agent-delegation.md): research, design, write"
        & $e "         the spec, HAND OFF. Give mod-implementer a self-contained prompt - files, exact"
        & $e "         changes, PLUGIN_VERSION + csproj bump, build, report - and let it write the code."
    }
    & $e ""
    & $e "         Rationalizations that have already defeated this, and do NOT count:"
    & $e "           'I'm mid-flow / had momentum'   -> that IS the failure this catches."
    & $e "           'it's just a quick check/probe' -> triage and probes are delegable too."
    & $e "           'I designed it, I'll just type' -> designing is your job; the doing is not."
    & $e "         The only test is whether delegating genuinely costs more than the task itself."
}

# ---------------------------------------------------------------------------------------------
# Self-test: synthesized events, positive AND negative, idempotency, fail-open.
# ---------------------------------------------------------------------------------------------
if ($SelfTest) {
    $me = $PSCommandPath
    $tmp = Join-Path $env:TEMP ("delegation-gate-selftest-" + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $tmp -Force | Out-Null

    function Invoke-Gate {
        param([string]$Json)
        $err = Join-Path $tmp ('err-' + [guid]::NewGuid().ToString('N') + '.txt')
        $inp = Join-Path $tmp ('in-'  + [guid]::NewGuid().ToString('N') + '.json')
        Set-Content -LiteralPath $inp -Value $Json -Encoding utf8
        # $me can contain spaces (this repo lives under "D:\Claude Projects\...") — Start-Process
        # -ArgumentList splits on spaces, so the -File path must be wrapped in literal quotes.
        $p = Start-Process -FilePath 'powershell.exe' `
                -ArgumentList @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', ('"' + $me + '"')) `
                -RedirectStandardInput $inp -RedirectStandardError $err -NoNewWindow -Wait -PassThru
        $stderr = ''
        if (Test-Path $err) { $stderr = Get-Content -LiteralPath $err -Raw -Encoding UTF8 }
        return @{ Code = $p.ExitCode; Err = "$stderr" }
    }
    $script:tfails = 0
    function Check { param([string]$Name, [bool]$Ok)
        if ($Ok) { Write-Host "  ok   $Name" } else { Write-Host "  FAIL $Name"; $script:tfails++ } }

    $sid = 'sess-' + [guid]::NewGuid().ToString('N')
    # main-session events (no agent_id)
    $mkMain = { param($tool, $inputObj) (@{ session_id = $sid; tool_name = $tool; tool_input = $inputObj } | ConvertTo-Json -Compress) }
    # subagent events (agent_id present)
    $mkSub  = { param($tool, $inputObj) (@{ session_id = $sid; agent_id = 'abc123'; agent_type = 'general-purpose'; tool_name = $tool; tool_input = $inputObj } | ConvertTo-Json -Compress) }

    # 1. main-session .cs edit blocks
    $r = Invoke-Gate (& $mkMain 'Edit' @{ file_path = 'C:\x\Foo.cs' })
    Check 'main .cs edit is refused (exit 2)' ($r.Code -eq 2)
    Check 'refusal carries the one-off question' ($r.Err -match 'RARE one-off')
    Check 'refusal names the orchestrator handoff' ($r.Err -match 'mod-implementer')
    Check 'refusal closes the momentum rationalization' ($r.Err -match 'IS the failure this catches')
    Check 'refusal closes the probe rationalization' ($r.Err -match 'triage and probes are delegable')

    # 2. subagent .cs edit passes (delegated)
    $r = Invoke-Gate (& $mkSub 'Edit' @{ file_path = 'C:\x\Foo.cs' })
    Check 'subagent .cs edit passes (exit 0)' ($r.Code -eq 0)

    # 3. non-source main edits pass
    Check 'main .md edit passes'   ((Invoke-Gate (& $mkMain 'Edit'  @{ file_path = 'C:\x\doc.md' })).Code -eq 0)
    Check 'main .txt write passes' ((Invoke-Gate (& $mkMain 'Write' @{ file_path = 'C:\x\note.txt' })).Code -eq 0)
    Check 'main .json edit passes' ((Invoke-Gate (& $mkMain 'Edit'  @{ file_path = 'C:\x\settings.json' })).Code -eq 0)

    # 4. .csproj gated; build gated; non-build shell + read not gated
    Check 'main .csproj write blocks' ((Invoke-Gate (& $mkMain 'Write' @{ file_path = 'C:\x\Mod.csproj' })).Code -eq 2)
    Check 'main dotnet build blocks'  ((Invoke-Gate (& $mkMain 'Bash'  @{ command = 'cd x && dotnet build' })).Code -eq 2)
    Check 'main ";"-separated build blocks' ((Invoke-Gate (& $mkMain 'PowerShell' @{ command = 'Set-Location x; dotnet build' })).Code -eq 2)
    # FALSE-POSITIVE regression lock: a git commit whose MESSAGE merely mentions "dotnet build" is
    # not a build invocation and must not gate (caught in fire-verify 2026-07-21).
    Check 'git commit mentioning dotnet build not gated' ((Invoke-Gate (& $mkMain 'Bash' @{ command = "git commit -m 'refuses a dotnet build inline'" })).Code -eq 0)
    Check 'subagent dotnet build passes' ((Invoke-Gate (& $mkSub 'Bash' @{ command = 'dotnet build' })).Code -eq 0)
    Check 'main git status not gated'    ((Invoke-Gate (& $mkMain 'Bash' @{ command = 'git status' })).Code -eq 0)
    Check 'main Read .cs not gated'      ((Invoke-Gate (& $mkMain 'Read' @{ file_path = 'C:\x\Foo.cs' })).Code -eq 0)

    # 4b. BepInEx log review -> log-analyst. Fresh session per check because the log marker is coarse
    #     (per-session): otherwise the first block sets the marker and the rest would pass.
    $log = 'D:\SteamLibrary\steamapps\common\ASKA\BepInEx\LogOutput.log'
    $freshMain = { param($t, $i) (@{ session_id = ('sess-' + [guid]::NewGuid().ToString('N')); tool_name = $t; tool_input = $i } | ConvertTo-Json -Compress) }
    $rl = Invoke-Gate (& $freshMain 'Read' @{ file_path = $log })
    Check 'main Read of BepInEx log blocks (exit 2)'   ($rl.Code -eq 2)
    Check 'log refusal names log-analyst'              ($rl.Err -match 'log-analyst')
    Check 'main Grep on BepInEx log blocks'            ((Invoke-Gate (& $freshMain 'Grep' @{ path = $log; pattern = 'x' })).Code -eq 2)
    Check 'main Bash grep of LogOutput.log blocks'     ((Invoke-Gate (& $freshMain 'Bash' @{ command = 'grep foo LogOutput.log' })).Code -eq 2)
    Check 'subagent Read of BepInEx log passes'        ((Invoke-Gate (& $mkSub 'Read' @{ file_path = $log })).Code -eq 0)
    Check 'main Read of a non-log file passes'         ((Invoke-Gate (& $freshMain 'Read' @{ file_path = 'C:\x\readme.txt' })).Code -eq 0)
    Check 'code refusal names mod-implementer'         ((Invoke-Gate (& $freshMain 'Write' @{ file_path = 'C:\x\New.cs' })).Err -match 'mod-implementer')

    # 5. idempotency: identical retry passes; a DIFFERENT file gates again
    Check 'retry of same .cs edit passes (marker)' ((Invoke-Gate (& $mkMain 'Edit' @{ file_path = 'C:\x\Foo.cs' })).Code -eq 0)
    Check 'a different .cs file gates again'        ((Invoke-Gate (& $mkMain 'Edit' @{ file_path = 'C:\x\Bar.cs' })).Code -eq 2)

    # 6. a NEW session gates again (marker is per-session)
    $sid = 'sess-' + [guid]::NewGuid().ToString('N')
    Check 'new session gates the .cs edit again' ((Invoke-Gate (& $mkMain 'Edit' @{ file_path = 'C:\x\Foo.cs' })).Code -eq 2)

    # 7. fail-open on empty / malformed stdin
    Check 'empty stdin fails open (exit 0)'      ((Invoke-Gate '').Code -eq 0)
    Check 'malformed stdin does not block (non-2)' ((Invoke-Gate 'not json {{{').Code -ne 2)

    Remove-Item -LiteralPath $tmp -Recurse -Force -ErrorAction SilentlyContinue
    if ($script:tfails -eq 0) { Write-Host "SELFTEST PASS"; exit 0 }
    Write-Host "SELFTEST FAILURES: $($script:tfails)"; exit 1
}

# ---------------------------------------------------------------------------------------------
# PreToolUse hook mode
# ---------------------------------------------------------------------------------------------
try {
    $raw = [Console]::In.ReadToEnd()
    if ([string]::IsNullOrWhiteSpace($raw)) { exit 0 }
    $ev = $raw | ConvertFrom-Json

    # Delegated work (any subagent) is always allowed — agent_id is present only inside a subagent.
    $agentId = ''
    if ($ev.PSObject.Properties.Name -contains 'agent_id' -and $ev.agent_id) { $agentId = [string]$ev.agent_id }
    if ($agentId -ne '') { exit 0 }

    $act = Test-GatedAction -ev $ev
    if (-not $act.Gate) { exit 0 }

    $sessionId = 'nosession'
    if ($ev.PSObject.Properties.Name -contains 'session_id' -and $ev.session_id) { $sessionId = [string]$ev.session_id }
    $safeSid = ($sessionId -replace '[^A-Za-z0-9_.-]', '_')

    $sha = [System.Security.Cryptography.SHA1]::Create()
    $hash = [System.BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($act.Key))).Replace('-', '').Substring(0, 16)
    $markerDir = Join-Path (Join-Path $env:TEMP 'claude-delegation-gate') $safeSid
    $marker = Join-Path $markerDir "$hash.ack"
    if (Test-Path -LiteralPath $marker) { exit 0 }
    if (-not (Test-Path -LiteralPath $markerDir)) { New-Item -ItemType Directory -Path $markerDir -Force | Out-Null }
    # Write the marker BEFORE blocking, so the immediate retry proceeds even if printing throws.
    Set-Content -LiteralPath $marker -Value (Get-Date -Format o) -Encoding utf8

    Write-Gate -What $act.What -Kind $act.Kind
    exit 2
}
catch {
    [Console]::Error.WriteLine("delegation-gate internal error (failing open): $($_.Exception.Message)")
    exit 1
}
