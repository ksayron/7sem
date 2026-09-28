# ADR-007: Saga for distributed loan request

Status: Accepted — 2026-09-17

## Context

Creating a request requires exclusive Catalog availability and Economy funds in
independent databases; no local transaction can make both atomic.

## Decision

Lending orchestrates a durable Saga: reserve copy, reserve points, create Loan;
release acquired resources on failure or timeout.

## Consequences

The failure model is explicit and recoverable without database repair. Users see
an accepted asynchronous request and process state; compensation and late-event
logic are required.
