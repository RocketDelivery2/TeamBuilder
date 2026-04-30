# GitHub Labels

This document describes the standardized label system for the TeamBuilder repository.

## Label Categories

### Type Labels

Type labels categorize issues and pull requests by their purpose:

- **bug** - Something is not working
- **enhancement** - New feature or request
- **documentation** - Improvements or additions to documentation
- **question** - Further information is requested
- **duplicate** - This issue or pull request already exists
- **invalid** - This does not seem right
- **wontfix** - This will not be worked on

### Contributor Labels

Contributor labels help contributors find issues suitable for their expertise level:

- **good first issue** - Good for newcomers; ideal starting point for new contributors
- **help wanted** - Extra attention is needed; contributions are welcome

### Area Labels

Area labels use the `area:` prefix to indicate which part of the codebase is affected. This standardized prefix provides several benefits:

- **Consistency**: All architectural layers and domains use the same naming convention
- **Automation**: PR labelers can automatically apply area labels based on changed files
- **Navigation**: Developers can quickly filter issues and PRs by area of interest
- **Scope Clarity**: Area labels make it easy to understand the scope and impact of changes

#### Core Architecture Areas

- **area:api** - Changes related to the ASP.NET Core Web API layer (`src/TeamBuilder.Api/**`)
- **area:application** - Changes related to application services and use cases (`src/TeamBuilder.Application/**`)
- **area:domain** - Changes related to domain models, rules, and entities (`src/TeamBuilder.Domain/**`)
- **area:infrastructure** - Changes related to persistence, EF Core, and external services (`src/TeamBuilder.Infrastructure/**`)

#### Cross-Cutting Concerns

- **area:database** - Azure SQL, EF Core migrations, schema, indexes, or persistence
- **area:tests** - Changes related to unit, integration, or test infrastructure
- **area:docs** - Documentation, README, guides, or discussion content
- **area:github-actions** - GitHub Actions, workflows, Dependabot, or automation

#### Domain Specific Areas

- **area:frontend** - Frontend clients, UI examples, static web clients, mobile clients, or client integrations
- **area:roster-language** - Common roster language, roster imports, roster schema, and interoperability
- **area:product** - Product vision, feature planning, user workflows, or TeamBuilder use cases

#### Platform and Process Areas

- **area:devops** - CI/CD, deployment planning, environments, or release process
- **area:architecture** - Architecture, solution structure, design patterns, or technical direction
- **area:community** - Discussions, contributor experience, community process, or open-source coordination

### Planning Labels

- **roadmap** - Roadmap planning, milestones, and future direction

## Automatic Label Application

The `.github/labeler.yml` file defines rules that automatically apply area labels to pull requests based on which files were changed. This automation:

- Reduces manual label application work
- Ensures consistent labeling across all PRs
- Helps reviewers quickly understand the scope of changes

### Label Automation Rules

| Area Label | File Patterns |
|---|---|
| area:api | `src/TeamBuilder.Api/**` |
| area:application | `src/TeamBuilder.Application/**` |
| area:domain | `src/TeamBuilder.Domain/**` |
| area:infrastructure | `src/TeamBuilder.Infrastructure/**` |
| area:database | `src/TeamBuilder.Infrastructure/**/Migrations/**`<br>`src/TeamBuilder.Infrastructure/**/Persistence/**` |
| area:tests | `tests/**` |
| area:docs | `docs/**`, `README.md`, `SECURITY.md`, `CONTRIBUTING.md`, `.github/copilot-instructions.md` |
| area:github-actions | `.github/workflows/**`, `.github/dependabot.yml`, `.github/labeler.yml` |
| area:devops | `.github/**`, `docs/deployment.md` |
| area:frontend | `frontend/**`, `web/**`, `clients/**`, `samples/frontend/**` |
| area:architecture | `docs/architecture/**`, `docs/design/**` |
| area:roster-language | `docs/roster/**`, `src/**/Roster/**` |
| area:product | `docs/product/**`, `docs/roadmap/**` |
| area:community | `docs/community/**`, `docs/contributing/**`, `CONTRIBUTING.md` |

## Creating or Updating Labels

Use the provided PowerShell script to create or update all labels:

```powershell
.\scripts\create-github-labels.ps1
```

Or target a different repository:

```powershell
.\scripts\create-github-labels.ps1 -Owner myorg -Repo myrepo
```

The script:
- Creates missing labels with standardized descriptions and colors
- Updates existing label descriptions and colors
- Is idempotent and safe to rerun
- Uses `gh label view` for reliable existence detection (handles names with `:` correctly)
- Requires GitHub CLI (`gh`) to be installed and authenticated

### Prerequisites

Install GitHub CLI: https://cli.github.com/

Authenticate before running:

```powershell
gh auth login
```

### Running the Script

```powershell
# From the repository root
.\scripts\create-github-labels.ps1
```

After the script completes, follow the **Migrating from Old Labels** section below to remove the superseded labels.

## Migrating from Old Labels

The following old labels exist in the repository and must be **manually renamed or deleted** in the GitHub UI after the standardized `area:` labels have been created by the script:

| Old label | Replaced by |
|---|---|
| `architecture` | `area:architecture` |
| `community` | `area:community` |
| `frontend` | `area:frontend` |
| `product` | `area:product` |
| `roster-language` | `area:roster-language` |

Steps to remove old labels:

1. Run `.\scripts\create-github-labels.ps1` to ensure all new labels exist.
2. Open **Settings → Labels** in the GitHub repository.
3. For each old label in the table above, click the **⋯** menu and select **Delete** (or **Edit** to rename it directly to the `area:` version, which preserves label history on existing issues).
4. Confirm the deletion or rename.

## Best Practices

- Apply **exactly one type label** to every issue and PR
- Apply **area labels** to clarify which parts of the codebase are affected
- Use **contributor labels** to encourage participation from newcomers
- Use **planning labels** for roadmap and milestone discussions
- Combine labels to tell a complete story (e.g., `bug` + `area:api` + `help wanted`)

## Further Reading

- [GitHub Labels Documentation](https://docs.github.com/en/issues/using-labels-and-milestones-to-track-work/managing-labels)
- [Pull Request Labeling with GitHub Actions](https://github.com/actions/labeler)
