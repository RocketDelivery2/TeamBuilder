# Private QA deployment

How to run TeamBuilder for 10–20 real testers on real phones: real OIDC sign-in, real HTTPS,
real Web Push, a real SQL Server database, a reverse proxy, an explicit migration step and
health/readiness checks. It covers the deployment contract (vendor neutral), a single-host
reference stack (`deploy/compose`), an Azure reference (`deploy/azure`) and the operational
steps around them.

Nothing in this repository has been provisioned. Host names, client IDs and resource names in
examples are placeholders. The release checklist with the evidence a QA release needs is
[release-checklist.md](release-checklist.md).

## Topology

```text
                 internet (testers' phones and laptops)
                              |
                       HTTPS :443 only
                              v
            +-----------------------------------+
            |  TLS ingress / reverse proxy      |   Caddy (deploy/compose) or the platform
            |  HSTS, HTTP -> HTTPS redirect     |   ingress (Azure Container Apps)
            +-----------------------------------+
               | /api/*                 | everything else
               v                        v
   +-----------------------+   +------------------------+
   | TeamBuilder API       |   | Web client (static     |
   | container, port 8080  |   | SPA/PWA + sw.js +      |
   | non-root, no shell    |   | /config.js)            |
   +-----------------------+   +------------------------+
               |
               v
   +-----------------------+        +----------------------------+
   | SQL Server / Azure SQL|  <---  | migrator (EF Core bundle), |
   | (one per environment) |        | run once per release       |
   +-----------------------+        +----------------------------+
               ^
   API -> push services (FCM, Mozilla, Apple, Windows) for Web Push, outbound only
```

* Only the proxy is reachable from the internet. The API listens on plain HTTP inside the
  private network; TLS terminates at the proxy.
* The proxy is the only address allowed to supply `X-Forwarded-For` / `X-Forwarded-Proto`
  (`ForwardedHeaders` settings). A client that sends those headers directly is ignored, and a
  `/0` "proxy network" is refused at startup.
* `/healthz/*` is for the orchestrator and is not routed publicly in the reference stack.
* The API and the web client are separate artifacts. The web client can live on the same
  origin behind the proxy (reference stack, no CORS needed) or on its own static host (Azure
  Static Web Apps; then the API's `AllowedOrigins` lists that exact origin).

## Environments

| | Development | LocalQA | QA | Production |
|---|---|---|---|---|
| Where | developer machine | `docker-compose.qa.yml` on a laptop | deployed, testers | deployed, public |
| `ASPNETCORE_ENVIRONMENT` | `Development` | `LocalQA` | `QA` | `Production` |
| Sign-in | developer tokens | developer tokens (banner) | real OIDC only | real OIDC only |
| HTTPS | optional | no (localhost) | required (proxy) | required (proxy) |
| Schema changes | opt-in startup migration | migration bundle (compose `migrate`) | migration bundle only | migration bundle only |
| Swagger | on | off | off unless `Swagger:Enabled=true` | off unless `Swagger:Enabled=true` |
| Database stamp checked | no | no | yes | yes |

Any other environment name is treated like QA/Production, so a typo can never relax a rule.

### Fail-closed startup

A deployed environment refuses to start (the process exits with `Refusing to start in QA: …`
listing every problem) when any of these hold:

* `Jwt:SigningKey` is set (developer tokens are for Development and LocalQA only).
* `Jwt:Authority` is missing, not `https://`, or still a `#{…}` placeholder.
* `Jwt:Audience` is missing, or `Jwt:RequireHttpsMetadata` is `false`.
* `Database:ApplyMigrationsOnStartup` is `true`.
* `ConnectionStrings:TeamBuilderSql` is missing, a placeholder or LocalDB.
* `AllowedOrigins` contains `*`, a non-https origin, a path or a placeholder.
* `ForwardedHeaders` is enabled with no proxy, an invalid one, or a `/0` network.
* Web Push is enabled without a valid subject and VAPID key pair.

`*` in `AllowedOrigins` is accepted only in Development (anonymous CORS, never with
credentials). The API never allows CORS credentials: it authenticates with bearer tokens.

### Environment separation

QA and Production each get their own:

| Item | Why |
|---|---|
| Database (and server credentials) | No shared rows; restoring one never touches the other. |
| OIDC registrations (SPA client and API audience) | A QA token is never accepted by Production. |
| VAPID key pair | Browser subscriptions are bound to the key; QA can never push to a Production subscription. |
| Host name / origin | Service workers, push subscriptions and storage are per origin. |
| Secret store (Key Vault, `.env` file) | A QA operator cannot read Production secrets. |
| Push subscriptions | They live in the environment's database (follows from the above). |

The API enforces the database part: each deployed database is **stamped** with its environment
name (`dotnet TeamBuilder.Api.dll database stamp-environment QA`, a database-level extended
property). Until the stamp matches `ASPNETCORE_ENVIRONMENT` (or `Deployment:DatabaseEnvironment`),
the outbox, Web Push, retention and series workers do not start and `/healthz/ready` is
Unhealthy. A QA instance pointed at the Production database by mistake therefore sends nothing
and receives no traffic. Re-stamping a database for another environment needs `--force`. A
restored backup keeps the stamp of the database it came from: re-stamp a copy before using it
elsewhere. Reading the stamp needs no extra permission for a database owner; a least-privilege
application user may need `VIEW DEFINITION` on the database.

No Production value is committed. `appsettings.QA.json` and `appsettings.Production.json` hold
only non-secret defaults (JSON console logs, empty OIDC values that the deployment must supply).

## Configuration reference (API)

Environment variables use `__` for `:`.

| Variable | QA value | Secret |
|---|---|---|
| `ASPNETCORE_ENVIRONMENT` | `QA` | no |
| `ConnectionStrings__TeamBuilderSql` | QA database, `Encrypt=True` | **yes** |
| `Jwt__Authority` | OIDC issuer, `https://…` | no |
| `Jwt__Audience` | API audience in access tokens | no |
| `Jwt__ExternalIdentity__SubjectClaim` | `sub` (most providers) or `oid` (Entra ID) | no |
| `Jwt__ExternalIdentity__Provider` | label stored on new identity links, e.g. `entra` | no |
| `AllowedOrigins` | empty (same origin) or exact `https://` web origins, comma separated | no |
| `ForwardedHeaders__Enabled`, `__KnownProxies__0` / `__KnownNetworks__0` | the proxy's address or network | no |
| `WebPush__Enabled`, `WebPush__Subject`, `WebPush__VapidPublicKey` | QA key pair, `mailto:` contact | no |
| `WebPush__VapidPrivateKey` | QA private key | **yes** |
| `Deployment__ShutdownTimeout` | `00:00:30` (default) | no |
| `ASPNETCORE_HTTP_PORTS` | `8080` (image default) | no |
| `Swagger__Enabled` | unset (off) | no |
| `Security__HttpsRedirection` | unset: on when forwarded headers are trusted | no |

The web client reads these at container start (one build serves every environment); none are
secrets:

| Variable (web container) | Value |
|---|---|
| `TEAMBUILDER_ENVIRONMENT` | `qa` (turns developer-token sign-in off whatever the build said) |
| `TEAMBUILDER_OIDC_AUTHORITY` | same issuer as `Jwt__Authority` |
| `TEAMBUILDER_OIDC_CLIENT_ID` | public SPA client ID |
| `TEAMBUILDER_OIDC_SCOPE` | `openid profile <api scope>` |
| `TEAMBUILDER_API_BASE_URL` | empty when same origin, else the API origin |
| `TEAMBUILDER_CSP_CONNECT_EXTRA` | extra origins the browser calls (e.g. a token endpoint on another host) |

On a static host without the nginx image, write `config.js` next to `index.html` yourself:

```js
window.__TEAMBUILDER_CONFIG__ = { "environment": "qa", "apiBaseUrl": "https://api.qa.example", "oidcAuthority": "https://issuer.example/", "oidcClientId": "<spa client id>", "oidcScope": "openid profile <api scope>" };
```

## QA OIDC registration

Register two things at the QA identity provider (do not reuse Production registrations):

| Value | QA setting |
|---|---|
| Authority / issuer | the provider's issuer URL for the QA tenant or realm (`Jwt__Authority`, `TEAMBUILDER_OIDC_AUTHORITY`) |
| SPA client | single-page application / public client, **Authorization Code + PKCE**, no client secret |
| Client ID | the SPA client's ID (`TEAMBUILDER_OIDC_CLIENT_ID`) |
| Redirect URI | `https://<qa web origin>/auth/callback` |
| Post-logout redirect URI | `https://<qa web origin>/` (used when the provider supports RP-initiated logout) |
| Allowed web origin (CORS at the IdP) | `https://<qa web origin>` |
| API resource / audience | e.g. `api://teambuilder-qa`; access tokens must carry it in `aud` (`Jwt__Audience`) |
| Scopes | `openid profile` plus the API scope, e.g. `api://teambuilder-qa/access` (`TEAMBUILDER_OIDC_SCOPE`) |
| Subject claim | a stable, issuer-scoped user id in the **access token**: `sub`, or `oid` for Entra ID |

Identity model: a validated token's exact issuer + subject is looked up in `PlayerIdentity` and
mapped to the internal `Player.Id`. There is no fallback that treats a GUID subject as a player
ID. A tester who first signed in with a developer token is a different player after signing in
with the real provider.

The web client signs in with oidc-client-ts (code flow with PKCE, tokens in `sessionStorage` for
the tab). **Sign out** ends the provider session too when the provider publishes an
`end_session_endpoint`; otherwise it forgets the local session.

## Web Push (VAPID) for QA

1. Generate a QA-only key pair (never reuse Production's):
   `docker run --rm <api image> outbox vapid-keys` prints `WebPush__VapidPublicKey=…` and
   `WebPush__VapidPrivateKey=…`.
2. Put the private key only in the QA secret store (`TEAMBUILDER_VAPID_PRIVATE_KEY` in the
   git-ignored `qa.env`, or the Key Vault secret `vapid-private-key`). It never goes into an
   image, the web build or `/config.js`.
3. Set `WebPush__Enabled=true`, `WebPush__Subject=mailto:<qa contact>` and the public key. The
   browser gets the public key from `GET /api/v1/push/config`; nothing else needs it.
4. Rotating the pair invalidates every QA browser subscription; testers turn alerts off and on.

Push delivery is best effort and outside readiness: if a push service is down, alerts are
retried and then dropped, in-app notifications still arrive, and the API keeps serving.

Real-device verification is manual and is **not** claimed by any automated check. Run it on:

* **Android Chrome**: open the QA site, sign in, open a full game, press **Notify me when a spot
  opens**, allow notifications. From another account leave the game. The alert arrives with the
  app in the background; tapping it opens the game.
* **iPhone Safari (iOS 16.4 or later)**: Share → **Add to Home Screen**, open TeamBuilder from
  the Home Screen icon (Web Push only works in the installed PWA), sign in, then as above.

Record results in the release checklist.

## Migrations: the release model

Application replicas never change the schema.

* **Build**: CI builds a self-contained Linux EF Core migration bundle (`efbundle-linux-x64`)
  and an idempotent SQL script, and the `migrator` image (`docker build --target migrator .`).
  The bundle needs no .NET install and no app configuration, only a connection string
  (`--connection "…"` or `ConnectionStrings__TeamBuilderSql`).
* **Apply to QA**: run the bundle once per release, before the API rolls out:
  `docker compose -f deploy/compose/docker-compose.release.yml --env-file deploy/compose/qa.env --profile release run --rm migrate`,
  or `az containerapp job start -g <rg> -n <prefix>-migrate`, or the standalone bundle from a
  machine that can reach the database.
* **Verify**: `docker run --rm -e ConnectionStrings__TeamBuilderSql=… <api image> database status`
  prints applied and pending migrations and exits 0 only when nothing is pending. The API's
  readiness also stays Unhealthy while the schema is behind the build.
* **Deploy the API** afterwards. A database that is *ahead* of the API (a newer release's
  migration) still reports ready, which is what lets you roll the API back.
* **First deployment only**: stamp the database
  (`--profile release run --rm stamp`, or `database stamp-environment QA`).

`Database:ApplyMigrationsOnStartup=true` remains available for Development and LocalQA. In any
deployed environment it stops the API from starting.

Migrations must stay backward compatible for one release (expand, then contract): add columns
and tables first, deploy code that uses them, and remove old ones in a later release. That is
what makes an API rollback safe without a schema rollback.

## Deploying with the reference stack (one Docker host)

`deploy/compose/docker-compose.release.yml` runs Caddy (automatic HTTPS from Let's Encrypt for a
public host name), the API, the web client and the release tools; the database is a managed SQL
Server, or the optional `local-sql` profile on the same host.

```bash
cp deploy/compose/qa.env.example deploy/compose/qa.env    # fill in; git-ignored
c="docker compose -f deploy/compose/docker-compose.release.yml --env-file deploy/compose/qa.env"
$c --profile release build                 # or set TEAMBUILDER_*_IMAGE to released images
$c --profile release run --rm migrate      # 1. apply migrations
$c --profile release run --rm stamp        # 2. first deployment only
$c up -d --wait                            # 3. API (waits for /healthz/ready), web, proxy
$c ps                                      # health of every service
node scripts/qa/release-smoke.mjs          # see "Release smoke" below
```

Each subsequent release: `migrate`, then `up -d --wait`. The API container's health check is
`dotnet TeamBuilder.Api.dll healthcheck ready` (the image has no shell or curl); the proxy only
starts routing once the API is ready.

## Azure reference

`deploy/azure/main.bicep` (+ `qa.bicepparam`, `staticwebapp.config.json`) describes one
environment: Container Apps (API with startup/liveness/readiness probes on `/healthz/*`),
a manual Container Apps Job for the migration bundle, Azure SQL Database (7-day point-in-time
restore), Static Web Apps for the SPA, Key Vault for `sql-connection-string` and
`vapid-private-key` (read by a managed identity) and Log Analytics. CI compiles it; it has never
been deployed from this repository, and it needs your subscription and credentials.

Order of operations: deploy the template; set the two Key Vault secrets; create the
application database user; `az containerapp job start` the migrator; run the `stamp` command
once with the same connection string; then roll the API image. For the SPA, build the web
artifact, write `config.js` with the QA values, replace `__API_ORIGIN__` / `__OIDC_ORIGIN__` in
`staticwebapp.config.json`, and upload with the Static Web Apps CLI or GitHub action.

Forwarded headers on Container Apps: the ingress (Envoy) sets `X-Forwarded-For`. Its source
range depends on the environment's networking; until you have confirmed it, leave
`forwardedHeadersKnownNetworks` empty. The API then sees the ingress address (per-IP rate limits
are coarser), and the ingress itself redirects HTTP to HTTPS (`allowInsecure: false`).

## Health, readiness and shutdown

| Endpoint | Checks | Use |
|---|---|---|
| `GET /healthz/live` | none (process is serving) | liveness and startup probes |
| `GET /healthz/ready` | `database`: reachable and no pending migration; `environment`: database stamp matches; `configuration`: deployment rules hold | readiness probe, load balancer |

Both return JSON such as
`{"status":"Healthy","version":"1.0.0","commit":"<sha>","checks":{"database":"Healthy",…}}`,
never exception text. `/health` and `/health/ready` remain as plain-text aliases. Web Push is
not part of readiness. On SIGTERM the API stops accepting work and waits up to
`Deployment:ShutdownTimeout` (30 s) for requests and workers; give the container a stop grace
period a little longer (the reference stack uses 40 s).

## Security headers, CORS and CSP

* API responses: `X-Content-Type-Options: nosniff`, `X-Frame-Options: DENY`,
  `Referrer-Policy: no-referrer`, `Content-Security-Policy: default-src 'none'; frame-ancestors 'none'`.
* HSTS (180 days) on API responses served over HTTPS in deployed environments, and on the whole
  site from the proxy.
* HTTPS redirection in the API only when the proxy's `X-Forwarded-Proto` is trusted (otherwise
  every proxied request would look like HTTP and loop); `/healthz` is never redirected.
* CORS: exact origins only, any method, headers `Authorization`, `Content-Type`, `Accept`,
  `X-Request-Id`; no credentials.
* Web client CSP: `default-src 'self'; script-src 'self'; style-src 'self'; connect-src 'self' <OIDC authority> [<API origin>]; worker-src 'self'; frame-ancestors 'none'; object-src 'none'`
  plus `nosniff`, `DENY`, `no-referrer`, and a `Permissions-Policy` allowing geolocation for the
  site itself only.

## Observability baseline

* QA and Production write JSON console logs with scopes: every request entry carries `TraceId`,
  `SpanId`, the `CorrelationId` (`X-Request-Id`, echoed to the client, capped at 128 characters),
  method, path, status and duration.
* Never logged: request bodies, headers (bearer tokens), query strings (discovery coordinates),
  OIDC subjects, push endpoints and keys, the VAPID private key, private venue addresses. The
  web container's access log records the path without the query string. The container release
  smoke checks the API and web logs for tokens, subjects, coordinates and the test address.
* Raising `Microsoft.AspNetCore` to Information for troubleshooting is safe: appsettings.json
  pins `Microsoft.AspNetCore.Hosting.Diagnostics` (whose request entries carry the full URL) and
  `System.Net.Http.HttpClient` (push endpoint URLs) at Warning. Override those two categories
  only on a machine whose logs are thrown away.
* Proxy errors: Caddy keeps access logging off and logs failed requests (for example a 502
  while the API restarts) with the query string replaced by `?REDACTED` and no Referer; the
  `err_id`, method, path and status remain. nginx does not log upstream errors for `/api/`,
  because its error lines quote the full request. The container release smoke stops the API,
  searches through each proxy and checks the logs.
* Troubleshooting: `/healthz/ready` per-check status; `outbox status` / `outbox failed` /
  `outbox replay`; `database status` / `show-environment`; refill metrics through
  `dotnet-counters` (meter `TeamBuilder.Refill`, see docs/deployment.md).

## Release smoke

`scripts/qa/release-smoke.mjs` walks the basketball loop against a running environment:
liveness, readiness, web shell + CSP + `/config.js`, `/players/me`, a standalone basketball game
at a private venue, address masking before/after joining and after leaving, discovery, claims to
READY, **Notify me**, leave, the vacancy outbox producing the in-app notification, the
replacement claim and READY again; the game is cancelled at the end.

```bash
TB_API_URL=https://qa.example TB_WEB_URL=https://qa.example TB_HEALTH_URL=skip \
TB_SMOKE_TOKENS="<host>,<player A>,<player B>,<player C>" node scripts/qa/release-smoke.mjs
```

Tokens come from four QA test accounts signed in through the real provider (copy each access
token from the browser's session storage, or use your provider's test-user flow). Without
tokens the script runs the anonymous checks only. Steps that need a person or device (real OIDC
sign-in, push registration, alert arrival on Android/iPhone) print as `HUMAN`/`DEVICE` and are
never counted as passed.

`scripts/qa/container-smoke.sh` (CI) builds the images and runs the local QA stack with the full
loop plus the QA topology behind Caddy: not ready before the bundle and the stamp, HTTPS, HSTS,
CSP, CORS, fail-closed startup cases and a backup restored to a separate database.

Performance smoke results (last-slot contention, discovery rate, 1,000-subscriber fanout,
concurrent workers) and how to rerun them: [private-qa-release-smoke.md](../performance/private-qa-release-smoke.md).

## Backup and restore (QA)

Requirements: daily automated backups with at least 7 days of point-in-time restore; restores
always go to a **separate** database first; schema verified before any use.

* **Azure SQL** (reference): point-in-time restore is built in (7 days in the template). Restore
  with `az sql db restore -g <rg> -s <server> -n TeamBuilderQA --dest-name TeamBuilderQA_Restore --time <UTC>`.
* **SQL Server on a VM/container**: `BACKUP DATABASE [TeamBuilderQA] TO DISK = N'/var/opt/mssql/backup/tbqa.bak' WITH COPY_ONLY, CHECKSUM, INIT`
  on a schedule (cron + sqlcmd, or SQL Agent), and copy the file off the host.
  Restore beside the original:
  `RESTORE DATABASE [TeamBuilderQA_Restore] FROM DISK = N'…/tbqa.bak' WITH CHECKSUM, MOVE N'TeamBuilderQA' TO N'…/TeamBuilderQA_Restore.mdf', MOVE N'TeamBuilderQA_log' TO N'…/TeamBuilderQA_Restore_log.ldf'`.
* **Validate** the restored copy: `docker run --rm -e ConnectionStrings__TeamBuilderSql=<restored> <api image> database status`
  (exit 0, no pending migrations) and `database show-environment` (the copy keeps the original's
  stamp). To promote the copy, point the QA connection string at it and roll the API; to use it
  for anything else, re-stamp it with `--force` first.

The container smoke backs up the QA database, restores it under a new name and checks the
migration history and stamp match.

## Rollback

* **API image**: redeploy the previous image tag (`TEAMBUILDER_API_IMAGE`, or the previous
  Container Apps revision). Readiness stays healthy when the schema is ahead of the image, so
  the previous release serves as long as the last migration was additive (expand/contract).
* **Web**: redeploy the previous web artifact; `sw.js` and `config.js` are `no-cache`, so
  browsers pick it up on the next load.
* **Schema**: not rolled back in place. Fix forward with a new migration, or restore the
  pre-release backup to a separate database and switch to it (data written since is lost).
  `efbundle <migration id>` can move a test database to an older migration; do not use it on QA
  data without a backup.

## Troubleshooting

| Symptom | Likely cause |
|---|---|
| API exits with `Refusing to start in QA: …` | The listed settings; see "Fail-closed startup". |
| `/healthz/ready` 503 with `database: Unhealthy` | Database unreachable, or the migration bundle has not run for this release (`database status`). |
| `/healthz/ready` 503 with `environment: Unhealthy` | Database not stamped, or stamped for another environment (`database show-environment`). Workers are paused. |
| Sign-in loops or "Sign-in is not configured" | `TEAMBUILDER_OIDC_*` missing, redirect URI not registered exactly, or the authority is not https. |
| `401` after signing in | Token audience or issuer differs from `Jwt__Audience` / `Jwt__Authority`, or the subject claim is not in the access token. |
| Browser console shows a CSP `connect-src` violation | The token endpoint is on another host: add it to `TEAMBUILDER_CSP_CONNECT_EXTRA`. |
| Every request redirects forever | `Security__HttpsRedirection=true` without trusted forwarded headers; remove it or configure `ForwardedHeaders`. |
| No alerts on iPhone | The site was opened in Safari, not from the Home Screen icon, or iOS is older than 16.4. |
