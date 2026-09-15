# finalize-rename.ps1
#
# Renames the project folder AND migrates Claude Code session state, so that running `claude` in the
# renamed folder CONTINUES this conversation instead of starting a fresh session.
#
# WHY THIS IS NEEDED
#   Claude Code keys its per-project state by the working-directory path. Rename the folder and the
#   key no longer matches, so history, memory, trust approval and tool permissions all appear to
#   vanish. Three things are path-keyed:
#
#     1. ~/.claude/projects/<sanitized-path>/      transcripts (.jsonl), sidecar dir, memory/
#     2. the "cwd" field INSIDE each .jsonl        1000+ occurrences in a long session
#     3. ~/.claude.json  ->  projects["<path>"]    trust dialog, allowedTools, MCP config
#
#   The sanitized key is the absolute path with ":" and "\" replaced by "-". Verified against an
#   existing project whose folder name already contains hyphens, so hyphens are preserved as-is.
#
# PRECONDITION
#   Claude Code must be CLOSED. The script refuses to run otherwise: the transcript is being appended
#   to live, and migrating a file mid-write would corrupt it.
#
# USAGE
#   powershell -ExecutionPolicy Bypass -File .\scripts\finalize-rename.ps1 -WhatIf   # dry run
#   powershell -ExecutionPolicy Bypass -File .\scripts\finalize-rename.ps1
#
# AFTERWARDS
#   cd <new folder>
#   claude --continue          # resumes the most recent session
#   claude --resume            # pick from a list

[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string]$Base    = "C:\Users\user\Desktop\workspace\Portfolio",
    [string]$OldName = "aegis-factory",
    [string]$NewName = "manufacturing-ai-reliability",
    [string]$ClaudeHome = "$env:USERPROFILE\.claude",
    [switch]$SkipBackup
)

$ErrorActionPreference = "Stop"
$old = Join-Path $Base $OldName
$new = Join-Path $Base $NewName

function Fail($m) { Write-Host "FAILED  $m" -ForegroundColor Red; exit 1 }
function Ok($m)   { Write-Host "  OK    $m" -ForegroundColor Green }
function Warn($m) { Write-Host "  WARN  $m" -ForegroundColor Yellow }
function Info($m) { Write-Host "        $m" -ForegroundColor DarkGray }
function Step($m) { Write-Host "`n$m" -ForegroundColor Cyan }

# Claude Code's project key: absolute path with ":" and "\" collapsed to "-".
function Get-ProjectKey([string]$path) { $path -replace '[:\\]', '-' }

$oldKey = Get-ProjectKey $old
$newKey = Get-ProjectKey $new
$projects = Join-Path $ClaudeHome "projects"
$oldProj  = Join-Path $projects $oldKey
$newProj  = Join-Path $projects $newKey
$claudeJson = Join-Path (Split-Path $ClaudeHome -Parent) ".claude.json"

Write-Host "`nFolder rename + session migration" -ForegroundColor Cyan
Write-Host "  folder   $OldName  ->  $NewName"
Write-Host "  proj key $oldKey"
Write-Host "        -> $newKey"

# ---------------------------------------------------------------- 1. guards
Step "1. Preconditions"

$running = Get-Process -Name "claude" -ErrorAction SilentlyContinue
if ($running) {
    Write-Host ""
    Fail "Claude Code is running (PID $($running.Id -join ', ')). Close it first - the transcript is being written to."
}
Ok "Claude Code is not running"

if (-not (Test-Path $old)) {
    if ((Test-Path $new) -and ((Get-Item $new -Force).LinkType -ne "Junction")) {
        Write-Host "`nAlready renamed." -ForegroundColor Green; exit 0
    }
    Fail "source folder '$old' not found"
}
Ok "source folder present"

if (Test-Path $new) {
    $it = Get-Item $new -Force
    if ($it.LinkType -ne "Junction") {
        Fail "'$new' exists and is NOT a junction. Two real folders means divergent copies - resolve by hand."
    }
    Ok "target path is the expected junction (will be removed)"
}

if (-not (Test-Path $oldProj)) { Warn "no session state at $oldProj - folder will still be renamed" }
else {
    $jsonl = @(Get-ChildItem $oldProj -Filter *.jsonl -ErrorAction SilentlyContinue)
    Ok "session state found: $($jsonl.Count) transcript(s)"
}
if (Test-Path $newProj) { Fail "'$newProj' already exists. Merging session state is not automatic - resolve by hand." }

# ---------------------------------------------------------------- 2. backup
if (-not $SkipBackup -and (Test-Path $oldProj)) {
    Step "2. Backup"
    $stamp  = Get-Date -Format "yyyyMMdd-HHmmss"
    $backup = Join-Path $ClaudeHome "projects-backup-$stamp"
    if ($PSCmdlet.ShouldProcess($oldProj, "back up to $backup")) {
        New-Item -ItemType Directory -Path $backup -Force | Out-Null
        Copy-Item $oldProj -Destination $backup -Recurse -Force
        Copy-Item $claudeJson -Destination (Join-Path $backup ".claude.json.bak") -Force -ErrorAction SilentlyContinue
        Ok "backup at $backup"
        Info "delete it once you have confirmed the session resumes"
    }
} else { Step "2. Backup - skipped" }

# ---------------------------------------------------------------- 3. folder
Step "3. Rename folder"
if ($PSCmdlet.ShouldProcess($old, "rename to $NewName")) {
    if (Test-Path $new) {
        [System.IO.Directory]::Delete($new, $false)      # removes the junction, not its target
        if (-not (Test-Path (Join-Path $old "MASTER_SPEC.md"))) { Fail "real folder vanished with the junction - STOP, restore from backup" }
        Ok "junction removed, real folder intact"
    }
    try { Rename-Item -Path $old -NewName $NewName -ErrorAction Stop; Ok "folder renamed" }
    catch {
        if (-not (Test-Path $new)) { New-Item -ItemType Junction -Path $new -Target $old | Out-Null }
        Fail "rename blocked ($($_.Exception.Message)). Junction restored - close the holding process and re-run."
    }
}

# ---------------------------------------------------------------- 4. session dir
Step "4. Move session state"
if ((Test-Path $oldProj) -and $PSCmdlet.ShouldProcess($oldProj, "rename to $newKey")) {
    Rename-Item -Path $oldProj -NewName $newKey -ErrorAction Stop
    Ok "transcripts + memory moved to $newKey"
}

# ---------------------------------------------------------------- 5. rewrite cwd
Step "5. Rewrite embedded cwd paths"
if ((Test-Path $newProj) -and $PSCmdlet.ShouldProcess("transcripts", "rewrite cwd")) {
    # The path appears JSON-escaped inside the .jsonl, i.e. each backslash is doubled:
    #   "cwd":"C:\\Users\\user\\...\\aegis-factory"
    # Use [string]::Replace, NOT -replace: the latter is a REGEX operator, and '\\' -> '\\\\'
    # there produces FOUR backslashes, so nothing matches and the migration silently does nothing.
    # That exact bug was caught by scripts/../test before this script was ever run for real.
    $findJson    = $old.Replace('\', '\\')
    $replaceJson = $new.Replace('\', '\\')
    $findFwd     = $old.Replace('\', '/')
    $replaceFwd  = $new.Replace('\', '/')
    Info "matching: $findJson"

    foreach ($f in Get-ChildItem $newProj -Filter *.jsonl -Recurse) {
        $tmp = "$($f.FullName).migrating"
        $hits = 0
        # Explicit UTF-8 both ways. The transcript contains non-ASCII text (this session is partly
        # in Korean); reading it with the PowerShell 5.1 default (ANSI) would corrupt it.
        $rd = [System.IO.StreamReader]::new($f.FullName, [System.Text.UTF8Encoding]::new($false), $true)
        $wr = [System.IO.StreamWriter]::new($tmp, $false, [System.Text.UTF8Encoding]::new($false))
        try {
            while ($null -ne ($line = $rd.ReadLine())) {
                if ($line.Contains($findJson)) { $hits++; $line = $line.Replace($findJson, $replaceJson) }
                if ($line.Contains($findFwd))  {          $line = $line.Replace($findFwd,  $replaceFwd)  }
                $wr.WriteLine($line)
            }
        } finally { $rd.Dispose(); $wr.Dispose() }

        if ($hits -eq 0) {
            Remove-Item $tmp -Force
            Warn "$($f.Name): 0 replacements - leaving file untouched (check the path constants)"
        } else {
            Move-Item $tmp $f.FullName -Force
            Ok "$($f.Name): $hits line(s) rewritten"
        }
    }
}

# ---------------------------------------------------------------- 6. .claude.json
Step "6. Migrate project settings (.claude.json)"
if ((Test-Path $claudeJson) -and $PSCmdlet.ShouldProcess($claudeJson, "re-key projects entry")) {
    # Keys here use the forward-slash absolute path, not the sanitized form.
    $oldJsonKey = $old -replace '\\', '/'
    $newJsonKey = $new -replace '\\', '/'

    $raw = Get-Content $claudeJson -Raw -Encoding UTF8
    $cfg = $raw | ConvertFrom-Json

    if ($cfg.projects -and $cfg.projects.PSObject.Properties.Name -contains $oldJsonKey) {
        $entry = $cfg.projects.$oldJsonKey
        $cfg.projects | Add-Member -NotePropertyName $newJsonKey -NotePropertyValue $entry -Force
        $cfg.projects.PSObject.Properties.Remove($oldJsonKey)
        ($cfg | ConvertTo-Json -Depth 100) | Set-Content $claudeJson -Encoding UTF8
        Ok "settings re-keyed (trust approval, allowedTools, MCP config preserved)"
    } else { Warn "no projects entry for the old path - nothing to migrate" }
}

# ---------------------------------------------------------------- 7. verify
Step "7. Verify"
if (-not $WhatIfPreference) {
    if (Test-Path (Join-Path $new "MASTER_SPEC.md")) { Ok "project files at new path" } else { Fail "project files missing" }
    if (Test-Path $newProj) { Ok "session state at $newKey" } else { Warn "no session state directory" }

    $leftover = 0
    if (Test-Path $newProj) {
        foreach ($f in Get-ChildItem $newProj -Filter *.jsonl -Recurse) {
            $leftover += (Select-String -Path $f.FullName -Pattern ([regex]::Escape($OldName)) -AllMatches -ErrorAction SilentlyContinue |
                          Measure-Object).Count
        }
    }
    if ($leftover -eq 0) { Ok "no stale path references in transcripts" }
    else { Warn "$leftover line(s) still mention '$OldName' (may be ordinary conversation text, not paths)" }

    Push-Location $new
    try {
        foreach ($t in @("validate_examples","consistency_check","safety_invariants")) {
            $p = "tests\contract\$t.mjs"
            if (Test-Path $p) {
                node $p *> $null
                if ($LASTEXITCODE -eq 0) { Ok "$t passed" } else { Warn "$t exited $LASTEXITCODE" }
            }
        }
    } finally { Pop-Location }

    Write-Host "`nDone. Continue the conversation with:" -ForegroundColor Cyan
    Write-Host "  cd `"$new`""
    Write-Host "  claude --continue`n"
}
