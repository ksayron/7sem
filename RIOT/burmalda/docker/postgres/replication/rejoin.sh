#!/bin/sh
# Runs in a temporary maintenance container while the target database container is stopped.
set -eu
export PGDATA="${PGDATA:-/var/lib/postgresql/data}"
case "$SOURCE_NODE" in postgres|postgres-replica) ;; *) exit 2;; esac
case "$NODE_ID" in postgres|postgres-replica) ;; *) exit 2;; esac
test "$SOURCE_NODE" != "$NODE_ID"
test -f "/control/$NODE_ID.fenced"
test -s "$PGDATA/PG_VERSION"
export PGPASSWORD="$POSTGRES_PASSWORD"
# Failure leaves the fence in place. Never erase/reinitialize a failed target volume.
gosu postgres pg_rewind --target-pgdata="$PGDATA" --source-server="host=$SOURCE_NODE port=5432 user=$POSTGRES_USER dbname=postgres" --progress
export PGPASSWORD="$REPLICATION_PASSWORD"
slot="slot_$(printf '%s' "$NODE_ID" | tr '-' '_')"
psql -v ON_ERROR_STOP=1 -h "$SOURCE_NODE" -p 5432 -U "$REPLICATION_USER" -d postgres -v slot="$slot" <<'SQL'
SELECT pg_create_physical_replication_slot(:'slot') WHERE NOT EXISTS (SELECT FROM pg_replication_slots WHERE slot_name = :'slot');
SQL
# pg_rewind copies the source configuration. Discard only its recovery settings.
sed -i '/^primary_conninfo[[:space:]]*=/d; /^primary_slot_name[[:space:]]*=/d' "$PGDATA/postgresql.auto.conf"
# Password is kept in a postgres-owned passfile, never returned in API output.
printf '%s:5432:*:%s:%s\n' "$SOURCE_NODE" "$REPLICATION_USER" "$REPLICATION_PASSWORD" > "$PGDATA/.pgpass"
chmod 600 "$PGDATA/.pgpass"
chown postgres:postgres "$PGDATA/.pgpass"
printf "primary_conninfo = 'host=%s port=5432 user=%s passfile=%s/.pgpass application_name=%s'\nprimary_slot_name = '%s'\n" "$SOURCE_NODE" "$REPLICATION_USER" "$PGDATA" "$NODE_ID" "$slot" >> "$PGDATA/postgresql.auto.conf"
touch "$PGDATA/standby.signal"
chown postgres:postgres "$PGDATA/standby.signal" "$PGDATA/postgresql.auto.conf"
rm "/control/$NODE_ID.fenced"
