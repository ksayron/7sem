# ADR-002: RabbitMQ as event bus

Status: Accepted — 2026-09-17

## Context

Loan facts have multiple independent consumers and should not form synchronous
HTTP chains.

## Decision

Use RabbitMQ topic exchanges with explicit command/event routing, durable
service queues, persistent messages, retries, and DLQs.

## Consequences

Producers do not know every consumer and services may be temporarily offline.
Delivery is at least once, so every consumer requires idempotency and operators
must monitor poison queues.
