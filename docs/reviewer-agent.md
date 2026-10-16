# The Reviewer Agent: design, guarantees, and honest limitations

This document describes `pr-reviewer-agent.yml`, the independent automated
reviewer that `pr-auto-merge.yml` now requires a `PASS` result from before it
will squash-merge a PR or enable GitHub's native auto-merge. It replaces the
mandatory-human-`reviewDecision == APPROVED` gate that an earlier revision of
this PR introduced, per an explicit repository-owner decision to run a fully
autonomous, policy-controlled pipeline instead of requiring human approval.

**Read this whole document, especially "What this does *not* guarantee"
below, before relying on this as a security control.** It is a genuine,
partial mitigation, not a cryptographically tamper-proof one.

## What the Reviewer Agent is

- A separate workflow file, `pr-reviewer-agent.yml`, triggered by
  `pull_request_target` on the same lifecycle events as `pr-auto-merge.yml`
  (`opened`, `reopened`, `synchronize`, `ready_for_review`).
- It fetches the diff between the PR's base and head commits via the GitHub
  REST **compare API** (`repos/{owner}/{repo}/compare/{base_sha}...{head_sha}`),
  not "the PR's current files" — compare is pinned to two immutable commit
  SHAs, so the reviewed diff can never silently drift to a different commit
  after the fact.
- It runs purely **deterministic, non-LLM policy checks** over that diff (see
  below) and maps the result to `PASS`, `FAIL`, or `INCONCLUSIVE`.
- It publishes the result as a GitHub Check Run named `Reviewer Agent`,
  scoped to the exact head SHA, with conclusion `success` / `failure` /
  `neutral` respectively. The check run's summary text explicitly states:
  **"NOT a GitHub-approved human review."** This is deliberate: an AI-
  generated automated check must never be presented or interpreted as an
  "approved" GitHub pull request review, and nothing in this design creates
  or submits an actual PR review.

## The deterministic checks it performs

1. **Control-plane path isolation.** If the diff touches
   `.github/workflows/**` or `scripts/ci/**` and the PR author is not the
   repository owner (`RocketDelivery2`), the result is an automatic `FAIL`.
   This is the concrete, enforceable answer to "prevent agents from
   manipulating their own review results or bypassing the merge gate": an
   autonomous agent's own PR cannot alter the review or merge logic and
   still receive a passing review for that change.
2. **Secret-pattern heuristic.** The diff's patch text is scanned for a small
   set of simple, high-confidence secret patterns (AWS access key IDs, PEM
   private-key headers, Slack tokens). Any match is a `FAIL`.
3. **Diff-size heuristic.** A diff touching more than 40 files or more than
   4000 changed lines cannot be meaningfully vouched for by the checks above,
   so it is routed to `INCONCLUSIVE` (which the merge gate treats the same as
   `FAIL` — never merge) rather than a blind `PASS`.

These checks are intentionally simple and auditable. No model or external
service is called, and no code from the PR is ever executed — both choices
avoid introducing a prompt-injection or arbitrary-code-execution surface
driven by attacker-controlled diff content.

## The merge gate (`pr-auto-merge.yml`)

`require_approved()` (GraphQL `reviewDecision`) has been replaced with a
check that queries `GET repos/{repo}/commits/{head_sha}/check-runs`, filters
for runs where `name == "Reviewer Agent"` **and** `app.slug == "github-actions"`
**and** `external_id == "teambuilder-pr-reviewer-agent"`, takes the most
recently completed matching run, and requires
`status == "completed" && conclusion == "success"`. Anything else —
no matching check run (`MISSING`), a check run with a matching name but the
wrong app/external_id (also treated as `MISSING` — see below), still running
(`PENDING`), `failure` (`FAIL`), or `neutral` (`INCONCLUSIVE`) — is treated
identically: refuse to merge or enable auto-merge, and disable any auto-merge
that was previously armed, since its passing result no longer applies to the
current state.

Filtering by `app.slug`/`external_id`, not just `name`, matters because check
run *names* are just strings: any GitHub App or integration with `checks:
write` on this repository could, in principle, create a check run also named
"Reviewer Agent". Requiring `app.slug == "github-actions"` restricts matches
to check runs created by a GitHub Actions run in this repository using the
default token (only GitHub Apps can create check runs at all — not plain
PATs or OAuth tokens). Requiring the specific `external_id` that
`pr-reviewer-agent.yml` sets on every check run it posts narrows this further
still, ruling out any other, unrelated GitHub Actions workflow in this
repository that happens to also create a check run literally named "Reviewer
Agent". See "What this does *not* guarantee" below for what this
origin-check still cannot rule out.

This query is made **by the exact current head SHA, every time, at every
decision point** (mirroring the existing re-check-before-each-decision
pattern this workflow already used for `reviewDecision`). That is what makes
the gate immune to staleness by construction: a check run created for an
older commit is simply never returned when the query is made for a newer
one. There is no separate "is this review stale" timestamp check needed,
because "stale" and "wrong SHA" are the same condition here.

Because `pull_request_target` events don't fire again once the Reviewer
Agent finishes (that's a `check_run` event, a different trigger type),
`pr-auto-merge.yml` also listens for `check_run: types: [completed]` so the
merge gate re-evaluates once a passing (or failing) Reviewer Agent result is
available. That event's `check_run.pull_requests[0].number` field is used
**only** to resolve which PR to re-evaluate — every other fact needed to
make the merge decision (base ref, head repo, author, draft state, and
critically the *current* head SHA) is re-resolved fresh via
`gh pr view $PR_NUMBER --json ...` rather than trusted from the triggering
payload, and the Reviewer Agent status itself is independently re-queried by
that freshly-resolved head SHA (never from the `check_run` payload's own
`head_sha` field). If that PR number is absent (the check run is not
associated with any open PR) or cannot be resolved (`gh pr view` fails,
e.g. the PR was closed between the check run completing and this workflow
running), the event is skipped with no merge calls made — fail closed, not
fail open.

Existing safeguards are unchanged and still enforced at the same decision
points: draft PRs, forked PRs, untrusted authors, `fail_if_head_moved`
(refuses to act if the head SHA has moved since the triggering event),
`mergeStateStatus` (GitHub's own merge-conflict and **required-status-check**
state, which is how CI success continues to be enforced), and
`--match-head-commit` on every merge call as a final belt-and-braces check.

## Separation of duties between the two workflows

| | `pr-auto-merge.yml` | `pr-reviewer-agent.yml` |
|---|---|---|
| Can write repository contents / merge PRs | Yes (`contents: write`) | **No** (`contents: read` only) |
| Can create or alter Check Runs | **No** (no `checks:` permission at all) | Yes (`checks: write`) |

Each workflow has only the permission the other needs to be unable to forge.
`pr-auto-merge.yml` cannot create a passing "Reviewer Agent" check run itself
even if its own logic were somehow bypassed, because it has no `checks:`
permission. `pr-reviewer-agent.yml` cannot merge a PR or alter its state even
if its own review logic were bypassed, because it has no `contents:` or
`pull-requests:` write permission. Because both are `pull_request_target`
workflows, GitHub also runs **the base branch's copy** of each file for the
run that evaluates a given PR, not the PR's own modified copy — so a PR
cannot grant itself expanded permissions in either workflow for the run that
judges it.

## What this does *not* guarantee (the honest limitation)

The `app.slug == "github-actions"` and `external_id ==
"teambuilder-pr-reviewer-agent"` check described above is a **real, meaningful
mitigation against external impersonation and accidental collisions** — it
rules out a check run from a different integration, a different repository's
workflow, or an unrelated tool that happens to choose the same display name.
It is **not** a mitigation against a modified copy of
`pr-reviewer-agent.yml` itself (or any other workflow file added to this
repository) deliberately setting the same `external_id` and relying on the
same shared `github-actions` App identity — see the token-sharing limitation
below, which this check does not remove.

Every workflow in this repository that uses the default `GITHUB_TOKEN`
authenticates as the **same `github-actions[bot]` App installation**. There
is no cryptographic or identity-based separation between "the workflow that
authored/reviewed code" and "the workflow that merges it" beyond the
permission-scoping described above. Permission scoping is a real, meaningful
control — a workload with `checks: write` but not `contents: write` provably
cannot merge a PR via the GitHub API, no matter what logic runs inside it —
but it is an authorization boundary, not a cryptographic identity boundary.
If a future change ever granted `pr-reviewer-agent.yml` additional
permissions it doesn't currently have (e.g. `contents: write`, to "simplify"
some unrelated task), that boundary would quietly disappear. Reviewers of
future changes to these two workflow files should treat any permission grant
beyond what's listed above as a change that needs explicit security review.

This repository's automation (this agent) has no tool or credential to
provision a distinct GitHub App with its own private key, or a dedicated PAT,
scoped only to posting Reviewer Agent check runs. That would be the
strongest available mitigation — a credential a compromised or malicious
`pr-auto-merge.yml` run could not use even if it tried, because it would
simply never have access to it. **This is the scoped follow-up this PR is
deliberately leaving for a repository administrator**, rather than silently
asserting independence that cannot currently be proven:

> **Follow-up (requires repository/organization admin action, not available
> to this agent):** Provision a dedicated GitHub App (or fine-grained PAT)
> with only `checks: write` on this repository, store its credential as a
> protected secret usable only by `pr-reviewer-agent.yml`, and have that
> workflow authenticate with it instead of `${{ github.token }}`. This would
> remove the shared-bot-identity limitation entirely and make the reviewer's
> independence a property enforced by GitHub's credential system rather than
> by this repository's own, self-modifiable workflow permissions.

## Branch protection / rulesets still matter

See [`branch-protection.md`](./branch-protection.md) for what a repository
administrator should configure on `main` (required status checks including
`Reviewer Agent`, and retained GitHub ruleset protections) as the
repository-settings half of this control — something this automation cannot
read or modify itself.
