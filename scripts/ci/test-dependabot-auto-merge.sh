#!/usr/bin/env bash
# Mocked-gh tests for the Dependabot auto-merge workflow's GraphQL result handling.
# Usage: scripts/ci/test-dependabot-auto-merge.sh
# Requires bash, python3 (standard library only), and jq.
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
workflow="$repo_root/.github/workflows/dependabot-auto-merge.yml"
work_dir="$(mktemp -d)"
trap 'rm -rf "$work_dir"' EXIT

script_path="$work_dir/script.sh"
python3 "$repo_root/scripts/ci/lib/extract_run_script.py" "$workflow" "$script_path"
mkdir -p "$work_dir/bin"

cat >"$work_dir/bin/gh" <<'MOCK_GH'
#!/usr/bin/env bash
set -euo pipefail

if [[ "$1" != "api" ]]; then
  echo "Unexpected gh command: $*" >&2
  exit 2
fi

if [[ "$2" == "graphql" ]]; then
  query=""
  for argument in "$@"; do
    if [[ "$argument" == query=* ]]; then
      query="${argument#query=}"
    fi
  done

  if [[ "$query" == mutation* ]]; then
    printf '%s\n' "$MOCK_MUTATION_RESPONSE"
    exit 0
  fi

  count_file="$MOCK_DIR/graphql_read_count"
  count=0
  [[ -f "$count_file" ]] && count="$(cat "$count_file")"
  count=$((count + 1))
  printf '%s\n' "$count" >"$count_file"

  if [[ "$count" == "1" ]]; then
    printf '%s\n' '{"data":{"repository":{"pullRequest":{"id":"PR_kwDO","autoMergeRequest":null}}}}'
  elif [[ "$MOCK_READBACK_MODE" == "never" || ( "$MOCK_READBACK_MODE" == "late" && "$count" == "2" ) ]]; then
    printf '%s\n' '{"data":{"repository":{"pullRequest":{"autoMergeRequest":null}}}}'
  else
    printf '%s\n' '{"data":{"repository":{"pullRequest":{"autoMergeRequest":{"enabledAt":"2026-10-20T12:00:00Z"}}}}}'
  fi
  exit 0
fi

if [[ "$2" == "repos/$REPO/rules/branches/main" ]]; then
  echo true
  exit 0
fi

echo "Unexpected gh API request: $*" >&2
exit 2
MOCK_GH
chmod +x "$work_dir/bin/gh"

pass=0
fail=0

assert_contains() {
  local haystack="$1" needle="$2" label="$3"
  if [[ "$haystack" == *"$needle"* ]]; then
    echo "  PASS: $label"
    pass=$((pass + 1))
  else
    echo "  FAIL: $label (missing '$needle')"
    fail=$((fail + 1))
  fi
}

assert_not_contains() {
  local haystack="$1" needle="$2" label="$3"
  if [[ "$haystack" != *"$needle"* ]]; then
    echo "  PASS: $label"
    pass=$((pass + 1))
  else
    echo "  FAIL: $label (unexpected '$needle')"
    fail=$((fail + 1))
  fi
}

assert_status() {
  local actual="$1" expected="$2" label="$3"
  if [[ "$actual" == "$expected" ]]; then
    echo "  PASS: $label"
    pass=$((pass + 1))
  else
    echo "  FAIL: $label (expected exit $expected, got $actual)"
    fail=$((fail + 1))
  fi
}

run_scenario() {
  local name="$1" mutation_response="$2" readback_mode="$3"
  local mock_dir="$work_dir/mock_$name"
  local summary_file="$work_dir/summary_$name"
  mkdir -p "$mock_dir"
  : >"$summary_file"

  SCENARIO_STATUS=0
  PATH="$work_dir/bin:$PATH" \
    MOCK_DIR="$mock_dir" \
    MOCK_MUTATION_RESPONSE="$mutation_response" \
    MOCK_READBACK_MODE="$readback_mode" \
    GITHUB_STEP_SUMMARY="$summary_file" \
    REPO="RocketDelivery2/TeamBuilder" \
    PR_NUMBER="185" \
    BASE_REF="main" \
    HEAD_REPO="RocketDelivery2/TeamBuilder" \
    BASE_REPO="RocketDelivery2/TeamBuilder" \
    AUTHOR="dependabot[bot]" \
    DRAFT="false" \
    UPDATE_TYPE="version-update:semver-patch" \
    DEPENDENCY_NAMES="System.Security.Cryptography.Pkcs" \
    bash "$script_path" || SCENARIO_STATUS=$?
  SCENARIO_SUMMARY="$(cat "$summary_file")"
}

echo "Scenario: confirmed mutation and read-back"
run_scenario success '{"data":{"enablePullRequestAutoMerge":{"pullRequest":{"id":"PR_kwDO"}}}}' enabled
assert_status "$SCENARIO_STATUS" 0 "confirmed mutation succeeds"
assert_contains "$SCENARIO_SUMMARY" "Enabled squash auto-merge" "reports success only after confirmation"

echo "Scenario: GraphQL error in HTTP-success response"
run_scenario graphql_error '{"errors":[{"message":"Resource not accessible by integration"}]}' enabled
assert_status "$SCENARIO_STATUS" 1 "job fails closed on GraphQL errors"
assert_contains "$SCENARIO_SUMMARY" "mutation response was missing or contained GraphQL errors" "reports the GraphQL error outcome"
assert_not_contains "$SCENARIO_SUMMARY" "Enabled squash auto-merge" "does not report success on GraphQL errors"

echo "Scenario: missing mutation result"
run_scenario missing_result '{"data":{"enablePullRequestAutoMerge":null}}' enabled
assert_status "$SCENARIO_STATUS" 1 "fails closed when mutation confirmation is missing"
assert_not_contains "$SCENARIO_SUMMARY" "Enabled squash auto-merge" "does not report success without mutation confirmation"

echo "Scenario: read-back remains disabled"
run_scenario readback_missing '{"data":{"enablePullRequestAutoMerge":{"pullRequest":{"id":"PR_kwDO"}}}}' never
assert_status "$SCENARIO_STATUS" 1 "fails closed when auto-merge stays disabled"
assert_contains "$SCENARIO_SUMMARY" "read-back did not confirm enabled state" "reports the missing enabled state"
assert_not_contains "$SCENARIO_SUMMARY" "Enabled squash auto-merge" "does not report false success"

echo "Scenario: read-back becomes enabled on retry"
run_scenario readback_delayed '{"data":{"enablePullRequestAutoMerge":{"pullRequest":{"id":"PR_kwDO"}}}}' late
assert_status "$SCENARIO_STATUS" 0 "succeeds after bounded state propagation delay"
assert_contains "$SCENARIO_SUMMARY" "Enabled squash auto-merge" "reports success after delayed confirmation"

echo "Results: $pass assertions passed, $fail failed"
[[ "$fail" == "0" ]]
