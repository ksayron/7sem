# Local infrastructure

The local environment uses one PostgreSQL server container. Database-per-service
is preserved as logical ownership: every service has a separate database and a
separate login role.

| Service | Database | Role | Container connection string |
| --- | --- | --- | --- |
| Identity | `identity_db` | `identity_app` | `Host=postgres;Port=5432;Database=identity_db;Username=identity_app;Password=${IDENTITY_DB_PASSWORD}` |
| Catalog | `catalog_db` | `catalog_app` | `Host=postgres;Port=5432;Database=catalog_db;Username=catalog_app;Password=${CATALOG_DB_PASSWORD}` |
| Lending | `lending_db` | `lending_app` | `Host=postgres;Port=5432;Database=lending_db;Username=lending_app;Password=${LENDING_DB_PASSWORD}` |
| Economy | `economy_db` | `economy_app` | `Host=postgres;Port=5432;Database=economy_db;Username=economy_app;Password=${ECONOMY_DB_PASSWORD}` |
| Reputation | `reputation_db` | `reputation_app` | `Host=postgres;Port=5432;Database=reputation_db;Username=reputation_app;Password=${REPUTATION_DB_PASSWORD}` |
| Notifications | `notification_db` | `notification_app` | `Host=postgres;Port=5432;Database=notification_db;Username=notification_app;Password=${NOTIFICATION_DB_PASSWORD}` |

The single PostgreSQL process is a local-development deployment choice, not a
shared-data model. A service must connect with its own role and must never query
another service's database. Hangfire tables should live in the database of the
service that owns each scheduled workflow (for example, overdue detection in
`lending_db`), so no separate shared Hangfire database is needed.

## Start the stack

Optionally copy `.env.example` to `.env` and change the local credentials, then
run:

```powershell
docker compose -f docker-compose.legacy.yml up -d
docker compose -f docker-compose.legacy.yml ps
```

RabbitMQ management is available at <http://localhost:15672> and Jaeger at
<http://localhost:16686>.

PostgreSQL initialization scripts run only when the `postgres-data` volume is
first created. If the database list or initialization credentials are changed
afterward, either migrate the existing instance or deliberately recreate the
local volume with `docker compose -f docker-compose.legacy.yml down -v` (this deletes all local data).

