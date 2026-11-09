#!/bin/sh
set -eu
export PGPASSWORD="$POSTGRES_PASSWORD"
attempt=0
while [ "$attempt" -lt 90 ]; do
  writers=""
  for node in postgres postgres-replica; do
    role=$(psql -h "$node" -U "$POSTGRES_USER" -d postgres -Atc "SELECT NOT pg_is_in_recovery() AND NOT current_setting('default_transaction_read_only')::boolean" 2>/dev/null || true)
    if [ "$role" = t ]; then writers="$writers $node"; fi
  done
  set -- $writers
  if [ "$#" -gt 1 ]; then echo 'Two writable PostgreSQL nodes; provisioning refused.' >&2; exit 1; fi
  if [ "$#" -eq 1 ]; then
    export PGHOST="$1" PROVISION_REMOTE=true
    sh /docker-entrypoint-initdb.d/10-init-databases.sh
    echo "Database provisioning completed on $PGHOST."
    exit 0
  fi
  attempt=$((attempt+1))
  sleep 2
done
echo 'No writable PostgreSQL within startup budget; fences and data preserved.' >&2
exit 1
