#!/usr/bin/env bash
# Validates an EF Core migration bundle against a real SQL Server before it is released:
#   1. a clean database gets every migration,
#   2. a second run changes nothing (idempotent),
#   3. a database at the previous release's schema (one migration behind) upgrades cleanly,
#   4. both databases end with exactly the migrations in source control.
#
#   scripts/qa/validate-migration-bundle.sh <efbundle> <sql container> <sa password>
#
# <sql container> is a running mcr.microsoft.com/mssql/server container reachable on
# localhost:<TB_SQL_PORT, default 1433> from this host (sqlcmd runs inside it). Databases are
# created with unique names and dropped afterwards.
set -euo pipefail

bundle="$1"; container="$2"; password="$3"
port="${TB_SQL_PORT:-1433}"
root="$(cd "$(dirname "$0")/../.." && pwd)"
suffix="$(date +%s)$RANDOM"
clean_db="TbBundleClean_$suffix"
prior_db="TbBundlePrior_$suffix"

mapfile -t migrations < <(find "$root/src/TeamBuilder.Infrastructure/Persistence/Migrations" -maxdepth 1 -name '2*.cs' ! -name '*.Designer.cs' -printf '%f\n' | sed 's/\.cs$//' | sort)
(( ${#migrations[@]} >= 2 )) || { echo "expected at least two migrations in source control" >&2; exit 1; }
latest="${migrations[-1]}"
previous="${migrations[-2]}"

cs() { echo "Server=localhost,$port;Database=$1;User Id=sa;Password=$password;Encrypt=True;TrustServerCertificate=True"; }
sqlq() { docker exec "$container" /opt/mssql-tools18/bin/sqlcmd -C -S localhost -U sa -P "$password" -d "$1" -h -1 -W -b -Q "SET NOCOUNT ON; $2" | tr -d '\r' | head -n 1; }
logs="$(mktemp -d)"
cleanup() { rm -rf "$logs"; for db in "$clean_db" "$prior_db"; do sqlq master "IF DB_ID(N'$db') IS NOT NULL BEGIN ALTER DATABASE [$db] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [$db]; END" >/dev/null 2>&1 || true; done; }
trap cleanup EXIT
pass() { printf 'PASS %s\n' "$*"; }
fail() { printf 'FAIL %s\n' "$*" >&2; exit 1; }
history() { sqlq "$1" "SELECT CONCAT(COUNT(*), ':', MAX(MigrationId)) FROM __EFMigrationsHistory"; }
expected="${#migrations[@]}:$latest"

start=$SECONDS
"$bundle" --connection "$(cs "$clean_db")" > "$logs/bundle-clean.log"
[[ "$(history "$clean_db")" == "$expected" ]] && pass "clean database migrated to $latest in $((SECONDS - start)) s" || fail "clean database history $(history "$clean_db"), expected $expected"

grep -q "No migrations were applied" <<<"$("$bundle" --connection "$(cs "$clean_db")")" && pass "second run is a no-op" || fail "second run applied migrations"

"$bundle" "$previous" --connection "$(cs "$prior_db")" > "$logs/bundle-prior.log"
[[ "$(history "$prior_db")" == "$(( ${#migrations[@]} - 1 )):$previous" ]] && pass "prepared prior schema ($previous)" || fail "could not prepare the prior schema"
"$bundle" --connection "$(cs "$prior_db")" > "$logs/bundle-upgrade.log"
grep -q "$latest" "$logs/bundle-upgrade.log" && [[ "$(history "$prior_db")" == "$expected" ]] \
  && pass "prior schema upgraded to $latest" || fail "upgrade from the prior schema failed"

tables_clean="$(sqlq "$clean_db" "SELECT COUNT(*) FROM sys.tables")"
tables_prior="$(sqlq "$prior_db" "SELECT COUNT(*) FROM sys.tables")"
[[ "$tables_clean" == "$tables_prior" ]] && pass "clean and upgraded databases have the same $tables_clean tables" || fail "table counts differ ($tables_clean vs $tables_prior)"
