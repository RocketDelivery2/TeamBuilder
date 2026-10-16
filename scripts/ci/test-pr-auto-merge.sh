#!/usr/bin/env bash
# Deterministic, mocked-gh test harness for the pr-auto-merge.yml run script.
# Extracts the embedded bash from the workflow YAML and exercises its eligibility
# and merge-decision branches against scripted GraphQL PR-state responses.
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
# runner image): the workflow's single `run: |` block has a known, fixed
# indentation, so a literal block-scalar extraction is sufficient and exact.
python3 - "$workflow" "$script_path" <<'PY'
import sys

workflow_path, out_path = sys.argv[1], sys.argv[2]
with open(workflow_path) as f:
    lines = f.readlines()

start = next(i for i, line in enumerate(lines) if line.strip() == "run: |")
marker_indent = len(lines[start]) - len(lines[start].lstrip(" "))

body = []
for line in lines[start + 1:]:
    if line.strip() == "":
        body.append("")
        continue
    indent = len(line) - len(line.lstrip(" "))
    if indent <= marker_indent:
        break
    body.append(line)

# Dedent by the indentation of the first non-blank body line.
first_indent = next(len(l) - len(l.lstrip(" ")) for l in body if l.strip() != "")
script = "".join(l[first_indent:] if l.strip() != "" else "\n" for l in body)

with open(out_path, "w") as f:
    f.write("#!/usr/bin/env bash\n")
    f.write(script)
PY

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
  BASE_REPO="RocketDelivery2/TeamBuilder"
  BASE_REF="main"
  HEAD_REPO="RocketDelivery2/TeamBuilder"
  EVENT_HEAD_SHA="headsha1"
  PR_NUMBER="42"
  AUTHOR="RocketDelivery2"
  DRAFT="false"
)

pr_state_json() {
  local head_sha="$1" merge_state="$2" review_decision="$3" auto_merge_enabled_at="$4"
  local review_json="null"
  [[ -n "$review_decision" ]] && review_json="\"$review_decision\""
  local auto_json="null"
  [[ -n "$auto_merge_enabled_at" ]] && auto_json="{\"enabledAt\":\"$auto_merge_enabled_at\"}"
  printf '{"data":{"repository":{"pullRequest":{"id":"PR_kwDO","state":"OPEN","headRefOid":"%s","mergeStateStatus":"%s","reviewDecision":%s,"autoMergeRequest":%s}}}}\n' \
    "$head_sha" "$merge_state" "$review_json" "$auto_json"
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

### Scenario 4: trusted author, no review yet (reviewDecision null), merge state CLEAN.
### Requirement 3: must NOT merge or enable auto-merge without an approval.
mock_dir="$work_dir/mock_no_review_clean"; mkdir -p "$mock_dir"
pr_state_json "headsha1" "CLEAN" "" "" > "$mock_dir/states.jsonl"
run_scenario "no_review_clean" "${common_env[@]}"
assert_eq "$SCENARIO_STATUS" "0" "no_review_clean: exits 0"
assert_contains "$SCENARIO_SUMMARY" "not APPROVED" "no_review_clean: skip message mentions APPROVED"
assert_eq "$SCENARIO_MERGE_CALLS" "" "no_review_clean: no merge/auto-merge calls without approval"

### Scenario 5: trusted author, REVIEW_REQUIRED (requested but not yet approved), CLEAN.
mock_dir="$work_dir/mock_review_required_clean"; mkdir -p "$mock_dir"
pr_state_json "headsha1" "CLEAN" "REVIEW_REQUIRED" "" > "$mock_dir/states.jsonl"
run_scenario "review_required_clean" "${common_env[@]}"
assert_eq "$SCENARIO_STATUS" "0" "review_required_clean: exits 0"
assert_contains "$SCENARIO_SUMMARY" "not APPROVED" "review_required_clean: skip message mentions APPROVED"
assert_eq "$SCENARIO_MERGE_CALLS" "" "review_required_clean: no merge calls"

### Scenario 6: trusted author, CHANGES_REQUESTED, auto-merge previously enabled -> disable it.
mock_dir="$work_dir/mock_changes_requested"; mkdir -p "$mock_dir"
pr_state_json "headsha1" "CLEAN" "CHANGES_REQUESTED" "2026-01-01T00:00:00Z" > "$mock_dir/states.jsonl"
run_scenario "changes_requested" "${common_env[@]}"
assert_eq "$SCENARIO_STATUS" "0" "changes_requested: exits 0"
assert_contains "$SCENARIO_SUMMARY" "reviewDecision is CHANGES_REQUESTED" "changes_requested: skip message"
assert_contains "$SCENARIO_MERGE_CALLS" "--disable-auto" "changes_requested: disables existing auto-merge"
assert_not_contains "$SCENARIO_MERGE_CALLS" "--squash --match-head-commit" "changes_requested: does not squash-merge"

### Scenario 7: approval dismissed after auto-merge was enabled (REVIEW_REQUIRED, auto-merge on).
### Requirement 4: approval is re-enforced against current state, not just a stale earlier check.
mock_dir="$work_dir/mock_dismissed_approval"; mkdir -p "$mock_dir"
pr_state_json "headsha1" "CLEAN" "REVIEW_REQUIRED" "2026-01-01T00:00:00Z" > "$mock_dir/states.jsonl"
run_scenario "dismissed_approval" "${common_env[@]}"
assert_eq "$SCENARIO_STATUS" "0" "dismissed_approval: exits 0"
assert_contains "$SCENARIO_SUMMARY" "approval requirement is no longer satisfied" "dismissed_approval: skip message"
assert_contains "$SCENARIO_MERGE_CALLS" "--disable-auto" "dismissed_approval: disables stale auto-merge"

### Scenario 8: trusted author, APPROVED, CLEAN -> immediate squash merge with exact head SHA.
mock_dir="$work_dir/mock_approved_clean"; mkdir -p "$mock_dir"
pr_state_json "headsha1" "CLEAN" "APPROVED" "" > "$mock_dir/states.jsonl"
run_scenario "approved_clean" "${common_env[@]}"
assert_eq "$SCENARIO_STATUS" "0" "approved_clean: exits 0"
assert_contains "$SCENARIO_SUMMARY" "Merged clean PR with squash" "approved_clean: merged message"
assert_contains "$SCENARIO_MERGE_CALLS" "--squash --match-head-commit headsha1" "approved_clean: squash merge call made"
assert_not_contains "$SCENARIO_MERGE_CALLS" "--auto" "approved_clean: does not use --auto for an immediate merge"

### Scenario 9: trusted author, APPROVED, not yet mergeable (BEHIND) -> enable auto-merge (not immediate merge).
mock_dir="$work_dir/mock_approved_behind"; mkdir -p "$mock_dir"
pr_state_json "headsha1" "BEHIND" "APPROVED" "" > "$mock_dir/states.jsonl"
run_scenario "approved_behind" "${common_env[@]}"
assert_eq "$SCENARIO_STATUS" "0" "approved_behind: exits 0"
assert_contains "$SCENARIO_SUMMARY" "Enabled normal squash auto-merge" "approved_behind: enabled auto-merge message"
assert_contains "$SCENARIO_MERGE_CALLS" "--squash --auto --match-head-commit headsha1" "approved_behind: auto-merge call made"

### Scenario 10: head moved since the event fired -> must refuse (fail closed), no merge calls.
mock_dir="$work_dir/mock_head_moved"; mkdir -p "$mock_dir"
pr_state_json "headsha2-moved" "CLEAN" "APPROVED" "" > "$mock_dir/states.jsonl"
run_scenario "head_moved" "${common_env[@]}"
assert_eq "$SCENARIO_STATUS" "1" "head_moved: exits non-zero (fails closed)"
assert_eq "$SCENARIO_MERGE_CALLS" "" "head_moved: no merge calls"

### Scenario 11: PR is already auto-merge-enabled and still APPROVED -> no redundant action.
mock_dir="$work_dir/mock_already_enabled"; mkdir -p "$mock_dir"
pr_state_json "headsha1" "BEHIND" "APPROVED" "2026-01-01T00:00:00Z" > "$mock_dir/states.jsonl"
run_scenario "already_enabled" "${common_env[@]}"
assert_eq "$SCENARIO_STATUS" "0" "already_enabled: exits 0"
assert_contains "$SCENARIO_SUMMARY" "Auto-merge is already enabled" "already_enabled: no-op message"
assert_eq "$SCENARIO_MERGE_CALLS" "" "already_enabled: no further merge calls"

echo
echo "Results: $pass passed, $fail failed"
if [[ "$fail" -gt 0 ]]; then
  exit 1
fi
