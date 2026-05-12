<#
.SYNOPSIS
    Appends a snapshot of the current terminal output and Copilot session into
    the tracked AI history files at the time of a commit to main/master.

.PARAMETER DryRun
    When set, prints the entries that would be appended but does not write any files.

.EXAMPLE
    .\.ai\scripts\append-checkin-history.ps1
    .\.ai\scripts\append-checkin-history.ps1 -DryRun
#>
[CmdletBinding()]
param(
    [switch]$DryRun
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# ── Repo root ────────────────────────────────────────────────────────────────

$repoRoot = (git rev-parse --show-toplevel 2>$null).Trim()
if (-not $repoRoot) {
    Write-Error "Not inside a Git repository."
    exit 1
}

Push-Location $repoRoot

try {

    # ── Source files ─────────────────────────────────────────────────────────

    $latestTerminalFile  = Join-Path $repoRoot '.ai\latest-terminal-output.md'
    $fallbackTerminalFile = Join-Path $repoRoot '.ai\terminal-history.md'
    $copilotSessionFile  = Join-Path $repoRoot '.ai\copilot-session.md'
    $todoFile            = Join-Path $repoRoot '.ai\todo.md'

    # ── Destination files ─────────────────────────────────────────────────────

    $historyDir          = Join-Path $repoRoot 'ai-history'
    $terminalHistoryFile = Join-Path $historyDir 'terminal-history.txt'
    $copilotHistoryFile  = Join-Path $historyDir 'copilot-chat-history.txt'

    # ── Git context ───────────────────────────────────────────────────────────

    $timestamp     = (Get-Date).ToUniversalTime().ToString('yyyy-MM-dd HH:mm:ss UTC')
    $branch        = (git branch --show-current 2>$null).Trim()
    $shortSha      = (git rev-parse --short HEAD 2>$null).Trim()
    $fullSha       = (git rev-parse HEAD 2>$null).Trim()
    $mergeInProgress = Test-Path (Join-Path $repoRoot '.git\MERGE_HEAD')
    $latestCommit  = (git log -1 --pretty=format:'%s' 2>$null).Trim()
    $gitStatus     = (git status --short 2>$null).Trim()
    if (-not $gitStatus) { $gitStatus = '(clean)' }

    # ── Read source content ───────────────────────────────────────────────────

    function Read-FileOrDefault {
        param([string]$Path, [string]$Default = '(not found)')
        if (Test-Path $Path) {
            return (Get-Content $Path -Raw -Encoding UTF8).Trim()
        }
        return $Default
    }

    $terminalContent = if (Test-Path $latestTerminalFile) {
        Read-FileOrDefault $latestTerminalFile
    } else {
        Read-FileOrDefault $fallbackTerminalFile
    }

    $copilotContent = Read-FileOrDefault $copilotSessionFile
    $todoContent    = Read-FileOrDefault $todoFile

    # ── Build entries ─────────────────────────────────────────────────────────

    $separator = '=' * 80

    $terminalEntry = @"
$separator
Timestamp  : $timestamp
Branch     : $branch
SHA (short): $shortSha
SHA (full) : $fullSha
Merge      : $mergeInProgress
Commit     : $latestCommit
Status     : $gitStatus
$separator
$terminalContent
"@

    $copilotEntry = @"
$separator
Timestamp  : $timestamp
Branch     : $branch
SHA (short): $shortSha
SHA (full) : $fullSha
Merge      : $mergeInProgress
Commit     : $latestCommit
$separator
--- Copilot Session ---
$copilotContent

--- TODOs ---
$todoContent
"@

    # ── Dry-run output ────────────────────────────────────────────────────────

    if ($DryRun) {
        Write-Host ''
        Write-Host '=== DRY RUN — no files will be written ===' -ForegroundColor Cyan
        Write-Host ''
        Write-Host "Target: $terminalHistoryFile" -ForegroundColor Yellow
        Write-Host '--- Terminal entry preview ---' -ForegroundColor Gray
        Write-Host $terminalEntry
        Write-Host ''
        Write-Host "Target: $copilotHistoryFile" -ForegroundColor Yellow
        Write-Host '--- Copilot entry preview ---' -ForegroundColor Gray
        Write-Host $copilotEntry
        Write-Host ''
        Write-Host '=== DRY RUN complete — no files written ===' -ForegroundColor Cyan
        exit 0
    }

    # ── Ensure destination directory and starter files exist ─────────────────

    if (-not (Test-Path $historyDir)) {
        New-Item -ItemType Directory -Path $historyDir | Out-Null
    }

    if (-not (Test-Path $terminalHistoryFile)) {
        $starterTerminal = "# TeamBuilder Terminal Checkin History`n`nThis file is automatically appended during commits to main.`n"
        [System.IO.File]::WriteAllText($terminalHistoryFile, $starterTerminal, [System.Text.Encoding]::UTF8)
    }

    if (-not (Test-Path $copilotHistoryFile)) {
        $starterCopilot = "# TeamBuilder Copilot Chat Checkin History`n`nThis file is automatically appended during commits to main.`n"
        [System.IO.File]::WriteAllText($copilotHistoryFile, $starterCopilot, [System.Text.Encoding]::UTF8)
    }

    # ── Append entries ────────────────────────────────────────────────────────

    $nl = "`n"
    [System.IO.File]::AppendAllText($terminalHistoryFile, $nl + $terminalEntry + $nl, [System.Text.Encoding]::UTF8)
    [System.IO.File]::AppendAllText($copilotHistoryFile,  $nl + $copilotEntry  + $nl, [System.Text.Encoding]::UTF8)

    Write-Host "Appended terminal snapshot  -> $terminalHistoryFile" -ForegroundColor Green
    Write-Host "Appended Copilot snapshot   -> $copilotHistoryFile"  -ForegroundColor Green

} finally {
    Pop-Location
}
