# ADR-006: Idempotent consumers and Inbox

Status: Accepted — 2026-09-17

## Context

RabbitMQ can redeliver after consumer failure or lost acknowledgements. Duplicate
financial effects are unacceptable.

## Decision

Store `(MessageId, Consumer)` with handler changes in one transaction before
ACK. Add business idempotency keys to financial transactions and fines.

## Consequences

Technical and semantic duplicates do not double-charge or reward users. Inbox
storage and transaction boundaries add overhead and require retention policy.
