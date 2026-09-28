# ADR-005: Transactional outbox

Status: Accepted — 2026-09-17

## Context

A process can fail after committing PostgreSQL but before publishing RabbitMQ,
or publish before a rolled-back commit.

## Decision

Insert serialized envelopes into the owning database transaction. A hosted
publisher retries unpublished rows and records failure details.

## Consequences

State and publication intent are atomic. Event delivery is delayed and at least
once; outbox growth, lag, and permanently failing rows need operational care.
