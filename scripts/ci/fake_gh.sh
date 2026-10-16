#!/usr/bin/env bash
# Mock `gh` CLI used to deterministically drive pr-auto-merge.yml's run script
# outside of GitHub Actions. Records every "gh pr merge" invocation so tests
# can assert exactly which merge/auto-merge/disable-auto actions were taken.
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
