#!/bin/sh
set -eu
# Idempotent: works on the current LR5 writer, including promoted postgres-replica.
create_library_database() {
  psql -v ON_ERROR_STOP=1 -U "$POSTGRES_USER" -d postgres -v db="$1" -v role="$2" -v password="$3" <<'SQL'
SELECT format('CREATE ROLE %I LOGIN PASSWORD %L', :'role', :'password') WHERE NOT EXISTS (SELECT FROM pg_roles WHERE rolname = :'role') \gexec
SELECT format('CREATE DATABASE %I OWNER %I', :'db', :'role') WHERE NOT EXISTS (SELECT FROM pg_database WHERE datname = :'db') \gexec
SELECT format('REVOKE ALL ON DATABASE %I FROM PUBLIC', :'db') \gexec
SELECT format('GRANT CONNECT, TEMPORARY ON DATABASE %I TO %I', :'db', :'role') \gexec
SQL
  psql -v ON_ERROR_STOP=1 -U "$POSTGRES_USER" -d "$1" -v role="$2" <<'SQL'
REVOKE CREATE ON SCHEMA public FROM PUBLIC;
SELECT format('GRANT ALL ON SCHEMA public TO %I', :'role') \gexec
SQL
}
create_library_database library_game_db library_game_app "${LIBRARY_GAME_DB_PASSWORD:-library_game_dev}"
create_library_database library_subscribers_db library_subscribers_app "${LIBRARY_SUBSCRIBERS_DB_PASSWORD:-library_subscribers_dev}"
