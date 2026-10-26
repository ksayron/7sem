#!/bin/sh
set -eu
test -s "$PGDATA/PG_VERSION"
# Only used when no promotion happened and the original source is still authoritative.
sed -i '/^default_transaction_read_only[[:space:]]*=/d' "$PGDATA/postgresql.auto.conf"
rm -f "/control/$NODE_ID.fenced"
