# TeamBuilder Deployment Guide

## Overview

TeamBuilder deploys as an API container, a migration bundle, a static web client and one SQL
Server database per environment, behind an HTTPS reverse proxy or ingress. The private-QA
runbook, [qa/private-qa-deployment.md](qa/private-qa-deployment.md), is the source of truth for
deploying QA and Production: topology, environment separation, OIDC registration, VAPID keys,
the migration release model, health checks, backup/restore and rollback. The deployment
contract and reference stacks are in [`deploy/`](../deploy/README.md), and the release
checklist is [qa/release-checklist.md](qa/release-checklist.md).

This page keeps the reference material that applies to every deployment.

---

## Environment Strategy

| Environment | `ASPNETCORE_ENVIRONMENT` | Purpose |
|---|---|---|
| Development | `Development` | Local developer machines (developer tokens, Swagger, opt-in startup migration). |
| LocalQA | `LocalQA` | The local docker-compose stack (`docker-compose.qa.yml`, developer tokens). |
| QA | `QA` | Deployed private QA with real OIDC and HTTPS. |
| Production | `Production` | Live environment. |

QA and Production (and any other name) are *deployed* environments: the API refuses to start
with a developer signing key, startup migrations, a wildcard or non-https CORS origin, a missing
or non-https OIDC authority, or an unreplaced `#{…}` placeholder.

## Configuration

All environment-specific values are injected as environment variables or from a secret store
at deployment time. `appsettings.QA.json` and `appsettings.Production.json` contain only
non-secret defaults (JSON console logging, empty OIDC values, the database-stamp check), so a
deployment that forgets a value fails at startup instead of running with a placeholder. The
full variable list is in the runbook's "Configuration reference". Platforms that route to a
container port must target `8080`, or set `ASPNETCORE_HTTP_PORTS` (the image has no shell, so a
`${PORT}`-style substitution in the entrypoint is not available).

### JWT / OIDC

| Variable | Purpose |
|---|---|
| `Jwt__Authority` | OIDC issuer/authority (https). Required outside Development and LocalQA. |
| `Jwt__Audience` | Audience the provider puts in API access tokens. Required outside Development and LocalQA. |
| `Jwt__RequireHttpsMetadata` | Must stay `true` outside local environments. |
| `Jwt__SigningKey` | Developer tokens; Development and LocalQA only. Rejected elsewhere. |
| `Jwt__Issuer` | Expected issuer on the developer-token path only. |
| `Jwt__ExternalIdentity__SubjectClaim` | Stable subject claim: `sub` by default, `oid` for Entra ID. |
| `Jwt__ExternalIdentity__TenantIdClaim` | Optional tenant metadata claim (`tid`). |
| `Jwt__ExternalIdentity__Provider` | Descriptive provider label stored on new identity links. |

A validated token's exact issuer and configured subject claim resolve through `PlayerIdentity`
to the internal `Player.Id`; no JWT claim is parsed as a player ID. See
[oidc-rollout.md](oidc-rollout.md) for provider-specific notes.

### Azure SQL

Provision one database per environment (the Azure reference template in `deploy/azure` does
this). Create a contained application user for the API rather than using the server
administrator:

```sql
CREATE USER [teambuilder-api] WITH PASSWORD = '<from your secret store>';
ALTER ROLE db_datareader ADD MEMBER [teambuilder-api];
ALTER ROLE db_datawriter ADD MEMBER [teambuilder-api];
GRANT VIEW DEFINITION TO [teambuilder-api];  -- lets the API read the environment stamp
```

The migration bundle and the one-time `database stamp-environment` need a user with DDL
rights (`db_owner`, or the administrator during the release step).

---

## Database Migrations

### Local Development

```bash
dotnet tool restore
dotnet ef database update --project src/TeamBuilder.Infrastructure --startup-project src/TeamBuilder.Api
```

The initial and subsequent migrations are already committed under
`src/TeamBuilder.Infrastructure/Persistence/Migrations/`. Do not recreate an
`InitialCreate` migration. Add a new named migration only when changing the
EF Core model, then review and commit it. The EF tools use
`TeamBuilderDesignTimeDbContextFactory`, so they never start the API host.

### QA/Production

Schema changes ship as an EF Core migration bundle, applied once per release before the API
rolls out; application replicas never migrate. The Release workflow builds and validates
`efbundle-linux-x64` and an idempotent SQL script, and `docker build --target migrator .` builds
the migrator image. Steps: [qa/private-qa-deployment.md](qa/private-qa-deployment.md#migrations-the-release-model).

```bash
dotnet tool restore
dotnet ef migrations bundle --project src/TeamBuilder.Infrastructure --startup-project src/TeamBuilder.Api \
  --configuration Release --self-contained --target-runtime linux-x64 --output efbundle
./efbundle --connection "<QA connection string>"
dotnet src/TeamBuilder.Api/bin/Release/net10.0/TeamBuilder.Api.dll database status   # or: docker run <api image> database status
```

If a reviewed SQL script is required instead, generate it with
`dotnet ef migrations script --idempotent --output migration.sql` and apply it with your DBA
process; the API's readiness reports Unhealthy until every migration in the build is applied.

---

## Refill delivery operations

**Current (TB-REFILL-002).** Everything below runs inside the API process and
SQL Server; there is no Redis, message bus or separate worker service.

### Web Push (VAPID)

Browser alerts are **off by default**, and dev, QA and tests work without them
(players still get in-app notifications). To turn them on, generate a VAPID
key pair once per environment:

```bash
dotnet run --project src/TeamBuilder.Api -- outbox vapid-keys
# or, from a published build or container:
dotnet TeamBuilder.Api.dll outbox vapid-keys
```

It prints `WebPush__VapidPublicKey=…` and `WebPush__VapidPrivateKey=…`. Store
the private key as a secret (Octopus sensitive variable, Render secret, `.env`
that git ignores); **never commit it**. Then set:

| Variable | Value |
|---|---|
| `WebPush__Enabled` | `true` |
| `WebPush__Subject` | a contact the push services can reach, `mailto:ops@example.com` or an `https:` URL |
| `WebPush__VapidPublicKey` | public key from the command (65-byte P-256 point, base64url) |
| `WebPush__VapidPrivateKey` | private key from the command (secret) |

With `Enabled=true` the API refuses to start unless the subject and a matching
key pair are present. Keep the same key pair for the life of the environment:
changing it invalidates every browser subscription (browsers re-subscribe the
next time the player opens the app with alerts on). Other settings:
`MaxDevicesPerPlayer` (10), `TimeToLive` (30 minutes; older alerts are
dropped, not sent), `MaxAttempts` (4), `RetryBaseDelay`/`RetryMaxDelay`,
`MaxConsecutiveFailures` (5), `RequestTimeout` (10 s), `BatchSize`,
`MaxConcurrency`, `AllowedEndpointHosts` (push services the server will post to;
defaults cover Chrome/Edge/Android (FCM), Firefox, Safari and Windows).
`AllowLocalhostEndpoints` exists for local dogfooding against a fake push
service and is rejected outside `Development`.

The web client needs a secure context (HTTPS, or `http://localhost`) for push;
over plain HTTP on another host the browser offers no push and the option is
hidden. `sw.js` and `manifest.webmanifest` are served with `no-cache` by the
web image's nginx.

### Reverse proxies and client addresses

`ForwardedHeaders` decides whose `X-Forwarded-For`/`-Proto` the API believes.
It is **off by default**: the TCP peer address is the client, and forwarded
headers are ignored. Enable it only with the proxies that actually sit in front
of the API:

| Variable | Meaning |
|---|---|
| `ForwardedHeaders__Enabled` | `true` to honour forwarded headers |
| `ForwardedHeaders__KnownProxies__0` | an exact proxy IP (repeat with `__1`, …) |
| `ForwardedHeaders__KnownNetworks__0` | a proxy network in CIDR form, e.g. a load balancer subnet |
| `ForwardedHeaders__ForwardLimit` | proxy hops to unwind (default 1) |

Enabled with no proxy listed is a startup error, never "trust everyone".
Headers from any other address are ignored, and only `ForwardLimit` entries
are taken from the right of `X-Forwarded-For`, so a client cannot spoof its
address by sending the header itself. The QA compose stack gives nginx a fixed
address (`172.29.80.10`) and trusts only it. On a platform whose load balancer
addresses are not fixed (Render, App Service), list its documented network
range or leave this off; per-IP limits then see the balancer's address.
Signed-in rate limits (claims, subscriptions, push registration) partition by
token issuer and subject and do not depend on this setting; only anonymous
discovery is limited per IP.

### Outbox retention

`OutboxMaintenanceWorker` purges finished history every
`OutboxMaintenance:Interval` (15 minutes):

| Rows | Deleted after | Setting |
|---|---|---|
| Completed outbox messages | 7 days | `CompletedRetention` |
| Accepted, failed or abandoned push deliveries | 7 days | `PushDeliveryRetention` |
| Disabled push subscriptions | 30 days | `DisabledPushSubscriptionRetention` |
| Failed outbox messages | never, unless set | `FailedRetention` (opt-in) |

Pending and Processing messages are never purged. Each statement deletes at
most `BatchSize` (1000) rows with `READPAST, ROWLOCK` in its own short
transaction, at most `MaxBatchesPerRun` (100) per table per pass, so several
API instances can purge at once without blocking each other or the workers.
`OutboxMaintenance__Enabled=false` turns the worker off.

### Inspecting and replaying failed messages

There is no admin HTTP route. Operators who can run the API binary against the
database (they already hold its connection string) use the maintenance command:

```bash
dotnet TeamBuilder.Api.dll outbox status          # counts by status
dotnet TeamBuilder.Api.dll outbox failed [take]   # failed messages: id, type, aggregate, times, attempts, error preview; never the payload
dotnet TeamBuilder.Api.dll outbox replay <id>     # requeue one failed message
dotnet TeamBuilder.Api.dll outbox purge           # run one retention pass now
# QA stack: docker compose -f docker-compose.qa.yml exec api dotnet TeamBuilder.Api.dll outbox status
```

`replay` moves the message's attempts into its history (`PriorAttemptCount`,
`ReplayCount`, `LastReplayedAtUtc`), makes it Pending and due now, and only
acts on a Failed message, so running it twice requeues it once. Handlers are
idempotent: replaying a message that had partly or fully delivered creates no
duplicate notifications or push deliveries. Fix the cause first (the error
preview says what failed), then replay.

### Refill metrics

Meter `TeamBuilder.Refill` (System.Diagnostics.Metrics; export with
OpenTelemetry or watch with `dotnet-counters monitor --counters TeamBuilder.Refill`):

- Counters: `teambuilder.push.attempted`, `.accepted`, `.permanent_failure`,
  `.transient_failure`, `.abandoned`, `.subscription_disabled`;
  `teambuilder.outbox.purged`, `.replayed`, `.processed`, `.skipped`, `.failed`;
  `teambuilder.notifications.created`, `.opened`; `teambuilder.refill.vacancy_opened`.
- Timings (ms): `teambuilder.refill.vacancy_to_notification`,
  `vacancy_to_push_accepted`, `push_click_to_game_open`,
  `game_open_to_claim_attempt`, `vacancy_to_replacement`.

Tags are fixed categories (push service, outcome, error category, reason,
table, `via`); logs carry ids and counts. Neither ever contains push endpoints
or keys, addresses, coordinates, tokens or email.

---

## Deployment Checklist

Use [qa/release-checklist.md](qa/release-checklist.md). In short: Release workflow green;
backup taken; migration bundle applied and `database status` clean; database stamped (first
deployment); API rolled out and `/healthz/ready` healthy; release smoke run; device checks done.

---

## Security Considerations

- Never commit secrets. The connection string and the VAPID private key live only in the
  environment's secret store; images and web builds contain no secrets.
- Use a dedicated database user for the API, TLS for all connections (`Encrypt=True`), and a
  separate database, OIDC client, VAPID key pair and host name per environment.
- The API sends `nosniff`, `X-Frame-Options: DENY`, `Referrer-Policy: no-referrer` and a
  `default-src 'none'` CSP; HSTS outside Development over HTTPS. CORS allows exact origins only
  and never credentials.
- HTTPS redirection runs in the API only when the proxy's forwarded headers are trusted; the
  ingress or proxy owns the HTTP→HTTPS redirect otherwise.
- Rate limits are in memory per API instance (discovery per IP; claims,
  subscriptions and push registration per token issuer and subject). Configure
  `ForwardedHeaders` for your proxy before relying on per-IP limits.
- Swagger is off outside Development unless `Swagger:Enabled=true`.

---

## Monitoring

- `GET /healthz/live` (process only) and `GET /healthz/ready` (database reachable and fully
  migrated, environment stamp, configuration) return JSON with per-check status, the build
  version and commit. `/health` and `/health/ready` are kept as plain-text aliases.
- QA and Production log JSON to the console with `TraceId`, `SpanId` and `CorrelationId`
  (`X-Request-Id`) scopes; collect stdout with your platform (Log Analytics in the Azure
  reference). Application Insights is not wired into the API.
- Refill metrics: see "Refill metrics" above.

---

## Troubleshooting

See the runbook's troubleshooting table. The most common deployment failures:

| Symptom | Cause |
|---|---|
| `Refusing to start in QA: …` | Unsafe or missing configuration; the message lists every item. |
| `/healthz/ready` 503, `database` Unhealthy | Database unreachable or the migration bundle has not run. |
| `/healthz/ready` 503, `environment` Unhealthy | Database not stamped for this environment. |
| CORS errors in the browser | The web origin is missing from `AllowedOrigins` (exact `https://` origin, no path). |
| `500` responses | Check the API's console logs by `CorrelationId`; clients only see a generic message. |

---

## Rollback Strategy

Redeploy the previous API image (the database may be one additive migration ahead; readiness
stays healthy) and the previous web artifact. Do not roll the schema back in place on QA or
Production data: fix forward, or restore the pre-release backup to a separate database and
switch to it. Details: [qa/private-qa-deployment.md](qa/private-qa-deployment.md#rollback).

---

## Contact

For deployment support, contact the DevOps team or open an issue on [GitHub](https://github.com/RocketDelivery2/TeamBuilder/issues).
