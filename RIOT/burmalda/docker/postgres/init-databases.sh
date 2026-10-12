#!/bin/sh
set -eu

# One PostgreSQL server hosts isolated logical databases. Each application role
# owns only its service database and must not read another service's tables.
create_service_database() {
  database_name="$1"
  role_name="$2"
  role_password="$3"

  psql --set=ON_ERROR_STOP=1 \
    --username "$POSTGRES_USER" \
    --dbname postgres \
    --set=database_name="$database_name" \
    --set=role_name="$role_name" \
    --set=role_password="$role_password" <<-'SQL'
    SELECT format('CREATE ROLE %I LOGIN PASSWORD %L', :'role_name', :'role_password')
    WHERE NOT EXISTS (SELECT FROM pg_roles WHERE rolname = :'role_name')
    \gexec

    SELECT format('CREATE DATABASE %I OWNER %I', :'database_name', :'role_name')
    WHERE NOT EXISTS (SELECT FROM pg_database WHERE datname = :'database_name')
    \gexec

    SELECT format('REVOKE ALL ON DATABASE %I FROM PUBLIC', :'database_name')
    \gexec
    SELECT format('GRANT CONNECT, TEMPORARY ON DATABASE %I TO %I', :'database_name', :'role_name')
    \gexec
SQL

  psql --set=ON_ERROR_STOP=1 \
    --username "$POSTGRES_USER" \
    --dbname "$database_name" \
    --set=role_name="$role_name" <<-'SQL'
    REVOKE CREATE ON SCHEMA public FROM PUBLIC;
    SELECT format('GRANT ALL ON SCHEMA public TO %I', :'role_name')
    \gexec
SQL
}

create_service_database identity_db identity_app "$IDENTITY_DB_PASSWORD"
create_service_database catalog_db catalog_app "$CATALOG_DB_PASSWORD"
create_service_database lending_db lending_app "$LENDING_DB_PASSWORD"
create_service_database economy_db economy_app "$ECONOMY_DB_PASSWORD"
create_service_database reputation_db reputation_app "$REPUTATION_DB_PASSWORD"
create_service_database notification_db notification_app "$NOTIFICATION_DB_PASSWORD"
create_service_database concurrency_lab_db concurrency_lab_app "${CONCURRENCY_LAB_DB_PASSWORD:-concurrency_lab_dev}"
