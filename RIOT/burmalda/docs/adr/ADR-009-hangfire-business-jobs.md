# ADR-009: Hangfire for scheduled business workflows

Status: Accepted — 2026-09-17

## Context

Overdue detection, reminders, and reservation expiry must survive process
restart and be observable.

## Decision

Use Hangfire with Lending-owned PostgreSQL storage for business schedules. Use a
lightweight hosted worker for continuous outbox delivery.

## Consequences

Business jobs gain persistence, retries, and a dashboard. Lending owns the job
schema and operational load. Infrastructure polling does not clutter business
job history.
