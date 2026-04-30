#!/usr/bin/env pwsh
<#
.SYNOPSIS
Apply standardized labels to all TeamBuilder GitHub Discussions.

.DESCRIPTION
Uses the GitHub GraphQL API via the GitHub CLI to apply the confirmed standardized
label set to every TeamBuilder discussion. Existing labels not in the target set
are removed. The script is idempotent and safe to rerun.

No secrets are required beyond a standard authenticated GitHub CLI session.

.PARAMETER Owner
The GitHub organization or user that owns the repository. Defaults to 'RocketDelivery2'.

.PARAMETER Repo
The GitHub repository name. Defaults to 'TeamBuilder'.

.EXAMPLE
.\scripts\update-discussion-labels.ps1

.EXAMPLE
.\scripts\update-discussion-labels.ps1 -Owner 'myorg' -Repo 'myrepo'

.NOTES
Requires GitHub CLI: https://cli.github.com/
Authenticate first with: gh auth login
#>

param(
    [string]$Owner = 'RocketDelivery2',
    [string]$Repo  = 'TeamBuilder'
)

$ErrorActionPreference = 'Stop'

if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
    Write-Error 'GitHub CLI (gh) is not installed. See https://cli.github.com/'
    exit 1
}

# ---------------------------------------------------------------------------
# Step 1 — Fetch all label node IDs from the repository
# ---------------------------------------------------------------------------
$labelQuery = 'query($owner:String!, $repo:String!) { repository(owner:$owner, name:$repo) { labels(first:100) { nodes { id name } } } }'
$labelData  = gh api graphql -F owner="$Owner" -F repo="$Repo" -f query="$labelQuery" | ConvertFrom-Json

$L = @{}
foreach ($node in $labelData.data.repository.labels.nodes) {
    $L[$node.name] = $node.id
}

Write-Host "Loaded $($L.Count) labels from $Owner/$Repo" -ForegroundColor Cyan

# ---------------------------------------------------------------------------
# Step 2 — Fetch all discussion IDs and their current labels
# ---------------------------------------------------------------------------
$discussionQuery = 'query($owner:String!, $repo:String!) { repository(owner:$owner, name:$repo) { discussions(first:50) { nodes { id number title labels(first:20) { nodes { id name } } } } } }'
$discussionData  = gh api graphql -F owner="$Owner" -F repo="$Repo" -f query="$discussionQuery" | ConvertFrom-Json
$discussions     = $discussionData.data.repository.discussions.nodes

# ---------------------------------------------------------------------------
# Step 3 — Define the target label set per discussion title keyword
#
# Keys are matched against the discussion title (case-insensitive substring).
# The FIRST matching rule wins. Use specific keywords to avoid false matches.
# ---------------------------------------------------------------------------
$rules = @(
    [pscustomobject]@{
        match  = 'Welcome to TeamBuilder'
        labels = @('area:community','help wanted')
    }
    [pscustomobject]@{
        match  = 'Vision, Mission, and Roadmap'
        labels = @('area:product','roadmap','help wanted')
    }
    [pscustomobject]@{
        match  = 'How to Contribute'
        labels = @('area:community','area:docs','documentation','help wanted','good first issue')
    }
    [pscustomobject]@{
        match  = 'Architecture: API-First'
        labels = @('area:architecture','area:api','area:database','help wanted')
    }
    [pscustomobject]@{
        match  = 'Roster Language Proposal'
        labels = @('area:roster-language','area:product','enhancement','help wanted','question')
    }
    [pscustomobject]@{
        match  = 'Product Use Cases'
        labels = @('area:product','area:community','question')
    }
    [pscustomobject]@{
        match  = 'Team Refill'
        labels = @('area:product','enhancement','question')
    }
    [pscustomobject]@{
        match  = 'Frontend Clients'
        labels = @('area:frontend','area:api','enhancement','help wanted')
    }
    [pscustomobject]@{
        match  = 'DevOps, Environments'
        labels = @('area:devops','area:database','area:github-actions','help wanted')
    }
    [pscustomobject]@{
        match  = 'Ask TeamBuilder Questions'
        labels = @('question','area:community')
    }
    [pscustomobject]@{
        match  = 'Show and Tell'
        labels = @('area:community','area:frontend','help wanted')
    }
)

# ---------------------------------------------------------------------------
# Step 4 — GraphQL mutations (single-label calls to avoid array issues)
# ---------------------------------------------------------------------------
$addQ    = 'mutation($lid:ID!, $labelId:ID!) { addLabelsToLabelable(input:{labelableId:$lid, labelIds:[$labelId]}) { clientMutationId } }'
$removeQ = 'mutation($lid:ID!, $labelId:ID!) { removeLabelsFromLabelable(input:{labelableId:$lid, labelIds:[$labelId]}) { clientMutationId } }'

function Invoke-AddLabel($discussionId, $labelName) {
    $labelId = $L[$labelName]
    if (-not $labelId) { Write-Host "    SKIP (unknown label): $labelName" -ForegroundColor DarkYellow; return }
    $out = gh api graphql -F lid="$discussionId" -F labelId="$labelId" -f query="$addQ" 2>&1
    if ($LASTEXITCODE -ne 0) { Write-Host "    ERR add '$labelName': $out" -ForegroundColor Red }
    else { Write-Host "    + $labelName" -ForegroundColor Green }
}

function Invoke-RemoveLabel($discussionId, $labelName) {
    $labelId = $L[$labelName]
    if (-not $labelId) { return }
    $out = gh api graphql -F lid="$discussionId" -F labelId="$labelId" -f query="$removeQ" 2>&1
    if ($LASTEXITCODE -ne 0) { Write-Host "    ERR remove '$labelName': $out" -ForegroundColor DarkYellow }
    else { Write-Host "    - $labelName" -ForegroundColor Yellow }
}

# ---------------------------------------------------------------------------
# Step 5 — Apply diffs
# ---------------------------------------------------------------------------
$successCount = 0
$skipCount    = 0

foreach ($d in ($discussions | Sort-Object number)) {
    $rule = $rules | Where-Object { $d.title -match [regex]::Escape($_.match) } | Select-Object -First 1

    if (-not $rule) {
        Write-Host "#$($d.number): $($d.title) — no matching rule, skipped" -ForegroundColor DarkYellow
        $skipCount++
        continue
    }

    Write-Host "`n#$($d.number): $($d.title)" -ForegroundColor Cyan

    $currentNames = $d.labels.nodes | ForEach-Object { $_.name }
    $targetNames  = $rule.labels

    $toAdd    = $targetNames  | Where-Object { $_ -notin $currentNames }
    $toRemove = $currentNames | Where-Object { $_ -notin $targetNames }

    if ($toAdd.Count -eq 0 -and $toRemove.Count -eq 0) {
        Write-Host '    (already correct)' -ForegroundColor DarkGray
    } else {
        foreach ($lbl in $toAdd)    { Invoke-AddLabel    $d.id $lbl }
        foreach ($lbl in $toRemove) { Invoke-RemoveLabel $d.id $lbl }
    }

    $successCount++
}

Write-Host ''
Write-Host "Done. Processed: $successCount  Skipped (no rule): $skipCount" -ForegroundColor Cyan
