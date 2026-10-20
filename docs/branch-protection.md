# Branch protection for `main`: required status checks and rulesets

`pr-auto-merge.yml` no longer requires a human-submitted `reviewDecision` of
`APPROVED`. Per an explicit repository-owner decision, the pipeline is now
fully autonomous: it requires a `PASS` result from the independent
`pr-reviewer-agent.yml` Reviewer Agent (see
[`reviewer-agent.md`](./reviewer-agent.md) for the full design and its
honest trust-limitation analysis), plus a clean `mergeStateStatus` (no
conflicts, all required status checks green), before it will squash-merge a
PR or enable GitHub's native auto-merge.

That in-workflow gate is **necessary but not sufficient** on its own: it
relies on `mergeStateStatus` correctly reflecting "all required status
checks have passed," which only happens if branch protection (or a ruleset)
on `main` actually lists the relevant checks as required. Without that
independent, workflow-external configuration, a PR with failing or
incomplete CI could still show a `mergeStateStatus` that doesn't block
`gh pr merge`, because GitHub does not require any particular status check
to pass unless an administrator has configured it to.

## What must be configured outside the workflow

On the `main` branch (via **Settings → Branches → Branch protection rules**,
or the newer **Settings → Rules → Rulesets**), the repository owner must
enable, at minimum:

1. **Require a pull request before merging.**
2. **Require status checks to pass before merging**, with the application CI
   checks required, and — now that review is no longer human-gated —
   strongly recommended to also require the **`Reviewer Agent`** check run
   published by `pr-reviewer-agent.yml`. Marking it required means GitHub
   itself will block a merge if that check is missing, pending, or not a
   `success`, as an independent, workflow-external backstop to the
   in-workflow `reviewer_status_for_sha()` query.
3. **Require branches to be up to date before merging**, if available,
   so a required check's result cannot be reused against a commit other
   than the one it actually evaluated.
4. Apply the rule to administrators too ("Do not allow bypassing the above
   settings" / disable "Allow specified actors to bypass required pull
   requests") so that an autonomous agent with `contents: write` (as
   `pr-auto-merge.yml` has) cannot be merged around this control by anyone,
   including repository admins, without an explicit, auditable exception.
5. **Retain existing GitHub ruleset protections** on `main` exactly as
   configured today (e.g. restrictions on force-pushes, deletions, and
   required linear history, if present) — this change does not touch, and is
   not a substitute for, those rules.

Branch protection and rulesets are **repository settings**, not workflow
YAML; they cannot be expressed in `pr-auto-merge.yml` or
`pr-reviewer-agent.yml` themselves, and this change does not and cannot
modify them. This document exists so the in-workflow Reviewer Agent gate is
understood as one half of a two-part control, with the other half living in
repository settings that an administrator must configure and periodically
audit.

## Why both halves are required

- **Branch protection alone**, without the in-workflow gate, would still let
  `pr-auto-merge.yml` call `gh pr merge --auto` on any PR regardless of the
  Reviewer Agent's result. GitHub's native auto-merge would then wait until
  branch protection's required-status-checks condition is satisfied before
  actually merging (if `Reviewer Agent` is configured as required), but the
  workflow's own `reviewer_status_for_sha()` re-checks are what prevent an
  *immediate* `gh pr merge --squash` (no `--auto`) from happening the instant
  `mergeStateStatus` reads `CLEAN` for any other reason.
- **The in-workflow gate alone**, without `Reviewer Agent` configured as a
  required status check, is not a complete control: `mergeStateStatus` could
  read `CLEAN` even with a failed or missing Reviewer Agent result if nothing
  in branch protection says that check matters, since GitHub computes
  `mergeStateStatus` purely from whatever checks are actually configured as
  required.

Both controls must be verified independently; `pr-auto-merge.yml` and
`pr-reviewer-agent.yml` only change the workflow half.

## Current state of `main` (read 2026-10-20)

The active ruleset is **TeamBuilder - Main Protection** (id `25248011`,
enforcement `active`, no bypass actors reported). It blocks deletion and
force pushes, requires linear history, requires a pull request (0
approving reviews, squash only), requests a Copilot code review on push,
and requires these status checks from GitHub Actions:

- `build` (`.NET` workflow)
- `Markdown lint` (`Markdown` workflow)
- `Reviewer Agent` (posted by `pr-reviewer-agent.yml`; added 2026-10-20)

With `Reviewer Agent` required, GitHub itself blocks a merge while the
Reviewer Agent result for the head commit is missing, `failure` or
`action_required`, and `dependabot-auto-merge.yml` arms native auto-merge
for patch and minor Dependabot updates. The Reviewer Agent fails any
non-owner PR that changes `.github/workflows/` or `scripts/ci/`, which
includes Dependabot's GitHub Actions updates; those need the owner to
review and merge them.

If `Reviewer Agent` is ever removed from the required checks, re-add it in
**Settings → Rules → Rulesets → TeamBuilder - Main Protection → Require
status checks to pass → Add checks** with source **GitHub Actions**.

## Verifying the configuration

Anyone with read access can see the rules that apply to `main`:

```sh
gh api repos/RocketDelivery2/TeamBuilder/rules/branches/main \
  --jq '.[] | select(.type == "required_status_checks") | .parameters.required_status_checks[].context'
```

Listing rulesets with their bypass actors needs administrator access:

```sh
gh api repos/RocketDelivery2/TeamBuilder/rulesets/25248011 --jq '{name, enforcement, bypass_actors}'
```
