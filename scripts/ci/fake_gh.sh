#!/usr/bin/env bash
# Mock `gh` CLI used to deterministically drive pr-auto-merge.yml's and
# pr-reviewer-agent.yml's run scripts outside of GitHub Actions. Records
# every "gh pr merge" and check-run-creation invocation so tests can assert
# exactly which merge/auto-merge/disable-auto/check-run actions were taken.
set -euo pipefail

mock_dir="${MOCK_DIR:?MOCK_DIR must be set}"

if [[ "$1 $2" == "api graphql" ]]; then
  idx_file="$mock_dir/state_idx"
  idx="$(cat "$idx_file" 2>/dev/null || echo 0)"
  states_file="$mock_dir/states.jsonl"
  total="$(wc -l < "$states_file")"
  line=$((idx + 1))
  if [[ "$line" -gt "$total" ]]; then
    line="$total"
  fi
  sed -n "${line}p" "$states_file"
  echo $((idx + 1)) > "$idx_file"
  exit 0
fi

# GET repos/{owner}/{repo}/commits/{sha}/check-runs -- used by
# pr-auto-merge.yml's reviewer_status_for_sha() to ask, fresh, whether the
# Reviewer Agent check run passed for an exact commit SHA. The real endpoint
# already filters server-side by SHA; the scenario fixture here stands in
# for "what the API would return for the SHA this test is exercising", so no
# argument-based filtering is needed in the mock itself.
if [[ "$1" == "api" && "$2" == */commits/*/check-runs ]]; then
  if [[ "${MOCK_CHECK_RUNS_FAIL:-0}" == "1" ]]; then
    echo "mock: not found" >&2
    exit 1
  fi
  cat "$mock_dir/checkruns.json"
  exit 0
fi

# POST repos/{owner}/{repo}/check-runs -- used by pr-reviewer-agent.yml to
# publish its own result. Recorded (not replayed) since the reviewer test
# harness asserts on what was *sent*, not a scripted response.
if [[ "$1" == "api" && "$2" == */check-runs ]]; then
  echo "$*" >> "$mock_dir/checkrun_calls.log"
  echo '{"id": 1}'
  exit 0
fi

# GET repos/{owner}/{repo}/compare/{base}...{head} -- used by
# pr-reviewer-agent.yml to fetch the exact, immutable diff under review.
if [[ "$1" == "api" && "$2" == *compare* ]]; then
  cat "$mock_dir/compare.json"
  exit 0
fi

# gh pr view <number> --json ... -- used by pr-auto-merge.yml's check_run
# event handling to resolve PR context that the check_run payload doesn't
# carry.
if [[ "$1 $2" == "pr view" ]]; then
  cat "$mock_dir/pr_view.json"
  exit 0
fi

if [[ "$1 $2" == "pr merge" ]]; then
  echo "$*" >> "$mock_dir/merge_calls.log"
  if [[ "$*" == *"--disable-auto"* ]]; then
    # Disabling auto-merge always "succeeds" in these tests.
    exit 0
  fi
  if [[ "${MOCK_MERGE_FAIL:-0}" == "1" ]]; then
    echo "mock: merge rejected (not mergeable)" >&2
    exit 1
  fi
  exit 0
fi

echo "fake_gh: unexpected invocation: $*" >&2
exit 1
