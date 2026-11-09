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

## Run with Docker — LR7 + LR8

Docker Desktop with Linux containers is the only runtime prerequisite. From this directory:

```powershell
docker compose up -d --build
docker compose ps
# Web: http://localhost:5300
# Five-node control panel: http://localhost:5300/raft-lab
# Optional LR6 game, C# brokers and subscribers:
docker compose --profile pubsub up -d --build
```

The default stack includes the web client, gateway, six business APIs, laboratory API,
five independent Raft containers, PostgreSQL primary/standby, RabbitMQ and telemetry.
Images build inside Docker; Visual Studio and a host .NET SDK are unnecessary.
Copy `.env.example` to `.env` to override local credentials. Services use Docker DNS;
the browser uses the same origin through nginx. Existing PostgreSQL/RabbitMQ volumes
are preserved. New Docker Raft and C# broker histories use their own named volumes.

```powershell
docker compose logs -f raft-node-1 concurrency-lab
./tools/Set-RaftNode.ps1 -NodeId 5 -Action Stop
./tools/Set-RaftNode.ps1 -NodeId 5 -Action Start
docker compose --profile pubsub down  # Keeps all persistent volumes.
```

The seeded local administrator is `admin@burmalda.local` / `ChangeMe123!`.
Gateway: `http://localhost:5000`; RabbitMQ: `http://localhost:15672`;
Jaeger: `http://localhost:16686`; metrics: `http://localhost:8889/metrics`.
See the [combined delivery and demonstration guide](docs/labs-7-8-docker.md).

## Local debugging

For host debugging, install the SDK from `global.json` and PowerShell 7.4+.
Stop application containers first to free their ports. Start the host version explicitly:

```powershell
./tools/Start-Lab.ps1 -Local -Full
./tools/Stop-Lab.ps1 -Local
# Or start infrastructure, then five host nodes before the Visual Studio profile:
docker compose up -d postgres postgres-replica rabbitmq
./tools/Start-RaftCluster.ps1 -Local
```

The normal startup scripts default to Docker. `-Local` explicitly selects the old
process-based debugger workflow. Never run both controllers against the same databases.
Services retain the development schema bootstrap; restarting does not erase data.

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

