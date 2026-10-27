#!/usr/bin/env bash
# Starts a throwaway trust-auth Postgres on port 54329 and seeds tableplsu_test.
# Usage: scripts/start-testdb.sh <data-dir>
set -e
B="/c/Program Files/PostgreSQL/18/bin"; D="${1:?data dir}"
[ -f "$D/PG_VERSION" ] || "$B/initdb.exe" -D "$D" -U postgres --auth=trust -E UTF8 >/dev/null
"$B/pg_ctl.exe" -D "$D" -o "-p 54329" -l "$D/log.txt" start >/dev/null 2>&1 &
sleep 3
if ! "$B/psql.exe" -h localhost -p 54329 -U postgres -tAc "select 1 from pg_database where datname='tableplsu_test'" | grep -q 1; then
  "$B/psql.exe" -h localhost -p 54329 -U postgres -qc "create database tableplsu_test"
  "$B/psql.exe" -h localhost -p 54329 -U postgres -d tableplsu_test -q -f "$(dirname "$0")/../tests/seed.sql"
fi
echo "TABLEPLSU_TEST_PG=Host=localhost;Port=54329;Username=postgres;Database=tableplsu_test"
