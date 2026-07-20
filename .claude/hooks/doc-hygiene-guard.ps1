<#
.SYNOPSIS
  askamods doc-hygiene guard. Enforces CLAUDE.md's "Doc update style - stateless, de-accreted"
  rules MECHANICALLY, at the moment a documentation edit is attempted.

.DESCRIPTION
  Two layers, both blocking (exit 2 = refuse the tool call; stderr is fed back to the model):

    1. SESSION GATE  - the first .md write/edit of a session is refused, with the doc-style rules
                       printed inline. The rules are extracted LIVE from CLAUDE.md, so there is no
                       second copy to drift. A per-session marker then lets later edits through.
    2. CONTENT SCAN  - every .md write/edit is scanned for "journey language": text that narrates
                       how the document changed (corrections, retractions, former beliefs) instead
                       of simply stating current truth. This is the defect the rules exist to stop,
                       and it is machine-checkable.

  Why blocking at PreToolUse: it sees the PROPOSED content before anything reaches disk, and its
  stderr lands in a tool result - the one channel that reliably reaches the model (SessionStart
  hook stdout is discarded by the harness, upstream bug #79299).

.NOTES
  Modes:
    (default)     PreToolUse hook - reads the hook JSON event on stdin.
    -ScanFile P   Scan a file's contents. Exit 1 if journey language found. For testing/auditing.
    -ScanStdin    Scan text piped on stdin (used by .githooks/pre-commit on the staged diff).
    -SelfTest     Run the built-in positive/negative test suite. Exit 1 on any failure.
#>
[CmdletBinding()]
param(
    [string]$ScanFile,
    [switch]$ScanStdin,
    [switch]$SelfTest,
    [switch]$ShowBlocks,
    [int]$SinceEpoch = 0
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# ---------------------------------------------------------------------------------------------
# Journey-language patterns. Each is a defect SIGNATURE: prose that describes the document's own
# history rather than the subject's current state. Extend this list when a new shape gets through -
# that is the intended maintenance path (a pattern added here can never be forgotten, unlike a
# note asking a future reader to watch out for it).
# ---------------------------------------------------------------------------------------------
$script:Patterns = @(
    @{ Rx = 'previously\s+(thought|believed|assumed|said|claimed|stated|recorded)';
       Why = 'states a former belief - delete it and state only what is true now' }
    @{ Rx = '(the\s+)?(old|earlier|previous|original|former)\s+(claim|text|note|wording|framing|understanding|statement|scope)';
       Why = 'points at superseded text instead of replacing it' }
    @{ Rx = 'retracted';
       Why = 'marks a retraction - rewrite the fact in place instead' }
    # NOTE (scratch-tested against the whole repo 2026-07-20): a bare "was/is false|wrong" is
    # UNUSABLE - "every read was False", "the filter was false-positive", "ApplyToAllBuildings is
    # false", "IsExhausted() alone is wrong for..." are all legitimate technical prose. The defect
    # is only ever an error attributed to the DOCUMENT'S OWN prior content, so both directions
    # require a doc-referential noun within the same sentence.
    @{ Rx = '(premise|claim|scope|note|text|statement|wording|framing|understanding|assumption|belief|guidance)[^.\n]{0,60}(was|were)\s+(wrong|false|incorrect)';
       Why = 'narrates a past error in the doc rather than stating the correct fact' }
    @{ Rx = '(was|were)\s+(wrong|false|incorrect)[^.\n]{0,60}(premise|claim|scope|note|text|statement|wording|framing|understanding|assumption)';
       Why = 'narrates a past error in the doc rather than stating the correct fact' }
    @{ Rx = 'used\s+to\s+(be|say|claim|think|state)';
       Why = 'narrates a past state' }
    @{ Rx = 'no\s+longer\s+(true|accurate|correct|applies)';
       Why = 'defines a fact by what it is not - state what IS' }
    # "Mod 10 SUPERSEDED by SeedScout" is a legitimate CURRENT STATUS. The defect is only a
    # pointer to other TEXT ("superseded by the block below"), so anchor on doc-structure nouns.
    @{ Rx = 'superseded\s+by\s+((the|this)\s+)?(block|section|note|text|entry|paragraph)\b';
       Why = 'dangling supersede chain - update or delete the stale text in the same edit' }
    @{ Rx = 'as\s+originally\s+(researched|written|planned|designed|scoped|noted)';
       Why = 'preserves the original version as a baseline the reader must reconcile' }
    # "attempt"/"pass" are deliberately NOT in the second alternation: "the first attempt patched
    # X and it failed" is legitimate DEAD-END knowledge, which this repo keeps forever by design.
    # Only narration about editing the document itself is a defect.
    @{ Rx = '(correction\s+attempt|(my|the)\s+(first|earlier|previous)\s+(correction|rewrite|edit|revision))';
       Why = 'narrates the authoring process' }
    @{ Rx = 'results?\s+rewrite\s+the\s+(approach|plan|section|block)';
       Why = 'asks the reader to reconcile two versions of the same content' }
    @{ Rx = 'amend\s+per\s+the';
       Why = 'defers reconciliation to the reader instead of doing it' }
    @{ Rx = '(REAL|CONFIRMED|real),\s+not\s+(theoretical|hypothetical)';
       Why = 'contrasts with a prior belief - just state the confirmed fact' }
    # Case-SENSITIVE: the all-caps emphasis is the narration signal. Ordinary lowercase
    # "changed by the game" / "rewritten by the caller" is legitimate technical prose.
    @{ Rx = '\b(CHANGED|REWRITTEN|CORRECTED)\s+by\s+the\b'; Cs = $true;
       Why = 'narrates what changed rather than stating current truth' }
    @{ Rx = 'this\s+(section|block|note|file|entry)\s+(was|has\s+been)\s+(rewritten|corrected|updated)\s+(because|after|when)';
       Why = 'narrates the edit itself' }
    # --- added by the 2026-07-20 audit: 8 novel phrasings, 0 of which the list above caught ---
    @{ Rx = '\bCorrection:';
       Why = 'correction prefix - rewrite the fact in place, no correction framing' }
    # Case-SENSITIVE: the all-caps accretion prefix, not the word "update" in prose.
    @{ Rx = '\bUPDATE(\s+\d{4}-\d{2}-\d{2})?:'; Cs = $true;
       Why = 'UPDATE-prefix accretion - fold the new fact into the text, dated if in-game' }
    # "if it turns out..." is legitimate forward-looking prose; only retrospective narration trips.
    @{ Rx = '(?<!if\s)\bit\s+turns\s+out\b';
       Why = 'narrates a discovery against a prior belief - state the fact directly' }
    @{ Rx = '\bas\s+(previously\s+)?assumed\b';
       Why = 'contrasts with a prior assumption - state the fact directly' }
    # Requires a doc-structure noun: "replaces the old dual-write ritual" / "replaces the original
    # guard" are CURRENT-state labels (accepted style, like "SUPERSEDED by SeedScout"); only
    # pointing at replaced TEXT is the defect.
    @{ Rx = 'replaces\s+the\s+(earlier|old|previous|original)\s+(section|block|note|text|entry|paragraph|claim|wording|statement)';
       Why = 'points at replaced text instead of deleting it' }
    @{ Rx = 'no\s+longer\s+(the\s+case|holds|valid|needed\s+because)';
       Why = 'defines a fact by what it is not - state what IS' }
    @{ Rx = '\bformerly\s+(believed|thought|assumed|said|claimed|stated|recommended)';
       Why = 'states a former belief - delete it and state only what is true now' }
    @{ Rx = '\b(we|it\s+is)\s+now\s+know(n)?\b|\bnow\s+known\s+to\s+be\b';
       Why = 'contrasts current knowledge with a prior state - just state the fact' }
    @{ Rx = 'contrary\s+to\s+what\s+(this|the)\s+(doc|file|note|section|plan)';
       Why = 'argues with the document instead of correcting it' }
)

function Find-JourneyLanguage {
    # NOTE: callers MUST wrap in @() - a single hit would otherwise unroll to a scalar and
    # .Count would throw under Set-StrictMode (caught in scratch testing).
    param([string]$Text)
    $hits = @()
    if ([string]::IsNullOrEmpty($Text)) { return $hits }
    $lines = @($Text -split "`r?`n")
    for ($i = 0; $i -lt $lines.Count; $i++) {
        foreach ($p in $script:Patterns) {
            $opts = 'IgnoreCase'
            if ($p.ContainsKey('Cs') -and $p.Cs) { $opts = 'None' }
            $m = [regex]::Match($lines[$i], $p.Rx, $opts)
            if ($m.Success) {
                $hits += [pscustomobject]@{
                    Line   = $i + 1
                    Match  = $m.Value
                    Why    = $p.Why
                    Text   = $lines[$i].Trim()
                }
            }
        }
    }
    return $hits
}

function Write-Hits {
    param($Hits, [string]$Where)
    [Console]::Error.WriteLine("DOC HYGIENE BLOCK: journey language in $Where")
    [Console]::Error.WriteLine("")
    foreach ($h in $Hits) {
        $t = $h.Text
        if ($t.Length -gt 120) { $t = $t.Substring(0, 120) + '...' }
        [Console]::Error.WriteLine(("  line {0}: `"{1}`"" -f $h.Line, $h.Match))
        [Console]::Error.WriteLine(("     why: {0}" -f $h.Why))
        [Console]::Error.WriteLine(("     in : {0}" -f $t))
        [Console]::Error.WriteLine("")
    }
    [Console]::Error.WriteLine("Docs record CURRENT TRUTH, not the journey. Rewrite the fact in place:")
    [Console]::Error.WriteLine("delete the stale statement rather than annotating it as stale. A reader")
    [Console]::Error.WriteLine("must never have to replay a fact->correction chain to learn what is true.")
    [Console]::Error.WriteLine("(Dated in-game confirmations and dead-end findings are NOT journey language -")
    [Console]::Error.WriteLine(" they are durable facts. The defect is narrating how the DOC changed.)")
}

# ---------------------------------------------------------------------------------------------
# Test / audit modes
# ---------------------------------------------------------------------------------------------
if ($SelfTest) {
    $fails = 0
    # MUST trip (the exact shapes that poisoned the docs on 2026-07-20)
    $positives = @(
        'The retracted text claimed villagers already work this way.',
        '**Both halves of that were wrong: the premise AND the resulting scope.**',
        'Blacklist design CHANGED by the census: go structural.',
        'Approach (phased) - as originally researched; amend per the Phase 0 results above:',
        'the villager-leak risk is REAL, not theoretical.',
        'Results rewrite the approach below - read this block first:',
        'My first correction attempt fixed only the premise.',
        'We previously believed the gate was inlined.',
        'This is superseded by the block below.',
        'The old claim was that CrafterFetchQuest hauls settlement-wide.',
        'That API used to be the recommended one.',
        'That guidance is no longer true.',
        # The 8 phrasings the 2026-07-20 audit proved were missed (0/8 caught pre-audit):
        'Correction: the gate is actually the outer one.',
        'UPDATE 2026-07-21: villagers do not fetch settlement-wide.',
        'It turns out the consumption site is X, not Y as assumed.',
        'This replaces the earlier section.',
        'That approach is no longer the case.',
        'We formerly believed the fetch was settlement-wide.',
        'We now know the fetch is station-local.',
        'Contrary to what this doc said, the gate fires.'
    )
    # MUST NOT trip (legitimate durable doc content from this repo)
    $negatives = @(
        '- **`CheckOwnedRequirements` is NOT inlined - the patch fires.** (confirmed in-game 2026-07-20)',
        '**DEAD-END (confirmed in-game 2026-07-20): `QuerySettlementResources()` HANGS the game.**',
        'Container acceptance is NOT storage-class-based alone. Pointer-verified in-game.',
        '⚠️ Mechanism unconfirmed - candidates are `GetFetchDepth` / `GetPersonalFetchDepth`.',
        'Do NOT patch IL2CPP methods with by-ref primitive params - trampoline NREs.',
        'Dead-end guard: the presence of `CrafterFetchQuest` is NOT evidence of settlement-wide reach.',
        'v1.2.1 design: HashSet<Monster> (Spawned postfix add / Despawned prefix remove).',
        'The original design brief called for a hotkey; it ships with one.',
        'Phase 0 - read-only diagnostic spike: COMPLETE, results above.',
        # Guards for the audit-added patterns (forward-looking / legitimate uses):
        'if it turns out the walk is too slow, cache per structure.',
        'The mod updates the config on reload; an update to the live cfg needs Cfg.Reload().',
        'This mod was formerly known as WarpTour.',
        'The refill replaces the well water level with the maximum.',
        '**Doc cadence (replaces the old per-commit dual-write ritual):**',
        '*Scope contract (replaces the original "never create tasks" guard):*',
        # Real false positives caught by scanning the whole repo 2026-07-20. Locked in here so a
        # future pattern edit cannot silently reintroduce them.
        'is **unconfirmed** - in the only test world so far every read was False and the dens were',
        '  revives). The broader filter was false-positive. v1.1.7 selection change (narrowed gate)',
        '- BuildingNameList - only used when ApplyToAllBuildings is false',
        '`IsExhausted() || GetQuantity() <= 0` (see dead-ends - `IsExhausted()` alone is wrong for',
        'WHAT is wrong (e.g. "full, but full of bones").',
        'WarpTourMod/ <- Mod 10: teleport-tour [v1.0.0 - SUPERSEDED by SeedScout; keep the primitive]',
        'Superseded by `LastDeserializeAt` as the live gate, so this is unused as of v1.0.0.',
        'or deletes that text in the same edit - no dangling "superseded by X below" chains.',
        'The networked value is changed by the game, not by the mod.',
        '**Dead-end (do NOT retry):** the first attempt patched `_OnWorldInstanceDataChanged`'
    )
    foreach ($s in $positives) {
        if (@(Find-JourneyLanguage -Text $s).Count -eq 0) {
            Write-Host "SELFTEST FAIL (missed): $s"; $fails++
        }
    }
    foreach ($s in $negatives) {
        $h = @(Find-JourneyLanguage -Text $s)
        if ($h.Count -ne 0) {
            Write-Host "SELFTEST FAIL (false positive): $s  --> matched '$($h[0].Match)'"; $fails++
        }
    }
    if ($fails -eq 0) {
        Write-Host "SELFTEST PASS: $($positives.Count) positives tripped, $($negatives.Count) negatives clean."
        exit 0
    }
    Write-Host "SELFTEST FAILURES: $fails"
    exit 1
}

if ($ShowBlocks) {
    # Review mode. -SinceEpoch <t> limits to blocks after t (pre-commit passes the last commit
    # time, so a commit shows only the blocks that happened during the work being committed).
    $projectDir = $env:CLAUDE_PROJECT_DIR
    if ([string]::IsNullOrWhiteSpace($projectDir)) { $projectDir = (Get-Location).Path }
    $logPath = Join-Path $projectDir '.claude\doc-hygiene-blocks.log'
    if (-not (Test-Path -LiteralPath $logPath)) { exit 0 }
    $rows = @(Get-Content -LiteralPath $logPath -Encoding UTF8 | Where-Object { $_ -match '\S' } |
        ForEach-Object {
            $f = $_ -split "`t", 4
            # Strip any BOM/stray non-digits before parsing (Add-Content writes a BOM on create).
            $ep = 0
            if ($f.Count -ge 1) { [void][int]::TryParse(($f[0] -replace '[^0-9]', ''), [ref]$ep) }
            if ($f.Count -ge 4 -and $ep -ge $SinceEpoch) {
                [pscustomobject]@{ When = [DateTimeOffset]::FromUnixTimeSeconds($ep).LocalDateTime
                                   File = $f[1]; Match = $f[2]; Context = $f[3] }
            }
        })
    if ($rows.Count -eq 0) { exit 0 }
    [Console]::Error.WriteLine("")
    [Console]::Error.WriteLine("DOC HYGIENE: $($rows.Count) doc edit(s) were BLOCKED during this work.")
    [Console]::Error.WriteLine("Review: was each one really journey language, or a false positive?")
    [Console]::Error.WriteLine("A false positive means the guard is bending the writing - say so and")
    [Console]::Error.WriteLine("it gets fixed in the pattern list, not worked around.")
    [Console]::Error.WriteLine("")
    foreach ($r in $rows) {
        [Console]::Error.WriteLine(("  {0:HH:mm} {1}  [{2}]" -f $r.When, $r.File, $r.Match))
        [Console]::Error.WriteLine(("         {0}" -f $r.Context))
    }
    [Console]::Error.WriteLine("")
    exit 0
}

if ($ScanFile) {
    if (-not (Test-Path -LiteralPath $ScanFile)) { Write-Host "no such file: $ScanFile"; exit 0 }
    $hits = @(Find-JourneyLanguage -Text (Get-Content -LiteralPath $ScanFile -Raw))
    if ($hits.Count -gt 0) { Write-Hits -Hits $hits -Where $ScanFile; exit 1 }
    exit 0
}

if ($ScanStdin) {
    $text = [Console]::In.ReadToEnd()
    $hits = @(Find-JourneyLanguage -Text $text)
    if ($hits.Count -gt 0) { Write-Hits -Hits $hits -Where 'the staged changes'; exit 1 }
    exit 0
}

# ---------------------------------------------------------------------------------------------
# PreToolUse hook mode
# ---------------------------------------------------------------------------------------------
# Fail OPEN on any internal error: a broken guard must never wedge the session. The pre-commit
# backstop still catches anything that slips past here.
try {
    $raw = [Console]::In.ReadToEnd()
    if ([string]::IsNullOrWhiteSpace($raw)) { exit 0 }
    $ev = $raw | ConvertFrom-Json

    $toolName = ''
    if ($ev.PSObject.Properties.Name -contains 'tool_name') { $toolName = [string]$ev.tool_name }
    if ($toolName -notin @('Write', 'Edit', 'MultiEdit')) { exit 0 }

    $ti = $ev.tool_input
    $filePath = ''
    if ($ti -and ($ti.PSObject.Properties.Name -contains 'file_path')) { $filePath = [string]$ti.file_path }
    if ([string]::IsNullOrWhiteSpace($filePath)) { exit 0 }

    # Documentation only: markdown. Source-code comments are out of scope by design.
    if ($filePath -notmatch '\.md$') { exit 0 }

    $projectDir = $env:CLAUDE_PROJECT_DIR
    if ([string]::IsNullOrWhiteSpace($projectDir)) { $projectDir = (Get-Location).Path }
    $normFile = $filePath -replace '/', '\'
    $normProj = $projectDir -replace '/', '\'
    if (-not $normFile.StartsWith($normProj, [StringComparison]::OrdinalIgnoreCase)) { exit 0 }

    # ---- Layer 1: session gate ----
    $sessionId = 'nosession'
    if ($ev.PSObject.Properties.Name -contains 'session_id' -and $ev.session_id) {
        $sessionId = [string]$ev.session_id
    }
    $safeId = ($sessionId -replace '[^A-Za-z0-9_.-]', '_')
    $markerDir = Join-Path $env:TEMP 'askamods-dochygiene'
    $marker = Join-Path $markerDir "$safeId.ack"

    if (-not (Test-Path -LiteralPath $marker)) {
        if (-not (Test-Path -LiteralPath $markerDir)) {
            New-Item -ItemType Directory -Path $markerDir -Force | Out-Null
        }
        # Write the marker BEFORE blocking, so the immediate retry proceeds.
        Set-Content -LiteralPath $marker -Value (Get-Date -Format o) -Encoding utf8

        $rules = $null
        $claudeMd = Join-Path $projectDir 'CLAUDE.md'
        if (Test-Path -LiteralPath $claudeMd) {
            # -Encoding UTF8 is REQUIRED: powershell 5.1 otherwise reads BOM-less UTF-8 as ANSI and
            # em dashes / ⚠️ arrive as mojibake (fire-verified 2026-07-20). Console encoding must be
            # set too, or the UTF-8 text is mangled again on the way out to stderr.
            try {
                [Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
                [Console]::InputEncoding  = [System.Text.UTF8Encoding]::new($false)
            } catch { }
            $lines = Get-Content -LiteralPath $claudeMd -Encoding UTF8
            $start = -1
            for ($i = 0; $i -lt $lines.Count; $i++) {
                # Exact grep marker - CLAUDE.md documents that this hook keys on this heading.
                if ($lines[$i] -match '^### Doc update style') { $start = $i; break }
            }
            if ($start -ge 0) {
                $buf = New-Object System.Collections.Generic.List[string]
                for ($i = $start; $i -lt $lines.Count; $i++) {
                    if ($i -gt $start -and $lines[$i] -match '^#{1,3} ') { break }
                    $buf.Add($lines[$i])
                }
                $rules = ($buf -join "`n")
            }
        }

        [Console]::Error.WriteLine("DOC HYGIENE GATE - first documentation edit this session.")
        [Console]::Error.WriteLine("Read these rules, then repeat the edit (it will go through).")
        [Console]::Error.WriteLine("")
        if ($rules) {
            [Console]::Error.WriteLine($rules)
        } else {
            [Console]::Error.WriteLine("(Could not extract '### Doc update style' from CLAUDE.md - read that section directly.)")
        }
        [Console]::Error.WriteLine("")
        [Console]::Error.WriteLine("The one that gets violated most: docs record CURRENT TRUTH, not the journey.")
        [Console]::Error.WriteLine("Rewrite facts in place. Never leave a stale statement annotated as stale.")
        exit 2
    }

    # ---- Layer 2: content scan (every edit) ----
    $proposed = New-Object System.Collections.Generic.List[string]
    if ($ti.PSObject.Properties.Name -contains 'content' -and $ti.content) {
        $proposed.Add([string]$ti.content)
    }
    if ($ti.PSObject.Properties.Name -contains 'new_string' -and $ti.new_string) {
        $proposed.Add([string]$ti.new_string)
    }
    if ($ti.PSObject.Properties.Name -contains 'edits' -and $ti.edits) {
        foreach ($e in $ti.edits) {
            if ($e.PSObject.Properties.Name -contains 'new_string' -and $e.new_string) {
                $proposed.Add([string]$e.new_string)
            }
        }
    }
    if ($proposed.Count -eq 0) { exit 0 }

    $hits = @(Find-JourneyLanguage -Text ($proposed -join "`n"))
    if ($hits.Count -gt 0) {
        # Leave an AUDIT TRAIL. A block is only visible in the model's tool result, so without this
        # the user can never review whether a block was legitimate - and the quiet failure mode is
        # the model rewording to dodge a pattern, degrading the doc with nobody the wiser.
        # .githooks/pre-commit surfaces these at commit time; `-ShowBlocks` reviews on demand.
        try {
            $logPath = Join-Path $projectDir '.claude\doc-hygiene-blocks.log'
            # True UTC epoch, matching git's %ct. NOT `Get-Date -UFormat %s`: on PS 5.1 that is
            # computed from the LOCAL clock (measured 14,399 s behind true epoch on this machine,
            # audit 2026-07-20) — pre-commit's since-last-commit filter would then silently hide
            # every block logged within ~4 h of the previous commit. Also culture-independent
            # (no [double]::Parse of a dotted string).
            $stamp = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
            $lines = foreach ($h in $hits) {
                $ctx = $h.Text -replace "`t", ' '
                if ($ctx.Length -gt 200) { $ctx = $ctx.Substring(0, 200) + '...' }
                "{0}`t{1}`t{2}`t{3}" -f $stamp, (Split-Path -Leaf $filePath), $h.Match, $ctx
            }
            Add-Content -LiteralPath $logPath -Value $lines -Encoding utf8
        } catch { }

        Write-Hits -Hits $hits -Where (Split-Path -Leaf $filePath)
        exit 2
    }
    exit 0
}
catch {
    # Never wedge the session on a guard bug — but exit 1, not 0: for PreToolUse, a non-2 nonzero
    # exit lets the tool call proceed AND surfaces stderr to the user, so a broken guard is a
    # visible event instead of a silent loss of protection (audit 2026-07-20).
    [Console]::Error.WriteLine("doc-hygiene-guard internal error (failing open): $($_.Exception.Message)")
    exit 1
}
