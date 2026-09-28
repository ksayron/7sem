# ADR-003: Database per service

Status: Accepted — 2026-09-17

## Context

A shared schema would allow accidental coupling and local joins that bypass
service contracts.

## Decision

Give every service a separate PostgreSQL database and login. One PostgreSQL
container may host them for local development, but roles cannot read each
other's databases.

## Consequences

Ownership is enforceable and each service can evolve its schema. Cross-service
transactions and joins are unavailable; events, APIs, projections, and Sagas
handle that work.
