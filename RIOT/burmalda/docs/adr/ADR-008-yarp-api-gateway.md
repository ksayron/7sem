# ADR-008: YARP API Gateway

Status: Accepted — 2026-09-17

## Context

Clients need one endpoint, consistent authentication, rate limiting, and
correlation while services remain independently addressable.

## Decision

Use YARP for routing and authorization policy enforcement. Keep business rules
inside services; only aggregate operational health in the gateway.

## Consequences

External topology is stable and cross-cutting policy is centralized. The gateway
is another availability boundary and must not become a business orchestrator.
