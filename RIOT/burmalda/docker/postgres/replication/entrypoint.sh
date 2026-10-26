#!/bin/sh
set -eu
export PGDATA="${PGDATA:-/var/lib/postgresql/data}"
mkdir -p /control "$PGDATA"
# This shared durable fence is checked on every container start, including Docker restarts.
if [ -f "/control/$NODE_ID.fenced" ]; then
  echo "$NODE_ID is fenced. Use LR5 Recover to rejoin it as a standby." >&2
  exit 1
fi
if [ "$NODE_ID" = postgres-replica ] && [ ! -s "$PGDATA/PG_VERSION" ]; then
  chown postgres:postgres "$PGDATA"
  chmod 700 "$PGDATA"
  export PGPASSWORD="$REPLICATION_PASSWORD"
  slot=slot_postgres_replica
  until psql -h postgres -p 5432 -U "$REPLICATION_USER" -d postgres -Atc 'SELECT 1' >/dev/null 2>&1; do sleep 1; done
  # Primary provisioning is performed before the replica is first started.
  psql -h postgres -p 5432 -U "$REPLICATION_USER" -d postgres -v ON_ERROR_STOP=1 -v slot="$slot" <<'SQL'
SELECT pg_create_physical_replication_slot(:'slot') WHERE NOT EXISTS (SELECT FROM pg_replication_slots WHERE slot_name = :'slot');
SQL
  gosu postgres pg_basebackup -h postgres -p 5432 -U "$REPLICATION_USER" -D "$PGDATA" -X stream -R -S "$slot"
fi
exec docker-entrypoint.sh postgres -p 5432 -c wal_level=replica -c max_wal_senders=10 -c max_replication_slots=10 -c hot_standby=on -c wal_log_hints=on -c wal_keep_size=128MB -c max_slot_wal_keep_size=512MB
