# Mobile dependency findings

Audit snapshot: 2026-10-23. Run `npm audit` to refresh the findings.

The initial SDK 58 dependency tree reports high-severity advisories,
including two root dependencies with no patched registry release at this
snapshot:

- `braces@3.0.3`: [nested-pattern stack exhaustion](https://github.com/advisories/GHSA-vfj7-8cjw-p6xm),
  reached through Metro's file matching and other development tools.
- `node-forge@1.4.0`: [RSA signature verification](https://github.com/advisories/GHSA-86w9-cpqp-85rv),
  reached through Expo CLI code-signing tooling.

The audit counts affected parent packages as well as the vulnerable leaf
packages. This is not evidence that all reported packages are separately
exploitable in a native runtime. Conversely, successful application tests
do not clear these advisories.

No forced downgrade, dependency suppression, or audit allowlist has been
applied. The CI audit remains a failing check while the registry reports
high-severity findings. Review the complete current audit before release.

The generated, unminified npm lockfile exceeds the repository's automated
Reviewer Agent size threshold. Dependency bootstrap therefore needs an
owner-reviewed resolution through the repository's review policy. The
mobile task does not change that policy or mark its check successful.
