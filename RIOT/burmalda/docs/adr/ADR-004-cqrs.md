# ADR-004: CQRS

Status: Accepted — 2026-09-17

## Context

Loan transitions require aggregate invariants while catalog search requires a
denormalized availability view.

## Decision

Separate command and query code paths. Use explicit handlers and a Catalog
`BookSearchProjection`; do not add a mediator dependency solely for naming.

## Consequences

Write rules stay focused and the read model can later move to a search engine.
Projection maintenance and eventual consistency add code and require monitoring.
