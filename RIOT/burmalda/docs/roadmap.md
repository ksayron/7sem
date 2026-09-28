# Runnable vertical-slice roadmap

## Phase 1 — implemented: identity and listing

Run Identity, Catalog, Gateway, PostgreSQL, RabbitMQ, and telemetry. A user can
register, authenticate, create a book, list a physical copy, and search the
Catalog projection. Admins can control accounts and moderate copies.

## Phase 2 — implemented: distributed request

Add Lending and Economy. The request Saga atomically reserves one copy under
contention, reserves BD, creates a Loan, compensates failures/timeouts, and
notifies the owner through an outbox/inbox message chain.

## Phase 3 — implemented: complete lending lifecycle

Add acceptance, handover code, activation/payment, return, completion/reward,
copy release, loan history, persisted notifications, and asynchronous
reputation.

## Phase 4 — implemented: time and violations

Add Hangfire overdue detection, handover/Saga expiry, reminders, Economy-owned
fines, operational dashboards, role-gated repair controls, retry queues, and
DLQs.

## Phase 5 — next: hardening

- replace development schema bootstrap with checked-in EF migrations;
- add Testcontainers integration tests for PostgreSQL, RabbitMQ, outbox, inbox,
  duplicate events, concurrent reservation, and the full success/failure flows;
- add refresh-token family reuse detection and secret management;
- add explicit readiness checks for PostgreSQL/RabbitMQ/OTLP;
- add a signed, audited DLQ replay endpoint and retention jobs;
- add pagination cursors and OpenAPI documents.

## Phase 6 — optional product depth

QR handover, ratings, disputes with evidence, copy queues, extensions,
achievements, recommendation projections, and real-time notification delivery.
Cloud orchestration, Kubernetes, Terraform, service mesh, and production CI/CD
remain separate future phases.
