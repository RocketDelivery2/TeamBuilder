# GitHub Labels

This document describes the standardized label system for the TeamBuilder repository.

## Label Categories

Labels are organized by category to support automatic PR labeling and consistent issue classification.

### Type Labels

Used to categorize the nature of an issue or pull request:

- **bug** - Something is not working
- **enhancement** - New feature or request
- **documentation** - Improvements or additions to documentation
- **question** - Further information is requested
- **duplicate** - This issue or pull request already exists
- **invalid** - This does not seem right
- **wontfix** - This will not be worked on

### Contributor Labels

Used to identify issues suitable for contribution:

- **good first issue** - Good for newcomers
- **help wanted** - Extra attention is needed

### Area Labels

Labels prefixed with `area:` identify which part of the system is affected. This enables:
- **Automatic PR labeling** - PRs are labeled based on changed files via `.github/labeler.yml`
- **Filtering and navigation** - Issues and PRs can be grouped by area
- **Ownership clarity** - Clear ownership boundaries between components
- **Focused discussions** - Targeted conversations within each domain

#### Core Architecture Areas

- **area:api** - Changes related to the ASP.NET Core Web API layer (`src/TeamBuilder.Api/**`)
- **area:application** - Changes related to application services and use cases (`src/TeamBuilder.Application/**`)
- **area:domain** - Changes related to domain models, rules, and entities (`src/TeamBuilder.Domain/**`)
- **area:infrastructure** - Changes related to persistence, EF Core, and external services (`src/TeamBuilder.Infrastructure/**`)

#### Database & Infrastructure

- **area:database** - Azure SQL, EF Core migrations, schema, indexes, or persistence
- **area:devops** - CI/CD, deployment planning, environments, or release process
- **area:github-actions** - GitHub Actions, workflows, Dependabot, or automation

#### Testing & Documentation

- **area:tests** - Changes related to unit, integration, or test infrastructure (`tests/**`)
- **area:docs** - Documentation, README, guides, or discussion content

#### Domain-Specific

- **area:frontend** - Frontend clients, UI examples, static web clients, mobile clients, or client integrations
- **area:roster-language** - Common roster language, roster imports, roster schema, and interoperability

#### Strategy & Direction

- **area:architecture** - Architecture, solution structure, design patterns, or technical direction
- **area:product** - Product vision, feature planning, user workflows, or TeamBuilder use cases
- **area:community** - Discussions, contributor experience, community process, or open-source coordination

### Planning Labels

Used for roadmap and milestone planning:

- **roadmap** - Roadmap planning, milestones, and future direction

## Automatic Labeling

The `.github/labeler.yml` configuration automatically applies `area:*` labels to pull requests based on the files they modify. This happens via the GitHub Actions `labeler` action on every PR.

### Label Mapping

The following file patterns are automatically matched:

| Label | File Pattern |
|-------|--------------|
| area:api | `src/TeamBuilder.Api/**` |
| area:application | `src/TeamBuilder.Application/**` |
| area:domain | `src/TeamBuilder.Domain/**` |
| area:infrastructure | `src/TeamBuilder.Infrastructure/**` |
| area:database | `src/TeamBuilder.Infrastructure/**/Migrations/**` or `**/Persistence/**` |
| area:tests | `tests/**` |
| area:docs | `docs/**`, `README.md`, `SECURITY.md`, `CONTRIBUTING.md`, `.github/copilot-instructions.md` |
| area:github-actions | `.github/workflows/**`, `.github/dependabot.yml`, `.github/labeler.yml` |
| area:devops | `.github/**`, `docs/deployment.md` |
| area:frontend | `frontend/**`, `web/**`, `clients/**`, `samples/frontend/**` |
| area:architecture | `docs/architecture/**` |
| area:roster-language | `docs/roster-schema/**`, `docs/roster-imports/**`, `docs/common-roster-language/**` |
| area:product | `docs/roadmap/**`, `docs/product/**`, `docs/use-cases/**` |
| area:community | `docs/community/**`, `docs/contributing/**`, `CONTRIBUTING.md` |

## Managing Labels

### Creating or Updating Labels

The repository includes a PowerShell script to create and update all labels with standardized descriptions and colors:

```powershell
.\scripts\create-github-labels.ps1
```

**Requirements:**
- GitHub CLI (`gh`) installed and authenticated
- PowerShell Core or Windows PowerShell 5.1+
- Repository write access

**What it does:**
- Creates missing labels
- Updates descriptions and colors for existing labels
- Maintains color consistency across related labels
- Is safe to rerun

### Manual Label Management

To manually manage labels via the GitHub UI:

1. Go to the repository **Settings** → **Labels**
2. Create new labels or edit existing ones
3. Use the standardized names and descriptions from the lists above

### Migrating Old Labels

If the repository has old label names (without the `area:` prefix), they should be:

1. **Renamed** to match the new standardized names, or
2. **Deleted** if they are no longer needed

The GitHub UI allows you to rename labels in bulk or individually via Settings → Labels.

## Best Practices

### When Creating Issues

- Choose one or more `area:*` labels that match the affected component(s)
- Add type labels (`bug`, `enhancement`, `documentation`, etc.)
- Add contributor labels (`good first issue`, `help wanted`) if applicable
- Avoid creating custom labels; use the standardized set

### When Creating Pull Requests

- Don't manually apply `area:*` labels; they are added automatically based on changed files
- Type labels and contributor labels should be applied manually if relevant
- If a PR touches multiple areas, all applicable area labels will be added automatically

### Label Queries

Filter issues and PRs by label in GitHub's issue list:

```
# Find all API-related issues
is:open label:area:api

# Find good first issues needing help
is:open label:"good first issue" label:"help wanted"

# Find all database work in current sprint
is:open label:area:database milestone:"Sprint 5"
```

## Color Scheme

All `area:*` labels use a consistent blue color scheme (`#0366d6`) to visually group them as a related set, making them easy to distinguish from type labels, contributor labels, and planning labels.

