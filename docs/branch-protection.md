# Branch protection for `main`: the independent approval control

`pr-auto-merge.yml` enforces `reviewDecision == APPROVED` in-workflow before it
will squash-merge a PR or enable GitHub's native auto-merge (see that
workflow's `require_approved` checks). That in-workflow gate is **necessary
but not sufficient** on its own: it relies on GitHub's own `reviewDecision`
field, which only exists, and only ever becomes `APPROVED`, if the
repository's branch protection (or a ruleset) actually requires reviews on
`main`. Without that independent, workflow-external control, `reviewDecision`
would stay `null` for every PR (nobody is required to review), and the
"approved" check in the workflow would never be satisfiable by design, which
is a fail-safe — but it also means the control described in this document
must be configured in the repository itself, not just in workflow code.

## What must be configured outside the workflow

On the `main` branch (via **Settings → Branches → Branch protection rules**,
or the newer **Settings → Rules → Rulesets**), the repository owner must
enable, at minimum:

1. **Require a pull request before merging.**
2. **Require approvals**, with the count set to at least 1.
3. **Dismiss stale pull request approvals when new commits are pushed.**
   This closes the gap the workflow's own `fail_if_head_moved` check and
   repeated `reviewDecision` re-checks are designed to cover from the
   workflow side: without this setting, an approval given on an earlier
   commit could still read as `APPROVED` after the head SHA changes.
4. Optionally, **Require review from Code Owners** — `.github/CODEOWNERS`
   already designates `@RocketDelivery2` as the owner of the entire tree, so
   this mainly matters once there is more than one trusted human reviewer.
5. Apply the rule to administrators too ("Do not allow bypassing the above
   settings" / disable "Allow specified actors to bypass required pull
   requests") so that an autonomous agent with `contents: write` (as
   `pr-auto-merge.yml` has) cannot be merged around this control by anyone,
   including repository admins, without an explicit, auditable exception.

Branch protection and rulesets are **repository settings**, not workflow
YAML; they cannot be expressed in `pr-auto-merge.yml` itself, and this
change does not and cannot modify them. This document exists so that the
in-workflow approval gate added to `pr-auto-merge.yml` is understood as one
half of a two-part control, with the other half living in repository
settings that an administrator must configure and periodically audit.

## Why both halves are required

- **Branch protection alone**, without the in-workflow gate, would still let
  `pr-auto-merge.yml` call `gh pr merge --auto` on any clean PR regardless of
  review state. GitHub's native auto-merge would then wait until branch
  protection's required-reviews condition is satisfied before actually
  merging, but the workflow would already report "enabled auto-merge" in its
  job summary without the approval having happened yet — a silent,
  easy-to-miss dependency on a setting that lives outside this file.
- **The in-workflow gate alone**, without branch protection requiring
  reviews, is not a real control: if no review is ever required for `main`,
  `reviewDecision` can still become `APPROVED` the moment literally anyone
  (including the PR's own author via a second account, or any collaborator
  without restriction) submits a single approving review, because GitHub
  computes `reviewDecision` from whatever review requirement is configured,
  defaulting to "no requirement" when branch protection does not mandate
  reviews.

Both controls must be verified independently; this PR only changes the
workflow half.

## Verifying the configuration

Repository admins can confirm the current state with:

```sh
gh api repos/RocketDelivery2/TeamBuilder/branches/main/protection \
  --jq '{required_approving_review_count: .required_pull_request_reviews.required_approving_review_count, dismiss_stale_reviews: .required_pull_request_reviews.dismiss_stale_reviews, enforce_admins: .enforce_admins.enabled}'
```

or, if the repository uses rulesets instead of classic branch protection:

```sh
gh api repos/RocketDelivery2/TeamBuilder/rulesets --jq '.[] | {name, enforcement, target}'
```

This repository's automation does not have permission to read or change
branch protection or rulesets; confirming and configuring them is a manual
administrative step outside the scope of any PR.
