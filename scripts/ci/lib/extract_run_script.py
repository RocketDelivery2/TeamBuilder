"""Extract the single `run: |` block-scalar bash script from a GitHub Actions
workflow YAML file, without depending on PyYAML (not guaranteed present on
every runner image). Used by scripts/ci/test-pr-auto-merge.sh and
scripts/ci/test-pr-reviewer-agent.sh to run the exact embedded script under a
mocked `gh` CLI.

Usage: python3 extract_run_script.py <workflow.yml> <out.sh>
"""
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
