# ADR-001: Microservice boundaries

Status: Accepted — 2026-09-17

## Context

The academic goal requires real independent consistency boundaries, but a
one-developer project cannot sustain a service per entity.

## Decision

Use Identity, Catalog, Lending, Economy, Reputation, and Notifications. Keep
fines monetary in Economy, violation detection in Lending, and admin commands
inside each owning service.

## Consequences

The design demonstrates independent databases and eventual consistency while
keeping Loan/Handover/Return together. It adds six deployables and cross-service
failure handling; Authors, Genres, and Admin are deliberately not extra services.
