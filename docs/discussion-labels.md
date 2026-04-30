# TeamBuilder Discussion Labels

This document records the standardized label assignments for all GitHub Discussions in the
TeamBuilder repository. Labels are applied using the confirmed standardized label set only.

## Final Label Assignments

### #5 — Welcome to TeamBuilder Discussions

| | |
|---|---|
| **Labels** | `area:community`, `help wanted` |
| **Removed** | `enhancement` |

---

### #6 — TeamBuilder Vision, Mission, and Roadmap

| | |
|---|---|
| **Labels** | `area:product`, `roadmap`, `help wanted` |
| **Removed** | `enhancement` |

---

### #7 — Start Here: How to Contribute to TeamBuilder

| | |
|---|---|
| **Labels** | `area:community`, `area:docs`, `documentation`, `help wanted`, `good first issue` |
| **Removed** | _(none)_ |

---

### #8 — TeamBuilder Architecture: API-First, Frontend-Agnostic, Azure SQL Ready

| | |
|---|---|
| **Labels** | `area:architecture`, `area:api`, `area:database`, `help wanted` |
| **Removed** | `enhancement` |

---

### #9 — Common Roster Language Proposal

| | |
|---|---|
| **Labels** | `area:roster-language`, `area:product`, `enhancement`, `help wanted`, `question` |
| **Removed** | _(none)_ |

---

### #10 — Product Use Cases: Sports, Gaming, Events, and Communities

| | |
|---|---|
| **Labels** | `area:product`, `area:community`, `question` |
| **Removed** | `enhancement` |

---

### #11 — Team Refill and Open Spot Matching

| | |
|---|---|
| **Labels** | `area:product`, `enhancement`, `question` |
| **Removed** | _(none)_ |

---

### #12 — Frontend Clients and Integration Ideas

| | |
|---|---|
| **Labels** | `area:frontend`, `area:api`, `enhancement`, `help wanted` |
| **Removed** | _(none)_ |

---

### #13 — DevOps, Environments, and Deployment Strategy

| | |
|---|---|
| **Labels** | `area:devops`, `area:database`, `area:github-actions`, `help wanted` |
| **Removed** | `enhancement` |

---

### #14 — Ask TeamBuilder Questions Here

| | |
|---|---|
| **Labels** | `question`, `area:community` |
| **Removed** | _(none)_ |

---

### #15 — Show and Tell: TeamBuilder Concepts, Mockups, and Integrations

| | |
|---|---|
| **Labels** | `area:community`, `area:frontend`, `help wanted` |
| **Removed** | `enhancement` |

---

## Updating Discussion Labels

### Automated (GitHub CLI)

Use the script at `scripts/update-discussion-labels.ps1` to re-apply the standardized
label set to all discussions. The script uses only GitHub CLI and the GraphQL API.
No secrets beyond a standard `gh auth login` session are required.

```powershell
.\scripts\update-discussion-labels.ps1
```

### Manual (GitHub UI)

1. Open the TeamBuilder repository on GitHub.
2. Go to **Discussions**.
3. Open the target discussion.
4. In the right sidebar, find **Labels**.
5. Click the gear icon next to Labels.
6. Add the recommended labels listed above.
7. Remove any labels listed under **Removed**.
8. Click away to save.

---

## Standardized Label Reference

Only the following labels are permitted on discussions. Do not create or use any other labels.

### Type Labels
`bug` `enhancement` `documentation` `question` `duplicate` `invalid` `wontfix`

### Contributor Labels
`good first issue` `help wanted`

### Area Labels
`area:api` `area:application` `area:architecture` `area:community` `area:database`
`area:devops` `area:docs` `area:domain` `area:frontend` `area:github-actions`
`area:infrastructure` `area:product` `area:roster-language` `area:tests`

### Planning Labels
`roadmap`
