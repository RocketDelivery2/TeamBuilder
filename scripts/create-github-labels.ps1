#!/usr/bin/env pwsh
<#
.SYNOPSIS
Create or update GitHub labels for the TeamBuilder repository.

.DESCRIPTION
This script creates or updates all standardized GitHub labels for the TeamBuilder repository
using the GitHub CLI. Labels are grouped by type: type, contributor, area, and planning.

The script is idempotent and safe to rerun. It will create missing labels and update
existing label descriptions and colors.

.PARAMETER Owner
The GitHub organization or user that owns the repository. Defaults to 'RocketDelivery2'.

.PARAMETER Repo
The GitHub repository name. Defaults to 'TeamBuilder'.

.EXAMPLE
.\scripts\create-github-labels.ps1

.EXAMPLE
.\scripts\create-github-labels.ps1 -Owner 'myorg' -Repo 'myrepo'

.NOTES
Requires GitHub CLI to be installed: https://cli.github.com/
#>

param(
    [string]$Owner = 'RocketDelivery2',
    [string]$Repo = 'TeamBuilder'
)

$ErrorActionPreference = 'Stop'

# Verify GitHub CLI is installed
if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
    Write-Error "GitHub CLI (gh) is not installed. Please install it from https://cli.github.com/"
    exit 1
}

# Define all labels with their properties
$labels = @(
    # Type labels
    @{ name = 'bug'; description = 'Something is not working'; color = 'd73a4a' },
    @{ name = 'enhancement'; description = 'New feature or request'; color = 'a2eeef' },
    @{ name = 'documentation'; description = 'Improvements or additions to documentation'; color = '0075ca' },
    @{ name = 'question'; description = 'Further information is requested'; color = 'd876e3' },
    @{ name = 'duplicate'; description = 'This issue or pull request already exists'; color = 'cfd3d7' },
    @{ name = 'invalid'; description = 'This does not seem right'; color = 'e4e669' },
    @{ name = 'wontfix'; description = 'This will not be worked on'; color = 'ffffff' },

    # Contributor labels
    @{ name = 'good first issue'; description = 'Good for newcomers'; color = '7057ff' },
    @{ name = 'help wanted'; description = 'Extra attention is needed'; color = '008672' },

    # Area labels
    @{ name = 'area:api'; description = 'Changes related to the ASP.NET Core Web API layer'; color = '1f6feb' },
    @{ name = 'area:application'; description = 'Changes related to application services and use cases'; color = '1f6feb' },
    @{ name = 'area:architecture'; description = 'Architecture, solution structure, design patterns, or technical direction'; color = '1f6feb' },
    @{ name = 'area:community'; description = 'Discussions, contributor experience, community process, or open-source coordination'; color = '1f6feb' },
    @{ name = 'area:database'; description = 'Azure SQL, EF Core migrations, schema, indexes, or persistence'; color = '1f6feb' },
    @{ name = 'area:devops'; description = 'CI/CD, deployment planning, environments, or release process'; color = '1f6feb' },
    @{ name = 'area:docs'; description = 'Documentation, README, guides, or discussion content'; color = '1f6feb' },
    @{ name = 'area:domain'; description = 'Changes related to domain models, rules, and entities'; color = '1f6feb' },
    @{ name = 'area:frontend'; description = 'Frontend clients, UI examples, static web clients, mobile clients, or client integrations'; color = '1f6feb' },
    @{ name = 'area:github-actions'; description = 'GitHub Actions, workflows, Dependabot, or automation'; color = '1f6feb' },
    @{ name = 'area:infrastructure'; description = 'Changes related to persistence, EF Core, and external services'; color = '1f6feb' },
    @{ name = 'area:product'; description = 'Product vision, feature planning, user workflows, or TeamBuilder use cases'; color = '1f6feb' },
    @{ name = 'area:roster-language'; description = 'Common roster language, roster imports, roster schema, and interoperability'; color = '1f6feb' },
    @{ name = 'area:tests'; description = 'Changes related to unit, integration, or test infrastructure'; color = '1f6feb' },

    # Planning labels
    @{ name = 'roadmap'; description = 'Roadmap planning, milestones, and future direction'; color = 'ffc274' }
)

$targetRepo = "$Owner/$Repo"
Write-Host "Creating or updating labels for $targetRepo..." -ForegroundColor Cyan

$successCount = 0
$failureCount = 0

foreach ($label in $labels) {
    try {
        # Try to get the label first
        $labelExists = gh label list --repo "$targetRepo" --limit 1000 | Select-String "^$([regex]::Escape($label.name))\s" -Quiet
        
        if ($labelExists) {
            Write-Host "Updating label: $($label.name)" -ForegroundColor Yellow
            # Update existing label
            gh label edit `
                --repo "$targetRepo" `
                --name "$($label.name)" `
                --description "$($label.description)" `
                --color "$($label.color)"
        }
        else {
            Write-Host "Creating label: $($label.name)" -ForegroundColor Green
            # Create new label
            gh label create `
                --repo "$targetRepo" `
                --name "$($label.name)" `
                --description "$($label.description)" `
                --color "$($label.color)"
        }
        
        $successCount++
    }
    catch {
        Write-Host "Failed to process label '$($label.name)': $_" -ForegroundColor Red
        $failureCount++
    }
}

Write-Host ""
Write-Host "Label synchronization complete!" -ForegroundColor Green
Write-Host "Successfully processed: $successCount labels" -ForegroundColor Green
if ($failureCount -gt 0) {
    Write-Host "Failed: $failureCount labels" -ForegroundColor Red
    exit 1
}
