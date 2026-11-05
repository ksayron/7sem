# Burmalda Доброта

Burmalda Доброта is an event-driven P2P book-lending platform. The repository
contains a standalone Blazor WebAssembly client, an API gateway, and six
independently persisted services: Identity, Catalog, Lending, Economy,
Reputation, and Notifications.

An isolated [concurrency lab](docs/concurrency-lab.md) adds a development-only
API, Blazor comparison page, and C# command-line client for LR 2 and LR 3.

The current implementation covers the complete first two vertical milestones:

- JWT registration/login/refresh and role-based administration;
- books, physical copies, availability, and a catalog read projection;
- BD wallets, reservations, append-only transactions, rewards, and fines;
- the loan-request Saga with copy/point reservation and compensation;
- owner acceptance, handover code, activation, return, and completion;
- Hangfire overdue detection, workflow expiry, and return reminders;
- asynchronous reputation and persisted notification projections;
- transactional outboxes and idempotent inbox consumers;
- durable RabbitMQ queues, retry queues, and dead-letter queues;
- YARP routing, rate limiting, health aggregation, OpenTelemetry, and Jaeger;
- role-gated admin controls and operational messaging counters.

The Blazor client covers account access, discovery and loan requests, shelf
management, loan transitions, wallet/reputation summaries, notifications, and
administrator health monitoring.

## Run locally

Copy `.env.example` to `.env` if you want to change infrastructure ports or
credentials, then start the dependencies:

```powershell
docker compose up -d
docker compose ps
```

`docker-compose.yml` intentionally contains infrastructure only: two PostgreSQL servers,
RabbitMQ, Jaeger, and the OpenTelemetry Collector. Application services run as
ordinary .NET processes. Consequently, rebuilding or stopping the solution does
not restart infrastructure, and restarting infrastructure does not manage any
Burmalda application process.

Open `Burmalda.slnx` in Visual Studio and select `Full application` to start the
client, gateway, six business services, and the concurrency lab under the debugger. `All backend services`
and `Web client` are also available for focused work. Each project has a stable
HTTP port in its `Properties/launchSettings.json` file.

Alternatively, start the projects in separate terminals:

```powershell
dotnet run --project src/Services/Identity/Burmalda.Identity.Api -- --urls http://localhost:5101
dotnet run --project src/Services/Catalog/Burmalda.Catalog.Api -- --urls http://localhost:5102
dotnet run --project src/Services/ConcurrencyLab/Burmalda.ConcurrencyLab.Api -- --urls http://localhost:5107
dotnet run --project src/Services/Lending/Burmalda.Lending.Api -- --urls http://localhost:5103
dotnet run --project src/Services/Economy/Burmalda.Economy.Api -- --urls http://localhost:5104
dotnet run --project src/Services/Reputation/Burmalda.Reputation.Api -- --urls http://localhost:5105
dotnet run --project src/Services/Notifications/Burmalda.Notifications.Api -- --urls http://localhost:5106
dotnet run --project src/Gateway/Burmalda.Gateway -- --urls http://localhost:5000
dotnet run --project src/Clients/Burmalda.Web -- --urls http://localhost:5300
```

The web client is available at `http://localhost:5300` and the gateway at
`http://localhost:5000`. RabbitMQ management is at
`http://localhost:15672`, Jaeger at `http://localhost:16686`, and aggregated
Prometheus metrics at `http://localhost:8889/metrics`.

The seeded local administrator is `admin@burmalda.local` / `ChangeMe123!` unless
overridden through application configuration (`AdminSeed__Email` and
`AdminSeed__Password`). Change both the administrator password and JWT signing
key outside disposable local environments.

Services create their schemas automatically in local development. This keeps a
new checkout runnable; versioned EF migrations should replace `EnsureCreated`
before a shared or production-like environment is introduced.

## Build and test

```powershell
dotnet restore Burmalda.slnx -m:1
dotnet build Burmalda.slnx --no-restore -m:1
dotnet test tests/Burmalda.Domain.Tests/Burmalda.Domain.Tests.csproj --no-build
```

## First end-to-end flow

1. Register Alice and Bob through `POST /api/identity/register`, then log in.
2. Alice creates a bibliographic book with `POST /api/catalog/books` and lists
   her physical copy with `POST /api/catalog/copies`.
3. Bob calls `POST /api/loans/requests` with the copy id.
4. Poll `GET /api/loans/requests/{sagaId}` until `Completed`; the response then
   contains the created loan id.
5. Alice calls `POST /api/loans/{id}/accept` and shares the returned handover
   code with Bob.
6. Bob calls `POST /api/loans/{id}/handover/confirm`.
7. Bob initiates return; Alice confirms it.

The complete architecture, contracts, and tradeoffs are under [docs](docs/architecture.md).

## Database and transaction lab

`http://localhost:5300/concurrency-lab` now includes PostgreSQL experiments for
two-client single-copy reservations, partial writes, COMMIT/ROLLBACK, and a
step-by-step saga compensation model. Discover also tracks the real RabbitMQ
Lending Saga after a book request.

```powershell
./tools/Start-Lab.ps1 -PostgresPort 5547 -Full
./tools/Stop-Lab.ps1
```

PostgreSQL host ports default to 5547 and 5548 (`-PostgresPort` / `-ReplicaPort`);
both containers listen internally on 5432. The script provisions
the isolated lab database on existing volumes. API logs use compact colored
`[info]`, `[warn]`, `[error]` labels. See [the lab guide and measured results](docs/lab-4-database-transactions.md).

## Replication and failover lab (LR5)

`http://localhost:5300/replication-lab` uses two real PostgreSQL 17 servers in
Compose with physical WAL streaming. It reads existing Catalog books and copies,
updates copy availability transactionally, stops actual containers, promotes the
standby, and rejoins the former primary with `pg_rewind`. The promoted server
remains writable; application connections follow the current primary.

```powershell
./tools/Start-Lab.ps1 -ReplicationOnly
dotnet run --project tools/ConcurrencyLab.Client --no-build -- rep-create
# Use an actual Copy ID printed by rep-create:
dotnet run --project tools/ConcurrencyLab.Client --no-build -- rep-cycle <copy-guid> 2
./tools/Stop-Lab.ps1
```

Docker is required, including for `-ReplicationOnly`. Use `-Full` to run the
business services and add Catalog data through the application. Existing database
volumes are preserved; a new lab session creates only a journal. Physical
replication includes every service database. See [the LR5 guide and measured
results](docs/lab-5-replication-failover.md). Historical file-model evidence remains
under `lab-results/replication-*`; current evidence uses `streaming-replication-*`.

## Library game and C# Pub/Sub lab (LR6)

`http://localhost:5300/library-game` adds a small first-person library game to the
existing Blazor WebAssembly client. Scene control and game rules use C# through
Blazor.ThreeJS; only browser input and the library's graphics backend use JavaScript.
Two custom C# brokers and a separate subscriber service deliver achievements,
abilities, notifications and analytics without changing existing RabbitMQ contracts.

```powershell
./tools/Start-PubSubLab.ps1
# Experiment panel: http://localhost:5300/pubsub-lab
dotnet test tests/Burmalda.LibraryGame.Tests
dotnet run --project tools/LibraryGame.Verify -- .
./tools/Stop-PubSubLab.ps1
```

See [the LR6 guide](docs/lab-6-pubsub-library.md) for controls, process failures,
durable queues, retry semantics and a requirement-by-requirement demonstration.

## Raft PostgreSQL control (LR7)

`http://localhost:5300/raft-lab` shows five independent C# Raft participants,
their persistent logs, elections, quorum reads, snapshots and real PostgreSQL
switchover. Catalog data remains in PostgreSQL. LR5 infrastructure controls now
use the same Raft cluster; reads never promote a database.

```powershell
./tools/Start-RaftLab.ps1
dotnet test tests/Burmalda.Raft.Tests
node tools/verify-raft-lab.cjs
```

See [the LR7 guide](docs/lab-7-raft.md) for protocol rules, safe async failover,
recovery, process/network experiments and the single-host boundary.

## Administration and monitoring

Admin and moderator endpoints are routed under `/api/admin/*`. They cover user
roles/deactivation, copy moderation and forced release, loan/Saga inspection and
timeout, economy adjustments, reputation adjustments, notification broadcasts,
and per-service outbox/inbox counters. `GET /api/admin/system/health` aggregates
service readiness. The Lending Hangfire dashboard is routed at `/hangfire` and
requires an Admin JWT.

Operational APIs intentionally do not query foreign databases. Each control is
performed by the service that owns the affected data.

