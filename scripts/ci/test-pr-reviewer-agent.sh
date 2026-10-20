#!/usr/bin/env bash
# Deterministic, mocked-gh test harness for the pr-reviewer-agent.yml run
# script. Extracts the embedded bash and exercises its PASS/FAIL/INCONCLUSIVE
# decision logic against scripted GitHub compare-API responses.
#
# Usage: scripts/ci/test-pr-reviewer-agent.sh
# Requires: bash, python3 (standard library only), jq.
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
workflow="$repo_root/.github/workflows/pr-reviewer-agent.yml"
work_dir="$(mktemp -d)"
trap 'rm -rf "$work_dir"' EXIT

script_path="$work_dir/script.sh"
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
  rm -f "$mock_dir/checkrun_calls.log"

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
  SCENARIO_CHECKRUN_CALLS="$(cat "$mock_dir/checkrun_calls.log" 2>/dev/null || true)"
}

mkdir -p "$work_dir/bin"
cp "$(dirname "${BASH_SOURCE[0]}")/fake_gh.sh" "$work_dir/bin/gh"
chmod +x "$work_dir/bin/gh"

common_env=(
  env
  BASE_REPO="RocketDelivery2/TeamBuilder"
  BASE_SHA="basesha1"
  HEAD_SHA="headsha1"
  AUTHOR="Copilot"
  PR_NUMBER="42"
  RUN_URL="https://github.com/RocketDelivery2/TeamBuilder/actions/runs/1"
  RETRY_DELAY_SECONDS="0"
)

compare_file() {
  local files_json="$1"
  printf '{"files":%s}\n' "$files_json"
}

### Scenario 1: ordinary application-code diff from a trusted automated
### author, no secret-like patterns, modest size -> PASS.
mock_dir="$work_dir/mock_clean_pass"; mkdir -p "$mock_dir"
compare_file '[{"filename":"src/Api/Controllers/TeamsController.cs","status":"modified","additions":10,"deletions":2,"patch":"+ var x = 1;"}]' \
  > "$mock_dir/compare.json"
run_scenario "clean_pass" "${common_env[@]}"
assert_eq "$SCENARIO_STATUS" "0" "clean_pass: exits 0"
assert_contains "$SCENARIO_SUMMARY" "Result: **PASS**" "clean_pass: PASS result"
assert_contains "$SCENARIO_CHECKRUN_CALLS" "-f conclusion=success" "clean_pass: posts success conclusion"
assert_contains "$SCENARIO_CHECKRUN_CALLS" "-f head_sha=headsha1" "clean_pass: check run scoped to exact head SHA"
assert_contains "$SCENARIO_CHECKRUN_CALLS" "-f external_id=teambuilder-pr-reviewer-agent" "clean_pass: check run tagged with the expected external_id so pr-auto-merge.yml can verify origin, not just name"

### Scenario 2: a non-owner author's diff touches workflow/control-plane
### files -> automatic FAIL. This is the concrete control against an agent
### manipulating its own review or merge logic (requirement: prevent
### self-manipulation of review results or bypassing the merge gate).
mock_dir="$work_dir/mock_control_plane_fail"; mkdir -p "$mock_dir"
compare_file '[{"filename":".github/workflows/pr-auto-merge.yml","status":"modified","additions":5,"deletions":1,"patch":"+ require_approved"}]' \
  > "$mock_dir/compare.json"
run_scenario "control_plane_fail" "${common_env[@]}" AUTHOR="Copilot"
assert_eq "$SCENARIO_STATUS" "0" "control_plane_fail: exits 0"
assert_contains "$SCENARIO_SUMMARY" "Result: **FAIL**" "control_plane_fail: FAIL result"
assert_contains "$SCENARIO_SUMMARY" "control-plane files" "control_plane_fail: explains control-plane reason"
assert_contains "$SCENARIO_CHECKRUN_CALLS" "-f conclusion=failure" "control_plane_fail: posts failure conclusion"

### Scenario 3: the repository owner themselves modifying workflow files is
### not treated as self-manipulation -> PASS (assuming no other issue).
mock_dir="$work_dir/mock_owner_control_plane_pass"; mkdir -p "$mock_dir"
compare_file '[{"filename":".github/workflows/pr-auto-merge.yml","status":"modified","additions":5,"deletions":1,"patch":"+ require_approved"}]' \
  > "$mock_dir/compare.json"
run_scenario "owner_control_plane_pass" "${common_env[@]}" AUTHOR="RocketDelivery2"
assert_eq "$SCENARIO_STATUS" "0" "owner_control_plane_pass: exits 0"
assert_contains "$SCENARIO_SUMMARY" "Result: **PASS**" "owner_control_plane_pass: PASS result"

### Scenario 4: diff contains a likely committed AWS access key -> FAIL.
mock_dir="$work_dir/mock_secret_fail"; mkdir -p "$mock_dir"
compare_file '[{"filename":"src/Api/appsettings.json","status":"modified","additions":1,"deletions":0,"patch":"+ \"Key\": \"AKIAABCDEFGHIJKLMNOP\""}]' \
  > "$mock_dir/compare.json"
run_scenario "secret_fail" "${common_env[@]}"
assert_eq "$SCENARIO_STATUS" "0" "secret_fail: exits 0"
assert_contains "$SCENARIO_SUMMARY" "Result: **FAIL**" "secret_fail: FAIL result"
assert_contains "$SCENARIO_SUMMARY" "committed secret pattern" "secret_fail: explains secret reason"

### Scenario 5: a very large diff cannot be vouched for deterministically ->
### INCONCLUSIVE (routed to a human) rather than a blind PASS.
mock_dir="$work_dir/mock_large_diff_inconclusive"; mkdir -p "$mock_dir"
large_files="$(python3 -c '
import json
print(json.dumps([
    {"filename": f"src/f{i}.cs", "status": "modified", "additions": 100, "deletions": 0, "patch": "+ noop"}
    for i in range(50)
]))
')"
compare_file "$large_files" > "$mock_dir/compare.json"
run_scenario "large_diff_inconclusive" "${common_env[@]}"
assert_eq "$SCENARIO_STATUS" "0" "large_diff_inconclusive: exits 0"
assert_contains "$SCENARIO_SUMMARY" "Result: **INCONCLUSIVE**" "large_diff_inconclusive: INCONCLUSIVE result"
# action_required, not neutral: a ruleset that requires this check treats
# neutral as passing, which would let INCONCLUSIVE satisfy it.
assert_contains "$SCENARIO_CHECKRUN_CALLS" "-f conclusion=action_required" "large_diff_inconclusive: posts action_required conclusion"

### Scenario 6: GitHub answers 503 to the compare call and to the first
### check-run post -> both are retried and the result is still published.
### Without a posted check run the merge gate would see MISSING forever.
mock_dir="$work_dir/mock_transient_503"; mkdir -p "$mock_dir"
compare_file '[{"filename":"docs/readme.md","status":"modified","additions":1,"deletions":0,"patch":"+ text"}]' \
  > "$mock_dir/compare.json"
run_scenario "transient_503" "${common_env[@]}" MOCK_FAIL_FIRST_N="2" MOCK_POST_FAIL_FIRST_N="1"
assert_eq "$SCENARIO_STATUS" "0" "transient_503: exits 0 after retrying"
assert_contains "$SCENARIO_CHECKRUN_CALLS" "-f conclusion=success" "transient_503: PASS still posted"
assert_contains "$SCENARIO_CHECKRUN_CALLS" "-f details_url=https://github.com/RocketDelivery2/TeamBuilder/actions/runs/1" "transient_503: check run links to the run that produced it"

### Scenario 7: compare keeps failing -> the job fails visibly instead of
### posting any result.
mock_dir="$work_dir/mock_compare_down"; mkdir -p "$mock_dir"
compare_file '[]' > "$mock_dir/compare.json"
run_scenario "compare_down" "${common_env[@]}" MOCK_FAIL_FIRST_N="10"
assert_eq "$SCENARIO_STATUS" "1" "compare_down: exits non-zero"
assert_eq "$SCENARIO_CHECKRUN_CALLS" "" "compare_down: posts no check run"

echo
echo "Results: $pass passed, $fail failed"
if [[ "$fail" -gt 0 ]]; then
  exit 1
fi
