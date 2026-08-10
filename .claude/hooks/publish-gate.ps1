<#
.SYNOPSIS
  Publish gate. Inspects a mod for unfinished/untested markers at the moment a Nexus upload is
  about to run, and refuses the upload. Applies to EVERY mod, not one.

.DESCRIPTION
  The defect this exists for (2026-08-10, GroundItemVacuumMod 1.9.1): an unfinished corpse-sweep
  feature was published to a live public Nexus page behind a config toggle. It had never been run
  in-game — the project held no confirmation it worked and no record that it failed. Its config
  description, which lands verbatim in every downloader's .cfg file, literally began
  "UNFINISHED FEATURE - off by default".

  docs/nexus-upload.md already carried a pre-upload check naming that exact setting. The check was
  READ during the session and the upload went ahead anyway, because a default of false felt like
  enough. Prose executes only when the model both remembers it and weighs it correctly; this is the
  process-hygiene prime rule (an instruction is a hope, a mechanism is a guarantee) applied to
  publishing. The user's verdict: "that was a fourteen-out-of-ten problem, so we need to ensure it
  cannot happen."

  Two checks, deliberately different in strength because the evidence differs in quality.

  CHECK A - user-visible config text. HARD BLOCK, no override.
    Scans every Config.Bind(...) call in <Mod>/**/*.cs for UNFINISHED / UNTESTED / EXPERIMENTAL /
    BROKEN / DO NOT SHIP. Those strings are written into the player's config file, so a hit means
    the build is literally telling downloaders it ships something unfinished. There is no way to
    wave this through: finish it, delete it, or reword it. Verified against history - this check
    fires on GroundItemVacuumMod at commit 68ed1ea (Plugin.cs:112) and is silent on every mod in
    the repo at 112046c.
    Scope is Config.Bind text ONLY, never comments. Code comments legitimately say "untested"
    about interop details (4 such comments exist across the repo today) and are nobody's business
    but ours.

  CHECK B - the mod's own documentation. Blocks once, repeat proceeds.
    Scans docs/mods/<mod>.md for pending/unverified/do-not-publish markers and prints them. These
    need judgment, not prohibition: 8 mod docs match this exact marker list today (craft-from-
    storage, den-respawn, fish-fillet, ground-item-vacuum, outhouse-composter, supply-chain,
    torch-fuel, tree-respawn) and most of those markers are irrelevant to shipping. The block
    exists to put them in front of the user's eyes, which is the step that was skipped.

  What it CANNOT do: verify the user was actually asked. No hook can read intent. It guarantees
  the evidence is surfaced and the upload stops; putting it to the user remains judgment
  (process-hygiene: a judgment call is prompted, not guaranteed). Check A is the part that is a
  real guarantee, because the only way past it is to change the shipped artifact.

  Fails OPEN on any error - never wedge the session.

.NOTES
  Modes:
    (default)     PreToolUse hook - reads hook JSON on stdin. Exit 2 = refuse.
    -Scan <Mod>   Audit one mod on demand. Exit 1 if Check A hits, 0 otherwise.
    -SelfTest     Built-in test suite (synthesized events + fixtures). Exit 1 on any failure.
#>
[CmdletBinding()]
param(
    [switch]$SelfTest,
    [string]$Scan,
    # Explicit repo root. Overrides $env:CLAUDE_PROJECT_DIR. The self-test passes fixtures this
    # way on purpose: relying on env inheritance made the suite pass under one shell and fail
    # under another, which is a test that lies rather than a gate that works.
    [string]$ProjectRoot
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Markers that must never reach a downloader's config file. Word-boundary matched, case-insensitive.
# Deliberately NOT here: 'PLACEHOLDER'. It read as a hard block on CraftFromStorageMod/Plugin.cs:208,
# where the text describes the GAME's prefab placeholder value, not the mod's own state. A false
# positive on an unwaivable block is worse than a missed marker, so this list stays narrow: each
# entry must be a word an author would only write about their OWN feature. The self-test carries
# that exact sentence as a regression lock.
$script:ConfigMarkers = @(
    'UNFINISHED', 'UNTESTED', 'EXPERIMENTAL', 'BROKEN',
    'DO NOT SHIP', "DON'T SHIP", 'NOT FOR RELEASE'
)
# Markers in a mod's doc that mean "this has not been proven". Surfaced, not prohibited.
$script:DocMarkers = @(
    'not yet run in-game', 'never been run in-game', 'do NOT publish',
    'UNVERIFIED', 'pending in-game', 'not confirmed in-game'
)

function Get-ConfigBindHits {
    <#  Returns @( @{ File; Line; Text } ) for Config.Bind(...) calls whose text carries a marker.
        Walks each .cs file, and on seeing Config.Bind( accumulates until parens balance, so a
        multi-line Bind call (the house style in this repo) is treated as one unit. #>
    param([string]$ModDir)
    $hits = @()
    if (-not (Test-Path -LiteralPath $ModDir)) { return $hits }

    $files = Get-ChildItem -LiteralPath $ModDir -Recurse -Filter *.cs -File -ErrorAction SilentlyContinue |
             Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' }

    foreach ($f in $files) {
        $lines = Get-Content -LiteralPath $f.FullName -Encoding UTF8 -ErrorAction SilentlyContinue
        if (-not $lines) { continue }
        for ($i = 0; $i -lt $lines.Count; $i++) {
            if ($lines[$i] -notmatch 'Config\.Bind\s*\(') { continue }
            # Accumulate the whole call: from this line until parens balance (cap at 30 lines).
            $depth = 0; $buf = @(); $startLine = $i + 1
            for ($j = $i; $j -lt [Math]::Min($lines.Count, $i + 30); $j++) {
                $buf += $lines[$j]
                $depth += ([regex]::Matches($lines[$j], '\(')).Count
                $depth -= ([regex]::Matches($lines[$j], '\)')).Count
                if ($depth -le 0 -and $j -gt $i) { break }
                if ($depth -le 0 -and $lines[$j] -match '\)') { break }
            }
            $call = ($buf -join ' ')
            foreach ($m in $script:ConfigMarkers) {
                $rx = '(?i)(^|[^A-Za-z])' + [regex]::Escape($m) + '($|[^A-Za-z])'
                if ($call -match $rx) {
                    $hits += @{ File = $f.FullName; Line = $startLine; Marker = $m; Text = $call.Trim() }
                    break
                }
            }
        }
    }
    return $hits
}

function Get-DocHits {
    param([string]$DocPath)
    $hits = @()
    if (-not (Test-Path -LiteralPath $DocPath)) { return $hits }
    $lines = Get-Content -LiteralPath $DocPath -Encoding UTF8 -ErrorAction SilentlyContinue
    if (-not $lines) { return $hits }
    for ($i = 0; $i -lt $lines.Count; $i++) {
        foreach ($m in $script:DocMarkers) {
            if ($lines[$i] -match ('(?i)' + [regex]::Escape($m))) {
                $hits += @{ Line = $i + 1; Marker = $m; Text = $lines[$i].Trim() }
                break
            }
        }
    }
    return $hits
}

function Resolve-Root {
    # One place, so hook mode and -Scan can never disagree about which repo they are looking at.
    # PUBLISH_GATE_ROOT wins: it is the self-test's channel for pointing a child process at a
    # fixture. It must NOT be an argument - passing extra params to `powershell -File` alongside
    # -RedirectStandardInput breaks the child's stdin read ("Invalid JSON primitive"), which
    # silently turned every block test into a fail-open pass. It must NOT be CLAUDE_PROJECT_DIR
    # either: that is ambient under the Bash tool and pointed the child at the real repo, which is
    # what made this suite pass under one shell and fail under another.
    if ($env:PUBLISH_GATE_ROOT) { return $env:PUBLISH_GATE_ROOT }
    if ($ProjectRoot) { return $ProjectRoot }
    if ($env:CLAUDE_PROJECT_DIR) { return $env:CLAUDE_PROJECT_DIR }
    return (Get-Location).Path
}

function Resolve-ModDocPath {
    # GroundItemVacuumMod -> docs/mods/ground-item-vacuum.md (kebab of the name minus the Mod suffix).
    param([string]$Root, [string]$ModName)
    $stem = $ModName -replace 'Mod$', ''
    $kebab = ($stem -creplace '(?<!^)([A-Z])', '-$1').ToLowerInvariant()
    $p = Join-Path $Root "docs/mods/$kebab.md"
    if (Test-Path -LiteralPath $p) { return $p }
    # Fall back to any mod doc whose name is a case-insensitive squash of the stem.
    $squash = $stem.ToLowerInvariant()
    $cand = Get-ChildItem -LiteralPath (Join-Path $Root 'docs/mods') -Filter *.md -File -ErrorAction SilentlyContinue |
            Where-Object { ($_.BaseName -replace '-', '') -eq $squash }
    if ($cand) { return $cand[0].FullName }
    return $null
}

# ---------------------------------------------------------------------------------------------
# -Scan: audit one mod on demand.
# ---------------------------------------------------------------------------------------------
if ($Scan) {
    $root = Resolve-Root
    $modDir = Join-Path $root $Scan
    # @() wrapping is load-bearing: PowerShell unrolls an empty array to $null, and .Count on
    # $null throws under StrictMode - which would make -Scan crash on exactly the clean mods it
    # is supposed to wave through.
    $a = @(Get-ConfigBindHits -ModDir $modDir)
    $docPath = Resolve-ModDocPath -Root $root -ModName $Scan
    $b = @(if ($docPath) { Get-DocHits -DocPath $docPath } else { @() })

    if ($a.Count -eq 0) { Write-Host "CHECK A (shipped config text): clean." }
    else {
        Write-Host "CHECK A (shipped config text): $($a.Count) BLOCKING hit(s)."
        foreach ($h in $a) { Write-Host ("  {0}:{1}  [{2}]" -f $h.File, $h.Line, $h.Marker) }
    }
    if ($b.Count -eq 0) { Write-Host "CHECK B (mod doc): clean." }
    else {
        Write-Host "CHECK B (mod doc, needs judgment): $($b.Count) hit(s) in $docPath"
        foreach ($h in $b) { Write-Host ("  line {0}  [{1}]  {2}" -f $h.Line, $h.Marker, $h.Text) }
    }
    if ($a.Count -gt 0) { exit 1 } else { exit 0 }
}

# ---------------------------------------------------------------------------------------------
# Self-test: fixtures + synthesized events, positive AND negative, idempotency, fail-open.
# ---------------------------------------------------------------------------------------------
if ($SelfTest) {
    $me = $PSCommandPath
    $tmp = Join-Path $env:TEMP ("publish-gate-selftest-" + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $tmp -Force | Out-Null

    function Invoke-Gate {
        param([string]$Json, [string]$ProjDir)
        $err = Join-Path $tmp ('err-' + [guid]::NewGuid().ToString('N') + '.txt')
        $inp = Join-Path $tmp ('in-'  + [guid]::NewGuid().ToString('N') + '.json')
        Set-Content -LiteralPath $inp -Value $Json -Encoding utf8
        # Point the child at the fixture through PUBLISH_GATE_ROOT, not an argument and not
        # CLAUDE_PROJECT_DIR. See Resolve-Root for why both of those were wrong.
        $old = $env:PUBLISH_GATE_ROOT
        $env:PUBLISH_GATE_ROOT = $ProjDir
        try {
            $p = Start-Process -FilePath 'powershell.exe' `
                    -ArgumentList @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', ('"' + $me + '"')) `
                    -RedirectStandardInput $inp -RedirectStandardError $err -NoNewWindow -Wait -PassThru
        } finally { $env:PUBLISH_GATE_ROOT = $old }
        $stderr = ''
        if (Test-Path $err) { $stderr = Get-Content -LiteralPath $err -Raw -Encoding UTF8 }
        return @{ Code = $p.ExitCode; Err = "$stderr" }
    }
    $script:tfails = 0
    function Check { param([string]$Name, [bool]$Ok)
        if ($Ok) { Write-Host "  ok   $Name" } else { Write-Host "  FAIL $Name"; $script:tfails++ } }

    # --- Fixture repo: one dirty mod, one clean mod. ---
    $proj = Join-Path $tmp 'repo'
    New-Item -ItemType Directory -Path (Join-Path $proj 'DirtyMod') -Force | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $proj 'CleanMod') -Force | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $proj 'docs/mods') -Force | Out-Null

    # Reproduces the real 1.9.1 defect: marker inside a multi-line Config.Bind description.
    Set-Content -LiteralPath (Join-Path $proj 'DirtyMod/Plugin.cs') -Encoding utf8 -Value @'
public class P {
    void Load() {
        IncludeCorpses = Config.Bind(
            "Corpses", "IncludeCorpses", false,
            "UNFINISHED FEATURE - off by default. When true, a sweep also sweeps corpses.");
    }
}
'@
    # Negative cases, all real text from this repo. These are REGRESSION LOCKS - a widened marker
    # list that trips any of them fails the self-test:
    #   1. "untested"/"experimental"/"BROKEN" in CODE COMMENTS (4 such comments exist across the
    #      repo). Comments are ours, not the downloader's.
    #   2. Config text describing the GAME's placeholder value, not the mod's state
    #      (CraftFromStorageMod/Plugin.cs:208, verbatim below).
    Set-Content -LiteralPath (Join-Path $proj 'CleanMod/Plugin.cs') -Encoding utf8 -Value @'
public class P {
    // Interop Dictionary enumeration is untested territory - if it throws, log once.
    // This experimental path is a BROKEN idea we kept notes on.
    void Load() {
        Radius = Config.Bind(
            "General", "Radius", 60f,
            "Only ground items within this many meters of the player are affected.");
        UiPollSeconds = Config.Bind(
            "2. Tuning", "UiPollSeconds", 0.2f,
            "in-game evidence showed the ItemThumbnailPanel postfixes run BEFORE vanilla writes " +
            "the real have/need text (count.text is still the prefab placeholder '99' at that moment)");
    }
}
'@
    Set-Content -LiteralPath (Join-Path $proj 'docs/mods/dirty.md') -Encoding utf8 `
        -Value "# Dirty`n> Status: the filter is not yet run in-game.`n"
    Set-Content -LiteralPath (Join-Path $proj 'docs/mods/clean.md') -Encoding utf8 `
        -Value "# Clean`n> Status: COMPLETE, confirmed in-game 2026-08-01.`n"

    $sid = 'sess-' + [guid]::NewGuid().ToString('N')
    $mk = { param($cmd, $s) (@{ session_id = $s; tool_name = 'Bash'; tool_input = @{ command = $cmd } } | ConvertTo-Json -Compress) }
    $up = 'gh workflow run nexus-upload.yml -f mod=DirtyMod -f version=1.0.0 -f category=main'
    $upClean = 'gh workflow run nexus-upload.yml -f mod=CleanMod -f version=1.0.0 -f category=main'

    # 1. Check A blocks the dirty mod.
    $r = Invoke-Gate (& $mk $up $sid) $proj
    Check 'upload of a mod with UNFINISHED config text is refused (exit 2)' ($r.Code -eq 2)
    Check 'refusal names the blocking check'    ($r.Err -match 'CHECK A')
    Check 'refusal quotes the offending file'   ($r.Err -match 'Plugin\.cs')
    Check 'refusal names the marker'            ($r.Err -match 'UNFINISHED')
    Check 'refusal states there is no override' ($r.Err -match 'cannot be waved through')

    # 2. Check A is NOT waivable - an immediate repeat still blocks.
    $r2 = Invoke-Gate (& $mk $up $sid) $proj
    Check 'repeat of a Check A block STILL refuses (no marker escape)' ($r2.Code -eq 2)

    # 3. Clean mod: Check A silent, Check B silent -> passes.
    $r3 = Invoke-Gate (& $mk $upClean $sid) $proj
    Check 'upload of a clean mod passes (exit 0)' ($r3.Code -eq 0)

    # 4. Check B alone blocks once, then the repeat proceeds.
    $sidB = 'sess-' + [guid]::NewGuid().ToString('N')
    Set-Content -LiteralPath (Join-Path $proj 'docs/mods/clean.md') -Encoding utf8 `
        -Value "# Clean`n> The whole-map pass is not yet run in-game.`n"
    $b1 = Invoke-Gate (& $mk $upClean $sidB) $proj
    Check 'doc-only markers block the first attempt (exit 2)' ($b1.Code -eq 2)
    Check 'doc block names CHECK B' ($b1.Err -match 'CHECK B')
    $b2 = Invoke-Gate (& $mk $upClean $sidB) $proj
    Check 'doc-only block passes on the deliberate repeat' ($b2.Code -eq 0)
    Set-Content -LiteralPath (Join-Path $proj 'docs/mods/clean.md') -Encoding utf8 `
        -Value "# Clean`n> Status: COMPLETE, confirmed in-game 2026-08-01.`n"

    # 5. Negative: non-upload commands are ignored, however much they mention the words.
    Check 'an unrelated command passes' `
        ((Invoke-Gate (& $mk 'git status' $sid) $proj).Code -eq 0)
    Check 'a commit message mentioning an UNFINISHED nexus upload passes' `
        ((Invoke-Gate (& $mk 'git commit -m "docs: note the nexus upload of an UNFINISHED thing"' $sid) $proj).Code -eq 0)

    # 6. Fail-open: an upload naming a mod that does not exist must not wedge the session.
    Check 'upload naming an unknown mod fails open (exit 0)' `
        ((Invoke-Gate (& $mk 'gh workflow run nexus-upload.yml -f mod=NoSuchMod -f version=1.0.0' $sid) $proj).Code -eq 0)

    # 7. Malformed stdin fails open.
    Check 'malformed event fails open (exit 0)' ((Invoke-Gate 'not json at all' $proj).Code -eq 0)

    # 8. -Scan mode agrees with the hook. -ProjectRoot is safe here: no stdin is redirected.
    $null = & powershell -NoProfile -ExecutionPolicy Bypass -File $me -ProjectRoot $proj -Scan 'DirtyMod' 2>&1
    Check '-Scan exits 1 on a dirty mod' ($LASTEXITCODE -eq 1)
    $null = & powershell -NoProfile -ExecutionPolicy Bypass -File $me -ProjectRoot $proj -Scan 'CleanMod' 2>&1
    Check '-Scan exits 0 on a clean mod' ($LASTEXITCODE -eq 0)

    # 9. Shell independence. An ambient CLAUDE_PROJECT_DIR must not drag the child back to the
    #    real repo - that is the bug that made this suite pass under the PowerShell tool and fail
    #    under the Bash tool, reporting fail-opens as passes.
    $stale = $env:CLAUDE_PROJECT_DIR
    $env:CLAUDE_PROJECT_DIR = 'C:\definitely\not\the\fixture'
    try {
        Check 'fixture root beats an ambient CLAUDE_PROJECT_DIR' `
            ((Invoke-Gate (& $mk $up $sid) $proj).Code -eq 2)
    } finally { $env:CLAUDE_PROJECT_DIR = $stale }

    Remove-Item -LiteralPath $tmp -Recurse -Force -ErrorAction SilentlyContinue
    if ($script:tfails -gt 0) { Write-Host "SELF-TEST FAILED ($script:tfails)"; exit 1 }
    Write-Host 'PASS'
    exit 0
}

# ---------------------------------------------------------------------------------------------
# Hook mode.
# ---------------------------------------------------------------------------------------------
try {
    $raw = [Console]::In.ReadToEnd()
    if (-not $raw) { exit 0 }
    # Discard anything before the first '{'. A UTF-8 BOM on the input makes ConvertFrom-Json throw
    # "Invalid JSON primitive", the catch fails the gate OPEN, and the block never happens - a gate
    # that has silently stopped gating. Trimming the BOM CODEPOINT is not enough: when the child's
    # stdin decoder is not UTF-8 the three BOM bytes arrive as three separate characters, so
    # [char]0xFEFF never matches (measured: a 7-character payload read as length 12). Cutting to the
    # first brace is decoding-independent. Found because the same self-test passed under pwsh and
    # failed under Windows PowerShell 5.1, scoring every fail-open as a pass.
    $brace = $raw.IndexOf('{')
    if ($brace -lt 0) { exit 0 }
    if ($brace -gt 0) { $raw = $raw.Substring($brace) }
    $ev = $raw | ConvertFrom-Json

    $tool = ''
    if ($ev.PSObject.Properties.Name -contains 'tool_name') { $tool = [string]$ev.tool_name }
    if ($tool -notin @('Bash', 'PowerShell')) { exit 0 }

    $cmd = ''
    if ($ev.PSObject.Properties.Name -contains 'tool_input' -and $ev.tool_input) {
        if ($ev.tool_input.PSObject.Properties.Name -contains 'command') { $cmd = [string]$ev.tool_input.command }
    }
    if (-not $cmd) { exit 0 }

    # Only a real upload invocation. Requires the workflow-run verb AND the workflow name, so prose
    # that merely mentions "nexus-upload" (a commit message, a doc edit) never trips it.
    if ($cmd -notmatch '(?i)workflow\s+run\b') { exit 0 }
    if ($cmd -notmatch '(?i)nexus-upload') { exit 0 }

    $mod = ''
    if ($cmd -match '(?i)-f\s+mod=([A-Za-z0-9_.]+)') { $mod = $Matches[1] }
    if (-not $mod) { exit 0 }

    $root = Resolve-Root
    $modDir = Join-Path $root $mod
    if (-not (Test-Path -LiteralPath $modDir)) { exit 0 }   # unknown mod -> fail open

    $aHits = @(Get-ConfigBindHits -ModDir $modDir)
    $docPath = Resolve-ModDocPath -Root $root -ModName $mod
    $bHits = @()
    if ($docPath) { $bHits = @(Get-DocHits -DocPath $docPath) }

    if ($aHits.Count -eq 0 -and $bHits.Count -eq 0) { exit 0 }

    [Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
    $e = { param($s) [Console]::Error.WriteLine($s) }

    if ($aHits.Count -gt 0) {
        # HARD BLOCK. No marker is written, so a repeat blocks again - by design.
        & $e "PUBLISH GATE - CHECK A FAILED, UPLOAD BLOCKED."
        & $e "$mod ships config text that tells downloaders it is unfinished."
        & $e ""
        & $e "This text is written verbatim into every downloader's .cfg file:"
        foreach ($h in $aHits) {
            & $e ("  {0}:{1}  [{2}]" -f $h.File, $h.Line, $h.Marker)
            $snip = $h.Text; if ($snip.Length -gt 200) { $snip = $snip.Substring(0, 200) + '...' }
            & $e ("     $snip")
        }
        & $e ""
        & $e "This cannot be waved through by repeating the call. The only ways past it:"
        & $e "  - finish the feature and verify it in-game, then reword the text; or"
        & $e "  - delete the feature from the build; or"
        & $e "  - reword the text if the marker is wrong."
        & $e ""
        & $e "Whichever you pick, PUT IT TO THE USER FIRST and get an explicit go-ahead. On"
        & $e "2026-08-10 an unfinished feature reached a public Nexus page because a pre-upload"
        & $e "check was read and walked past. A default of 'off' is NOT a reason to ship it."
        exit 2
    }

    # CHECK B only: surface and block once; the deliberate retry proceeds.
    $sessionId = 'nosess'
    if ($ev.PSObject.Properties.Name -contains 'session_id' -and $ev.session_id) { $sessionId = [string]$ev.session_id }
    $safeSid = ($sessionId -replace '[^A-Za-z0-9_.-]', '_')
    $keyHash = [BitConverter]::ToString(
        [System.Security.Cryptography.SHA1]::Create().ComputeHash(
            [Text.Encoding]::UTF8.GetBytes("$mod|$cmd"))).Replace('-', '').Substring(0, 16)
    $markerDir = Join-Path (Join-Path $env:TEMP 'claude-publish-gate') $safeSid
    $marker = Join-Path $markerDir "$keyHash.ack"
    if (Test-Path -LiteralPath $marker) { exit 0 }
    if (-not (Test-Path -LiteralPath $markerDir)) { New-Item -ItemType Directory -Path $markerDir -Force | Out-Null }
    Set-Content -LiteralPath $marker -Value (Get-Date -Format o) -Encoding utf8

    & $e "PUBLISH GATE - CHECK B. $mod's own documentation says parts of it are not proven."
    & $e ""
    foreach ($h in $bHits) {
        $snip = $h.Text; if ($snip.Length -gt 160) { $snip = $snip.Substring(0, 160) + '...' }
        & $e ("  {0}:{1}  [{2}]" -f $docPath, $h.Line, $h.Marker)
        & $e ("     $snip")
    }
    & $e ""
    & $e "Repeat the call to proceed. First, answer this to the USER, not to yourself:"
    & $e ""
    & $e "    Which of these ships in this upload, and has he agreed to each one?"
    & $e ""
    & $e "  Most pending markers are irrelevant to shipping - that is why this is not a hard block."
    & $e "  But 'it defaults to off' is NOT an answer. On 2026-08-10 an unfinished feature reached a"
    & $e "  public Nexus page behind a false default, and the user rated it a 14-out-of-10 failure."
    exit 2
}
catch {
    # Fail open - never wedge the session. Set PUBLISH_GATE_DEBUG=1 to see why it bailed;
    # a silent fail-open is how a gate quietly stops gating (process-hygiene: test the
    # failure path, and make it observable).
    if ($env:PUBLISH_GATE_DEBUG) { [Console]::Error.WriteLine("PUBLISH GATE debug: $_") }
    exit 0
}
