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

Report security concerns privately using GitHub's private vulnerability reporting form:
https://github.com/OWNER/REPOSITORY/security/advisories/new

If that form is unavailable, contact the maintainer directly at: security@example.com

If you are unsure which route to use, use the private vulnerability reporting form first and fall back to the security email above.

Include:

- A clear description of the issue
- Steps to reproduce
- Affected files, endpoints, or configuration
- Potential impact
- Suggested fix, if known

## Secrets and Credentials

Do not commit secrets, passwords, API keys, Azure SQL connection strings, Octopus API keys, or production configuration values to this repository.

Deployment secrets should be stored in GitHub Environments, GitHub Actions Secrets, Azure Key Vault, or Octopus Deploy variable sets.
