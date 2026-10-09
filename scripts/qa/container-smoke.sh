#!/usr/bin/env bash
# Container release smoke: builds the API, migrator and web images and exercises them the way
# a private-QA release uses them. Runs in the Release workflow and locally (Docker + Node 20+):
#
#   scripts/qa/container-smoke.sh            # build images, run both phases
#   TB_SKIP_BUILD=1 scripts/qa/container-smoke.sh   # reuse images already built/tagged
#
# Phase 1, local QA stack (docker-compose.qa.yml, LocalQA, developer tokens): migration bundle
#   before the API, the full basketball refill loop (scripts/qa/release-smoke.mjs), logs free of
#   tokens, subjects and search coordinates, and a forced API redeploy that keeps the data.
# Phase 2, release topology (deploy/compose, ASPNETCORE_ENVIRONMENT=QA behind Caddy HTTPS):
#   not ready before the bundle and the environment stamp, ready after; HTTPS, HSTS, CSP, CORS
#   allowlist and API headers through the proxy; fail-closed startup on unsafe settings; QA
#   backup restored to a separate database with the same schema.
# The QA phase uses a placeholder OIDC authority, so it checks anonymous behaviour only; real
# sign-in is a human step in docs/qa/release-checklist.md.
set -euo pipefail

root="$(cd "$(dirname "$0")/../.." && pwd)"
cd "$root"
work="$(mktemp -d)"
trap 'status=$?; cleanup; rm -rf "$work"; exit $status' EXIT

sql_password="Smoke_$(openssl rand -hex 12)!Aa1"
signing_key="$(openssl rand -hex 32)"

qa=(docker compose -p tb-smoke-qa -f docker-compose.qa.yml --env-file "$work/qa.env")
rel=(docker compose -p tb-smoke-release -f deploy/compose/docker-compose.release.yml --env-file "$work/release.env")

cleanup() {
  "${qa[@]}" down -v --remove-orphans >/dev/null 2>&1 || true
  "${rel[@]}" --profile release --profile local-sql down -v --remove-orphans >/dev/null 2>&1 || true
}

step() { printf '\n== %s\n' "$*"; }
fail() { printf 'FAIL %s\n' "$*" >&2; exit 1; }
pass() { printf 'PASS %s\n' "$*"; }

cat > "$work/qa.env" <<EOF
TEAMBUILDER_QA_SQL_PASSWORD=$sql_password
TEAMBUILDER_JWT_SIGNING_KEY=$signing_key
EOF

cat > "$work/release.env" <<EOF
TEAMBUILDER_ENVIRONMENT_SLUG=smoke
TEAMBUILDER_ASPNETCORE_ENVIRONMENT=QA
TEAMBUILDER_WEB_ENVIRONMENT=qa
TEAMBUILDER_SITE_ADDRESS=localhost
TEAMBUILDER_VERSION=${TEAMBUILDER_VERSION:-0.0.0-smoke}
TEAMBUILDER_SOURCE_REVISION=${TEAMBUILDER_SOURCE_REVISION:-$(git rev-parse HEAD 2>/dev/null || echo unknown)}
TEAMBUILDER_SQL_SA_PASSWORD=$sql_password
TEAMBUILDER_SQL_CONNECTION_STRING=Server=sql;Database=TeamBuilderQA;User Id=sa;Password=$sql_password;Encrypt=True;TrustServerCertificate=True
TEAMBUILDER_OIDC_AUTHORITY=https://login.smoke.invalid/qa/v2.0
TEAMBUILDER_OIDC_API_AUDIENCE=api://teambuilder-smoke
TEAMBUILDER_OIDC_SPA_CLIENT_ID=teambuilder-smoke-spa
TEAMBUILDER_OIDC_SCOPE=openid profile api://teambuilder-smoke/access
EOF

if [[ "${TB_SKIP_BUILD:-}" != "1" ]]; then
  step "Building images"
  "${qa[@]}" build
  "${rel[@]}" --profile release build
fi

# ---------------------------------------------------------------- phase 1: local QA stack
step "Phase 1: local QA stack (LocalQA, developer tokens)"
"${qa[@]}" up -d --no-build --wait --wait-timeout 300
[[ "$("${qa[@]}" ps -a --format '{{.Service}} {{.ExitCode}}' | awk '$1=="migrate"{print $2}')" == "0" ]] \
  && pass "migration bundle ran before the API" || fail "migrate service did not exit 0"

TB_API_URL=http://localhost:8080 TB_HEALTH_URL=http://localhost:5080 TB_WEB_URL=http://localhost:8080 \
TEAMBUILDER_JWT_SIGNING_KEY="$signing_key" TB_SMOKE_RESULTS="$work/smoke-qa.json" \
  node scripts/qa/release-smoke.mjs

sqlq() { # sqlq <compose array name> <database> <query>
  local -n stack=$1
  "${stack[@]}" exec -T sql /opt/mssql-tools18/bin/sqlcmd -C -S localhost -U sa -P "$sql_password" -d "$2" -h -1 -W -b -Q "SET NOCOUNT ON; $3" | tr -d '\r' | head -n 1
}

players_before="$(sqlq qa TeamBuilderQA 'SELECT COUNT(*) FROM Players')"
"${qa[@]}" up -d --no-build --no-deps --force-recreate --wait --wait-timeout 180 api
players_after="$(sqlq qa TeamBuilderQA 'SELECT COUNT(*) FROM Players')"
[[ "$players_before" -ge 4 && "$players_before" == "$players_after" ]] \
  && pass "API redeploy kept the database ($players_after players)" || fail "player count changed across redeploy ($players_before -> $players_after)"

"${qa[@]}" logs api web > "$work/qa-logs.txt" 2>&1
if grep -Eq 'eyJ[A-Za-z0-9_-]{10,}|smoke-[a-z0-9]+-(host|pa|pb|pc)|lat=|lon=|0\.0223|150\.0456|1 Smoke Test Way' "$work/qa-logs.txt"; then
  grep -En 'eyJ|smoke-|lat=|lon=|0\.0223|150\.0456|Smoke Test Way' "$work/qa-logs.txt" | head -5 >&2
  fail "logs contain a token, subject, coordinate or private address"
fi
pass "API and web logs contain no JWT, OIDC subject, search coordinates or private address"
"${qa[@]}" down -v --remove-orphans >/dev/null

# ---------------------------------------------------------------- phase 2: release topology
step "Phase 2: release topology (QA behind HTTPS proxy)"
"${rel[@]}" --profile local-sql up -d --no-build --wait --wait-timeout 300 sql
"${rel[@]}" up -d --no-build --no-deps api

api_ready() { "${rel[@]}" exec -T api dotnet TeamBuilder.Api.dll healthcheck "${1:-ready}" >/dev/null 2>&1; }
wait_for() { # wait_for <seconds> <command...>
  local deadline=$((SECONDS + $1)); shift
  until "$@"; do (( SECONDS < deadline )) || return 1; sleep 2; done
}

wait_for 90 api_ready live || fail "API did not become live"
api_ready ready && fail "API reported ready on an empty database" || pass "not ready before the migration bundle"

"${rel[@]}" --profile release run --rm --no-deps migrate > "$work/migrate.txt"
grep -q "Done." "$work/migrate.txt" && pass "migration bundle applied to a clean database" || fail "migration bundle failed"
grep -q "No migrations were applied" <<<"$("${rel[@]}" --profile release run --rm --no-deps migrate)" \
  && pass "migration bundle is idempotent" || fail "second bundle run changed the schema"
sleep 1
api_ready ready && fail "API reported ready on an unstamped database" || pass "not ready before the environment stamp"

grep -q "Stamped for QA" <<<"$("${rel[@]}" --profile release run --rm --no-deps stamp)" && pass "database stamped for QA" || fail "stamp failed"
wait_for 30 api_ready ready && pass "ready after bundle and stamp" || fail "API not ready after bundle and stamp"

"${rel[@]}" up -d --no-build --wait --wait-timeout 180
curl_tls=(curl -sk --resolve localhost:443:127.0.0.1)
headers="$("${curl_tls[@]}" -D - -o "$work/index.html" https://localhost/)"
grep -q 'id="root"' "$work/index.html" && pass "web app served over HTTPS" || fail "web app not served"
grep -qi '^strict-transport-security:' <<<"$headers" && pass "HSTS on the site" || fail "no HSTS header"
grep -qi "^content-security-policy:.*connect-src 'self' https://login.smoke.invalid" <<<"$headers" \
  && pass "CSP allows only self and the OIDC authority for connections" || fail "CSP connect-src not as expected"
grep -q '"environment": "qa"' <<<"$("${curl_tls[@]}" https://localhost/config.js)" && pass "runtime config says qa (developer tokens off)" || fail "config.js not written"
[[ "$("${curl_tls[@]}" -o /dev/null -w '%{http_code}' http://localhost/ --max-redirs 0)" =~ ^30[178]$ ]] \
  && pass "plain HTTP redirects to HTTPS" || fail "HTTP did not redirect"

api_headers="$("${curl_tls[@]}" -D - -o /dev/null -H 'Origin: https://evil.example' https://localhost/api/v1/push/config)"
grep -q '^HTTP/[0-9.]* 200' <<<"$api_headers" || fail "anonymous API call through the proxy failed"
grep -qi '^access-control-allow-origin' <<<"$api_headers" && fail "CORS allowed an unlisted origin" || pass "CORS does not allow an unlisted origin"
grep -qi "^x-content-type-options: nosniff" <<<"$api_headers" && grep -qi "^content-security-policy: default-src 'none'" <<<"$api_headers" \
  && pass "API security headers present" || fail "API security headers missing"
[[ "$("${curl_tls[@]}" -o /dev/null -w '%{http_code}' https://localhost/api/v1/players/me)" == "401" ]] \
  && pass "API requires a bearer token through the proxy" || fail "unauthenticated /players/me was not 401"

TB_API_URL=https://localhost TB_WEB_URL=https://localhost TB_HEALTH_URL=skip TB_INSECURE_TLS=1 \
  node scripts/qa/release-smoke.mjs

api_image="$("${rel[@]}" config --images api 2>/dev/null | head -n 1)"
api_image="${api_image:-teambuilder-api:local}"
refuses() { # refuses <description> <env...>: the QA API must exit non-zero with "Refusing to start"
  local description="$1"; shift
  local out
  if out="$(timeout 60 docker run --rm -e ASPNETCORE_ENVIRONMENT=QA \
      -e ConnectionStrings__TeamBuilderSql='Server=sql;Database=x;User Id=u;Password=p' \
      -e Jwt__Authority=https://login.smoke.invalid/qa/v2.0 -e Jwt__Audience=api://teambuilder-smoke "$@" "$api_image" 2>&1)"; then
    fail "QA API started with $description"
  fi
  grep -q "Refusing to start in QA" <<<"$out" && pass "QA API refuses to start with $description" || fail "unexpected failure for $description: $(tail -n 3 <<<"$out")"
}
refuses "a developer signing key" -e Jwt__SigningKey=0123456789abcdef0123456789abcdef
refuses "startup migrations enabled" -e Database__ApplyMigrationsOnStartup=true
refuses "an http OIDC authority" -e Jwt__Authority=http://login.smoke.invalid/
refuses "a wildcard CORS origin" -e AllowedOrigins='*'
refuses "a placeholder connection string" -e ConnectionStrings__TeamBuilderSql='Server=#{AzureSql.ServerName}'
if out="$(timeout 60 docker run --rm -e ASPNETCORE_ENVIRONMENT=QA -e ForwardedHeaders__Enabled=true -e ForwardedHeaders__KnownNetworks__0=0.0.0.0/0 \
    -e ConnectionStrings__TeamBuilderSql='Server=sql;Database=x;User Id=u;Password=p' \
    -e Jwt__Authority=https://login.smoke.invalid/qa/v2.0 -e Jwt__Audience=api://teambuilder-smoke "$api_image" 2>&1)"; then
  fail "API started trusting forwarded headers from 0.0.0.0/0"
fi
grep -q "must not contain a /0 network" <<<"$out" && pass "API refuses to trust forwarded headers from every address" || fail "unexpected /0 failure"

step "Backup and restore to a separate database"
"${rel[@]}" exec -T sql /opt/mssql-tools18/bin/sqlcmd -C -S localhost -U sa -P "$sql_password" -b -Q \
  "BACKUP DATABASE [TeamBuilderQA] TO DISK = N'/var/opt/mssql/data/teambuilder-qa.bak' WITH COPY_ONLY, INIT, CHECKSUM; \
   RESTORE DATABASE [TeamBuilderQA_Restore] FROM DISK = N'/var/opt/mssql/data/teambuilder-qa.bak' WITH CHECKSUM, \
     MOVE N'TeamBuilderQA' TO N'/var/opt/mssql/data/TeamBuilderQA_Restore.mdf', \
     MOVE N'TeamBuilderQA_log' TO N'/var/opt/mssql/data/TeamBuilderQA_Restore_log.ldf';" > "$work/backup.txt"
schema_query="SELECT CONCAT(COUNT(*), ':', MAX(MigrationId)) FROM __EFMigrationsHistory"
source_schema="$(sqlq rel TeamBuilderQA "$schema_query")"
restored_schema="$(sqlq rel TeamBuilderQA_Restore "$schema_query")"
[[ -n "$source_schema" && "$source_schema" == "$restored_schema" ]] \
  && pass "restored database has the same migrations ($restored_schema)" || fail "restored schema differs ($source_schema vs $restored_schema)"
restored_cs="Server=sql;Database=TeamBuilderQA_Restore;User Id=sa;Password=$sql_password;Encrypt=True;TrustServerCertificate=True"
"${rel[@]}" --profile release run --rm --no-deps -e ConnectionStrings__TeamBuilderSql="$restored_cs" stamp database status > "$work/restore-status.txt" \
  && pass "restored database has no pending migrations" || fail "restored database has pending migrations"
grep -q "Stamped for QA" <<<"$("${rel[@]}" --profile release run --rm --no-deps -e ConnectionStrings__TeamBuilderSql="$restored_cs" stamp database show-environment)" \
  && pass "restored copy keeps its environment stamp (re-stamp a copy before using it elsewhere)" || fail "restored stamp missing"

step "Container smoke passed"
