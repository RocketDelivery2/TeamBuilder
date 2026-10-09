# Deployment contract

TeamBuilder deploys as independent pieces. Any platform that provides these can run it; the
runbook is [docs/qa/private-qa-deployment.md](../docs/qa/private-qa-deployment.md).

| Piece | Artifact | Contract |
|---|---|---|
| HTTPS ingress / reverse proxy | your platform, or Caddy in `compose/` | TLS termination, HTTP→HTTPS redirect, HSTS; routes `/api/*` to the API; sets `X-Forwarded-For`/`-Proto` and is the only address the API trusts for them |
| API | `Dockerfile` target `api` (`teambuilder-api`) | plain HTTP on 8080, non-root, no shell; config and secrets as environment variables; probes `GET /healthz/live` and `GET /healthz/ready`; SIGTERM drains within `Deployment:ShutdownTimeout` |
| Migrations | `Dockerfile` target `migrator`, or `efbundle-linux-x64` from the Release workflow | run once per release before the API; needs only `ConnectionStrings__TeamBuilderSql` |
| Web client (SPA/PWA) | `apps/TeamBuilder.Web` build (`teambuilder-web` image or the web artifact) | static files; `/config.js` per environment; `sw.js`, `config.js`, `manifest.webmanifest` served `no-cache`; CSP as documented |
| Database | SQL Server 2022 / Azure SQL | one per environment, stamped with its environment name, backed up |
| Secrets | your secret store | connection string and VAPID private key only; never in images or web builds |

Reference implementations:

* [`compose/`](compose/): single Docker host with Caddy (automatic HTTPS), for a small private QA.
* [`azure/`](azure/): Azure Container Apps + Azure SQL + Static Web Apps + Key Vault (Bicep).
  Compiled in CI, never provisioned from this repository.
