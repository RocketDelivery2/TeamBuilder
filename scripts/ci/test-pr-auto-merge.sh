#!/usr/bin/env bash
# Deterministic, mocked-gh test harness for the pr-auto-merge.yml run script.
# Extracts the embedded bash from the workflow YAML and exercises its eligibility
# and merge-decision branches against scripted GraphQL PR-state and Reviewer
# Agent check-run responses.
#
# Usage: scripts/ci/test-pr-auto-merge.sh
# Requires: bash, python3 (standard library only), jq (same tools the workflow itself uses).
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
workflow="$repo_root/.github/workflows/pr-auto-merge.yml"
work_dir="$(mktemp -d)"
trap 'rm -rf "$work_dir"' EXIT

script_path="$work_dir/script.sh"
# Deliberately does not depend on PyYAML (not guaranteed present on every
# runner image): see scripts/ci/lib/extract_run_script.py.
python3 "$repo_root/scripts/ci/lib/extract_run_script.py" "$workflow" "$script_path"

pass=0
fail=0

assert_contains() {
  local haystack="$1" needle="$2" label="$3"
  if [[ "$haystack" == *"$needle"* ]]; then
    echo "  PASS: $label"
    pass=$((pass + 1))
  else
    echo "  FAIL: $label"
    echo "    expected to find: $needle"
    echo "    in: $haystack"
    fail=$((fail + 1))
  fi
}

assert_not_contains() {
  local haystack="$1" needle="$2" label="$3"
  if [[ "$haystack" != *"$needle"* ]]; then
    echo "  PASS: $label"
    pass=$((pass + 1))
  else
    echo "  FAIL: $label (unexpectedly present)"
    echo "    found: $needle"
    echo "    in: $haystack"
    fail=$((fail + 1))
  fi
}

assert_eq() {
  local actual="$1" expected="$2" label="$3"
  if [[ "$actual" == "$expected" ]]; then
    echo "  PASS: $label"
    pass=$((pass + 1))
  else
    echo "  FAIL: $label (expected '$expected', got '$actual')"
    fail=$((fail + 1))
  fi
}

run_scenario() {
  local name="$1"
  shift
  echo "Scenario: $name"

  local mock_dir="$work_dir/mock_$name"
  mkdir -p "$mock_dir"
  rm -f "$mock_dir/merge_calls.log" "$mock_dir/state_idx"

  local summary_file="$work_dir/summary_$name"
  : > "$summary_file"

  local status=0
  PATH="$work_dir/bin:$PATH" \
    MOCK_DIR="$mock_dir" \
    GITHUB_STEP_SUMMARY="$summary_file" \
    "$@" \
    bash "$script_path" || status=$?

  SCENARIO_STATUS="$status"
  SCENARIO_SUMMARY="$(cat "$summary_file" 2>/dev/null || true)"
  SCENARIO_MERGE_CALLS="$(cat "$mock_dir/merge_calls.log" 2>/dev/null || true)"
}

mkdir -p "$work_dir/bin"
cp "$(dirname "${BASH_SOURCE[0]}")/fake_gh.sh" "$work_dir/bin/gh"
chmod +x "$work_dir/bin/gh"

common_env=(
  env
  EVENT_NAME="pull_request_target"
  BASE_REPO="RocketDelivery2/TeamBuilder"
  BASE_REF="main"
  HEAD_REPO="RocketDelivery2/TeamBuilder"
  EVENT_HEAD_SHA="headsha1"
  PR_NUMBER_FROM_EVENT="42"
  AUTHOR="RocketDelivery2"
  DRAFT="false"
  RETRY_DELAY_SECONDS="0"
)

pr_state_json() {
  local head_sha="$1" merge_state="$2" auto_merge_enabled_at="$3"
  local auto_json="null"
  [[ -n "$auto_merge_enabled_at" ]] && auto_json="{\"enabledAt\":\"$auto_merge_enabled_at\"}"
  printf '{"data":{"repository":{"pullRequest":{"id":"PR_kwDO","state":"OPEN","headRefOid":"%s","mergeStateStatus":"%s","autoMergeRequest":%s}}}}\n' \
    "$head_sha" "$merge_state" "$auto_json"
}

# Fixture for the GET repos/.../commits/{sha}/check-runs endpoint consulted
# by reviewer_status_for_sha(). "missing" yields an empty check_runs array.
checkruns_json() {
  local status="$1"
  case "$status" in
    missing)
      echo '{"check_runs":[]}'
      ;;
    pending)
      echo '{"check_runs":[{"id":10,"name":"Reviewer Agent","status":"in_progress","conclusion":null,"completed_at":null,"app":{"slug":"github-actions"},"external_id":"teambuilder-pr-reviewer-agent"}]}'
      ;;
    pass)
      echo '{"check_runs":[{"id":10,"name":"Reviewer Agent","status":"completed","conclusion":"success","completed_at":"2026-01-01T00:00:00Z","app":{"slug":"github-actions"},"external_id":"teambuilder-pr-reviewer-agent"}]}'
      ;;
    fail)
      echo '{"check_runs":[{"id":10,"name":"Reviewer Agent","status":"completed","conclusion":"failure","completed_at":"2026-01-01T00:00:00Z","app":{"slug":"github-actions"},"external_id":"teambuilder-pr-reviewer-agent"}]}'
      ;;
    inconclusive)
      echo '{"check_runs":[{"id":10,"name":"Reviewer Agent","status":"completed","conclusion":"neutral","completed_at":"2026-01-01T00:00:00Z","app":{"slug":"github-actions"},"external_id":"teambuilder-pr-reviewer-agent"}]}'
      ;;
    action_required)
      echo '{"check_runs":[{"id":10,"name":"Reviewer Agent","status":"completed","conclusion":"action_required","started_at":"2026-01-01T00:00:00Z","completed_at":"2026-01-01T00:00:00Z","app":{"slug":"github-actions"},"external_id":"teambuilder-pr-reviewer-agent"}]}'
      ;;
    pass_then_fail)
      # A re-run of the review on the same commit: the newer run decides.
      echo '{"check_runs":[{"id":11,"name":"Reviewer Agent","status":"completed","conclusion":"failure","started_at":"2026-01-01T00:05:00Z","completed_at":"2026-01-01T00:05:00Z","app":{"slug":"github-actions"},"external_id":"teambuilder-pr-reviewer-agent"},{"id":10,"name":"Reviewer Agent","status":"completed","conclusion":"success","started_at":"2026-01-01T00:00:00Z","completed_at":"2026-01-01T00:00:00Z","app":{"slug":"github-actions"},"external_id":"teambuilder-pr-reviewer-agent"}]}'
      ;;
    fail_then_pass)
      echo '{"check_runs":[{"id":10,"name":"Reviewer Agent","status":"completed","conclusion":"failure","started_at":"2026-01-01T00:00:00Z","completed_at":"2026-01-01T00:00:00Z","app":{"slug":"github-actions"},"external_id":"teambuilder-pr-reviewer-agent"},{"id":11,"name":"Reviewer Agent","status":"completed","conclusion":"success","started_at":"2026-01-01T00:05:00Z","completed_at":"2026-01-01T00:05:00Z","app":{"slug":"github-actions"},"external_id":"teambuilder-pr-reviewer-agent"}]}'
      ;;
    pass_wrong_app)
      # Same name and a "success" conclusion, but neither the app.slug nor
      # the external_id pr-reviewer-agent.yml tags its own check runs with
      # match -- i.e. some other check run that merely happens to be named
      # "Reviewer Agent". Must be treated identically to MISSING.
      echo '{"check_runs":[{"id":10,"name":"Reviewer Agent","status":"completed","conclusion":"success","completed_at":"2026-01-01T00:00:00Z","app":{"slug":"some-other-app"},"external_id":"not-ours"}]}'
      ;;
    pass_missing_external_id)
      # Correct app.slug but no (or a mismatched) external_id: still not
      # trusted, since external_id is the tag that is actually under this
      # workflow's exclusive control.
      echo '{"check_runs":[{"id":10,"name":"Reviewer Agent","status":"completed","conclusion":"success","completed_at":"2026-01-01T00:00:00Z","app":{"slug":"github-actions"}}]}'
      ;;
    *)
      echo "unknown checkruns fixture: $status" >&2
      exit 1
      ;;
  esac
}

### Scenario 1: draft PR is skipped before any GitHub API call is made.
run_scenario "draft_pr" "${common_env[@]}" DRAFT="true"
assert_eq "$SCENARIO_STATUS" "0" "draft_pr: exits 0"
assert_contains "$SCENARIO_SUMMARY" "Skipping: draft pull request." "draft_pr: skip message"
assert_eq "$SCENARIO_MERGE_CALLS" "" "draft_pr: no merge calls"

### Scenario 2: fork PR (head repo != base repo) is skipped before any API call.
run_scenario "fork_pr" "${common_env[@]}" HEAD_REPO="someoneelse/TeamBuilder"
assert_eq "$SCENARIO_STATUS" "0" "fork_pr: exits 0"
assert_contains "$SCENARIO_SUMMARY" "fork pull request is not eligible" "fork_pr: skip message"
assert_eq "$SCENARIO_MERGE_CALLS" "" "fork_pr: no merge calls"

### Scenario 3: untrusted author is skipped before any API call.
run_scenario "untrusted_author" "${common_env[@]}" AUTHOR="randomuser"
assert_eq "$SCENARIO_STATUS" "0" "untrusted_author: exits 0"
assert_contains "$SCENARIO_SUMMARY" "not in the trusted actor allowlist" "untrusted_author: skip message"
assert_eq "$SCENARIO_MERGE_CALLS" "" "untrusted_author: no merge calls"

### Scenario 4: trusted author, Reviewer Agent result MISSING (never ran), merge state CLEAN.
### Requirement 6: must NOT merge or enable auto-merge without a PASS result.
mock_dir="$work_dir/mock_reviewer_missing_clean"; mkdir -p "$mock_dir"
pr_state_json "headsha1" "CLEAN" "" > "$mock_dir/states.jsonl"
checkruns_json missing > "$mock_dir/checkruns.json"
run_scenario "reviewer_missing_clean" "${common_env[@]}"
assert_eq "$SCENARIO_STATUS" "0" "reviewer_missing_clean: exits 0"
assert_contains "$SCENARIO_SUMMARY" "Reviewer Agent status is 'MISSING'" "reviewer_missing_clean: skip message mentions MISSING"
assert_eq "$SCENARIO_MERGE_CALLS" "" "reviewer_missing_clean: no merge/auto-merge calls without a PASS review"

### Scenario 5: trusted author, Reviewer Agent still running (PENDING), CLEAN.
mock_dir="$work_dir/mock_reviewer_pending_clean"; mkdir -p "$mock_dir"
pr_state_json "headsha1" "CLEAN" "" > "$mock_dir/states.jsonl"
checkruns_json pending > "$mock_dir/checkruns.json"
run_scenario "reviewer_pending_clean" "${common_env[@]}"
assert_eq "$SCENARIO_STATUS" "0" "reviewer_pending_clean: exits 0"
assert_contains "$SCENARIO_SUMMARY" "Reviewer Agent status is 'PENDING'" "reviewer_pending_clean: skip message mentions PENDING"
assert_eq "$SCENARIO_MERGE_CALLS" "" "reviewer_pending_clean: no merge calls"

### Scenario 6: trusted author, Reviewer Agent result FAIL, CLEAN.
### Requirement 9 ("failed reviews"): an explicit FAIL must never merge.
mock_dir="$work_dir/mock_reviewer_fail_clean"; mkdir -p "$mock_dir"
pr_state_json "headsha1" "CLEAN" "" > "$mock_dir/states.jsonl"
checkruns_json fail > "$mock_dir/checkruns.json"
run_scenario "reviewer_fail_clean" "${common_env[@]}"
assert_eq "$SCENARIO_STATUS" "0" "reviewer_fail_clean: exits 0"
assert_contains "$SCENARIO_SUMMARY" "Reviewer Agent status is 'FAIL'" "reviewer_fail_clean: skip message mentions FAIL"
assert_eq "$SCENARIO_MERGE_CALLS" "" "reviewer_fail_clean: no merge calls"

### Scenario 7: trusted author, Reviewer Agent result INCONCLUSIVE, CLEAN.
mock_dir="$work_dir/mock_reviewer_inconclusive_clean"; mkdir -p "$mock_dir"
pr_state_json "headsha1" "CLEAN" "" > "$mock_dir/states.jsonl"
checkruns_json inconclusive > "$mock_dir/checkruns.json"
run_scenario "reviewer_inconclusive_clean" "${common_env[@]}"
assert_eq "$SCENARIO_STATUS" "0" "reviewer_inconclusive_clean: exits 0"
assert_contains "$SCENARIO_SUMMARY" "Reviewer Agent status is 'INCONCLUSIVE'" "reviewer_inconclusive_clean: skip message mentions INCONCLUSIVE"
assert_eq "$SCENARIO_MERGE_CALLS" "" "reviewer_inconclusive_clean: no merge calls"

### Scenario 8: result turned FAIL after auto-merge had previously been enabled -> disable it.
### Requirement 9 ("stale reviews"): a result that no longer satisfies the gate
### must disable any auto-merge that was armed under a now-invalid assumption.
mock_dir="$work_dir/mock_reviewer_fail_disables_auto_merge"; mkdir -p "$mock_dir"
pr_state_json "headsha1" "CLEAN" "2026-01-01T00:00:00Z" > "$mock_dir/states.jsonl"
checkruns_json fail > "$mock_dir/checkruns.json"
run_scenario "reviewer_fail_disables_auto_merge" "${common_env[@]}"
assert_eq "$SCENARIO_STATUS" "0" "reviewer_fail_disables_auto_merge: exits 0"
assert_contains "$SCENARIO_SUMMARY" "no longer satisfied" "reviewer_fail_disables_auto_merge: skip message"
assert_contains "$SCENARIO_MERGE_CALLS" "--disable-auto" "reviewer_fail_disables_auto_merge: disables stale auto-merge"
assert_not_contains "$SCENARIO_MERGE_CALLS" "--squash --match-head-commit" "reviewer_fail_disables_auto_merge: does not squash-merge"

### Scenario 9: head advanced past a previously-PASSed review that auto-merge
### was relying on; the new head's Reviewer Agent result is MISSING.
### Requirement 9 ("stale reviews"): re-querying by the *current* exact SHA
### (not trusting the earlier enablement) must disable the now-stale auto-merge.
mock_dir="$work_dir/mock_reviewer_stale_after_new_push"; mkdir -p "$mock_dir"
pr_state_json "headsha1" "CLEAN" "2026-01-01T00:00:00Z" > "$mock_dir/states.jsonl"
checkruns_json missing > "$mock_dir/checkruns.json"
run_scenario "reviewer_stale_after_new_push" "${common_env[@]}"
assert_eq "$SCENARIO_STATUS" "0" "reviewer_stale_after_new_push: exits 0"
assert_contains "$SCENARIO_SUMMARY" "Reviewer Agent status is 'MISSING'" "reviewer_stale_after_new_push: skip message mentions MISSING"
assert_contains "$SCENARIO_MERGE_CALLS" "--disable-auto" "reviewer_stale_after_new_push: disables stale auto-merge"

### Scenario 9b: a check run named "Reviewer Agent" exists and conclusion is
### "success", but it was not published by the expected GitHub App (wrong
### app.slug, wrong external_id) -- i.e. not merely identified by check
### name. Must be treated identically to MISSING, never as PASS.
mock_dir="$work_dir/mock_reviewer_wrong_app_origin"; mkdir -p "$mock_dir"
pr_state_json "headsha1" "CLEAN" "" > "$mock_dir/states.jsonl"
checkruns_json pass_wrong_app > "$mock_dir/checkruns.json"
run_scenario "reviewer_wrong_app_origin" "${common_env[@]}"
assert_eq "$SCENARIO_STATUS" "0" "reviewer_wrong_app_origin: exits 0"
assert_contains "$SCENARIO_SUMMARY" "Reviewer Agent status is 'MISSING'" "reviewer_wrong_app_origin: treated as MISSING despite matching name+success"
assert_eq "$SCENARIO_MERGE_CALLS" "" "reviewer_wrong_app_origin: no merge calls"

### Scenario 9c: correct app.slug but missing/mismatched external_id -- the
### app-slug check alone is not sufficient (every workflow in this repo using
### the default token shares that slug); external_id is the specific tag
### only pr-reviewer-agent.yml sets. Must also be treated as MISSING.
mock_dir="$work_dir/mock_reviewer_missing_external_id"; mkdir -p "$mock_dir"
pr_state_json "headsha1" "CLEAN" "" > "$mock_dir/states.jsonl"
checkruns_json pass_missing_external_id > "$mock_dir/checkruns.json"
run_scenario "reviewer_missing_external_id" "${common_env[@]}"
assert_eq "$SCENARIO_STATUS" "0" "reviewer_missing_external_id: exits 0"
assert_contains "$SCENARIO_SUMMARY" "Reviewer Agent status is 'MISSING'" "reviewer_missing_external_id: treated as MISSING without the expected external_id"
assert_eq "$SCENARIO_MERGE_CALLS" "" "reviewer_missing_external_id: no merge calls"

### Scenario 10: trusted author, Reviewer Agent PASS, CLEAN -> immediate squash merge with exact head SHA.
mock_dir="$work_dir/mock_reviewer_pass_clean"; mkdir -p "$mock_dir"
pr_state_json "headsha1" "CLEAN" "" > "$mock_dir/states.jsonl"
checkruns_json pass > "$mock_dir/checkruns.json"
run_scenario "reviewer_pass_clean" "${common_env[@]}"
assert_eq "$SCENARIO_STATUS" "0" "reviewer_pass_clean: exits 0"
assert_contains "$SCENARIO_SUMMARY" "Merged clean PR with squash" "reviewer_pass_clean: merged message"
assert_contains "$SCENARIO_MERGE_CALLS" "--squash --match-head-commit headsha1" "reviewer_pass_clean: squash merge call made"
assert_not_contains "$SCENARIO_MERGE_CALLS" "--auto" "reviewer_pass_clean: does not use --auto for an immediate merge"

### Scenario 11: Reviewer Agent PASS but required CI checks are not yet green
### (mergeStateStatus BLOCKED) -> enable auto-merge rather than merging now;
### GitHub's own required-status-check enforcement still gates the eventual
### merge. Requirement 9 ("missing CI checks").
mock_dir="$work_dir/mock_reviewer_pass_ci_not_passed"; mkdir -p "$mock_dir"
pr_state_json "headsha1" "BLOCKED" "" > "$mock_dir/states.jsonl"
checkruns_json pass > "$mock_dir/checkruns.json"
run_scenario "reviewer_pass_ci_not_passed" "${common_env[@]}"
assert_eq "$SCENARIO_STATUS" "0" "reviewer_pass_ci_not_passed: exits 0"
assert_contains "$SCENARIO_SUMMARY" "Enabled normal squash auto-merge" "reviewer_pass_ci_not_passed: enabled auto-merge message"
assert_contains "$SCENARIO_MERGE_CALLS" "--squash --auto --match-head-commit headsha1" "reviewer_pass_ci_not_passed: auto-merge call made"

### Scenario 12: head moved since the event fired -> must refuse (fail closed), no merge calls.
### Requirement 9 ("head changes").
mock_dir="$work_dir/mock_head_moved"; mkdir -p "$mock_dir"
pr_state_json "headsha2-moved" "CLEAN" "" > "$mock_dir/states.jsonl"
checkruns_json pass > "$mock_dir/checkruns.json"
run_scenario "head_moved" "${common_env[@]}"
assert_eq "$SCENARIO_STATUS" "1" "head_moved: exits non-zero (fails closed)"
assert_eq "$SCENARIO_MERGE_CALLS" "" "head_moved: no merge calls"

### Scenario 13: PR is already auto-merge-enabled and still PASS -> no redundant action.
mock_dir="$work_dir/mock_already_enabled"; mkdir -p "$mock_dir"
pr_state_json "headsha1" "BLOCKED" "2026-01-01T00:00:00Z" > "$mock_dir/states.jsonl"
checkruns_json pass > "$mock_dir/checkruns.json"
run_scenario "already_enabled" "${common_env[@]}"
assert_eq "$SCENARIO_STATUS" "0" "already_enabled: exits 0"
assert_contains "$SCENARIO_SUMMARY" "Auto-merge is already enabled" "already_enabled: no-op message"
assert_eq "$SCENARIO_MERGE_CALLS" "" "already_enabled: no further merge calls"

### Scenarios 14+: workflow_run-triggered re-evaluation, fired when the
### "PR Reviewer Agent" workflow completes. PR context is resolved via the
### REST pulls endpoint since the workflow_run payload has no pull_request.*
### fields.
pr_rest_json() {
  local head_sha="$1" author="${2:-RocketDelivery2}" draft="${3:-false}" head_repo="${4:-RocketDelivery2/TeamBuilder}" state="${5:-open}"
  printf '{"number":42,"state":"%s","draft":%s,"user":{"login":"%s"},"base":{"ref":"main"},"head":{"sha":"%s","repo":{"full_name":"%s"}}}\n' \
    "$state" "$draft" "$author" "$head_sha" "$head_repo"
}

workflow_run_env=(
  env
  EVENT_NAME="workflow_run"
  WORKFLOW_RUN_EVENT="pull_request_target"
  WORKFLOW_RUN_HEAD_SHA="headsha1"
  WORKFLOW_RUN_PR_NUMBER="42"
  BASE_REPO="RocketDelivery2/TeamBuilder"
  RETRY_DELAY_SECONDS="0"
)

### Scenario 14: review completed after the PR was opened (the PR's own
### pull_request_target run saw MISSING); PASS and CLEAN -> immediate merge.
mock_dir="$work_dir/mock_workflow_run_pass"; mkdir -p "$mock_dir"
checkruns_json pass > "$mock_dir/checkruns.json"
pr_rest_json "headsha1" > "$mock_dir/pr_rest.json"
pr_state_json "headsha1" "CLEAN" "" > "$mock_dir/states.jsonl"
run_scenario "workflow_run_pass" "${workflow_run_env[@]}"
assert_eq "$SCENARIO_STATUS" "0" "workflow_run_pass: exits 0"
assert_contains "$SCENARIO_SUMMARY" "Triggering event: workflow_run" "workflow_run_pass: summary names the trigger"
assert_contains "$SCENARIO_SUMMARY" "Merged clean PR with squash" "workflow_run_pass: merged message"
assert_contains "$SCENARIO_MERGE_CALLS" "--squash --match-head-commit headsha1" "workflow_run_pass: exact-head squash merge"

### Scenario 15: review completed but required CI is still running (delayed
### workflow completion) -> arm native auto-merge, which GitHub only
### completes once the required checks pass.
mock_dir="$work_dir/mock_workflow_run_ci_pending"; mkdir -p "$mock_dir"
checkruns_json pass > "$mock_dir/checkruns.json"
pr_rest_json "headsha1" > "$mock_dir/pr_rest.json"
pr_state_json "headsha1" "BLOCKED" "" > "$mock_dir/states.jsonl"
run_scenario "workflow_run_ci_pending" "${workflow_run_env[@]}"
assert_eq "$SCENARIO_STATUS" "0" "workflow_run_ci_pending: exits 0"
assert_contains "$SCENARIO_MERGE_CALLS" "--squash --auto --match-head-commit headsha1" "workflow_run_ci_pending: arms auto-merge for the exact head"
assert_not_contains "$SCENARIO_MERGE_CALLS" "--squash --match-head-commit" "workflow_run_ci_pending: no immediate merge while required CI is pending"

### Scenario 16: the review finished for an older commit (a newer push
### landed meanwhile) -> skip; the newer head's own review run will decide.
mock_dir="$work_dir/mock_workflow_run_stale_head"; mkdir -p "$mock_dir"
checkruns_json pass > "$mock_dir/checkruns.json"
pr_rest_json "headsha2-new" > "$mock_dir/pr_rest.json"
pr_state_json "headsha2-new" "CLEAN" "" > "$mock_dir/states.jsonl"
run_scenario "workflow_run_stale_head" "${workflow_run_env[@]}"
assert_eq "$SCENARIO_STATUS" "0" "workflow_run_stale_head: exits 0"
assert_contains "$SCENARIO_SUMMARY" "but the PR head is now headsha2-new" "workflow_run_stale_head: skip message"
assert_eq "$SCENARIO_MERGE_CALLS" "" "workflow_run_stale_head: no merge calls"

### Scenario 17: Reviewer Agent run not triggered by a pull request.
mock_dir="$work_dir/mock_workflow_run_not_pr"; mkdir -p "$mock_dir"
run_scenario "workflow_run_not_pr" "${workflow_run_env[@]}" WORKFLOW_RUN_EVENT="push"
assert_eq "$SCENARIO_STATUS" "0" "workflow_run_not_pr: exits 0"
assert_contains "$SCENARIO_SUMMARY" "not a pull request" "workflow_run_not_pr: skip message"
assert_eq "$SCENARIO_MERGE_CALLS" "" "workflow_run_not_pr: no merge calls"

### Scenario 18: no PR association in the payload and no open PR has the
### reviewed commit as its head -> skip, fail closed.
mock_dir="$work_dir/mock_workflow_run_no_pr"; mkdir -p "$mock_dir"
echo '[]' > "$mock_dir/commit_pulls.json"
run_scenario "workflow_run_no_pr" "${workflow_run_env[@]}" WORKFLOW_RUN_PR_NUMBER=""
assert_eq "$SCENARIO_STATUS" "0" "workflow_run_no_pr: exits 0"
assert_contains "$SCENARIO_SUMMARY" "not associated with exactly one open pull request" "workflow_run_no_pr: skip message"
assert_eq "$SCENARIO_MERGE_CALLS" "" "workflow_run_no_pr: no merge calls"

### Scenario 19: no PR association in the payload, but exactly one open PR
### has the reviewed commit as its head -> resolved and evaluated normally.
mock_dir="$work_dir/mock_workflow_run_pr_from_commit"; mkdir -p "$mock_dir"
echo '[{"number":42,"state":"open","head":{"sha":"headsha1"},"base":{"repo":{"full_name":"RocketDelivery2/TeamBuilder"}}},{"number":7,"state":"closed","head":{"sha":"headsha1"},"base":{"repo":{"full_name":"RocketDelivery2/TeamBuilder"}}}]' > "$mock_dir/commit_pulls.json"
checkruns_json pass > "$mock_dir/checkruns.json"
pr_rest_json "headsha1" > "$mock_dir/pr_rest.json"
pr_state_json "headsha1" "CLEAN" "" > "$mock_dir/states.jsonl"
run_scenario "workflow_run_pr_from_commit" "${workflow_run_env[@]}" WORKFLOW_RUN_PR_NUMBER=""
assert_eq "$SCENARIO_STATUS" "0" "workflow_run_pr_from_commit: exits 0"
assert_contains "$SCENARIO_SUMMARY" "PR: #42" "workflow_run_pr_from_commit: resolved the open PR"
assert_contains "$SCENARIO_MERGE_CALLS" "pr merge 42 " "workflow_run_pr_from_commit: merges that PR"

### Scenario 20: the PR number cannot be resolved (deleted or inaccessible).
mock_dir="$work_dir/mock_workflow_run_unresolvable_pr"; mkdir -p "$mock_dir"
# Deliberately no pr_rest.json: the mocked API call fails like a 404.
run_scenario "workflow_run_unresolvable_pr" "${workflow_run_env[@]}" WORKFLOW_RUN_PR_NUMBER="999"
assert_eq "$SCENARIO_STATUS" "0" "workflow_run_unresolvable_pr: exits 0"
assert_contains "$SCENARIO_SUMMARY" "could not resolve pull request #999" "workflow_run_unresolvable_pr: skip message"
assert_eq "$SCENARIO_MERGE_CALLS" "" "workflow_run_unresolvable_pr: no merge calls"

### Scenario 21: PR closed before its review finished.
mock_dir="$work_dir/mock_workflow_run_closed_pr"; mkdir -p "$mock_dir"
pr_rest_json "headsha1" "RocketDelivery2" "false" "RocketDelivery2/TeamBuilder" "closed" > "$mock_dir/pr_rest.json"
run_scenario "workflow_run_closed_pr" "${workflow_run_env[@]}"
assert_eq "$SCENARIO_STATUS" "0" "workflow_run_closed_pr: exits 0"
assert_contains "$SCENARIO_SUMMARY" "is no longer open" "workflow_run_closed_pr: skip message"
assert_eq "$SCENARIO_MERGE_CALLS" "" "workflow_run_closed_pr: no merge calls"

### Scenarios 22-24: draft, untrusted author and fork are re-checked from
### fresh PR data on the workflow_run path too, not only on pull_request_target.
mock_dir="$work_dir/mock_workflow_run_draft"; mkdir -p "$mock_dir"
pr_rest_json "headsha1" "RocketDelivery2" "true" > "$mock_dir/pr_rest.json"
run_scenario "workflow_run_draft" "${workflow_run_env[@]}"
assert_contains "$SCENARIO_SUMMARY" "Skipping: draft pull request." "workflow_run_draft: skip message"
assert_eq "$SCENARIO_MERGE_CALLS" "" "workflow_run_draft: no merge calls"

mock_dir="$work_dir/mock_workflow_run_untrusted"; mkdir -p "$mock_dir"
pr_rest_json "headsha1" "dependabot[bot]" > "$mock_dir/pr_rest.json"
run_scenario "workflow_run_untrusted" "${workflow_run_env[@]}"
assert_contains "$SCENARIO_SUMMARY" "author 'dependabot[bot]' is not in the trusted actor allowlist" "workflow_run_untrusted: skip message"
assert_eq "$SCENARIO_MERGE_CALLS" "" "workflow_run_untrusted: no merge calls"

mock_dir="$work_dir/mock_workflow_run_fork"; mkdir -p "$mock_dir"
pr_rest_json "headsha1" "RocketDelivery2" "false" "someoneelse/TeamBuilder" > "$mock_dir/pr_rest.json"
run_scenario "workflow_run_fork" "${workflow_run_env[@]}"
assert_contains "$SCENARIO_SUMMARY" "fork pull request is not eligible" "workflow_run_fork: skip message"
assert_eq "$SCENARIO_MERGE_CALLS" "" "workflow_run_fork: no merge calls"

### Scenario 25: GitHub answers 503 twice, then recovers -> the reads are
### retried and the decision is the same as without the outage.
mock_dir="$work_dir/mock_transient_503"; mkdir -p "$mock_dir"
checkruns_json pass > "$mock_dir/checkruns.json"
pr_rest_json "headsha1" > "$mock_dir/pr_rest.json"
pr_state_json "headsha1" "CLEAN" "" > "$mock_dir/states.jsonl"
run_scenario "transient_503" "${workflow_run_env[@]}" MOCK_FAIL_FIRST_N="2"
assert_eq "$SCENARIO_STATUS" "0" "transient_503: exits 0 after retrying"
assert_contains "$SCENARIO_MERGE_CALLS" "--squash --match-head-commit headsha1" "transient_503: still merges the exact head"

### Scenario 26: the Reviewer Agent result cannot be read at all (sustained
### outage) while auto-merge is armed -> fail the run, make no merge call,
### and do not disarm an auto-merge that a valid review armed.
mock_dir="$work_dir/mock_reviewer_unavailable"; mkdir -p "$mock_dir"
pr_state_json "headsha1" "CLEAN" "2026-01-01T00:00:00Z" > "$mock_dir/states.jsonl"
run_scenario "reviewer_unavailable" "${common_env[@]}" MOCK_CHECK_RUNS_FAIL="1"
assert_eq "$SCENARIO_STATUS" "1" "reviewer_unavailable: exits non-zero"
assert_contains "$SCENARIO_SUMMARY" "Could not read the Reviewer Agent result" "reviewer_unavailable: explains why"
assert_eq "$SCENARIO_MERGE_CALLS" "" "reviewer_unavailable: no merge or disable-auto calls"

### Scenario 27: duplicate Reviewer Agent runs on one commit (a re-run) ->
### the newest run decides, in both directions.
mock_dir="$work_dir/mock_duplicate_pass_then_fail"; mkdir -p "$mock_dir"
pr_state_json "headsha1" "CLEAN" "" > "$mock_dir/states.jsonl"
checkruns_json pass_then_fail > "$mock_dir/checkruns.json"
run_scenario "duplicate_pass_then_fail" "${common_env[@]}"
assert_contains "$SCENARIO_SUMMARY" "Reviewer Agent status is 'FAIL'" "duplicate_pass_then_fail: newest FAIL wins"
assert_eq "$SCENARIO_MERGE_CALLS" "" "duplicate_pass_then_fail: no merge calls"

mock_dir="$work_dir/mock_duplicate_fail_then_pass"; mkdir -p "$mock_dir"
pr_state_json "headsha1" "CLEAN" "" > "$mock_dir/states.jsonl"
checkruns_json fail_then_pass > "$mock_dir/checkruns.json"
run_scenario "duplicate_fail_then_pass" "${common_env[@]}"
assert_contains "$SCENARIO_MERGE_CALLS" "--squash --match-head-commit headsha1" "duplicate_fail_then_pass: newest PASS merges"

### Scenario 28: check-runs are queried by name, so a commit with more
### than one page of check runs cannot hide the Reviewer Agent's run.
assert_contains "$(cat "$mock_dir/checkrun_queries.log")" "check-runs?check_name=Reviewer%20Agent&filter=all&per_page=100" "check-runs query filters by check name"

### Scenario 29: INCONCLUSIVE is posted as action_required -> never merges.
mock_dir="$work_dir/mock_reviewer_action_required"; mkdir -p "$mock_dir"
pr_state_json "headsha1" "CLEAN" "" > "$mock_dir/states.jsonl"
checkruns_json action_required > "$mock_dir/checkruns.json"
run_scenario "reviewer_action_required" "${common_env[@]}"
assert_contains "$SCENARIO_SUMMARY" "Reviewer Agent status is 'INCONCLUSIVE'" "reviewer_action_required: read as INCONCLUSIVE"
assert_eq "$SCENARIO_MERGE_CALLS" "" "reviewer_action_required: no merge calls"

### Scenario 30: PASS but the PR conflicts with main -> say so, no merge calls.
mock_dir="$work_dir/mock_merge_conflict"; mkdir -p "$mock_dir"
pr_state_json "headsha1" "DIRTY" "" > "$mock_dir/states.jsonl"
checkruns_json pass > "$mock_dir/checkruns.json"
run_scenario "merge_conflict" "${common_env[@]}"
assert_eq "$SCENARIO_STATUS" "0" "merge_conflict: exits 0"
assert_contains "$SCENARIO_SUMMARY" "merge conflict" "merge_conflict: skip message"
assert_eq "$SCENARIO_MERGE_CALLS" "" "merge_conflict: no merge calls"

### Static checks on the trigger wiring, which the mocked script cannot see.
### check_run must not come back: the Reviewer Agent's GITHUB_TOKEN check
### run never triggers it, and other apps' check runs shared the concurrency
### group and cancelled real evaluations. The workflow_run filter must name
### the reviewer workflow exactly, or the re-evaluation silently never runs.
echo "Static: trigger wiring"
reviewer_name="$(sed -n 's/^name: //p' "$repo_root/.github/workflows/pr-reviewer-agent.yml")"
auto_merge_yaml="$(cat "$workflow")"
assert_not_contains "$auto_merge_yaml" $'\n  check_run:' "pr-auto-merge.yml does not trigger on check_run"
assert_contains "$auto_merge_yaml" $'  workflow_run:\n    workflows:\n      - '"$reviewer_name"$'\n' "pr-auto-merge.yml triggers on completion of '$reviewer_name'"
assert_contains "$auto_merge_yaml" "github.event.workflow_run.pull_requests[0].number || github.event.workflow_run.head_sha" "concurrency group keys workflow_run events by PR number, else by reviewed commit"

echo
echo "Results: $pass passed, $fail failed"
if [[ "$fail" -gt 0 ]]; then
  exit 1
fi
