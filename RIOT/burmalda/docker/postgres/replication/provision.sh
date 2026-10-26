#!/bin/sh
set -eu
psql -v ON_ERROR_STOP=1 -U "$POSTGRES_USER" -d postgres -v repl_user="$REPLICATION_USER" -v repl_password="$REPLICATION_PASSWORD" <<'SQL'
SELECT format('CREATE ROLE %I WITH LOGIN REPLICATION PASSWORD %L', :'repl_user', :'repl_password')
WHERE NOT EXISTS (SELECT FROM pg_roles WHERE rolname = :'repl_user') \gexec
SELECT format('ALTER ROLE %I WITH LOGIN REPLICATION PASSWORD %L', :'repl_user', :'repl_password') \gexec
CHECKPOINT;
SQL
if ! grep -q '# LR5 streaming replication' "$PGDATA/pg_hba.conf"; then
  printf '\n# LR5 streaming replication\nhost replication %s all scram-sha-256\n' "$REPLICATION_USER" >> "$PGDATA/pg_hba.conf"
fi
psql -v ON_ERROR_STOP=1 -U "$POSTGRES_USER" -d postgres -c 'SELECT pg_reload_conf()'
