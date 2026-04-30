# Security Policy

## Supported Versions

TeamBuilder is currently under active development.

| Version | Supported |
| ------- | --------- |
| `main` branch | Yes |
| Older commits / non-`main` revisions | No |

Security fixes are applied to the latest code on the `main` branch only. Fixes are not currently backported to older commits or other revisions unless dedicated release branches are introduced in the future.

## Reporting a Vulnerability

Please do not open a public GitHub issue for security vulnerabilities.

Report security concerns privately using the repository's **Security** tab and submit a private vulnerability report. For guidance, see GitHub's documentation:
https://docs.github.com/code-security/security-advisories/guidance-on-reporting-and-writing-information-about-vulnerabilities/privately-reporting-a-security-vulnerability

If private vulnerability reporting is unavailable, do not open a public issue containing vulnerability details. Wait until private reporting is available again or use another non-public maintainer contact channel only if one is explicitly documented by this project.

If you are unsure which route to use, use private vulnerability reporting first.

Include:

- A clear description of the issue
- Steps to reproduce
- Affected files, endpoints, or configuration
- Potential impact
- Suggested fix, if known

## Secrets and Credentials

Do not commit secrets, passwords, API keys, Azure SQL connection strings, Octopus API keys, or production configuration values to this repository.

Deployment secrets should be stored in GitHub Environments, GitHub Actions Secrets, Azure Key Vault, or Octopus Deploy variable sets.
