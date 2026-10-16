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
      echo '{"check_runs":[{"name":"Reviewer Agent","status":"in_progress","conclusion":null,"completed_at":null}]}'
      ;;
    pass)
      echo '{"check_runs":[{"name":"Reviewer Agent","status":"completed","conclusion":"success","completed_at":"2026-01-01T00:00:00Z"}]}'
      ;;
    fail)
      echo '{"check_runs":[{"name":"Reviewer Agent","status":"completed","conclusion":"failure","completed_at":"2026-01-01T00:00:00Z"}]}'
      ;;
    inconclusive)
      echo '{"check_runs":[{"name":"Reviewer Agent","status":"completed","conclusion":"neutral","completed_at":"2026-01-01T00:00:00Z"}]}'
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

### Scenario 14: check_run-triggered re-evaluation (Reviewer Agent just completed).
### PR context is resolved via `gh pr view` since the check_run payload has no
### pull_request.* fields; result is PASS and CLEAN -> immediate merge.
mock_dir="$work_dir/mock_check_run_trigger_pass"; mkdir -p "$mock_dir"
checkruns_json pass > "$mock_dir/checkruns.json"
cat > "$mock_dir/pr_view.json" <<'JSON'
{"baseRefName":"main","headRepositoryOwner":{"login":"RocketDelivery2"},"headRepository":{"name":"TeamBuilder"},"headRefOid":"headsha1","isDraft":false,"author":{"login":"RocketDelivery2"}}
JSON
pr_state_json "headsha1" "CLEAN" "" > "$mock_dir/states.jsonl"
run_scenario "check_run_trigger_pass" \
  env \
  EVENT_NAME="check_run" \
  CHECK_RUN_NAME="Reviewer Agent" \
  CHECK_RUN_PR_NUMBER="42" \
  BASE_REPO="RocketDelivery2/TeamBuilder" \
  EVENT_HEAD_SHA="headsha1"
assert_eq "$SCENARIO_STATUS" "0" "check_run_trigger_pass: exits 0"
assert_contains "$SCENARIO_SUMMARY" "Merged clean PR with squash" "check_run_trigger_pass: merged message"
assert_contains "$SCENARIO_MERGE_CALLS" "--squash --match-head-commit headsha1" "check_run_trigger_pass: squash merge call made"

### Scenario 15: check_run event for an unrelated check -> skip without resolving PR context.
mock_dir="$work_dir/mock_check_run_trigger_unrelated"; mkdir -p "$mock_dir"
run_scenario "check_run_trigger_unrelated" \
  env \
  EVENT_NAME="check_run" \
  CHECK_RUN_NAME="Some Other Check" \
  CHECK_RUN_PR_NUMBER="42" \
  BASE_REPO="RocketDelivery2/TeamBuilder"
assert_eq "$SCENARIO_STATUS" "0" "check_run_trigger_unrelated: exits 0"
assert_contains "$SCENARIO_SUMMARY" "not Reviewer Agent" "check_run_trigger_unrelated: skip message"
assert_eq "$SCENARIO_MERGE_CALLS" "" "check_run_trigger_unrelated: no merge calls"

echo
echo "Results: $pass passed, $fail failed"
if [[ "$fail" -gt 0 ]]; then
  exit 1
fi
